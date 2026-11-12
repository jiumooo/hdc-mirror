using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HdcMirror.App.Resources;
using HdcMirror.Core.Diagnostics;
using HdcMirror.App.Services;
using HdcMirror.Core.Hdc;
using HdcMirror.Core.Scrcpy;
using HdcMirror.Decode;
using HdcMirror.Decode.Video;

namespace HdcMirror.App.Video;

/// <summary>镜像会话对外暴露的状态。</summary>
internal enum MirrorStatus
{
    /// <summary>未启动或已停止。</summary>
    Stopped = 0,

    /// <summary>正在枚举设备、拉起守护进程、建立端口转发。</summary>
    Starting = 1,

    /// <summary>已建流，正在接收并解码帧（设备静止时可能长时间无帧，属正常）。</summary>
    Streaming = 2,

    /// <summary>启动或运行期失败。</summary>
    Failed = 3,
}

/// <summary>
/// M1b 镜像会话：把 M1a 的 gRPC 帧流接上解码器，产出可上屏的 BGRA32 帧。
///
/// 数据通路（线程）：
/// <code>
///   线程池线程 A：ScrcpyClient.ReadFramesAsync  ──►  Annex B 访问单元
///        │                                            │
///        ├─ 配置帧变化？──► IH264Decoder.Configure      │
///        └─ IH264Decoder.TryDecode ──► BGRA32 ──► FrameExchange.Publish（覆盖式单帧槽）
///                                                          │
///   UI 线程（MainWindow 定时器）：FrameExchange.Consume ──► WriteableBitmap.WritePixels
/// </code>
/// 解码与颜色转换全部在 A 上完成，UI 线程只做一次像素拷贝。零帧本身不触发任何动作（不重连、不报错）；只有当设备侧真正结束帧流时才走有界自动重连。
/// </summary>
internal sealed class MirrorSession : IDisposable
{
    private const string Category = "App.Mirror";

    /// <summary>解码连续失败多少次后判定会话失败。</summary>
    private const int MaxConsecutiveDecodeErrors = 60;

    // 抓取帧率 / 码率改成用户在设置页可调（见 Services\AppSettings），每次连接现读，不再写死常量。
    // 实测参考：请求 30 时设备只推 ~15 fps、请求 60 时推 ~31 fps，本机解码 ~4 ms/帧，瓶颈在设备侧抓取。
    /// <summary>
    /// 设备在画面长时间无变化时会主动结束推送（实测静止约 80 秒后服务端关流）；
    /// 手机休眠、WiFi 抖动、USB 被拔也会让流断开。这些都不是解码失败，
    /// 一律进入自动重连，重连总时长以 <see cref="ReconnectBudget"/> 为上限。
    /// </summary>
    private static readonly TimeSpan ReconnectBudget = TimeSpan.FromMinutes(10);

    /// <summary>两次重连之间的等待时间：2 秒起，逐次翻倍，最长 10 秒。</summary>
    private static readonly TimeSpan ReconnectMinDelay = TimeSpan.FromSeconds(2);

    private readonly ILogSink _log;
    private readonly FrameExchange _frames = new();
    private readonly SynchronizationContext? _ui;
    private readonly Action<MirrorStatus, string?> _report;
    private readonly Stopwatch _clock = new();

    private CancellationTokenSource? _cts;
    private Task? _pipeline;
    private HdcRunner? _controlRunner;
    private HdcRunner? _deviceRunner;
    private ScrcpyDaemon? _daemon;
    private HdcPortForwarder? _forwarder;
    private ScrcpyClient? _client;
    private IH264Decoder? _decoder;
    private long _decoded;
    private long _firstFrameMilliseconds = -1;
    private long _lastFrameTimestamp;
    private long _decodeErrors;
    private long _decodeMsSum;
    private long _decodeMsMax;
    private long _decodeStatsStart;
    private int _requestedFrameRate = 60;
    private int _running;
    private int _disposed;
    private byte[] _configured = Array.Empty<byte>();

    public MirrorSession(ILogSink log, Action<MirrorStatus, string?> report)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _ui = SynchronizationContext.Current;
    }

    /// <summary>最新帧槽，供 UI 线程取走。</summary>
    public FrameExchange Frames => _frames;

    /// <summary>已成功解码并发布的帧数。</summary>
    public long DecodedFrames => Interlocked.Read(ref _decoded);

    /// <summary>解码失败的累计次数（单帧失败会被丢弃，不影响会话）。</summary>
    public long DecodeErrors => Interlocked.Read(ref _decodeErrors);

    /// <summary>从 <see cref="Start"/> 到第一帧解码完成的耗时；未出帧时为 <c>null</c>。</summary>
    public TimeSpan? FirstFrameLatency =>
        Interlocked.Read(ref _firstFrameMilliseconds) < 0
            ? null
            : TimeSpan.FromMilliseconds(Interlocked.Read(ref _firstFrameMilliseconds));

    /// <summary>最近一帧解码完成的时刻（Stopwatch 时间戳），无帧时为 0。</summary>
    public long LastFrameTimestamp => Interlocked.Read(ref _lastFrameTimestamp);

    /// <summary>距最近一帧已经过去多少毫秒；尚未出帧时返回 <c>null</c>。零帧不代表断流，只代表画面没变化。</summary>
    public long? MillisecondsSinceLastFrame
    {
        get
        {
            var last = Interlocked.Read(ref _lastFrameTimestamp);
            return last == 0 ? null : _clock.ElapsedMilliseconds - last;
        }
    }

    /// <summary>会话是否在运行。</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>启动镜像。立即返回，实际流水线在后台线程运行。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _clock.Restart();
        Interlocked.Exchange(ref _firstFrameMilliseconds, -1);
        Interlocked.Exchange(ref _lastFrameTimestamp, 0);
        var token = _cts.Token;
        _pipeline = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    /// <summary>停止镜像并清理设备侧资源（端口转发、自建守护进程）。</summary>
    public async Task StopAsync()
    {
        if (!IsRunning && _pipeline is null)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略。
        }

        var pipeline = _pipeline;
        if (pipeline is not null)
        {
            try
            {
                await pipeline.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消。
            }
        }

        _pipeline = null;
        Interlocked.Exchange(ref _running, 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _pipeline?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException ex)
        {
            _log.Warn(Category, "停止镜像会话时出现异常。", ex);
        }

        _cts?.Dispose();
        _cts = null;
        _decoder?.Dispose();
        _decoder = null;
    }

    private void Report(MirrorStatus status, string? message)
    {
        if (_ui is null)
        {
            _report(status, message);
            return;
        }

        _ui.Post(static state => { var (callback, args) = ((Action<MirrorStatus, string?>, (MirrorStatus, string?)))state!; callback(args.Item1, args.Item2); }, (_report, (status, message)));
    }

    /// <summary>第 n 次重连前的等待时间：2 秒起逐次翻倍，最长 10 秒。</summary>
    private static TimeSpan NextReconnectDelay(int attempt)
    {
        var seconds = ReconnectMinDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt, 3));
        return TimeSpan.FromSeconds(Math.Min(seconds, 10));
    }

    /// <summary>一次会话尝试的结果，决定外层是继续退避重连还是就此停下。</summary>
    private enum AttemptOutcome
    {
        /// <summary>值得再连一次（设备不在、流被关、网络抖）。</summary>
        Retry,

        /// <summary>已有确定结论：要么给出了失败原因，要么已按请求停止。</summary>
        Stop,
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var outcome = await RunAttemptAsync(cancellationToken).ConfigureAwait(false);
                if (outcome == AttemptOutcome.Stop)
                {
                    return;
                }

                if (_clock.Elapsed >= ReconnectBudget)
                {
                    _log.Warn(Category, $"自动重连已持续 {(int)ReconnectBudget.TotalMinutes} 分钟仍未恢复，停止重连。");
                    Report(MirrorStatus.Failed, string.Format(Strings.Err_Mirror_Start, Strings.Err_Mirror_StreamClosed));
                    return;
                }

                var delay = NextReconnectDelay(attempt);
                _log.Info(Category, $"连接已断开，{delay.TotalSeconds:0} 秒后自动重连（第 {attempt + 1} 次）。");
                Report(MirrorStatus.Starting, Strings.Status_Mirror_Reconnecting);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Info(Category, "镜像会话已按请求停止。");
        }
        catch (Exception ex)
        {
            _log.Error(Category, "镜像会话失败。", ex);
            Report(MirrorStatus.Failed, string.Format(Strings.Err_Mirror_Start, ex.Message));
        }
        finally
        {
            await ReleaseDeviceAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// 建立一次完整的镜像会话。<see cref="AttemptOutcome.Retry"/> 表示这次失败属于
    /// 「设备暂时不在 / 流断了 / 网络抖了」，值得按退避策略再连一次；
    /// <see cref="AttemptOutcome.Stop"/> 表示已给出确定的失败原因或已按请求停止。
    /// </summary>
    private async Task<AttemptOutcome> RunAttemptAsync(CancellationToken cancellationToken)
    {
        try
        {
            Report(MirrorStatus.Starting, Strings.Status_Mirror_Starting);

            _controlRunner = new HdcRunner(hdcPath: null, deviceSerial: null, log: _log, commandTimeout: TimeSpan.FromSeconds(30));
            var enumerator = new HdcDeviceEnumerator(_controlRunner, _log);
            var device = await enumerator.SelectPreferredAsync(cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                // 手机休眠、刚开机、WiFi 切网都会短暂枚举不到设备，这属于可重试状态。
                _log.Warn(Category, "暂时没有找到可用设备，等它重新上线。");
                Report(MirrorStatus.Starting, Strings.Status_Mirror_Reconnecting);
                return AttemptOutcome.Retry;
            }

            _log.Info(Category, $"已选定设备（{device.Kind}）。");

            Report(MirrorStatus.Starting, Strings.Status_Mirror_PreparingDaemon);
            _deviceRunner = new HdcRunner(hdcPath: null, deviceSerial: device.Serial, log: _log, commandTimeout: TimeSpan.FromSeconds(60));
            int requestedRate = AppSettings.Current.EffectiveFrameRate > 0
                ? AppSettings.Current.EffectiveFrameRate
                : DisplayModes.AutoFrameRate;
            _requestedFrameRate = requestedRate;

            _daemon = new ScrcpyDaemon(
                _deviceRunner,
                new ScrcpyDaemonOptions
                {
                    JarPath = ResolveJarPath(),
                    // 华为 daemon 的 -scale 只接受 1：传 0.5 / 2 / 50 都会让守护进程起不来（实测三次），
                    // 所以分辨率固定为手机原生，画质百分比改为按比例下调编码码率（100% = 设置里的码率上限）。
                    Scale = 1d,
                    FrameRate = requestedRate,
                    BitRate = Math.Max(1_000_000, AppSettings.Current.EffectiveBitRate),
                },
                _log);

            // 端口转发不依赖设备侧监听者：先提交，让它与 2~3 秒的 daemon 启动并行，省一次往返。
            _forwarder = new HdcPortForwarder(_deviceRunner, _log);
            Task<int> forwardTask = _forwarder.EstablishAsync(cancellationToken);

            if (!await _daemon.EnsureRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                // 设备刚断过线时 so / daemon 可能还没就绪，重试比直接判失败更符合实际。
                _log.Warn(Category, "设备侧 daemon 未就绪，稍后重试。");
                Report(MirrorStatus.Starting, Strings.Status_Mirror_Reconnecting);
                return AttemptOutcome.Retry;
            }

            cancellationToken.ThrowIfCancellationRequested();

            Report(MirrorStatus.Starting, Strings.Status_Mirror_Forwarding);
            int port = await forwardTask.ConfigureAwait(false);
            if (port <= 0)
            {
                _log.Warn(Category, "端口转发建立失败，稍后重试。");
                Report(MirrorStatus.Starting, Strings.Status_Mirror_Reconnecting);
                return AttemptOutcome.Retry;
            }

            Report(MirrorStatus.Starting, Strings.Status_Mirror_WaitingFrame);
            _client = new ScrcpyClient(new ScrcpyClientOptions { Endpoint = $"http://127.0.0.1:{port}" }, _log);
            _decoder = DecodePipeline.CreateH264Decoder(_log);

            // 每次尝试都是全新解码器，必须清掉上一次的配置帧缓存，否则重连后不会重新喂 SPS/PPS。
            _configured = Array.Empty<byte>();

            // 设备只在画面变化时推流；建流成功后要一次 IDR 才能拿到首个完整画面。
            // 高频请求会被设备主动断流（实测 result=77），所以整个会话只在这里要一次。
            await _client.RequestKeyFrameAsync(cancellationToken).ConfigureAwait(false);

            Report(MirrorStatus.Streaming, null);
            _log.Info(Category, $"帧流已建立（本地端口 {port}）。");

            var consecutiveErrors = 0;
            _decodeStatsStart = _clock.ElapsedMilliseconds;
            _decodeMsSum = 0;
            _decodeMsMax = 0;
            await foreach (var frame in _client.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extradata = _client.Extradata;
                if (extradata.Length > 0 && !extradata.Span.SequenceEqual(_configured))
                {
                    // 每次 IDR 都会重发 32 字节配置帧，解码器必须支持运行期更新。
                    _configured = extradata.ToArray();
                    _decoder.Configure(_configured);
                }

                if (_configured.Length == 0)
                {
                    continue;
                }

                try
                {
                    long decodeStart = _clock.ElapsedMilliseconds;
                    if (!_decoder.TryDecode(frame.AnnexB, frame.Pts, out var decoded) || decoded is null)
                    {
                        continue;
                    }

                    consecutiveErrors = 0;
                    _frames.Publish(
                        decoded.Pixels.AsSpan(0, decoded.Stride * decoded.Height),
                        decoded.Width,
                        decoded.Height,
                        decoded.Stride);

                    var count = Interlocked.Increment(ref _decoded);

                    // 解码节奏统计：定位「手机 60 fps、软件只有 30」到底是码流没给够还是解码跟不上。
                    _decodeMsSum += _clock.ElapsedMilliseconds - decodeStart;
                    _decodeMsMax = Math.Max(_decodeMsMax, _clock.ElapsedMilliseconds - decodeStart);
                    if (count % 120 == 0)
                    {
                        double span = Math.Max(1, _clock.ElapsedMilliseconds - _decodeStatsStart);
                        _log.Info(Category, string.Format(
                            CultureInfo.InvariantCulture,
                            "解码统计：近 {0:0.0} 秒 {1} 帧 ≈ {2:0.0} fps，平均 {3:0.0} ms、最慢 {4} ms；请求 {5} Hz、码率 {6} Mbps。",
                            span / 1000d,
                            120,
                            120 * 1000d / span,
                            _decodeMsSum / 120d,
                            _decodeMsMax,
                            _requestedFrameRate,
                            AppSettings.Current.EffectiveBitRate / 1_000_000d));
                        _decodeMsSum = 0;
                        _decodeMsMax = 0;
                        _decodeStatsStart = _clock.ElapsedMilliseconds;
                    }
                    Interlocked.Exchange(ref _lastFrameTimestamp, _clock.ElapsedMilliseconds);
                    if (count == 1)
                    {
                        Interlocked.Exchange(ref _firstFrameMilliseconds, _clock.ElapsedMilliseconds);
                        _log.Info(Category, $"首帧已解码：{decoded.Width}×{decoded.Height}，耗时 {_clock.ElapsedMilliseconds} ms。");
                    }
                }
                catch (H264DecodeException ex)
                {
                    Interlocked.Increment(ref _decodeErrors);
                    consecutiveErrors++;
                    _log.Warn(Category, $"解码失败（第 {consecutiveErrors} 次连续失败）：{ex.Message}");
                    if (consecutiveErrors >= MaxConsecutiveDecodeErrors)
                    {
                        // 连续解码失败通常意味着流已经坏了；重连无意义，直接给出失败原因。
                        Report(MirrorStatus.Failed, string.Format(Strings.Err_Mirror_Decode, ex.Message));
                        return AttemptOutcome.Stop;
                    }
                }
            }

            // 枚举正常结束＝服务端关流；设备在画面长时间无变化时会这样做，交给外层重连。
            _log.Info(Category, "设备侧结束了帧流。");
            return AttemptOutcome.Retry;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Info(Category, "镜像会话已按请求停止。");
            return AttemptOutcome.Stop;
        }
        catch (ScrcpyStreamException ex)
        {
            // 服务端关流 / 网络中断：不判定为失败，交给外层有界重连。
            _log.Warn(Category, $"设备侧结束了帧流：{ex.Message}");
            return AttemptOutcome.Retry;
        }
        catch (Exception ex)
        {
            // 拔数据线、手机休眠、WiFi 掉线都会落到这里；按可重试处理，总时长由外层封顶。
            _log.Error(Category, "镜像尝试失败，稍后自动重连。", ex);
            Report(MirrorStatus.Starting, Strings.Status_Mirror_Reconnecting);
            return AttemptOutcome.Retry;
        }
        finally
        {
            await ReleaseDeviceAsync().ConfigureAwait(false);
        }
    }

    private async Task ReleaseDeviceAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.EndAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn(Category, "结束帧流时出现异常。", ex);
            }

            try
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn(Category, "释放帧流客户端时出现异常。", ex);
            }

            _client = null;
        }

        if (_forwarder is not null)
        {
            try
            {
                await _forwarder.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn(Category, "释放端口转发时出现异常。", ex);
            }

            _forwarder = null;
        }

        if (_daemon is not null)
        {
            try
            {
                await _daemon.CleanupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn(Category, "清理上游守护进程时出现异常。", ex);
            }

            _daemon = null;
        }

        _deviceRunner = null;
        _controlRunner = null;

        _decoder?.Dispose();
        _decoder = null;
    }

    /// <summary>
    /// 解析上游守护进程的 jar：优先环境变量，其次本机已知的用户级目录，
    /// 最后才是程序目录下的 tools/ native/ lib/。源码里不写死任何绝对路径。
    /// </summary>
    private static string? ResolveJarPath()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("HDCMIRROR_JAR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        // ① 本软件自己的用户级目录：把 jar 放进这里即可，不依赖别的软件。
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var ownLib = Path.Combine(localAppData, "HdcMirror", "lib");
            var found = NewestJarIn(ownLib);
            if (found is not null)
            {
                return found;
            }
        }

        // ② 上游 hnscrcpy 的安装目录：用户机器上通常已经有这份 jar，直接复用。
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var upstreamLib = Path.Combine(profile, ".hnscrcpy", "lib");
            var found = NewestJarIn(upstreamLib);
            if (found is not null)
            {
                return found;
            }
        }

        // ③ 程序目录：安装包把它放在 tools/ 或 native/ 或 lib/ 下都能认出来。
        var baseDirectory = AppContext.BaseDirectory;
        foreach (var folder in new[] { "tools", "native", "lib" })
        {
            var found = NewestJarIn(Path.Combine(baseDirectory, folder));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>取目录里版本最新的 jar（不递归）。</summary>
    private static string? NewestJarIn(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var candidates = Directory.GetFiles(directory, "*.jar");
        if (candidates.Length == 0)
        {
            return null;
        }

        Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
        return candidates[^1];
    }
}
