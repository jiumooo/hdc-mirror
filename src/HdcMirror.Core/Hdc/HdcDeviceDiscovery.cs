using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.Core.Hdc;

/// <summary>扫描流程当前所处的阶段。界面用它挑资源文案，Core 不出面向界面的文字。</summary>
public enum HdcDiscoveryStage
{
    /// <summary>读 <c>hdc list targets</c>，先把已连接设备放进结果。</summary>
    Enumerating,

    /// <summary>等 <c>hdc discover</c> 的局域网广播结果。</summary>
    Broadcasting,

    /// <summary>对局域网主机做端口探测。</summary>
    ProbingPorts,

    /// <summary>对指定主机做全端口深度扫描（可选，很慢）。</summary>
    DeepScanning,

    /// <summary>结束。</summary>
    Completed,
}

/// <summary>一次进度回调。<see cref="Detail"/> 只放地址这类机器可读内容，不含界面文案。</summary>
/// <param name="Stage">当前阶段。</param>
/// <param name="Completed">已完成的工作单元数。</param>
/// <param name="Total">总工作单元数，0 表示该阶段无法预估总量。</param>
/// <param name="Detail">当前处理的地址，可为空字符串。</param>
public sealed record HdcDiscoveryProgress(HdcDiscoveryStage Stage, int Completed, int Total, string Detail)
{
    /// <summary>完成比例，用于进度条；总量未知时为 0。</summary>
    public double Ratio => Total > 0 ? Math.Clamp((double)Completed / Total, 0d, 1d) : 0d;
}

/// <summary>扫描到的一台候选设备。</summary>
/// <param name="DisplayName">显示名。默认是 <see cref="Endpoint"/>，界面可用设备信息读取器补充。</param>
/// <param name="Address">IPv4 地址。</param>
/// <param name="Port">无线调试端口；未知时为 0。</param>
/// <param name="IsConnected">是否已经在 <c>hdc list targets</c> 里（即已连接）。</param>
public sealed record HdcDiscoveredDevice(string DisplayName, string Address, int Port, bool IsConnected)
{
    /// <summary><c>ip:port</c> 形式的端点。</summary>
    public string Endpoint => Port > 0
        ? Address + ":" + Port.ToString(CultureInfo.InvariantCulture)
        : Address;

    /// <summary>是否是带端口的无线端点。</summary>
    public bool IsWireless => Port > 0;
}

/// <summary>扫描参数。默认值按"温柔、可取消、不扫满全网"的实测结论给。</summary>
public sealed class HdcDiscoveryOptions
{
    /// <summary>
    /// 要扫描的网段。支持 <c>192.0.2.0/24</c>、<c>192.0.2</c>、<c>192.0.2.</c> 三种写法。
    /// 为空时自动取本机每个 IPv4 网卡所在的那个 /24（不按掩码展开，避免 /20 段扫 4000 台）。
    /// </summary>
    public IReadOnlyList<string> Subnets { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 候选端口。鸿蒙的无线调试端口是**随机会话端口**（实测这台是五位数），
    /// 所以这里只放协议上的固定端口；随机端口要靠 <c>hdc discover</c> 广播或用户手填。
    /// </summary>
    public IReadOnlyList<int> CandidatePorts { get; init; } = new[] { 8710, 5555 };

    /// <summary>上次连过的端点（<c>ip:port</c>），会优先做一次 TCP 可达性确认。</summary>
    public IReadOnlyList<string> KnownEndpoints { get; init; } = Array.Empty<string>();

    /// <summary>是否调用 <c>hdc discover</c> 走局域网 UDP 广播（实测固定 6.1 秒返回，且本机被防火墙挡住）。</summary>
    public bool EnableBroadcast { get; init; } = true;

    /// <summary>广播阶段的超时。</summary>
    public TimeSpan BroadcastTimeout { get; init; } = TimeSpan.FromSeconds(7);

    /// <summary>端口探测的并发上限。</summary>
    public int MaxConcurrency { get; init; } = 64;

    /// <summary>单个 TCP 连接的超时（被丢包的闭端口要等满这个时间）。</summary>
    public int ConnectTimeoutMilliseconds { get; init; } = 300;

    /// <summary>
    /// hdc 端点静默窗。连上之后在这个窗口内对端既不主动发数据也不关闭连接，才算「像 hdc」。
    /// <para>
    /// 实测依据（docs/12）：鸿蒙设备上的 hdc daemon 接受连接后一直静默等客户端握手，
    /// 不发送任何 banner；而普通服务（实测 NAS 上的 8710/5555）连上后立刻关闭连接
    /// （读回 0 字节 EOF），或被识别出非 hdc 的 banner。这一条把"端口开着"和"这是 hdc"分开。
    /// </para>
    /// </summary>
    public int HdcSilenceWindowMilliseconds { get; init; } = 400;

    /// <summary>是否对 <see cref="DeepScanHosts"/> 做全端口扫描。默认关闭：单机 65535 端口约需数分钟。</summary>
    public bool DeepScan { get; init; }

    /// <summary>深度扫描的目标主机地址。为空时取 <see cref="KnownEndpoints"/> 里的地址。</summary>
    public IReadOnlyList<string> DeepScanHosts { get; init; } = Array.Empty<string>();

    /// <summary>深度扫描的端口下界。</summary>
    public int DeepScanPortMin { get; init; } = 1024;

    /// <summary>深度扫描的端口上界。</summary>
    public int DeepScanPortMax { get; init; } = 65535;

    /// <summary>深度扫描的并发上限。</summary>
    public int DeepScanConcurrency { get; init; } = 96;

    /// <summary>进度心跳间隔：即使某阶段很久没变化，也要在这个间隔内推一次进度。</summary>
    public int ProgressIntervalMilliseconds { get; init; } = 1000;

    /// <summary>
    /// 是否用 <c>hdc tconn</c> 对候选端点做最终验证。
    /// <para>
    /// 默认开启：实测光看「端口开着且静默」仍会误报（局域网里另外两台 5555 服务就是同类静默端口），
    /// 而 <c>tconn</c> 对非 hdc 端口 425 ms 内返回 <c>[Fail]Connect failed</c>，是唯一权威判定。
    /// 验证成功后若该端点原本没连上，会自动 <c>tconn &lt;ep&gt; -remove</c> 还原现场，
    /// 所以扫描不会留下"被扫描时顺手连上"的连接。
    /// </para>
    /// </summary>
    public bool VerifyWithTconn { get; init; } = true;
}

/// <summary>
/// 局域网设备发现。
/// <para>
/// 实测选型（详见 docs/12-设备发现与信息读取.md）：本机 <c>hdc discover</c>（UDP 8710 广播）
/// 固定 6.1 秒返回 <c>total:0</c>，mDNS 四类服务名均无应答，手机也不回 ICMP，
/// 因此发现分四层：① <c>hdc list targets</c> 已连接设备 ② <c>hdc discover</c> 广播
/// ③ 本机所在 /24 的候选端口 TCP 探测 ④ 指定主机全端口深度扫描（可选）。
/// </para>
/// <para>
/// 全流程可取消；进度以 <see cref="HdcDiscoveryProgress"/> 推送，最长静默 1 秒。
/// </para>
/// </summary>
public sealed class HdcDeviceDiscovery
{
    private const string Category = "discovery";

    private static readonly Regex EndpointPattern = new(
        @"(?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d{1,5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HdcRunner _serverRunner;
    private readonly ILogSink _log;

    /// <summary>创建发现器。</summary>
    /// <param name="hdcPath">hdc 路径，为空时按 <see cref="HdcLocator"/> 查找。</param>
    /// <param name="log">日志出口，可为空。</param>
    /// <param name="options">扫描参数，为空时用默认值。</param>
    public HdcDeviceDiscovery(string? hdcPath = null, ILogSink? log = null, HdcDiscoveryOptions? options = null)
    {
        _log = log ?? NullLogSink.Instance;
        Options = options ?? new HdcDiscoveryOptions();
        Validate(Options);

        // 发现阶段的命令都不带 -t：discover 与 list targets 是 server 级命令。
        _serverRunner = new HdcRunner(
            hdcPath,
            deviceSerial: null,
            log: _log,
            commandTimeout: Options.BroadcastTimeout + TimeSpan.FromSeconds(2));
    }

    /// <summary>当前使用的扫描参数。</summary>
    public HdcDiscoveryOptions Options { get; }

    private static void Validate(HdcDiscoveryOptions options)
    {
        if (options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxConcurrency, "MaxConcurrency must be at least 1.");
        }

        if (options.ConnectTimeoutMilliseconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ConnectTimeoutMilliseconds, "ConnectTimeoutMilliseconds must be at least 1.");
        }

        if (options.HdcSilenceWindowMilliseconds < 50)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.HdcSilenceWindowMilliseconds, "HdcSilenceWindowMilliseconds must be at least 50.");
        }

        if (options.ProgressIntervalMilliseconds < 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ProgressIntervalMilliseconds, "ProgressIntervalMilliseconds must be at least 100.");
        }

        if (options.DeepScan && (options.DeepScanPortMin < 1 || options.DeepScanPortMax > 65535 || options.DeepScanPortMin > options.DeepScanPortMax))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.DeepScanPortMax, "Deep scan port range is invalid.");
        }
    }

    /// <summary>执行一次扫描。</summary>
    /// <param name="progress">进度回调，可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<HdcDiscoveredDevice>> ScanAsync(
        IProgress<HdcDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var state = new ProgressState(Options.ProgressIntervalMilliseconds);
        using var heartbeatSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = PumpProgressAsync(progress, state, heartbeatSource.Token);
        var stopwatch = Stopwatch.StartNew();
        var found = new Dictionary<string, HdcDiscoveredDevice>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // ① 已连接设备：先把"已连接"这一档占住，后面的探测不会把它降级成"仅发现"。
            state.Enter(HdcDiscoveryStage.Enumerating, 0, 0, string.Empty);
            foreach (var device in await EnumerateConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                found[device.Endpoint] = device;
            }

            _log.Info(Category, $"已连接无线设备 {found.Count} 台。");
            var preConnected = new HashSet<string>(found.Keys, StringComparer.OrdinalIgnoreCase);

            // ② 广播与端口探测并行：广播固定 6.1 秒，没必要串在探测前面等。
            state.Enter(HdcDiscoveryStage.Broadcasting, 0, 0, string.Empty);
            var broadcastTask = Options.EnableBroadcast
                ? BroadcastAsync(cancellationToken)
                : Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            var probedTask = ProbeCandidatePortsAsync(state, cancellationToken);

            IReadOnlyList<string> broadcasted = Array.Empty<string>();
            try
            {
                broadcasted = await broadcastTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Warn(Category, "广播阶段超时，忽略其结果。");
            }

            var probed = await probedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var endpoint in broadcasted)
            {
                Merge(found, endpoint, HdcDiscoveryStage.Broadcasting);
            }

            foreach (var endpoint in probed)
            {
                Merge(found, endpoint, HdcDiscoveryStage.ProbingPorts);
            }

            // ③ 可选深度扫描：只针对用户点名的地址，不对整个 /24 展开 65535 端口。
            if (Options.DeepScan)
            {
                foreach (var endpoint in await DeepScanAsync(state, cancellationToken).ConfigureAwait(false))
                {
                    Merge(found, endpoint, HdcDiscoveryStage.DeepScanning);
                }
            }

            // ④ tconn 验证：把"端口静默"的误报剔掉，并还原验证时产生的临时连接。
            if (Options.VerifyWithTconn)
            {
                await FilterWithTconnAsync(found, preConnected, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            heartbeatSource.Cancel();
            await heartbeat.ConfigureAwait(false);
            stopwatch.Stop();
            state.Enter(HdcDiscoveryStage.Completed, state.Completed, state.Total, string.Empty);
            progress?.Report(state.Snapshot());
            _log.Info(Category,
                $"扫描结束：候选 {found.Count} 个（已连接 {CountConnected(found)} 个），耗时 {stopwatch.ElapsedMilliseconds} ms。");
        }

        return found.Values
            .OrderByDescending(static d => d.IsConnected)
            .ThenBy(static d => d.Address, StringComparer.Ordinal)
            .ThenBy(static d => d.Port)
            .ToList();
    }

    /// <summary>主动连接一个端点（<c>hdc tconn ip:port</c>），供界面「连接」按钮使用。</summary>
    /// <param name="endpoint"><c>ip:port</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>hdc 报告连接成功时返回 <see langword="true"/>。</returns>
    public async Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return await TconnAsync(endpoint.Trim(), remove: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>断开一个端点（<c>hdc tconn ip:port -remove</c>），供界面「断开」按钮使用。</summary>
    /// <param name="endpoint"><c>ip:port</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> DisconnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return await TconnAsync(endpoint.Trim(), remove: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TconnAsync(string endpoint, bool remove, CancellationToken cancellationToken)
    {
        var arguments = remove
            ? new[] { "tconn", endpoint, "-remove" }
            : new[] { "tconn", endpoint };
        try
        {
            var result = await _serverRunner.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
            var output = result.CombinedOutput;
            _log.Info(Category,
                $"{(remove ? "tconn -remove" : "tconn")} {endpoint} → exit={result.ExitCode}：{Collapse(output, 200)}");
            return result.Succeeded
                   && !output.Contains("fail", StringComparison.OrdinalIgnoreCase)
                   && !output.Contains("error", StringComparison.OrdinalIgnoreCase);
        }
        catch (HdcException ex)
        {
            _log.Warn(Category, $"{(remove ? "tconn -remove" : "tconn")} {endpoint} 失败。", ex);
            return false;
        }
    }

    private static int CountConnected(Dictionary<string, HdcDiscoveredDevice> found)
        => found.Values.Count(static d => d.IsConnected);

    private void Merge(Dictionary<string, HdcDiscoveredDevice> found, string endpoint, HdcDiscoveryStage stage)
    {
        if (!TrySplitEndpoint(endpoint, out var address, out var port))
        {
            return;
        }

        var key = address + ":" + port.ToString(CultureInfo.InvariantCulture);
        if (found.ContainsKey(key))
        {
            return;
        }

        found[key] = new HdcDiscoveredDevice(key, address, port, false);
        _log.Info(Category, $"{stage} 阶段发现 {key}");
    }

    private async Task<IReadOnlyList<HdcDiscoveredDevice>> EnumerateConnectedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var enumerator = new HdcDeviceEnumerator(_serverRunner, _log);
            var devices = await enumerator.EnumerateAsync(cancellationToken).ConfigureAwait(false);

            var results = new List<HdcDiscoveredDevice>(devices.Count);
            foreach (var device in devices)
            {
                if (!device.IsWireless)
                {
                    // USB 设备没有 IP:端口，属于另一条链路，不混进局域网候选列表。
                    continue;
                }

                if (!TrySplitEndpoint(device.Serial, out var address, out var port))
                {
                    continue;
                }

                results.Add(new HdcDiscoveredDevice(device.Serial, address, port, true));
            }

            return results;
        }
        catch (HdcException ex)
        {
            _log.Warn(Category, "list targets 失败，本次扫描没有已连接设备。", ex);
            return Array.Empty<HdcDiscoveredDevice>();
        }
    }

    private async Task<IReadOnlyList<string>> BroadcastAsync(CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Options.BroadcastTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _serverRunner.RunAsync(new[] { "discover" }, timeoutSource.Token).ConfigureAwait(false);
            stopwatch.Stop();
            _log.Info(Category,
                $"hdc discover 用时 {stopwatch.ElapsedMilliseconds} ms → {Collapse(result.CombinedOutput, 400)}");
            return ExtractEndpoints(result.CombinedOutput);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Warn(Category,
                $"hdc discover 超过 {Options.BroadcastTimeout.TotalSeconds:F0} 秒未返回；本机实测固定 6.1 秒。");
            return Array.Empty<string>();
        }
        catch (HdcException ex)
        {
            _log.Warn(Category, "hdc discover 调用失败。", ex);
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<string>> ProbeCandidatePortsAsync(ProgressState state, CancellationToken cancellationToken)
    {
        var ports = Options.CandidatePorts;
        var hosts = CollectHosts();
        var known = Options.KnownEndpoints;
        var total = (hosts.Count * ports.Count) + known.Count;
        state.Enter(HdcDiscoveryStage.ProbingPorts, 0, total, string.Empty);

        var hits = new List<string>(4);
        using var gate = new SemaphoreSlim(Options.MaxConcurrency, Options.MaxConcurrency);
        var tasks = new List<Task>(total);

        foreach (var endpoint in known)
        {
            if (!TrySplitEndpoint(endpoint, out var address, out var port))
            {
                state.Advance(1);
                continue;
            }

            tasks.Add(ProbeOneAsync(address, port, gate, hits, state, cancellationToken));
        }

        foreach (var host in hosts)
        {
            foreach (var port in ports)
            {
                tasks.Add(ProbeOneAsync(host, port, gate, hits, state, cancellationToken));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        _log.Info(Category, $"端口探测完成：{hosts.Count} 台主机 × {ports.Count} 个端口 + {known.Count} 个已知端点，命中 {hits.Count} 个。");
        return hits;
    }

    private async Task<IReadOnlyList<string>> DeepScanAsync(ProgressState state, CancellationToken cancellationToken)
    {
        var hosts = Options.DeepScanHosts.Count > 0
            ? Options.DeepScanHosts
            : Options.KnownEndpoints.Select(static e => TrySplitEndpoint(e, out var address, out _) ? address : null)
                .Where(static a => a is not null)
                .Select(static a => a!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        if (hosts.Count == 0)
        {
            _log.Warn(Category, "开启了深度扫描但没有指定主机，跳过（随机端口不能在全网段盲扫）。");
            return Array.Empty<string>();
        }

        var portCount = Options.DeepScanPortMax - Options.DeepScanPortMin + 1;
        var total = checked(hosts.Count * portCount);
        state.Enter(HdcDiscoveryStage.DeepScanning, 0, total, string.Empty);
        _log.Info(Category, $"深度扫描 {hosts.Count} 台主机 × {portCount} 端口，并发 {Options.DeepScanConcurrency}，预计较慢。");

        var hits = new List<string>(2);
        using var gate = new SemaphoreSlim(Options.DeepScanConcurrency, Options.DeepScanConcurrency);

        foreach (var hostText in hosts)
        {
            if (!IPAddress.TryParse(hostText, out var address))
            {
                state.Advance(portCount);
                continue;
            }

            var hostHits = 0;
            const int window = 2048;
            for (var start = Options.DeepScanPortMin; start <= Options.DeepScanPortMax; start += window)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(start + window - 1, Options.DeepScanPortMax);
                var batch = new List<Task>(end - start + 1);
                for (var port = start; port <= end; port++)
                {
                    batch.Add(ProbeOneAsync(address, port, gate, hits, state, cancellationToken));
                }

                await Task.WhenAll(batch).ConfigureAwait(false);
                hostHits = 0;
                foreach (var hit in hits)
                {
                    if (hit.StartsWith(hostText + ":", StringComparison.Ordinal))
                    {
                        hostHits++;
                    }
                }

                // 一台机器上已经命中端口就没必要继续扫它的其余端口：无线调试只开一个端口。
                if (hostHits > 0)
                {
                    var remaining = Options.DeepScanPortMax - end;
                    if (remaining > 0)
                    {
                        state.Advance(remaining);
                    }

                    _log.Info(Category, $"{hostText} 已命中端口，停止对该主机的深度扫描。");
                    break;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return hits;
    }

    private async Task ProbeOneAsync(
        string address,
        int port,
        SemaphoreSlim gate,
        List<string> hits,
        ProgressState state,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out var ipAddress))
        {
            state.Advance(1);
            return;
        }

        await ProbeOneAsync(ipAddress, port, gate, hits, state, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeOneAsync(
        IPAddress address,
        int port,
        SemaphoreSlim gate,
        List<string> hits,
        ProgressState state,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            state.SetDetail(address.ToString());
            var verdict = await ProbeEndpointAsync(address, port, cancellationToken).ConfigureAwait(false);
            if (verdict == EndpointVerdict.HdcCandidate)
            {
                var endpoint = address + ":" + port.ToString(CultureInfo.InvariantCulture);
                lock (hits)
                {
                    if (!hits.Contains(endpoint))
                    {
                        hits.Add(endpoint);
                    }
                }

                _log.Info(Category, $"hdc 端点候选：{endpoint}");
            }
            else if (verdict != EndpointVerdict.Closed)
            {
                var endpoint = address + ":" + port.ToString(CultureInfo.InvariantCulture);
                _log.Debug(Category, $"排除 {endpoint}：{verdict}");
            }
        }
        catch (OperationCanceledException)
        {
            // 取消时静默退出，由外层统一检查。
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }

            state.Advance(1);
        }
    }

    /// <summary>
    /// 判定一个 TCP 端口后面是不是 hdc daemon。
    /// <para>
    /// 三步：① 连接（超时即 <see cref="EndpointVerdict.Closed"/>）② 在静默窗内等对端开口
    /// ③ 有数据就查 hdc 特征串（magic <c>HW</c> 或 <c>OHOS HDC</c>）。
    /// 实测鸿蒙设备的 hdc 端口全程静默，所以「静默窗内没被关闭也没乱发数据」= 候选。
    /// </para>
    /// </summary>
    private async Task<EndpointVerdict> ProbeEndpointAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var connectSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectSource.CancelAfter(Options.ConnectTimeoutMilliseconds);
        try
        {
            await client.ConnectAsync(address, port, connectSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or ArgumentException)
        {
            return EndpointVerdict.Closed;
        }

        using var readSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readSource.CancelAfter(Options.HdcSilenceWindowMilliseconds);
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, readSource.Token).ConfigureAwait(false);
            if (read == 0)
            {
                // 对端立刻关闭：普通服务或已经完成一次会话，不是待连的 hdc。
                return EndpointVerdict.ClosedByPeer;
            }

            return LooksLikeHdc(buffer, read) ? EndpointVerdict.HdcCandidate : EndpointVerdict.Banner;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 静默窗到点仍在连着：hdc daemon 的行为。
            return EndpointVerdict.HdcCandidate;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            return EndpointVerdict.ClosedByPeer;
        }
    }

    /// <summary>检查对端首包是否带 hdc 特征（magic <c>HW</c> 或 <c>OHOS HDC</c>）。</summary>
    private static bool LooksLikeHdc(byte[] buffer, int length)
    {
        if (length >= 2 && buffer[0] == (byte)'H' && buffer[1] == (byte)'W')
        {
            return true;
        }

        var text = System.Text.Encoding.ASCII.GetString(buffer, 0, length);
        return text.Contains("OHOS HDC", StringComparison.Ordinal);
    }

    /// <summary>端口探测结论。</summary>
    private enum EndpointVerdict
    {
        /// <summary>连不上（端口关闭或被丢包）。</summary>
        Closed,

        /// <summary>连上后被对端立刻关闭。</summary>
        ClosedByPeer,

        /// <summary>连上后对端发了非 hdc 的数据。</summary>
        Banner,

        /// <summary>连上后静默或带 hdc 特征——按候选上报。</summary>
        HdcCandidate,
    }

    /// <summary>
    /// 用 <c>hdc tconn</c> 做最终验证：连不上的候选剔除；连得上但扫描前没连的，验证完立刻断开还原现场。
    /// </summary>
    private async Task FilterWithTconnAsync(
        Dictionary<string, HdcDiscoveredDevice> found,
        HashSet<string> preConnected,
        CancellationToken cancellationToken)
    {
        foreach (var entry in found.Values.Where(static d => !d.IsConnected).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await TconnAsync(entry.Endpoint, remove: false, cancellationToken).ConfigureAwait(false))
            {
                found.Remove(entry.Endpoint);
                _log.Info(Category, $"{entry.Endpoint} 未通过 tconn 验证，已从候选剔除。");
                continue;
            }

            if (preConnected.Contains(entry.Endpoint))
            {
                continue;
            }

            // 验证会产生一条真实连接，扫描结束后要断开，否则下次扫描它会变成"已连接"。
            if (!await TconnAsync(entry.Endpoint, remove: true, cancellationToken).ConfigureAwait(false))
            {
                _log.Warn(Category, $"{entry.Endpoint} 验证后断开失败，可能残留连接。");
            }
        }
    }

    /// <summary>
    /// 从任意 hdc 输出里抠出 <c>ip:port</c>：过滤本机地址（UDP 广播会被自己的网卡收回来）
    /// 与非法端口。
    /// </summary>
    /// <param name="output">hdc 的合并输出。</param>
    public static IReadOnlyList<string> ExtractEndpoints(string? output)
    {
        var results = new List<string>(2);
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        var local = GetLocalAddresses();
        foreach (Match match in EndpointPattern.Matches(output))
        {
            var address = match.Groups["ip"].Value;
            if (!IPAddress.TryParse(address, out var parsed))
            {
                continue;
            }

            if (!int.TryParse(match.Groups["port"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port < 1
                || port > 65535)
            {
                continue;
            }

            // 自己发的广播会从本机网卡回环回来，必须排掉。
            if (local.Contains(parsed.ToString()))
            {
                continue;
            }

            var endpoint = parsed + ":" + port.ToString(CultureInfo.InvariantCulture);
            if (!results.Contains(endpoint))
            {
                results.Add(endpoint);
            }
        }

        return results;
    }

    /// <summary>列出本机每个 IPv4 网卡所在的 /24（自动扫描用）。</summary>
    public static IReadOnlyList<string> DescribeLocalSubnets()
    {
        var subnets = new List<string>(4);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var text = unicast.Address.ToString();
                if (!subnets.Contains(text))
                {
                    subnets.Add(text);
                }
            }
        }

        return subnets;
    }

    private static HashSet<string> GetLocalAddresses()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in DescribeLocalSubnets())
        {
            set.Add(address);
        }

        set.Add(IPAddress.Loopback.ToString());
        return set;
    }

    private IReadOnlyList<IPAddress> CollectHosts()
    {
        var local = GetLocalAddresses();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new List<IPAddress>(260);

        foreach (var subnet in ResolveSubnets())
        {
            if (!TryParseSubnetPrefix(subnet, out var prefix))
            {
                continue;
            }

            for (var host = 1; host <= 254; host++)
            {
                var text = prefix + "." + host.ToString(CultureInfo.InvariantCulture);
                if (local.Contains(text) || !seen.Add(text))
                {
                    continue;
                }

                if (IPAddress.TryParse(text, out var address))
                {
                    hosts.Add(address);
                }
            }
        }

        // 已知端点所在的主机即使不在扫描网段里，也要作为候选。
        foreach (var endpoint in Options.KnownEndpoints)
        {
            if (TrySplitEndpoint(endpoint, out var address, out _) && seen.Add(address)
                && IPAddress.TryParse(address, out var parsed) && !local.Contains(address))
            {
                hosts.Add(parsed);
            }
        }

        return hosts;
    }

    private IEnumerable<string> ResolveSubnets()
    {
        if (Options.Subnets.Count > 0)
        {
            foreach (var item in Options.Subnets)
            {
                if (TryParseSubnetPrefix(item, out var prefix))
                {
                    yield return prefix + ".0";
                }
            }

            yield break;
        }

        foreach (var address in DescribeLocalSubnets())
        {
            if (TryParseSubnetPrefix(address, out var prefix))
            {
                yield return prefix + ".0";
            }
        }
    }

    private static bool TryParseSubnetPrefix(string? value, out string prefix)
    {
        prefix = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var slash = text.IndexOf('/');
        if (slash > 0)
        {
            text = text[..slash];
        }

        text = text.TrimEnd('.');

        var parts = text.Split('.');
        if (parts.Length < 3)
        {
            return false;
        }

        for (var index = 0; index < 3; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var octet)
                || octet < 0
                || octet > 255)
            {
                return false;
            }
        }

        prefix = parts[0] + "." + parts[1] + "." + parts[2];
        return true;
    }

    private static bool TrySplitEndpoint(string? endpoint, out string address, out int port)
    {
        address = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var text = endpoint.Trim();
        var separator = text.LastIndexOf(':');
        if (separator <= 0 || separator == text.Length - 1)
        {
            return false;
        }

        if (!IPAddress.TryParse(text[..separator], out var parsed))
        {
            return false;
        }

        if (!int.TryParse(text[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 1
            || value > 65535)
        {
            return false;
        }

        address = parsed.ToString();
        port = value;
        return true;
    }

    private static async Task PumpProgressAsync(
        IProgress<HdcDiscoveryProgress>? progress,
        ProgressState state,
        CancellationToken token)
    {
        if (progress is null)
        {
            return;
        }

        progress.Report(state.Snapshot());
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(state.IntervalMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                progress.Report(state.Snapshot());
            }
        }
        catch (OperationCanceledException)
        {
            // 正常收尾路径。
        }
    }

    private static string Collapse(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return flat.Length <= maxLength ? flat : flat[..maxLength] + "…(truncated)";
    }

    /// <summary>进度状态的线程安全快照，供心跳线程与工作线程共用。</summary>
    private sealed class ProgressState
    {
        private readonly object _gate = new();
        private HdcDiscoveryStage _stage = HdcDiscoveryStage.Enumerating;
        private int _completed;
        private int _total;
        private string _detail = string.Empty;

        public ProgressState(int intervalMilliseconds)
        {
            IntervalMilliseconds = intervalMilliseconds;
        }

        public int IntervalMilliseconds { get; }

        public int Completed
        {
            get
            {
                lock (_gate)
                {
                    return _completed;
                }
            }
        }

        public int Total
        {
            get
            {
                lock (_gate)
                {
                    return _total;
                }
            }
        }

        public void Enter(HdcDiscoveryStage stage, int completed, int total, string detail)
        {
            lock (_gate)
            {
                _stage = stage;
                _completed = completed;
                _total = total;
                _detail = detail;
            }
        }

        public void Advance(int delta)
        {
            lock (_gate)
            {
                _completed += delta;
            }
        }

        public void SetDetail(string detail)
        {
            lock (_gate)
            {
                _detail = detail;
            }
        }

        public HdcDiscoveryProgress Snapshot()
        {
            lock (_gate)
            {
                return new HdcDiscoveryProgress(_stage, _completed, _total, _detail);
            }
        }
    }
}
