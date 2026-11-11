using System.Diagnostics;
using System.Text;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.Core.Hdc;

/// <summary>一次 hdc 调用的原始结果。</summary>
public sealed class HdcResult
{
    /// <summary>构造结果对象。</summary>
    /// <param name="arguments">传给 hdc 的参数（不含 <c>-t &lt;设备&gt;</c> 前缀）。</param>
    /// <param name="exitCode">进程退出码。</param>
    /// <param name="standardOutput">标准输出。</param>
    /// <param name="standardError">标准错误。</param>
    public HdcResult(IReadOnlyList<string> arguments, int exitCode, string standardOutput, string standardError)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Arguments = arguments;
        ExitCode = exitCode;
        StandardOutput = standardOutput ?? string.Empty;
        StandardError = standardError ?? string.Empty;
    }

    /// <summary>传给 hdc 的参数。</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>进程退出码，0 表示成功。</summary>
    public int ExitCode { get; }

    /// <summary>标准输出。</summary>
    public string StandardOutput { get; }

    /// <summary>标准错误。</summary>
    public string StandardError { get; }

    /// <summary>是否成功（退出码为 0）。</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>stdout 与 stderr 拼在一起并去掉首尾空白，便于整体判断关键字。</summary>
    public string CombinedOutput => (StandardOutput + StandardError).Trim();

    /// <inheritdoc />
    public override string ToString()
        => $"hdc {string.Join(' ', Arguments)} -> exit={ExitCode}";
}

/// <summary>hdc 层所有异常的基类。</summary>
public class HdcException : Exception
{
    /// <summary>以消息构造。</summary>
    public HdcException(string message)
        : base(message)
    {
    }

    /// <summary>以消息与内部异常构造。</summary>
    public HdcException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>找不到 hdc 可执行文件。</summary>
public sealed class HdcNotFoundException : HdcException
{
    /// <summary>以消息与已尝试路径构造。</summary>
    public HdcNotFoundException(string message, IReadOnlyList<string> searchedPaths)
        : base(message)
    {
        SearchedPaths = searchedPaths ?? Array.Empty<string>();
    }

    /// <summary>按顺序尝试过的路径，便于在界面上告诉用户具体缺什么。</summary>
    public IReadOnlyList<string> SearchedPaths { get; }
}

/// <summary>hdc 进程返回了非零退出码。</summary>
public sealed class HdcCommandException : HdcException
{
    /// <summary>以失败结果构造，消息里带上原始 stderr。</summary>
    public HdcCommandException(HdcResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    /// <summary>原始结果。</summary>
    public HdcResult Result { get; }

    /// <summary>退出码。</summary>
    public int ExitCode => Result.ExitCode;

    private static string BuildMessage(HdcResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var stderr = result.StandardError.Trim();
        var stdout = result.StandardOutput.Trim();
        var detail = stderr.Length > 0 ? stderr : stdout;
        return detail.Length > 0
            ? $"{result}；原始输出：{detail}"
            : $"{result}；没有输出";
    }
}

/// <summary>hdc 进程在超时时间内没有结束。</summary>
public sealed class HdcTimeoutException : HdcException
{
    /// <summary>以参数与超时构造。</summary>
    public HdcTimeoutException(IReadOnlyList<string> arguments, TimeSpan timeout)
        : base($"hdc {string.Join(' ', arguments ?? Array.Empty<string>())} 在 {timeout.TotalSeconds:F0} 秒内未结束，已强制终止。")
    {
        Timeout = timeout;
    }

    /// <summary>超时阈值。</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>
/// 按固定优先级定位 <c>hdc.exe</c>：
/// ① 用户设置里指定的路径 ② <c>%LOCALAPPDATA%\HdcMirror\hdc.exe</c> ③ 程序目录下 <c>tools\hdc.exe</c> ④ <c>PATH</c>。
/// </summary>
public static class HdcLocator
{
    /// <summary>可执行文件名（Windows 带 <c>.exe</c>）。</summary>
    public static string ExecutableName { get; } = OperatingSystem.IsWindows() ? "hdc.exe" : "hdc";

    /// <summary>按优先级展开所有候选路径（可能包含不存在的路径，用于错误提示）。</summary>
    /// <param name="userConfiguredPath">用户在设置里指定的路径，可为空。</param>
    public static IReadOnlyList<string> EnumerateCandidates(string? userConfiguredPath)
    {
        var candidates = new List<string>(8);

        if (!string.IsNullOrWhiteSpace(userConfiguredPath))
        {
            candidates.Add(Environment.ExpandEnvironmentVariables(userConfiguredPath!.Trim()));
        }

        if (!string.IsNullOrEmpty(AppPaths.DataRoot))
        {
            candidates.Add(Path.Combine(AppPaths.DataRoot, ExecutableName));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "HdcMirror", ExecutableName));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", ExecutableName));

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVariable))
        {
            foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(Path.Combine(entry, ExecutableName));
            }
        }

        return candidates;
    }

    /// <summary>按优先级找到第一个真实存在的 hdc。</summary>
    /// <param name="userConfiguredPath">用户在设置里指定的路径，可为空。</param>
    /// <param name="path">找到的绝对路径。</param>
    /// <param name="searchedPaths">按顺序尝试过的路径。</param>
    /// <returns>找到返回 <see langword="true"/>。</returns>
    public static bool TryResolve(string? userConfiguredPath, out string path, out IReadOnlyList<string> searchedPaths)
    {
        searchedPaths = EnumerateCandidates(userConfiguredPath);
        foreach (var candidate in searchedPaths)
        {
            if (File.Exists(candidate))
            {
                path = Path.GetFullPath(candidate);
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    /// <summary>按优先级找到 hdc，找不到时抛出带完整尝试清单的异常。</summary>
    /// <param name="userConfiguredPath">用户在设置里指定的路径，可为空。</param>
    public static string Resolve(string? userConfiguredPath)
    {
        if (TryResolve(userConfiguredPath, out var path, out var searched))
        {
            return path;
        }

        var message = "找不到 " + ExecutableName + "。已按顺序尝试：" +
                      string.Join(" → ", searched) +
                      "。请在设置里指定 hdc.exe 路径，或把 hdc.exe 放到 %LOCALAPPDATA%\\HdcMirror\\ 下。";
        throw new HdcNotFoundException(message, searched);
    }
}

/// <summary>
/// hdc 命令执行封装：异步、带超时与取消、捕获 stdout/stderr、非零退出码转异常。
/// <para>
/// 安全约束：一律 <c>UseShellExecute = false</c> + <see cref="ProcessStartInfo.ArgumentList"/> 传参，
/// 不经过命令行字符串拼接，因此路径里的空格、引号、<c>&amp;</c> 不会被 shell 解释。
/// </para>
/// </summary>
public sealed class HdcRunner
{
    private readonly ILogSink _log;

    /// <summary>创建执行器。</summary>
    /// <param name="hdcPath">用户设置里指定的 hdc 路径；为空时按 <see cref="HdcLocator"/> 的优先级查找。</param>
    /// <param name="deviceSerial">目标设备标识；非空时每条命令自动加 <c>-t &lt;设备&gt;</c>。</param>
    /// <param name="log">日志出口，可为空。</param>
    /// <param name="commandTimeout">单条命令超时，默认 30 秒。</param>
    public HdcRunner(string? hdcPath = null, string? deviceSerial = null, ILogSink? log = null, TimeSpan? commandTimeout = null)
    {
        HdcPath = HdcLocator.Resolve(hdcPath);
        DeviceSerial = string.IsNullOrWhiteSpace(deviceSerial) ? null : deviceSerial!.Trim();
        _log = log ?? NullLogSink.Instance;
        CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(30);
        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout), CommandTimeout, "超时必须为正。");
        }
    }

    /// <summary>实际使用的 hdc 绝对路径。</summary>
    public string HdcPath { get; }

    /// <summary>目标设备标识（USB 序列号或 <c>ip:port</c>），可为空。</summary>
    public string? DeviceSerial { get; }

    /// <summary>单条命令的超时。</summary>
    public TimeSpan CommandTimeout { get; }

    /// <summary>执行一条 hdc 命令，不因非零退出码抛异常。</summary>
    /// <param name="arguments">参数列表（不含 <c>-t &lt;设备&gt;</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<HdcResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var argumentList = arguments as IReadOnlyList<string> ?? arguments.ToList();
        if (argumentList.Count == 0)
        {
            throw new ArgumentException("至少需要一个 hdc 参数。", nameof(arguments));
        }

        var startInfo = new ProcessStartInfo(HdcPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (DeviceSerial is not null)
        {
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(DeviceSerial);
        }

        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new HdcException($"无法启动 {HdcPath}。");
            }
        }
        catch (Exception ex) when (ex is not HdcException)
        {
            throw new HdcException($"无法启动 {HdcPath}：{ex.Message}", ex);
        }

        // 先起读取，避免管道写满导致子进程阻塞（.NET 的经典死锁点）。
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new HdcTimeoutException(argumentList, CommandTimeout);
        }

        var stdout = await ReadSafelyAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await ReadSafelyAsync(stderrTask).ConfigureAwait(false);
        var result = new HdcResult(argumentList, process.ExitCode, stdout, stderr);
        _log.Debug("hdc", result.ToString());
        return result;
    }

    /// <summary>执行一条 hdc 命令，非零退出码抛 <see cref="HdcCommandException"/>。</summary>
    /// <param name="arguments">参数列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<HdcResult> RunCheckedAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new HdcCommandException(result);
        }

        return result;
    }

    /// <summary>在设备上执行一条 shell 命令（<c>hdc shell &lt;command&gt;</c>），不因非零退出码抛异常。</summary>
    /// <param name="shellCommand">整条 shell 命令，作为单个参数交给 hdc，不经本地 shell 解释。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<HdcResult> ShellAsync(string shellCommand, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellCommand);
        return RunAsync(new[] { "shell", shellCommand }, cancellationToken);
    }

    private static async Task<string> ReadSafelyAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程被强杀时管道可能已断，已读到的内容仍交给调用方判断。
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程可能刚好自己退出，忽略。
        }
    }
}
