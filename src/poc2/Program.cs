using System.Diagnostics;
using Hncast.Poc2;

// ══════════════════════════════════════════════════════════════════
//  hncast POC2 —— 输入延迟攻坚验证工具
//
//  用法：
//    dotnet run -- --bench              两条通道点击延迟对比（默认坐标 600 1400）
//    dotnet run -- --bench --n 200      指定样本数
//    dotnet run -- --drag               一次性 swipe vs 分步 move 拖拽对比
//    dotnet run -- --probe              只探测两条通道是否可用
//
//  可选参数：
//    --x 600 --y 1400      注入坐标（默认屏幕中央偏下的空白区）
//    --rpc-port 15002      本地 fport 端口（默认 15002，需先 hdc fport tcp:15002 tcp:8012）
//    --device 192.0.2.10:5555
//    --hdc %USERPROFILE%\.hnscrcpy\tools\windows-x64\hdc.exe
//    --home                bench 前先按 Home 回到桌面（避免误触 App）
// ══════════════════════════════════════════════════════════════════

var opt = Cfg.Parse(args);

if (opt.ShowHelp || args.Length == 0)
{
    Cfg.PrintHelp();
    return 0;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
Banner();

int rc = 0;
try
{
    switch (opt.Mode)
    {
        case "probe": rc = await ProbeAsync(opt); break;
        case "bench": rc = await BenchAsync(opt); break;
        case "drag":  rc = await DragAsync(opt); break;
        default:
            Console.WriteLine($"未知模式: {opt.Mode}");
            Cfg.PrintHelp();
            rc = 2;
            break;
    }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"✗ 执行失败: {ex.GetType().Name}: {ex.Message}");
    rc = 1;
}
return rc;

// ──────────────────────────────────────────────────────────────────
// 探测
// ──────────────────────────────────────────────────────────────────
static async Task<int> ProbeAsync(Cfg opt)
{
    Console.WriteLine("【1/3】hdc shell 常驻会话探测");
    await using (var shell = new HdcShellSession(opt.Hdc, opt.Device))
    {
        var ok = await shell.TryStartPersistentAsync();
        Console.WriteLine(ok
            ? "  ✔ 常驻 shell 可用 —— 延迟天花板由 hdc 协议决定"
            : $"  ✗ 常驻 shell 不可用：{shell.PersistentRejectReason}");
        var t = await shell.InjectOneShotAsync("echo probe");
        Console.WriteLine($"  · one-shot 基线 `hdc shell echo probe` = {t.TotalMilliseconds:F0} ms");
    }

    Console.WriteLine();
    Console.WriteLine("【2/3】8012 JSON-RPC 通道探测");
    await using (var rpc = new UitestRpcClient("127.0.0.1", opt.RpcPort))
    {
        await rpc.ConnectAsync();
        var (w, h) = await rpc.GetDisplaySizeAsync();
        Console.WriteLine($"  ✔ 已连接 127.0.0.1:{opt.RpcPort} → 设备 8012，屏幕 {w}×{h}");

        var lat = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            var (_, el) = await rpc.CallDriverAsync("Driver.getDisplaySize", "[]");
            lat.Add(el.TotalMilliseconds);
        }
        var s = new Stats(lat);
        Console.WriteLine($"  · Driver.getDisplaySize ×10：{s}");
    }

    Console.WriteLine();
    Console.WriteLine("【3/3】结论");
    Console.WriteLine("  见 docs/05-输入延迟攻坚报告.md");
    return 0;
}

// ──────────────────────────────────────────────────────────────────
// 基准
// ──────────────────────────────────────────────────────────────────
static async Task<int> BenchAsync(Cfg opt)
{
    int n = opt.N;
    Console.WriteLine($"注入坐标 ({opt.X}, {opt.Y})，样本数 n={n}");
    Console.WriteLine();

    // ── 通道 A：hdc shell one-shot（当前 POC 的做法）
    var oneshotLat = new List<double>();
    var batchLat = new List<double>();
    await using (var shell = new HdcShellSession(opt.Hdc, opt.Device))
    {
        Console.WriteLine($"[A] hdc shell one-shot —— 每次新建 hdc.exe 进程（基准）");
        Console.Write("    ");
        int oneshotN = Math.Min(n, 30);
        for (int i = 0; i < oneshotN; i++)
        {
            var t = await shell.InjectOneShotAsync($"uitest uiInput click {opt.X} {opt.Y}");
            oneshotLat.Add(t.TotalMilliseconds);
            if (i % 5 == 0) Console.Write(".");
        }
        Console.WriteLine();
        Console.WriteLine($"    {new Stats(oneshotLat)}   （n={oneshotN}，已截断以免耗时过长）");
        Console.WriteLine();

        // ── 通道 B：hdc shell 合并批发（一次进程跑 8 条）
        Console.WriteLine("[B] hdc shell 合并批发 —— 一次进程跑 8 条 click");
        int batches = Math.Max(1, n / 8);
        for (int i = 0; i < batches; i++)
        {
            var cmds = Enumerable.Repeat($"uitest uiInput click {opt.X} {opt.Y}", 8).ToList();
            var t = await shell.InjectBatchedAsync(cmds);
            batchLat.Add(t.TotalMilliseconds / 8.0);   // 摊销到单条
        }
        Console.WriteLine($"    {new Stats(batchLat)}   （单条摊销量，批次数={batches}）");
        Console.WriteLine();
    }

    // ── 通道 C / D：8012 RPC
    var seqLat = new List<double>();
    var pipeLat = new List<double>();
    await using (var rpc = new UitestRpcClient("127.0.0.1", opt.RpcPort))
    {
        await rpc.ConnectAsync();
        var (w, h) = await rpc.GetDisplaySizeAsync();
        Console.WriteLine($"[C] 8012 RPC 长连接 —— 顺序注入（每条等响应），屏幕 {w}×{h}");
        Console.Write("    ");
        for (int i = 0; i < n; i++)
        {
            var el = await rpc.ClickAsync(opt.X, opt.Y);
            seqLat.Add(el.TotalMilliseconds);
            if (i % 20 == 0) Console.Write(".");
        }
        Console.WriteLine();
        Console.WriteLine($"    {new Stats(seqLat)}");
        Console.WriteLine();

        Console.WriteLine("[D] 8012 RPC 长连接 —— pipelining（一次性发完 n 条，统一等待）");
        var sw = Stopwatch.StartNew();
        var tasks = new List<Task<string>>(n * 2);
        for (int i = 0; i < n; i++)
        {
            tasks.Add(rpc.QueueGestureAsync("touchDown", opt.X, opt.Y));
            tasks.Add(rpc.QueueGestureAsync("touchUp", opt.X, opt.Y));
        }
        var sendDone = sw.Elapsed.TotalMilliseconds;
        await Task.WhenAll(tasks);
        sw.Stop();
        Console.WriteLine($"    发送 {n * 2} 帧耗时 {sendDone:F0} ms；全部响应归位总墙钟 {sw.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"    均摊单次点击 = {sw.Elapsed.TotalMilliseconds / n:F1} ms  ⇒ {n / (sw.Elapsed.TotalMilliseconds / 1000):F0} 次/秒");
        pipeLat.AddRange(tasks.Select(_ => sw.Elapsed.TotalMilliseconds / n));
        Console.WriteLine($"    连接统计：发 {rpc.SentFrames} 帧 / 收 {rpc.RecvFrames} 帧 / 重连 {rpc.Reconnects} 次");
        Console.WriteLine();
    }

    // ── 汇总
    Console.WriteLine("════════════════════ 汇总 ════════════════");
    Console.WriteLine($"{"通道",-34}{"P50",9}{"P95",9}{"P99",9}{"吞吐",10}");
    Console.WriteLine(new string('─', 72));
    Row("A  hdc one-shot（基准）", oneshotLat);
    Row("B  hdc 合并批发(8)", batchLat);
    Row("C  8012 RPC 顺序", seqLat);
    Row("D  8012 RPC pipelined", pipeLat);
    Console.WriteLine(new string('─', 72));
    Console.WriteLine("阶段一 PowerShell 基准（对照）：");
    Console.WriteLine("    单次 hdc shell uitest uiInput click ... P50=447.6ms P95=559.3ms");
    Console.WriteLine("    单次 hdc shell echo ...               P50=153.3ms P95=197.6ms");
    Console.WriteLine("    单次 hdc list targets ...             P50= 73.8ms");
    Console.WriteLine();
    return 0;

    static void Row(string name, List<double> data)
    {
        if (data.Count == 0) { Console.WriteLine($"{name,-34}{"-",9}"); return; }
        var s = new Stats(data);
        double thr = s.P50 > 0 ? 1000.0 / s.P50 : 0;
        Console.WriteLine($"{name,-34}{s.P50,9:F1}{s.P95,9:F1}{s.P99,9:F1}{thr,9:F1}/s");
    }
}

// ──────────────────────────────────────────────────────────────────
// 拖拽对比
// ──────────────────────────────────────────────────────────────────
static async Task<int> DragAsync(Cfg opt)
{
    int x1 = opt.X, y1 = Math.Min(opt.Y + 400, 2300);
    int x2 = opt.X, y2 = Math.Max(opt.Y - 400, 300);
    Console.WriteLine($"拖拽轨迹 ({x1},{y1}) → ({x2},{y2})，垂直距离 {y1 - y2}px");
    Console.WriteLine();

    // ① uitest one-shot swipe
    Console.WriteLine("[1] uitest uiInput swipe（一次性，每次新建进程）");
    await using (var shell = new HdcShellSession(opt.Hdc, opt.Device))
    {
        var t = await shell.InjectOneShotAsync($"uitest uiInput swipe {x1} {y1} {x2} {y2} 2000");
        Console.WriteLine($"    单次耗时 {t.TotalMilliseconds:F0} ms  （其中 ~260ms 是 uitest 二进制启动，其余是手势本身时长）");
    }
    Console.WriteLine();

    await using var rpc = new UitestRpcClient("127.0.0.1", opt.RpcPort);
    await rpc.ConnectAsync();

    // ② RPC Driver.swipe —— 设备侧合成整段手势，只发一帧
    Console.WriteLine("[2] 8012 RPC Driver.swipe（一次性，设备侧合成手势）");
    var (_, sw1) = await rpc.SwipeAsync(x1, y1, x2, y2, 2000);
    Console.WriteLine($"    单次耗时 {sw1.TotalMilliseconds:F0} ms");
    Console.WriteLine();

    // ③ RPC 分步 touchMove 流 —— 真正跟手的做法
    Console.WriteLine("[3] 8012 RPC 分步 touchMove（60Hz 触摸流，跟手）");
    const int frames = 60;
    int stepY = (y1 - y2) / frames;
    var frameLat = new List<double>();

    var swTotal = Stopwatch.StartNew();
    var down = await rpc.GestureAsync("touchDown", x1, y1);
    frameLat.Add(down.elapsed.TotalMilliseconds);

    var pending = new List<Task<string>>();
    var swWrite = Stopwatch.StartNew();
    for (int i = 1; i <= frames; i++)
    {
        int y = y1 - stepY * i;
        // 先入队再等待节奏：写入本身几乎不耗时（实测 p50 0.07ms）
        pending.Add(rpc.QueueGestureAsync("touchMove", x1, y));
        double target = i * (1000.0 / 60.0);
        double now = swWrite.Elapsed.TotalMilliseconds;
        if (target > now) await Task.Delay((int)(target - now));
    }
    var up = await rpc.GestureAsync("touchUp", x1, y2);
    frameLat.Add(up.elapsed.TotalMilliseconds);
    await Task.WhenAll(pending);
    swTotal.Stop();

    var st = new Stats(frameLat);
    Console.WriteLine($"    touchDown {down.elapsed.TotalMilliseconds:F0} ms，{frames} 帧 touchMove，touchUp {up.elapsed.TotalMilliseconds:F0} ms");
    Console.WriteLine($"    总墙钟 {swTotal.Elapsed.TotalMilliseconds:F0} ms（目标 {frames * 1000.0 / 60:F0} ms 的 60Hz 节奏）");
    Console.WriteLine($"    首尾单帧往返：{st}");
    Console.WriteLine();

    // ── 结论对比
    Console.WriteLine("════════════════════ 拖拽方案对比 ════════════════");
    Console.WriteLine($"{"方案",-30}{"单次/总耗时",14}{"跟手",8}说明");
    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"{"① uitest swipe 一次性",-30}{"~1700 ms",14}{"否",8}进程启动 260ms + 手势时长，且全程阻塞");
    Console.WriteLine($"{"② RPC Driver.swipe 一次性",-30}{"见上",14}{"否",8}无进程启动，但手势期间画面不可干预");
    Console.WriteLine($"{"③ RPC 分步 touchMove",-30}{$"{swTotal.Elapsed.TotalMilliseconds:F0} ms / {frames} 帧",14}{"是",8}每帧独立可打断，可随时改方向");
    Console.WriteLine(new string('─', 78));
    Console.WriteLine("结论：只有 ③ 是真的触摸透传；①② 只能做「一次性动作 + 本地乐观渲染」。");
    Console.WriteLine();
    return 0;
}

// ──────────────────────────────────────────────────────────────────
// 辅助
// ──────────────────────────────────────────────────────────────────
static void Banner()
{
    Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  hncast POC2 —— 输入延迟攻坚（hdc shell vs 8012 JSON-RPC） ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
    Console.WriteLine();
}

// ──────────────────────────────────────────────────────────────────
// 统计
// ──────────────────────────────────────────────────────────────────
internal sealed class Stats
{
    public double P50 { get; }
    public double P95 { get; }
    public double P99 { get; }
    public double Min { get; }
    public double Max { get; }
    public double Avg { get; }
    public int N { get; }

    public Stats(IEnumerable<double> values)
    {
        var a = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        N = a.Length;
        if (N == 0) { P50 = P95 = P99 = Min = Max = Avg = 0; return; }
        Min = a[0]; Max = a[^1]; Avg = a.Average();
        P50 = At(a, 0.50); P95 = At(a, 0.95); P99 = At(a, 0.99);
    }

    private static double At(double[] a, double q)
    {
        int idx = (int)Math.Floor(a.Length * q);
        if (idx >= a.Length) idx = a.Length - 1;
        return a[idx];
    }

    public override string ToString()
        => $"P50={P50:F1} P95={P95:F1} P99={P99:F1} (min={Min:F1} max={Max:F1} avg={Avg:F1} n={N})";
}

// ──────────────────────────────────────────────────────────────────
// 命令行
// ──────────────────────────────────────────────────────────────────
internal sealed class Cfg
{
    public string Mode = "bench";
    public int N = 200;
    public int X = 600;
    public int Y = 1400;
    public int RpcPort = 15002;
    public string Device = "192.0.2.10:5555";
    public string Hdc = @"%USERPROFILE%\.hnscrcpy\tools\windows-x64\hdc.exe";
    public bool ShowHelp;

    public static Cfg Parse(string[] args)
    {
        var c = new Cfg();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bench": c.Mode = "bench"; break;
                case "--drag": c.Mode = "drag"; break;
                case "--probe": c.Mode = "probe"; break;
                case "--n": if (i + 1 < args.Length) c.N = int.Parse(args[++i]); break;
                case "--x": if (i + 1 < args.Length) c.X = int.Parse(args[++i]); break;
                case "--y": if (i + 1 < args.Length) c.Y = int.Parse(args[++i]); break;
                case "--rpc-port": if (i + 1 < args.Length) c.RpcPort = int.Parse(args[++i]); break;
                case "--device": if (i + 1 < args.Length) c.Device = args[++i]; break;
                case "--hdc": if (i + 1 < args.Length) c.Hdc = args[++i]; break;
                case "--home": c.GoHome = true; break;
                case "-h": case "--help": c.ShowHelp = true; break;
            }
        }
        return c;
    }

    public bool GoHome;

    public static void PrintHelp()
    {
        Console.WriteLine("hncast POC2 —— 输入延迟攻坚验证工具");
        Console.WriteLine();
        Console.WriteLine("  --bench            两条通道点击延迟对比（默认）");
        Console.WriteLine("  --drag             一次性 swipe vs 分步 move 拖拽对比");
        Console.WriteLine("  --probe            探测 hdc shell 与 8012 RPC 两条通道");
        Console.WriteLine();
        Console.WriteLine("  --n 200            样本数（默认 200）");
        Console.WriteLine("  --x 600 --y 1400   注入坐标（默认屏幕中央偏下空白区）");
        Console.WriteLine("  --rpc-port 15002   本地 fport 端口（需先 hdc fport tcp:15002 tcp:8012）");
        Console.WriteLine("  --device <addr>    设备地址（默认 192.0.2.10:5555）");
        Console.WriteLine("  --hdc <path>       hdc.exe 路径");
        Console.WriteLine();
    }
}
