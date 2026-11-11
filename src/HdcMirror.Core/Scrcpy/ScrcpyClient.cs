using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.Core.Scrcpy;

/// <summary>视频流连接参数。</summary>
public sealed class ScrcpyClientOptions
{
    /// <summary>gRPC 端点，形如 <c>http://127.0.0.1:15000</c>（h2c 明文）。</summary>
    public string Endpoint { get; init; } = "http://127.0.0.1:15000";

    /// <summary>建连超时。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 单条消息的最大接收字节数。默认抬到 100 MB —— 默认值 4 MB 会让大 IDR 直接炸掉整条流。
    /// </summary>
    public int MaxMessageBytes { get; init; } = 100 * 1024 * 1024;

    /// <summary>实测基准：SPS(23) + PPS(9) 合包正好 32 字节。仅供一致性校验与告警。</summary>
    public int ExpectedConfigurationFrameLength { get; init; } = 32;
}

/// <summary>视频流异常：建连失败、流被服务端中断、onEnd 失败。</summary>
public sealed class ScrcpyStreamException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="message">说明。</param>
    /// <param name="innerException">内部异常。</param>
    /// <param name="statusCode">gRPC 状态码（若来自 gRPC 调用）。</param>
    public ScrcpyStreamException(string message, Exception? innerException = null, StatusCode? statusCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>gRPC 状态码。</summary>
    public StatusCode? StatusCode { get; }
}

/// <summary>
/// 投屏服务 gRPC 客户端。
/// <para>
/// 三条硬约束都在这里落地：
/// ① data 走 <c>payload</c> 的 field 3（契约文件已实测对齐，解析出来若为空即说明字段号漂了）；
/// ② 视频字节在 <c>ParamValue</c> 的 field 5（<c>valBytes</c>）；
/// ③ 首条消息是 32 字节的 SPS+PPS 合包，必须截留成 extradata，绝不能当帧交给解码器。
/// </para>
/// <para>
/// 断线语义：设备侧约 9 分钟会因空闲/鉴权回收把流置为 UNAVAILABLE。本类不静默停 ——
/// 流异常抛 <see cref="ScrcpyStreamException"/>，正常但非本端请求的收尾也抛异常，让上层能重建整条链路。
/// </para>
/// </summary>
public sealed class ScrcpyClient : IAsyncDisposable
{
    private const string Category = "scrcpy";

    /// <summary>配置帧标志位（SPS+PPS 合包）。</summary>
    public const int ConfigFrameFlag = 8;

    /// <summary>IDR 关键帧标志位。</summary>
    public const int KeyFrameFlag = 2;

    private readonly ScrcpyClientOptions _options;
    private readonly ILogSink _log;
    private readonly GrpcChannel _channel;
    private readonly ScrcpyService.ScrcpyServiceClient _client;

    private byte[]? _extradata;
    private ScrcpySps? _sps;
    private bool _endRequested;
    private bool _disposed;
    private long _messagesReceived;
    private long _videoBytes;

    static ScrcpyClient()
    {
        // h2c（明文 HTTP/2）在 .NET 上必须显式打开，否则 GrpcChannel 一律建连失败。
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    /// <summary>创建客户端（此时并不建连，首次拉流才会连）。</summary>
    /// <param name="options">连接参数。</param>
    /// <param name="log">日志出口，可为空。</param>
    public ScrcpyClient(ScrcpyClientOptions options, ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new ArgumentException("Endpoint 不能为空。", nameof(options));
        }

        _options = options;
        _log = log ?? NullLogSink.Instance;

        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = options.ConnectTimeout,
        };

        _channel = GrpcChannel.ForAddress(options.Endpoint, new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = options.MaxMessageBytes,
            MaxSendMessageSize = options.MaxMessageBytes,
        });

        _client = new ScrcpyService.ScrcpyServiceClient(_channel);
    }

    /// <summary>是否已经拿到配置帧。</summary>
    public bool HasExtradata => _extradata is not null;

    /// <summary>配置帧字节（SPS+PPS 的 Annex B 合包）；未收到时为空。</summary>
    public ReadOnlyMemory<byte> Extradata => _extradata is null ? ReadOnlyMemory<byte>.Empty : _extradata;

    /// <summary>实测的配置帧长度，用于与 32 字节基准比对。</summary>
    public int ConfigurationFrameLength { get; private set; }

    /// <summary>从配置帧解析出的 SPS 结果；未收到或解析失败时为 <see langword="null"/>。</summary>
    public ScrcpySps? Sps => _sps;

    /// <summary>累计收到的消息条数（含配置帧）。</summary>
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    /// <summary>累计视频字节数（不含配置帧）。</summary>
    public long VideoBytes => Interlocked.Read(ref _videoBytes);

    /// <summary>
    /// 拉取视频帧。配置帧被截留为 extradata，不从这里吐出。
    /// </summary>
    /// <param name="cancellationToken">取消令牌；取消时以 <see cref="OperationCanceledException"/> 结束。</param>
    public async IAsyncEnumerable<ScrcpyFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _endRequested = false;
        using var call = _client.onStart(new Empty(), cancellationToken: cancellationToken);
        _log.Info(Category, $"onStart 已发起：{_options.Endpoint}");
        var stream = call.ResponseStream;

        while (true)
        {
            if (!await MoveNextAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                if (!_endRequested && !cancellationToken.IsCancellationRequested)
                {
                    // 服务端自己把流关了，等同于断线：不静默退出，让上层重建链路。
                    throw new ScrcpyStreamException("服务端结束了视频流且没有收到 onEnd 回应，按断线处理。");
                }

                yield break;
            }

            Interlocked.Increment(ref _messagesReceived);
            var frame = Convert(stream.Current);
            if (frame is not null)
            {
                yield return frame;
            }
        }
    }

    /// <summary>调用 onEnd 正常收尾会话。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端结果码为 0 时返回 <see langword="true"/>。</returns>
    public async Task<bool> EndAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _endRequested = true;
        try
        {
            var reply = await _client.onEndAsync(new Empty(), cancellationToken: cancellationToken).ResponseAsync
                .ConfigureAwait(false);

            // 只记结果码，绝不拼接 protobuf 消息对象（其 ToString 会抛 IllegalStateException）。
            _log.Info(Category, $"onEnd 返回 result={reply.Result}");
            return reply.Result == 0;
        }
        catch (RpcException ex)
        {
            throw new ScrcpyStreamException($"onEnd 调用失败：{ex.StatusCode} {ex.Status.Detail}", ex, ex.StatusCode);
        }
    }

    /// <summary>
    /// 请求一个 IDR 关键帧。设备只按画面变化推流，画面长时间静止或刚接入时可能一直没有帧，
    /// 这里主动要一帧，是协议层唯一能让出图不必等画面变化的手段。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端结果码为 0 时返回 <see langword="true"/>。</returns>
    public async Task<bool> RequestKeyFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var reply = await _client.onRequestIDRFrameAsync(new Empty(), cancellationToken: cancellationToken)
                .ResponseAsync.ConfigureAwait(false);

            _log.Info(Category, $"onRequestIDRFrame 返回 result={reply.Result}");
            return reply.Result == 0;
        }
        catch (RpcException ex)
        {
            throw new ScrcpyStreamException(
                $"请求关键帧失败：{ex.StatusCode} {ex.Status.Detail}",
                ex,
                ex.StatusCode);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _endRequested = true;

        // 先优雅关停（等在途调用收尾），再释放 channel；2.66 的 GrpcChannel 只有同步 Dispose。
        await _channel.ShutdownAsync().ConfigureAwait(false);
        _channel.Dispose();
    }

    private static async Task<bool> MoveNextAsync(IAsyncStreamReader<ReplyMessage> stream, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.MoveNext(cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("视频流读取已取消。", ex, cancellationToken);
            }

            // 设备侧约 9 分钟一次 UNAVAILABLE，必须让上层看见，不能静默停。
            throw new ScrcpyStreamException($"视频流中断：{ex.StatusCode} {ex.Status.Detail}", ex, ex.StatusCode);
        }
    }

    /// <summary>把一条 ReplyMessage 转成帧；配置帧返回 <see langword="null"/> 并写入 extradata。</summary>
    private ScrcpyFrame? Convert(ReplyMessage message)
    {
        var payload = message.Payload;
        if (!payload.TryGetValue("data", out var dataValue))
        {
            _log.Warn(Category, "消息里没有 data 键，已跳过（若持续出现说明 payload 字段号漂移）。");
            return null;
        }

        var data = dataValue.ValBytes;
        if (data.Length == 0)
        {
            _log.Warn(Category, "data 载荷为空，已跳过。");
            return null;
        }

        var flags = (int)(payload.TryGetValue("flags", out var flagsValue) ? flagsValue.ValLong : 0);
        var pts = payload.TryGetValue("pts", out var ptsValue) ? ptsValue.ValLong : 0L;

        if (payload.TryGetValue("len", out var lenValue) && lenValue.ValLong != data.Length)
        {
            // len 与 data 长度实测完全相等，是很好的协议一致性校验位。
            _log.Warn(Category, $"len={lenValue.ValLong} 与实际 data 长度 {data.Length} 不一致。");
        }

        var bytes = data.ToByteArray();

        if ((flags & ConfigFrameFlag) != 0)
        {
            CaptureExtradata(bytes);
            return null;
        }

        Interlocked.Add(ref _videoBytes, bytes.LongLength);
        var isKeyFrame = (flags & KeyFrameFlag) != 0 || ScrcpySpsParser.ContainsIdrSlice(bytes);
        return new ScrcpyFrame(bytes, isKeyFrame, pts, flags);
    }

    private void CaptureExtradata(byte[] bytes)
    {
        _extradata = bytes;
        ConfigurationFrameLength = bytes.Length;
        if (bytes.Length != _options.ExpectedConfigurationFrameLength)
        {
            _log.Warn(
                Category,
                $"配置帧长度 {bytes.Length} 与实测基准 {_options.ExpectedConfigurationFrameLength} 不一致；仍按 extradata 缓存。");
        }

        var sps = ScrcpySpsParser.ParseAnnexB(bytes);
        if (sps is null)
        {
            _log.Warn(Category, $"配置帧 {bytes.Length} 字节里没解析出 SPS。");
            return;
        }

        _sps = sps;
        _log.Info(Category, $"配置帧 {bytes.Length} 字节，SPS：{sps.Describe()}");
    }
}
