using System.Diagnostics;
using System.Text;

namespace Hncast.Poc2;

/// <summary>
/// hdc shell 注入通道。
///
/// 三种形态，按可用性自动降级：
///   1) Persistent —— 起一个常驻的 `hdc -t DEV shell` 进程，stdin 写命令、stdout 读哨兵行
///      `__DONE__<seq>`，按序配对。**hdc 3.2.0c 实测不支持**：交互模式直接打印
///      `Not support stdio TTY mode` 并退出，stdin 被接管时回 `Unknown operation command...`。
///      代码保留完整实现，运行时会自己探测失败并降级，探测结果写进报告。
///   2) Batch —— 合并批发：把同一时间窗口内的多条命令拼成一句
///      `hdc shell "c1; c2; c3; echo __DONE__<seq>"`，一次进程启动摊掉多条。
///   3) OneShot —— 每次一条命令一个进程，即当前 POC 的做法，作为基准。
///
/// 关键坑（都踩过）：
///   * CreateNoWindow + RedirectStandardInput 缺一不可，否则会弹控制台窗口。
///   * hdc 的 stdout/stderr 都要读，否则管道写满会卡死（hdc 会往 stderr 打 [I]/[W] 日志）。
///   * 哨兵必须是独占一行的完整前缀匹配，不能 Contains —— hdc 会回显你写进去的命令行。
///   * 掉线判定：哨兵超时 → 杀进程 → 重建；重建也失败就抛给上层，由上层决定是否降级。
///   * 命令里不要带引号嵌套（`hdc shell "a \"b\""` 在 Windows 上会被 cmd 层再剥一次壳）。
/// </summary>
public sealed class HdcShellSession : IAsyncDisposable
{
    private const string SentinelPrefix = "__DONE__";

    private readonly string _hdcPath;
    private readonly string _device;
    private readonly int _defaultTimeoutMs;

    private Process? _persistent;
    private Task? _readerTask;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<string>> _waiters = new();
    private readonly StringBuilder _tail = new();
    private long _seq;
    private CancellationTokenSource? _readerCts;

    /// <summary>常驻模式是否真的可用（探测结果）。</summary>
    public bool PersistentAvailable { get; private set; }

    /// <summary>探测过程中 hdc 给出的拒绝原因，写报告用。</summary>
    public string? PersistentRejectReason { get; private set; }

    /// <summary>当前生效的形态：persistent / batch / oneshot。</summary>
    public string Mode { get; private set; } = "unknown";

    public HdcShellSession(string hdcPath, string device, int defaultTimeoutMs = 4000)
    {
        _hdcPath = hdcPath;
        _device = device;
        _defaultTimeoutMs = defaultTimeoutMs;
    }

    // ────────────────────────────────────────────────────────────────
    // 常驻模式：探测 + 建立
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 尝试建立常驻交互式 shell。hdc 3.2.0c 不支持，本方法会如实返回 false 并记下原因。
    /// </summary>
    public async Task<bool> TryStartPersistentAsync(int probeTimeoutMs = 2500)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _hdcPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(_device);
            psi.ArgumentList.Add("shell");

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();
            _persistent = proc;
            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadLoopAsync(proc, _readerCts.Token));

            // 握手：写一条 echo，看能不能读到哨兵
            var seq = Interlocked.Increment(ref _seq);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiters) _waiters[seq] = tcs;
            await proc.StandardInput.WriteAsync($"echo {SentinelPrefix}{seq}\n");
            await proc.StandardInput.FlushAsync();

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(probeTimeoutMs));
            if (winner == tcs.Task)
            {
                PersistentAvailable = true;
                Mode = "persistent";
                return true;
            }

            // 失败：把 hdc 的报错抓出来当证据
            if (proc.HasExited)
                PersistentRejectReason = $"hdc 进程已退出 (exit={proc.ExitCode})；stderr/stdout: {TailText()}";
            else
                PersistentRejectReason = $"写入 {probeTimeoutMs}ms 内无哨兵响应；tail: {TailText()}";

            PersistentAvailable = false;
            Mode = "oneshot";
            await KillPersistentAsync();
            return false;
        }
        catch (Exception ex)
        {
            PersistentRejectReason = ex.GetType().Name + ": " + ex.Message;
            PersistentAvailable = false;
            Mode = "oneshot";
            await KillPersistentAsync();
            return false;
        }
    }

    private string TailText()
    {
        lock (_tail)
        {
            var s = _tail.ToString().Replace("\r", "").Trim();
            return s.Length > 400 ? s[^400..] : s;
        }
    }

    private async Task ReadLoopAsync(Process proc, CancellationToken ct)
    {
        try
        {
            // stdout / stderr 都要读，否则管道写满会互相卡死
            var outTask = PumpAsync(proc.StandardOutput, ct);
            var errTask = PumpAsync(proc.StandardError, ct);
            await Task.WhenAll(outTask, errTask);
        }
        catch { /* 进程被杀时会抛，忽略 */ }
    }

    private async Task PumpAsync(StreamReader reader, CancellationToken ct)
    {
        var buf = new char[4096];
        var line = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await reader.ReadAsync(buf, ct); }
            catch { break; }
            if (n <= 0) break;

            for (int i = 0; i < n; i++)
            {
                char c = buf[i];
                if (c == '\n')
                {
                    var s = line.ToString().TrimEnd('\r');
                    line.Clear();
                    OnLine(s);
                }
                else if (c == '\r') { /* ignore */ }
                else
                {
                    line.Append(c);
                    if (line.Length > 8192) line.Clear(); // 防爆
                }
            }

            lock (_tail)
            {
                _tail.Append(buf, 0, n);
                if (_tail.Length > 4096) _tail.Remove(0, _tail.Length - 4096);
            }
        }
    }

    private void OnLine(string line)
    {
        if (!line.StartsWith(SentinelPrefix, StringComparison.Ordinal)) return;
        var numStr = line[SentinelPrefix.Length..].Trim();
        if (!long.TryParse(numStr, out var seq)) return;
        TaskCompletionSource<string>? tcs = null;
        lock (_waiters)
        {
            if (_waiters.TryGetValue(seq, out tcs)) _waiters.Remove(seq);
        }
        tcs?.TrySetResult(line);
    }

    // ────────────────────────────────────────────────────────────────
    // 三种注入形态
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 注入一条命令，返回端到端往返耗时。自动选当前可用的最快形态。
    /// </summary>
    public async Task<TimeSpan> InjectAsync(string cmd, CancellationToken ct = default)
    {
        if (PersistentAvailable) return await InjectPersistentAsync(cmd, ct);
        if (Mode == "batch") return await InjectBatchedAsync(new[] { cmd }, ct);
        return await InjectOneShotAsync(cmd, ct);
    }

    /// <summary>常驻模式：写一条命令 + 一条哨兵，等哨兵回来。</summary>
    public async Task<TimeSpan> InjectPersistentAsync(string cmd, CancellationToken ct = default)
    {
        if (_persistent is null || _persistent.HasExited)
        {
            if (!await TryStartPersistentAsync()) throw new InvalidOperationException("常驻 shell 不可用: " + PersistentRejectReason);
        }
        var proc = _persistent!;
        var seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters) _waiters[seq] = tcs;

        var sw = Stopwatch.StartNew();
        await _ioGate.WaitAsync(ct);
        try
        {
            await proc.StandardInput.WriteAsync($"{cmd}; echo {SentinelPrefix}{seq}\n");
            await proc.StandardInput.FlushAsync();
        }
        finally { _ioGate.Release(); }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(_defaultTimeoutMs, ct));
        sw.Stop();
        if (done != tcs.Task)
        {
            lock (_waiters) _waiters.Remove(seq);
            await KillPersistentAsync();
            throw new TimeoutException($"哨兵 {seq} {_defaultTimeoutMs}ms 未归，已重建会话");
        }
        return sw.Elapsed;
    }

    /// <summary>一次进程启动跑多条命令，返回整批墙钟。</summary>
    public async Task<TimeSpan> InjectBatchedAsync(IReadOnlyList<string> cmds, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _seq);
        var joined = string.Join("; ", cmds) + $"; echo {SentinelPrefix}{seq}";
        var sw = Stopwatch.StartNew();
        await RunShellAsync(joined, ct);
        sw.Stop();
        return sw.Elapsed;
    }

    /// <summary>基准形态：一条命令一个新进程。</summary>
    public async Task<TimeSpan> InjectOneShotAsync(string cmd, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await RunShellAsync(cmd, ct);
        sw.Stop();
        return sw.Elapsed;
    }

    private async Task<int> RunShellAsync(string cmd, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _hdcPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(_device);
        psi.ArgumentList.Add("shell");
        psi.ArgumentList.Add(cmd);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        // 必须同时把两个流读干净，否则 hdc 会因管道满而挂住
        var o = proc.StandardOutput.ReadToEndAsync(ct);
        var e = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(o, e);
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    /// <summary>把当前模式切到合并批发（批量基准用）。</summary>
    public void UseBatchMode() => Mode = "batch";
    public void UseOneShotMode() => Mode = "oneshot";

    // ────────────────────────────────────────────────────────────────
    // 清理
    // ────────────────────────────────────────────────────────────────

    private async Task KillPersistentAsync()
    {
        PersistentAvailable = false;
        try { _readerCts?.Cancel(); } catch { }
        if (_persistent is not null)
        {
            try { if (!_persistent.HasExited) _persistent.Kill(entireProcessTree: true); } catch { }
            try { await _persistent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _persistent.Dispose();
            _persistent = null;
        }
        lock (_waiters)
        {
            foreach (var kv in _waiters) kv.Value.TrySetException(new InvalidOperationException("会话已重建"));
            _waiters.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await KillPersistentAsync();
        _ioGate.Dispose();
    }
}
