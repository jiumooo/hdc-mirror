using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using HdcMirror.Core.Diagnostics;
using HdcMirror.Core.Hdc;

namespace HdcMirror.Core.Input;

/// <summary>
/// 设备侧 uitest daemon 的输入通道（裸 TCP JSON-RPC，默认设备端口 8012）。
/// <para>
/// 线格式：<c>"_uitestkit_rpc_message_head_"</c>(28B) + 8 字节大端长度 + JSON(UTF-8) + <c>"_uitestkit_rpc_message_tail_"</c>(28B)。
/// 响应是裸 JSON，多个响应首尾相连，必须按花括号配平切分。
/// </para>
/// <para>
/// 这条通道支持 <c>touchDown/touchMove/touchUp</c>，是唯一能做出"跟手拖拽"的路径；
/// <c>uitest uiInput</c> 只有一次性 click/swipe，做不了连续触摸。
/// </para>
/// </summary>
public sealed class UitestInputChannel : IAsyncDisposable
{
    /// <summary>设备侧 uitest daemon 的 TCP 端口。</summary>
    public const int DeviceRpcPort = 8012;

    private const string Category = "input";
    private const string ModuleName = "com.ohos.devicetest.hypiumApiHelper";

    private static readonly byte[] Head = Encoding.ASCII.GetBytes("_uitestkit_rpc_message_head_");
    private static readonly byte[] Tail = Encoding.ASCII.GetBytes("_uitestkit_rpc_message_tail_");

    private readonly ILogSink _log;
    private readonly HdcRunner _runner;
    private readonly int _localPort;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<string> _outbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writeLoop;
    private readonly Task _readLoop;
    private int _disposed;

    // ── 诊断：入队 → 真正写进 socket 的耗时（毫秒），每 60 条打一行 ──
    private readonly System.Collections.Concurrent.ConcurrentQueue<long> _stamps = new();
    private double _delaySum;
    private long _delayMax;
    private long _written;

    /// <summary>链路已断（写/读循环捕获到 IO 异常）。TCP 的 Connected 属性不会及时反映对端关闭，必须自己记。</summary>
    private int _broken;

    /// <summary>最后一次按下/移动的坐标，抬起时原样带回（见 <see cref="TouchUp"/> 的说明）。</summary>
    private int _lastX;

    /// <summary>最后一次按下/移动的坐标，抬起时原样带回。</summary>
    private int _lastY;

    private UitestInputChannel(HdcRunner runner, ILogSink log, int localPort, TcpClient tcp, int devicePort)
    {
        _runner = runner;
        _log = log;
        _localPort = localPort;
        _tcp = tcp;
        _stream = tcp.GetStream();
        _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        DevicePort = devicePort;
        _writeLoop = Task.Run(WriteLoopAsync);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>本机被转发的端口。</summary>
    public int LocalPort => _localPort;

    /// <summary>设备侧被转发的端口（正常是 8012）。</summary>
    public int DevicePort { get; }

    /// <summary>通道是否还活着。</summary>
    public bool IsBroken => Volatile.Read(ref _broken) != 0;

    /// <inheritdoc />
    /// <summary>还没写进 socket 的手势条数（诊断用）。</summary>
    public int PendingCount => _outbox.Reader.Count;

    /// <summary>
    /// 丢掉还没写进 socket 的手势帧。
    /// 鼠标上报率可到 500~1000 次/秒，而设备按 60Hz 回放，
    /// 抬手时若不清队列，设备会把积压的几百帧慢慢放完——表现就是「手停了，手机还在滑」。
    /// </summary>
    public void DropPending()
    {
        int dropped = 0;
        while (_outbox.Reader.TryRead(out _))
        {
            dropped++;
        }

        if (dropped > 0)
        {
            _log.Info(Category, $"抬手前丢弃积压 {dropped} 帧，避免松手后还在滑");
        }
    }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _broken) == 0 && _tcp.Connected;

    /// <summary>
    /// 建立到设备 uitest daemon 的通道：先 <c>hdc fport</c> 把设备 8012 转到本机空闲端口，再连上去。
    /// </summary>
    /// <param name="runner">已绑定目标设备的 hdc 执行器。</param>
    /// <param name="log">日志出口。</param>
    /// <param name="firstPort">本机端口扫描起点。</param>
    /// <param name="lastPort">本机端口扫描终点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已连接的通道。</returns>
    /// <exception cref="HdcException">端口区间内没有可用的转发。</exception>
    public static async Task<UitestInputChannel> ConnectAsync(
        HdcRunner runner,
        ILogSink? log = null,
        int firstPort = 15600,
        int lastPort = 15620,
        CancellationToken cancellationToken = default,
        bool sweepStale = true)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ILogSink sink = log ?? NullLogSink.Instance;

        // 上一次会话被强杀（进程崩溃、直接结束任务）时来不及撤转发，
        // 设备上会攒下一串 tcp:156xx → 8012。开工前先扫掉自己这一段，免得越积越多。
        if (sweepStale)
        {
            await ClearStaleForwardsAsync(runner, sink, firstPort, lastPort, cancellationToken).ConfigureAwait(false);
        }

        for (int port = firstPort; port <= lastPort; port++)
        {
            HdcResult result = await runner.RunAsync(
                new[]
                {
                    "fport",
                    "tcp:" + port.ToString(CultureInfo.InvariantCulture),
                    "tcp:" + DeviceRpcPort.ToString(CultureInfo.InvariantCulture),
                },
                cancellationToken).ConfigureAwait(false);

            string output = result.CombinedOutput;
            bool forwarded = output.Contains("[Forward]", StringComparison.OrdinalIgnoreCase)
                || (result.Succeeded && !output.Contains("[Fail]", StringComparison.OrdinalIgnoreCase));
            if (!forwarded)
            {
                continue;
            }

            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                sink.Warn(Category, $"本地端口 {port} 连不上 uitest daemon：{ex.Message}");
                tcp.Dispose();
                await runner.RunAsync(
                    new[]
                    {
                        "fport",
                        "rm",
                        "tcp:" + port.ToString(CultureInfo.InvariantCulture),
                        // hdc 3.2.0c 必须带远端规格才真删；只给 tcp:15600 会静默成功但不生效。
                        "tcp:" + DeviceRpcPort.ToString(CultureInfo.InvariantCulture),
                    },
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            sink.Info(Category, $"输入通道已建立：tcp:{port} → tcp:{DeviceRpcPort}");
            return new UitestInputChannel(runner, sink, port, tcp, DeviceRpcPort);
        }

        throw new HdcException($"在 {firstPort}~{lastPort} 内没有建立起到设备 {DeviceRpcPort} 端口的转发。");
    }

    /// <summary>
    /// 清掉本进程端口区间内残留的历史转发。只动 <paramref name="firstPort"/>~<paramref name="lastPort"/>，
    /// 上游 hnscrcpy 或别的工具用的端口一律不碰。
    /// </summary>
    private static async Task ClearStaleForwardsAsync(
        HdcRunner runner,
        ILogSink sink,
        int firstPort,
        int lastPort,
        CancellationToken cancellationToken)
    {
        HdcResult listing;
        try
        {
            listing = await runner.RunAsync(new[] { "fport", "ls" }, cancellationToken).ConfigureAwait(false);
        }
        catch (HdcException ex)
        {
            sink.Warn(Category, $"读取端口转发列表失败，跳过清理：{ex.Message}");
            return;
        }

        string target = "tcp:" + DeviceRpcPort.ToString(CultureInfo.InvariantCulture);
        foreach (string line in listing.CombinedOutput.Split('\n'))
        {
            int idx = line.IndexOf("tcp:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || !line.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string local = line.Substring(idx);
            int space = local.IndexOfAny(new[] { ' ', '\t', '\r' });
            if (space > 0)
            {
                local = local[..space];
            }

            if (!int.TryParse(local.AsSpan("tcp:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                || port < firstPort
                || port > lastPort)
            {
                continue;
            }

            await runner.RunAsync(
                    new[]
                    {
                        "fport",
                        "rm",
                        "tcp:" + port.ToString(CultureInfo.InvariantCulture),
                        // hdc 3.2.0c 必须带远端规格才真删；只给 tcp:15600 会静默成功但不生效。
                        "tcp:" + DeviceRpcPort.ToString(CultureInfo.InvariantCulture),
                    },
                cancellationToken).ConfigureAwait(false);
            sink.Info(Category, $"清掉残留转发 tcp:{port} → {target}");
        }
    }

    /// <summary>按下。</summary>
    /// <param name="x">设备像素 X。</param>
    /// <param name="y">设备像素 Y。</param>
    public void TouchDown(int x, int y)
    {
        _lastX = x;
        _lastY = y;
        EnqueueGesture("touchDown", x, y);
    }

    /// <summary>移动。</summary>
    /// <param name="x">设备像素 X。</param>
    /// <param name="y">设备像素 Y。</param>
    public void TouchMove(int x, int y)
    {
        _lastX = x;
        _lastY = y;
        EnqueueGesture("touchMove", x, y);
    }

    /// <summary>
    /// 抬起。<b>必须带最后停留的坐标</b>：设备侧收到 (0,0) 的抬起会把触点挪到左上角，
    /// 表现为画板上多出一条斜线、列表松手后弹回原位。
    /// </summary>
    public void TouchUp() => EnqueueGesture("touchUp", _lastX, _lastY);

    /// <summary>单击：按下 → 抬起。</summary>
    /// <param name="x">设备像素 X。</param>
    /// <param name="y">设备像素 Y。</param>
    public void Tap(int x, int y)
    {
        TouchDown(x, y);
        TouchUp();
    }

    /// <summary>往队列里塞一条手势请求（非阻塞，写失败会记日志）。</summary>
    private void EnqueueGesture(string api, int x, int y)
    {
        string json = string.Format(
            CultureInfo.InvariantCulture,
            "{{\"module\":\"{0}\",\"method\":\"Gestures\",\"params\":{{\"api\":\"{1}\",\"args\":{{\"x\":{2},\"y\":{3}}}}}}}",
            ModuleName,
            api,
            x,
            y);

        _stamps.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
        if (!_outbox.Writer.TryWrite(json))
        {
            _log.Warn(Category, "输入队列已满，丢弃一条手势。");
        }
    }

    private async Task WriteLoopAsync()
    {
        var buffer = new byte[Head.Length + 8 + 4096 + Tail.Length];
        try
        {
            await foreach (string json in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                byte[] payload = Encoding.UTF8.GetBytes(json);
                int offset = 0;
                Head.CopyTo(buffer, offset);
                offset += Head.Length;
                BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), payload.Length);
                offset += 8;

                if (offset + payload.Length + Tail.Length > buffer.Length)
                {
                    var bigger = new byte[offset + payload.Length + Tail.Length];
                    buffer.AsSpan(0, offset).CopyTo(bigger);
                    buffer = bigger;
                }

                payload.CopyTo(buffer, offset);
                offset += payload.Length;
                Tail.CopyTo(buffer, offset);
                offset += Tail.Length;

                await _stream.WriteAsync(buffer.AsMemory(0, offset), _cts.Token).ConfigureAwait(false);

                if (_stamps.TryDequeue(out long stamp))
                {
                    double delay = (System.Diagnostics.Stopwatch.GetTimestamp() - stamp) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                    _delaySum += delay;
                    if (delay > _delayMax)
                    {
                        _delayMax = (long)Math.Ceiling(delay);
                    }

                    long written = ++_written;
                    if (written % 60 == 0)
                    {
                        _log.Info(Category, string.Format(
                            CultureInfo.InvariantCulture,
                            "输入写入延迟：近 60 条平均 {0:0.0} ms，最大 {1} ms",
                            _delaySum / 60d,
                            _delayMax));
                        _delaySum = 0;
                        _delayMax = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Interlocked.Exchange(ref _broken, 1);
            _log.Warn(Category, $"输入通道写入结束：{ex.Message}");
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[8192];
        var pending = new StringBuilder();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                DrainResponses(pending);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        { 
                Interlocked.Exchange(ref _broken, 1);
            _log.Warn(Category, $"输入通道读取结束：{ex.Message}");
        }
    }

    /// <summary>响应没有分隔符，按花括号配平把完整对象切出来丢掉。</summary>
    private void DrainResponses(StringBuilder pending)
    {
        int start = -1;
        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < pending.Length; i++)
        {
            char c = pending[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0)
                    {
                        start = i;
                    }

                    depth++;
                    break;
                case '}':
                    if (depth > 0)
                    {
                        depth--;
                        if (depth == 0 && start >= 0)
                        {
                            pending.Remove(0, i + 1);
                            i = -1;
                            start = -1;
                        }
                    }

                    break;
                default:
                    break;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outbox.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_writeLoop, _readLoop).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
        catch (AggregateException)
        {
        }

        _stream.Dispose();
        _tcp.Dispose();

        try
        {
            await _runner.RunAsync(new[]
            {
                "fport",
                "rm",
                "tcp:" + _localPort.ToString(CultureInfo.InvariantCulture),
                "tcp:" + DevicePort.ToString(CultureInfo.InvariantCulture),
            }).ConfigureAwait(false);
        }
        catch (HdcException ex)
        {
            _log.Warn(Category, $"撤销输入转发失败：{ex.Message}");
        }

        _cts.Dispose();
    }
}
