using System.Globalization;
using System.IO.Compression;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.Core.Hdc;

/// <summary>设备侧 scrcpy 服务的启动参数。</summary>
public sealed record ScrcpyDaemonOptions
{
    /// <summary>上游 hosScrcpy 安装包（jar）路径，变体 so 从这里取。发布包不打包它，只能运行时定位。</summary>
    public string? JarPath { get; init; }

    /// <summary>强制指定某个 so 条目（相对 jar 的全名），为空时自动挑选。</summary>
    public string? ServerEntry { get; init; }

    /// <summary>首选内核版本标记，默认 <c>6.8</c>（实测 uitest 7.0.0.1 直接可用 6.8 变体）。</summary>
    public string PreferredKernelVersion { get; init; } = "6.8";

    /// <summary>设备侧工作目录。</summary>
    public string RemoteDirectory { get; init; } = "/data/local/tmp";

    /// <summary>缩放比例，1 = 原始分辨率。</summary>
    public int Scale { get; init; } = 1;

    /// <summary>目标帧率。</summary>
    public int FrameRate { get; init; } = 30;

    /// <summary>目标码率（bit/s）。</summary>
    public int BitRate { get; init; } = 8_388_608;

    /// <summary>daemon 内部监听端口（由 hdc fport 转发）。</summary>
    public int Port { get; init; } = 5000;

    /// <summary>关键帧间隔（毫秒）。</summary>
    public int IFrameInterval { get; init; } = 2000;

    /// <summary>等待 socket 监听的最长时间。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// 设备侧投屏服务（scrcpy daemon）的生命周期管理：推送 so → 起 daemon → 等 socket → 停止时 kill -9。
/// <para>
/// 判活只认 <c>/proc/net/unix</c> 里的 <c>scrcpy_grpc_socket</c>：设备上可能已经有上游客户端起的
/// daemon，但它没带 <c>--extension-name</c>，不服务这个 socket，因此进程存在 ≠ 可用。
/// </para>
/// <para>
/// 变体选择不按 uitest 版本号匹配 —— 实测 uitest 7.0.0.1 直接跑 6.8 变体。策略是把 jar 里可用的
/// 变体按「首选项 → 是否 unix 专用 → 内核版本降序」排好，逐个尝试，成功即记下。
/// </para>
/// </summary>
public sealed class ScrcpyDaemon : IDisposable, IAsyncDisposable
{
    /// <summary>daemon 暴露的抽象 socket 名。</summary>
    public const string AbstractSocketName = "scrcpy_grpc_socket";

    private const string Category = "daemon";
    private const string ExtensionPrefix = "libscreen_casting_";

    private readonly HdcRunner _runner;
    private readonly ScrcpyDaemonOptions _options;
    private readonly ILogSink _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _pushedFiles = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>为真时下一次 <see cref="EnsureRunningAsync"/> 忽略复用，先杀掉再重建。</summary>
    private bool _restartRequested;

    /// <summary>创建 daemon 管理器（构造时不接触设备）。</summary>
    /// <param name="runner">hdc 执行器，应已绑定目标设备。</param>
    /// <param name="options">启动参数。</param>
    /// <param name="log">日志出口，可为空。</param>
    public ScrcpyDaemon(HdcRunner runner, ScrcpyDaemonOptions options, ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        _runner = runner;
        _options = options;
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>本次成功加载的 jar 条目全名，未启动时为 <see langword="null"/>。</summary>
    public string? LoadedVariant { get; private set; }

    /// <summary>本次推送到设备的 so 路径，未推送时为 <see langword="null"/>。</summary>
    public string? RemoteServerPath { get; private set; }

    /// <summary>daemon 进程号，未知时为 -1。</summary>
    public int ProcessId { get; private set; } = -1;

    /// <summary>daemon 是否由本实例启动（决定清理时是否 kill）。</summary>
    public bool StartedByUs { get; private set; }

    /// <summary>daemon 是否已在监听 gRPC socket。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> IsSocketListeningAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner
            .ShellAsync($"cat /proc/net/unix | grep {AbstractSocketName}", cancellationToken)
            .ConfigureAwait(false);
        return result.StandardOutput.Contains(AbstractSocketName, StringComparison.Ordinal);
    }

    /// <summary>
    /// 确保 daemon 可用：socket 已在监听则直接复用，否则推送 so 并逐个变体尝试启动。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可用时返回 <see langword="true"/>。</returns>
    /// <exception cref="HdcException">所有变体都没起来。</exception>
    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsSocketListeningAsync(cancellationToken).ConfigureAwait(false))
            {
                // 设备上已有 socket，可能是上一次会话被强杀后留下的 daemon：它的 gRPC 流还占着，
                // 新会话能连上却永远收不到帧。所以先清掉本工具变体的 daemon，再重建一个干净的。
                var stale = await FindExtensionDaemonsAsync(cancellationToken).ConfigureAwait(false);

                // 快路径：socket 在监听，且服务它的正是本进程刚起的那个 daemon，就直接复用。
                // 设备侧 uitest 拉起要 2~3 秒，重连时省下这段时间；握手拿不到帧时调用方会 RequestRestart 走重建分支。
                if (!_restartRequested && StartedByUs && ProcessId > 0 && stale.Contains(ProcessId))
                {
                    _log.Info(Category, $"复用本进程启动的 daemon pid={ProcessId}，跳过重启。");
                    return true;
                }

                _restartRequested = false;
                if (stale.Count == 0)
                {
                    StartedByUs = false;
                    _log.Info(Category, "socket 已在监听，复用现有 daemon（不接管其生命周期）。");
                    return true;
                }

                foreach (var stalePid in stale)
                {
                    var killText = "kill -9 " + stalePid.ToString(CultureInfo.InvariantCulture);
                    await _runner.ShellAsync(killText, cancellationToken).ConfigureAwait(false);
                }

                _log.Info(Category, "清理上一次会话遗留的 daemon：" + string.Join(",", stale));
                for (var i = 0; i < 10; i++)
                {
                    if (!await IsSocketListeningAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
                }
            }

            var variants = ResolveVariants();
            var failures = new List<string>(variants.Count);

            foreach (var variant in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remoteName = ExtensionPrefix + variant.Key + ".z.so";
                var remotePath = _options.RemoteDirectory.TrimEnd('/') + "/" + remoteName;
                _log.Info(Category, $"尝试变体 {variant.EntryName}");

                try
                {
                    await EnsurePushedAsync(variant, remotePath, cancellationToken).ConfigureAwait(false);
                    var start = await StartAsync(remoteName, cancellationToken).ConfigureAwait(false);
                    if (!start.Succeeded)
                    {
                        failures.Add($"{variant.Key}: start-daemon 退出码 {start.ExitCode} {start.StandardError.Trim()}");
                        await KillByNameAsync(remoteName, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (await WaitForSocketAsync(_options.StartupTimeout, cancellationToken).ConfigureAwait(false))
                    {
                        LoadedVariant = variant.EntryName;
                        RemoteServerPath = remotePath;
                        StartedByUs = true;
                        ProcessId = await FindProcessIdAsync(remoteName, cancellationToken).ConfigureAwait(false);
                        _log.Info(
                            Category,
                            $"daemon 就绪：变体 {variant.Key}，pid={ProcessId}，socket={AbstractSocketName}。");
                        return true;
                    }

                    failures.Add($"{variant.Key}: {_options.StartupTimeout.TotalSeconds:F0} 秒内 socket 未监听");
                    await KillByNameAsync(remoteName, cancellationToken).ConfigureAwait(false);
                }
                catch (HdcException ex)
                {
                    failures.Add($"{variant.Key}: {ex.Message}");
                    await KillByNameAsync(remoteName, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new HdcException("所有设备侧 so 变体都没能起来：" + string.Join("；", failures));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>要求下一次确保 daemon 时重新拉起（握手拿不到帧、卡在等待时调用）。</summary>
    public void RequestRestart() => _restartRequested = true;

    /// <summary>停止本实例启动的 daemon。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停止 daemon 并删除本次推送过的远端文件。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await RemovePushedFilesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            CleanupAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Error(Category, "释放 daemon 时清理失败，设备上可能残留 so 或 daemon。", ex);
        }

        _gate.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await CleanupAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(Category, "释放 daemon 时清理失败，设备上可能残留 so 或 daemon。", ex);
        }

        _gate.Dispose();
    }

    /// <summary>列出 jar 里可选变体并排好序。</summary>
    private IReadOnlyList<ServerVariant> ResolveVariants()
    {
        var jarPath = _options.JarPath;
        if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath))
        {
            throw new HdcException(
                $"找不到上游 jar（{jarPath ?? "未指定"}），无法取得设备侧 so 变体；请把 jar 放到安装目录或指定路径。");
        }

        using var archive = ZipFile.OpenRead(jarPath);
        var variants = new List<ServerVariant>(16);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith("libscrcpy/", StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".z.so", StringComparison.OrdinalIgnoreCase)
                || name.Contains("emulator", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = name["libscrcpy/".Length..];
            var key = KeyOf(fileName);
            variants.Add(new ServerVariant(name, entry.Length, key, SortVersionOf(key)));
        }

        if (variants.Count == 0)
        {
            throw new HdcException($"{jarPath} 里没有可用的 libscrcpy/*.z.so 变体。");
        }

        var explicitEntry = _options.ServerEntry;
        return variants
            .OrderByDescending(v => explicitEntry is not null
                                    && string.Equals(v.EntryName, explicitEntry, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => string.Equals(v.Key, _options.PreferredKernelVersion, StringComparison.Ordinal))
            .ThenBy(v => v.EntryName.Contains("_unix_", StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(v => v.SortVersion)
            .ThenBy(v => v.EntryName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>从变体文件名里取版本标记：<c>libscrcpy_server_unix_6.8-20260801.z.so</c> → <c>6.8</c>。</summary>
    private static string KeyOf(string fileName)
    {
        const string Suffix = ".z.so";
        var stem = fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^Suffix.Length]
            : fileName;

        var dash = stem.IndexOf('-', StringComparison.Ordinal);
        var head = dash >= 0 ? stem[..dash] : stem;

        var underscore = head.LastIndexOf('_');
        var token = underscore >= 0 ? head[(underscore + 1)..] : head;
        if (token.Length > 0 && char.IsAsciiDigit(token[0]))
        {
            return token;
        }

        // 形如 libscrcpy_server0（没有下划线分数）时，退回到末尾连续数字。
        var digits = 0;
        while (digits < head.Length && char.IsAsciiDigit(head[head.Length - 1 - digits]))
        {
            digits++;
        }

        return digits > 0 ? head[^digits..] : "0";
    }

    private static double SortVersionOf(string key)
    {
        var parts = key.Split('.');
        var major = double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMajor)
            ? parsedMajor
            : 0;
        var minor = parts.Length > 1
                    && double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMinor)
            ? parsedMinor
            : 0;
        return (major * 1000) + minor;
    }

    /// <summary>找出由本工具变体（<see cref="ExtensionPrefix"/>）拉起的 daemon 进程号。</summary>
    private async Task<List<int>> FindExtensionDaemonsAsync(CancellationToken cancellationToken)
    {
        var ps = await _runner.ShellAsync("ps -ef", cancellationToken).ConfigureAwait(false);
        var pids = new List<int>();
        foreach (var line in ps.StandardOutput.Split('\n'))
        {
            if (!line.Contains("--extension-name " + ExtensionPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                pids.Add(pid);
            }
        }

        return pids;
    }
    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (!StartedByUs || ProcessId <= 0)
        {
            _log.Info(Category, "没有由本进程启动的 daemon 需要停止。");
            return;
        }

        var pid = ProcessId;
        ProcessId = -1;
        StartedByUs = false;

        // SIGTERM 会被设备侧拒绝，只能 -9。
        var kill = await _runner.ShellAsync($"kill -9 {pid.ToString(CultureInfo.InvariantCulture)}", cancellationToken)
            .ConfigureAwait(false);
        _log.Info(Category, $"kill -9 {pid} 退出码 {kill.ExitCode}");

        for (var i = 0; i < 10; i++)
        {
            if (!await IsSocketListeningAsync(cancellationToken).ConfigureAwait(false))
            {
                _log.Info(Category, "socket 已释放。");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        _log.Warn(Category, "kill -9 之后 socket 仍在，可能有其他 daemon 在服务它。");
    }

    private async Task RemovePushedFilesCoreAsync(CancellationToken cancellationToken)
    {
        if (_pushedFiles.Count == 0)
        {
            return;
        }

        foreach (var path in _pushedFiles.ToArray())
        {
            var remove = await _runner.ShellAsync($"rm -f {path}", cancellationToken).ConfigureAwait(false);
            var check = await _runner.ShellAsync($"ls {path} 2>/dev/null", cancellationToken).ConfigureAwait(false);
            var stillThere = check.StandardOutput.Contains(path, StringComparison.Ordinal);
            _log.Info(Category, $"删除 {path}：rm 退出码 {remove.ExitCode}，残留={stillThere}");
            if (!stillThere)
            {
                _pushedFiles.Remove(path);
            }
        }
    }

    private async Task EnsurePushedAsync(ServerVariant variant, string remotePath, CancellationToken cancellationToken)
    {
        var stat = await _runner.ShellAsync($"stat -c %s {remotePath} 2>/dev/null", cancellationToken)
            .ConfigureAwait(false);
        if (stat.Succeeded
            && long.TryParse(stat.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remoteSize)
            && remoteSize == variant.LengthBytes)
        {
            _log.Info(Category, $"远端 {remotePath} 已存在且大小一致（{remoteSize} 字节），跳过推送。");
            return;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(remotePath));
        try
        {
            await Task.Run(() => ExtractVariant(variant.EntryName, tempPath), cancellationToken).ConfigureAwait(false);
            var send = await _runner.RunAsync(new[] { "file", "send", tempPath, remotePath }, cancellationToken)
                .ConfigureAwait(false);
            if (!send.Succeeded)
            {
                throw new HdcCommandException(send);
            }

            _pushedFiles.Add(remotePath);
            _log.Info(Category, $"已推送 {variant.EntryName} → {remotePath}（{variant.LengthBytes} 字节）。");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时文件删不掉不影响设备侧启动，系统会自行回收临时目录。
            }
        }
    }

    private void ExtractVariant(string entryName, string destination)
    {
        using var archive = ZipFile.OpenRead(_options.JarPath!);
        var entry = archive.GetEntry(entryName) ?? throw new HdcException($"jar 里找不到 {entryName}。");
        entry.ExtractToFile(destination, overwrite: true);
    }

    private Task<HdcResult> StartAsync(string remoteName, CancellationToken cancellationToken)
    {
        var command =
            $"cd {_options.RemoteDirectory} && uitest start-daemon singleness --extension-name {remoteName}" +
            $" -scale {_options.Scale.ToString(CultureInfo.InvariantCulture)}" +
            $" -frameRate {_options.FrameRate.ToString(CultureInfo.InvariantCulture)}" +
            $" -bitRate {_options.BitRate.ToString(CultureInfo.InvariantCulture)}" +
            $" -p {_options.Port.ToString(CultureInfo.InvariantCulture)}" +
            $" -iFrameInterval {_options.IFrameInterval.ToString(CultureInfo.InvariantCulture)} &";

        _log.Info(Category, command);
        return _runner.ShellAsync(command, cancellationToken);
    }

    private async Task<bool> WaitForSocketAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsSocketListeningAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return await IsSocketListeningAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task KillByNameAsync(string remoteName, CancellationToken cancellationToken)
    {
        var pid = await FindProcessIdAsync(remoteName, cancellationToken).ConfigureAwait(false);
        if (pid <= 0)
        {
            return;
        }

        var kill = await _runner.ShellAsync($"kill -9 {pid.ToString(CultureInfo.InvariantCulture)}", cancellationToken)
            .ConfigureAwait(false);
        _log.Info(Category, $"清理本次启动的 daemon pid={pid}，kill -9 退出码 {kill.ExitCode}");
    }

    private async Task<int> FindProcessIdAsync(string remoteName, CancellationToken cancellationToken)
    {
        var result = await _runner
            .ShellAsync($"ps -ef | grep extension-name | grep {remoteName} | grep -v grep", cancellationToken)
            .ConfigureAwait(false);

        var best = -1;
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            if (!line.Contains(remoteName, StringComparison.Ordinal)
                || !line.Contains("uitest", StringComparison.Ordinal)
                || line.Contains("grep", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var field in fields)
            {
                if (int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > best)
                {
                    // ps -ef 的列顺序里第二列是 pid，取同族最大值即最新起的那个进程。
                    best = pid;
                    break;
                }
            }
        }

        return best;
    }

    private sealed record ServerVariant(string EntryName, long LengthBytes, string Key, double SortVersion);
}
