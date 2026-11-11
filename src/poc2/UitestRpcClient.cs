using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Hncast.Poc2;

/// <summary>
/// 与设备侧 uitest daemon (TCP 8012) 的 JSON-RPC 长连接客户端。
///
/// 这是本项目的**低延迟注入通道**：不起进程、不建新会话、不做 TCP 握手，
/// 一次触摸事件只有一次「写帧 → 读响应」的往返。
///
/// 线格式（实测，hdc fport 转发的裸 TCP 流）：
///   ┌────────────────────────────────────┬──────────────┬─────────────┬────────────────────────────────────┐
///   │ "_uitestkit_rpc_message_head_" 28B │ len: 8B 大端 │ JSON (UTF-8)│ "_uitestkit_rpc_message_tail_" 28B │
///   └────────────────────────────────────┴──────────────┴─────────────┴────────────────────────────────────┘
///   响应：**裸 JSON，没有 head/tail 包裹**，多个响应直接首尾相连，必须自己按花括号配平切分。
///
/// 三条硬性事实（实测，别改）：
///   1. module 必须是包名 `com.ohos.devicetest.hypiumApiHelper`，写别的会被拒。
///   2. 同一连接上并发写多帧，**设备按到达顺序处理**，响应也保序 —— 所以可以用
///      「先入队、后不等待」的方式做 pipelining（60Hz/120Hz 触摸流就是这么来的）。
///   3. 每个请求在 daemon 里是独立 session，hilog 里每次都会打 `SessionDeath3!`，
///      这是设计如此，不是错误。
/// </summary>
public sealed class UitestRpcClient : IAsyncDisposable
{
    private const string HeadMarker = "_uitestkit_rpc_message_head_";
    private const string TailMarker = "_uitestkit_rpc_message_tail_";
    private const string Module = "com.ohos.devicetest.hypiumApiHelper";

    private static readonly byte[] HeadBytes = Encoding.ASCII.GetBytes(HeadMarker);
    private static readonly byte[] TailBytes = Encoding.ASCII.GetBytes(TailMarker);

    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly Queue<TaskCompletionSource<string>> _pending = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private volatile bool _broken;

    public int TimeoutMs { get; init; } = 3000;
    public long SentFrames;
    public long RecvFrames;
    public int Reconnects;

    public UitestRpcClient(string host = "127.0.0.1", int port = 15002)
    {
        _host = host;
        _port = port;
    }

    public bool Connected => _tcp?.Connected == true && !_broken;

    // ────────────────────────────────────────────────────────────────
    // 连接管理
    // ────────────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Connected) return;
        await _connectGate.WaitAsync(ct);
        try
        {
            if (Connected) return;
            await DisposeConnectionAsync();

            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(_host, _port, ct);
            tcp.NoDelay = true;
            _tcp = tcp;
            _stream = tcp.GetStream();
            _broken = false;
            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_stream!, _readCts!.Token));
        }
        finally { _connectGate.Release(); }
    }

    /// <summary>掉线后重建（供上层在超时/异常时调用）。</summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref Reconnects);
        await _connectGate.WaitAsync(ct);
        try
        {
            await DisposeConnectionAsync();
            _broken = true;
            _broken = false;
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(_host, _port, ct);
            tcp.NoDelay = true;
            _tcp = tcp;
            _stream = tcp.GetStream();
            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_stream!, _readCts!.Token));
        }
        finally { _connectGate.Release(); }
    }

    private async Task DisposeConnectionAsync()
    {
        try { _readCts?.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
        if (_readTask is not null)
        {
            try { await _readTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            _readTask = null;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 读循环：裸 JSON 流按花括号配平切分，按 FIFO 配对到等待者
    // ────────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var chars = new char[16384];
        var decoder = Encoding.UTF8.GetDecoder();
        var sb = new StringBuilder(512);
        int depth = 0;
        bool inString = false, escaped = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n <= 0) break;
                int cn = decoder.GetChars(buffer, 0, n, chars, 0);
                for (int i = 0; i < cn; i++)
                {
                    char ch = chars[i];
                    if (inString)
                    {
                        sb.Append(ch);
                        if (escaped) escaped = false;
                        else if (ch == '\\') escaped = true;
                        else if (ch == '"') inString = false;
                        continue;
                    }
                    if (ch == '"') { inString = true; sb.Append(ch); continue; }
                    if (ch == '{') { depth++; sb.Append(ch); continue; }
                    if (ch == '}')
                    {
                        depth--;
                        sb.Append(ch);
                        if (depth <= 0)
                        {
                            var json = sb.ToString();
                            sb.Clear();
                            depth = 0;
                            Interlocked.Increment(ref RecvFrames);
                            Dispatch(json);
                        }
                        continue;
                    }
                    if (depth > 0) sb.Append(ch);
                    // depth==0 时的空白/垃圾直接丢弃
                }
            }
        }
        catch { /* 连接关闭 */ }
        finally
        {
            FailAllPending(new IOException("RPC 连接已断开"));
        }
    }

    private void Dispatch(string json)
    {
        TaskCompletionSource<string>? tcs = null;
        lock (_pendingLock)
        {
            if (_pending.Count > 0) tcs = _pending.Dequeue();
        }
        tcs?.TrySetResult(json);
    }

    private void FailAllPending(Exception ex)
    {
        _broken = true;
        lock (_pendingLock)
        {
            while (_pending.Count > 0)
            {
                var tcs = _pending.Dequeue();
                tcs.TrySetException(ex);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 发送 / 请求
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 只入队并发出，不等响应。返回的 Task 在响应到达时完成。
    /// 按调用顺序入队 ⇒ 与设备处理顺序一致（已实测保序）。
    /// </summary>
    public async Task<string> QueueAsync(string json, CancellationToken ct = default)
    {
        if (_broken || _stream is null) await ConnectAsync(ct);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending.Enqueue(tcs);

        try
        {
            await WriteFrameAsync(json, ct);
        }
        catch (Exception ex)
        {
            FailAllPending(ex);
            throw;
        }
        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs), ct);
    }

    /// <summary>请求并等待响应（端到端往返）。</summary>
    public async Task<(string json, TimeSpan elapsed)> RequestAsync(string json, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await QueueAsync(json, ct);
            sw.Stop();
            return (r, sw.Elapsed);
        }
        catch (TimeoutException)
        {
            // 超时会把响应错位给后来的请求，必须整条连接重建
            _broken = true;
            FailAllPending(new TimeoutException("RPC 超时，连接已作废"));
            throw;
        }
    }

    private async Task WriteFrameAsync(string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[HeadBytes.Length + 8 + body.Length + TailBytes.Length];
        int p = 0;
        Buffer.BlockCopy(HeadBytes, 0, frame, p, HeadBytes.Length); p += HeadBytes.Length;
        ulong len = (ulong)body.Length;
        for (int i = 7; i >= 0; i--) frame[p++] = (byte)((len >> (8 * i)) & 0xFF);
        Buffer.BlockCopy(body, 0, frame, p, body.Length); p += body.Length;
        Buffer.BlockCopy(TailBytes, 0, frame, p, TailBytes.Length);

        await _writeGate.WaitAsync(ct);
        try
        {
            var s = _stream ?? throw new IOException("未连接");
            await s.WriteAsync(frame, 0, frame.Length, ct);
            await s.FlushAsync(ct);
            Interlocked.Increment(ref SentFrames);
        }
        finally { _writeGate.Release(); }
    }

    // ────────────────────────────────────────────────────────────────
    // Hypium API 封装
    // ────────────────────────────────────────────────────────────────

    private static string DriverJson(string api, string argsJson)
        => "{\"module\":\"" + Module + "\",\"method\":\"callHypiumApi\",\"params\":{\"api\":\"" + api
           + "\",\"this\":\"Driver#0\",\"args\":" + argsJson + "}}";

    private static string GestureJson(string api, int x, int y)
        => "{\"module\":\"" + Module + "\",\"method\":\"Gestures\",\"params\":{\"api\":\"" + api
           + "\",\"args\":{\"x\":" + x + ",\"y\":" + y + "}}}";

    public Task<(string json, TimeSpan elapsed)> CallDriverAsync(string api, string argsJson, CancellationToken ct = default)
        => RequestAsync(DriverJson(api, argsJson), ct);

    public Task<(string json, TimeSpan elapsed)> GestureAsync(string api, int x, int y, CancellationToken ct = default)
        => RequestAsync(GestureJson(api, x, y), ct);

    /// <summary>只发不等（触摸流用）。返回的 Task 可用于最后统一等待。</summary>
    public Task<string> QueueGestureAsync(string api, int x, int y, CancellationToken ct = default)
        => QueueAsync(GestureJson(api, x, y), ct);

    public Task<string> QueueDriverAsync(string api, string argsJson, CancellationToken ct = default)
        => QueueAsync(DriverJson(api, argsJson), ct);

    // ────────────────────────────────────────────────────────────────
    // 高层动作
    // ────────────────────────────────────────────────────────────────

    /// <summary>读屏尺寸（顺带当探活）。</summary>
    public async Task<(int w, int h)> GetDisplaySizeAsync(CancellationToken ct = default)
    {
        var (json, _) = await CallDriverAsync("Driver.getDisplaySize", "[]", ct);
        int w = 0, h = 0;
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"x\"\\s*:\\s*(\\d+)\\s*,\\s*\"y\"\\s*:\\s*(\\d+)");
        if (m.Success) { int.TryParse(m.Groups[1].Value, out w); int.TryParse(m.Groups[2].Value, out h); }
        return (w, h);
    }

    /// <summary>一次 RPC 完成点击（touchDown + touchUp 两条帧，合并等待）。</summary>
    public async Task<TimeSpan> ClickAsync(int x, int y, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var t1 = QueueGestureAsync("touchDown", x, y, ct);
        var t2 = QueueGestureAsync("touchUp", x, y, ct);
        await Task.WhenAll(t1, t2);
        sw.Stop();
        return sw.Elapsed;
    }

    /// <summary>一次性滑动（设备侧合成手势，单帧请求）。</summary>
    public Task<(string json, TimeSpan elapsed)> SwipeAsync(int x1, int y1, int x2, int y2, int speed = 2000, CancellationToken ct = default)
        => CallDriverAsync("Driver.swipe", $"[{x1},{y1},{x2},{y2},{speed}]", ct);

    public ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
