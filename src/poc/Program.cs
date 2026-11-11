// hncast POC —— 鸿蒙投屏 + 反向控制 端到端验证（纯 console，无 GUI）
//
// 用法：
//   hncast-poc.exe [--device <DEVICE_ADDR>] [--local-port 15001]
//                  [--hdc <path>] [--jar <path>] [--samples <dir>]
//                  [--max-frames 30] [--timeout 15] [--dump] [--keep-daemon]
//
// 阶段：
//   A 设备在线检查 → B 确保 so / 起 daemon / 建 fport → C gRPC onStart 收流存盘 → D 点击注入 + 截图 MD5 比对

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;

namespace HnCast.Poc;

internal static class Program
{
    // ─────────────────────────── 配置 ───────────────────────────

    private sealed class Cfg
    {
        public string Device = "";   // 运行时由 --device 提供；仓库内禁止写死真机地址
        public int LocalPort = 15001;
        public string Hdc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                         @".hnscrcpy\tools\windows-x64\hdc.exe");
        public string Jar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                         @".hnscrcpy\lib\hosScrcpy-1.0.20-beta.jar");
        public string SoEntry = "libscrcpy/libscrcpy_server_unix_6.8-20260801.z.so";
        public string SoName = "libscreen_casting.z.so";       // 设备端必须改这个名字
        public string Samples = "";
        public int MaxFrames = 30;
        public int TimeoutSec = 15;
        public bool Dump;
        public bool KeepDaemon;
        public bool Interact = true;   // 收流中途做一次 swipe，用来观测「注入→画面变化→帧回流」
        public bool Analyze;           // 离线模式：只分析 samples/ 下已落盘的数据，不碰设备
    }

    private const string RemoteDir = "/data/local/tmp";
    private const string AbstractSocket = "scrcpy_grpc_socket";

    private static Cfg _c = new();
    private static int _daemonPid = -1;

    // ─────────────────────────── 入口 ───────────────────────────

    private static async Task<int> Main(string[] rawArgs)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* redirected */ }

        _c = ParseArgs(rawArgs);
        if (string.IsNullOrEmpty(_c.Samples)) _c.Samples = Path.Combine(FindRepoRoot(), "samples");
        Directory.CreateDirectory(_c.Samples);

        Banner();
        if (_c.Analyze) return AnalyzeMode();

        // 设备地址必须由调用方提供：仓库内不写死真机地址，且空串会让
        // PhaseA 的 stdout.Contains("") 恒为 true，造成假通过。
        if (string.IsNullOrWhiteSpace(_c.Device))
        {
            Console.Error.WriteLine("✗ 未提供设备地址。请用 --device <ip:port> 传入（示例：--device 192.0.2.10:5555，示例地址）。");
            return 2;
        }

        var swTotal = Stopwatch.StartNew();
        bool okA = false, okB = false, okC = false, okD = false;
        var notes = new List<string>();

        // ── A: 设备在线 ──
        try
        {
            okA = PhaseA_Device();
            if (!okA) { Console.WriteLine("✗ 无设备，终止"); return 2; }
        }
        catch (Exception ex) { Console.WriteLine("✗ 阶段A异常: " + ex.Message); return 2; }

        // ── B: so + daemon + fport ──
        try { okB = PhaseB_Prepare(); }
        catch (Exception ex) { Console.WriteLine("✗ 阶段B异常: " + ex.Message); }

        if (okB)
        {
            // ── C: gRPC 收流 ──
            try { okC = await PhaseC_Stream(); }
            catch (Exception ex) { Console.WriteLine("✗ 阶段C异常: " + ex.GetType().Name + ": " + ex.Message); }

            // ── D: 点击注入 + 截图比对 ──
            try { okD = PhaseD_Input(ref notes); }
            catch (Exception ex) { Console.WriteLine("✗ 阶段D异常: " + ex.Message); }
        }

        // ── 清理 ──
        Cleanup();

        swTotal.Stop();
        Console.WriteLine();
        Console.WriteLine("════════════════ 汇总 ════════════════");
        Console.WriteLine($"  A 设备在线          : {(okA ? "✔ 通过" : "✗ 失败")}");
        Console.WriteLine($"  B so/daemon/fport   : {(okB ? "✔ 通过" : "✗ 失败")}");
        Console.WriteLine($"  C gRPC 视频流       : {(okC ? "✔ 通过" : "✗ 失败")}");
        Console.WriteLine($"  D 点击注入生效      : {(okD ? "✔ 通过" : "✗ 失败")}");
        foreach (var n in notes) Console.WriteLine("  · " + n);
        Console.WriteLine($"  总耗时              : {swTotal.Elapsed.TotalSeconds:F1} 秒");
        Console.WriteLine("═════════════════════════════════════");

        return (okA && okB && okC && okD) ? 0 : 1;
    }

    private static void Banner()
    {
        Console.WriteLine("╔═══════════════════════════════════════════════════╗");
        Console.WriteLine("║  hncast POC · 鸿蒙投屏 + 反向控制 · console 验证   ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════╝");
        Console.WriteLine($"  device={_c.Device}  localPort={_c.LocalPort}");
        Console.WriteLine($"  hdc={_c.Hdc}");
        Console.WriteLine($"  samples={_c.Samples}");
        Console.WriteLine();
    }

    // ─────────────────────────── A: 设备在线 ───────────────────────────

    private static bool PhaseA_Device()
    {
        Section("A", "设备在线检查");
        var (code, stdout, stderr) = Hdc("list targets");
        Console.WriteLine($"  hdc list targets → exit={code} out={(stdout + stderr).Replace("\r", "").Replace("\n", " | ").Trim()}");
        if (stdout.Contains(_c.Device)) { Console.WriteLine($"  ✔ 设备已连接: {_c.Device}"); return true; }

        Console.WriteLine("  · 未在列表中，尝试 hdc tconn 重连…");
        var (c2, o2, e2) = Hdc($"tconn {_c.Device}");
        Console.WriteLine($"  hdc tconn → exit={c2} {(o2 + e2).Trim()}");
        Thread.Sleep(1500);
        var (c3, o3, _) = Hdc("list targets");
        if (o3.Contains(_c.Device)) { Console.WriteLine($"  ✔ 重连成功: {_c.Device}"); return true; }
        Console.WriteLine($"  ✗ 设备不可用（exit={c3}）: {o3.Trim()}");
        return false;
    }

    // ─────────────────────────── B: so + daemon + fport ───────────────────────────

    private static bool PhaseB_Prepare()
    {
        Section("B", "设备侧准备：so / daemon / fport");

        // B1 —— so 是否就位（大小匹配即跳过）
        long localSize = LocalSoSize();
        string remotePath = RemoteDir + "/" + _c.SoName;
        var (_, sizeOut, _e) = Hdc($"shell \"stat -c %s {remotePath} 2>/dev/null || echo MISSING\"");
        string sizeRaw = sizeOut.Trim();
        Console.WriteLine($"  · 本地 so 大小 = {localSize:N0} B；设备端 stat = {sizeRaw}");

        bool needPush = true;
        if (long.TryParse(sizeRaw, out long rsize) && rsize == localSize && localSize > 0)
        {
            Console.WriteLine("  ✔ 设备端 so 大小已匹配，跳过推送");
            needPush = false;
        }
        if (needPush)
        {
            Console.WriteLine("  · 正在从 jar 解包并推送 so（首推约 1-3 秒）…");
            string tmp = Path.Combine(Path.GetTempPath(), _c.SoName);
            if (!ExtractSoFromJar(tmp)) { Console.WriteLine("  ✗ jar 内未找到对应 so，且设备端也不可用"); return false; }
            var (pc, po, pe) = Hdc($"file send \"{tmp}\" {remotePath}");
            Console.WriteLine($"  hdc file send → exit={pc} {(po + pe).Trim()}");
            try { File.Delete(tmp); } catch { }
            var (_, s2, _) = Hdc($"shell \"stat -c %s {remotePath}\"");
            Console.WriteLine($"  · 推送后设备端大小 = {s2.Trim()}（期望 {localSize}）");
        }

        // B2 —— daemon 是否已有加载本 so 的实例
        bool sockUp = SocketExists();
        Console.WriteLine($"  · 抽象 socket @{AbstractSocket} 存在 = {sockUp}");
        if (!sockUp)
        {
            Console.WriteLine("  · 启动 daemon（cd 到 /data/local/tmp，扩展名只给文件名）…");
            string cmd = $"shell \"cd {RemoteDir} && uitest start-daemon singleness --extension-name {_c.SoName} " +
                         "-scale 1 -frameRate 30 -bitRate 8388608 -p 5000 -iFrameInterval 2000 &\"";
            var (dc, _, de) = Hdc(cmd);
            Console.WriteLine($"  hdc {cmd} → exit={dc} {de.Trim()}");
        }
        else
        {
            Console.WriteLine("  · 已有 daemon 在服务该 socket，复用");
        }

        // B3 —— 等 socket 就绪（最多 15 秒，每 3 秒播报）
        for (int i = 1; i <= 5; i++)
        {
            if (SocketExists()) { Console.WriteLine($"  ✔ socket 已就绪（等待 {i * 3 - 3} 秒）"); break; }
            Console.Write($"  · 等待 socket 就绪… 已等 {i * 3} 秒\r");
            Thread.Sleep(3000);
            if (i == 5) { Console.WriteLine("  ✗ socket 始终未出现（daemon 启动失败）"); return false; }
        }
        _daemonPid = FindOurDaemonPid();
        Console.WriteLine($"  · 本 POC 的 daemon pid = {(_daemonPid > 0 ? _daemonPid.ToString() : "未识别")}");

        // B4 —— fport
        Hdc($"fport rm tcp:{_c.LocalPort} localabstract:{AbstractSocket}");
        var (fc, fo, fe) = Hdc($"fport tcp:{_c.LocalPort} localabstract:{AbstractSocket}");
        Console.WriteLine($"  hdc fport tcp:{_c.LocalPort} localabstract:{AbstractSocket} → exit={fc} {(fo + fe).Trim()}");
        Thread.Sleep(400);

        var (_, ls, _) = Hdc("fport ls");
        bool fwd = ls.Contains($"tcp:{_c.LocalPort}");
        Console.WriteLine($"  {(fwd ? "✔" : "✗")} fport 列表确认: {(fwd ? "转发已建立" : "未找到转发")}");
        if (!fwd) return false;

        Console.WriteLine($"  ✔ 阶段B完成：so 就位 / socket 在线 / 127.0.0.1:{_c.LocalPort} 已转发");
        return true;
    }

    private static long LocalSoSize()
    {
        // 首选：设备端已有 247989，本地只需知道期望值 → 从 jar zip 目录读长度（不落盘）
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(_c.Jar);
            var e = zip.Entries.FirstOrDefault(x => x.FullName == _c.SoEntry);
            if (e != null) return e.Length;
        }
        catch (Exception ex) { Console.WriteLine("  · 读 jar 失败: " + ex.Message); }
        return -1;
    }

    private static bool ExtractSoFromJar(string dst)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(_c.Jar);
            var e = zip.Entries.FirstOrDefault(x => x.FullName == _c.SoEntry);
            if (e == null) return false;
            e.ExtractToFile(dst, true);
            return true;
        }
        catch (Exception ex) { Console.WriteLine("  · 解包失败: " + ex.Message); return false; }
    }

    private static bool SocketExists()
    {
        var (_, o, _) = Hdc($"shell \"cat /proc/net/unix | grep {AbstractSocket}\"");
        return o.Contains(AbstractSocket);
    }

    private static int FindOurDaemonPid()
    {
        var (_, o, _) = Hdc("shell \"ps -ef | grep extension-name | grep " + _c.SoName + " | grep -v grep\"");
        foreach (var line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(_c.SoName)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int pid)) return pid;
        }
        return -1;
    }

    // ─────────────────────────── C: gRPC 视频流 ───────────────────────────

    private static async Task<bool> PhaseC_Stream()
    {
        Section("C", "gRPC onStart 收流");
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        var addr = $"http://127.0.0.1:{_c.LocalPort}";
        Console.WriteLine($"  · 建立 gRPC channel: {addr} (h2c 明文)");
        var swConn = Stopwatch.StartNew();
        using var channel = GrpcChannel.ForAddress(addr, new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = 100 * 1024 * 1024,
            MaxSendMessageSize = 100 * 1024 * 1024
        });

        var marshaller = Marshallers.Create<byte[]>(b => b ?? Array.Empty<byte>(), d => d ?? Array.Empty<byte>());
        var onStart = new Method<byte[], byte[]>(MethodType.ServerStreaming, "ScrcpyService", "onStart", marshaller, marshaller);
        var onEnd = new Method<byte[], byte[]>(MethodType.Unary, "ScrcpyService", "onEnd", marshaller, marshaller);

        var invoker = channel.CreateCallInvoker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_c.TimeoutSec));
        var call = invoker.AsyncServerStreamingCall(onStart, null, new CallOptions(cancellationToken: cts.Token), Array.Empty<byte>());

        string extradataPath = Path.Combine(_c.Samples, "extradata.bin");
        string streamPath = Path.Combine(_c.Samples, "stream.h264");
        using var fsExtra = new FileStream(extradataPath, FileMode.Create, FileAccess.Write);
        using var fsStream = new FileStream(streamPath, FileMode.Create, FileAccess.Write);

        int frames = 0;
        long bytesVideo = 0;
        double tFirst = -1;
        var sw = Stopwatch.StartNew();
        var bodySizes = new List<int>();
        var scenarios = new List<string>();
        var tagReport = new SortedDictionary<string, string>();

        Console.WriteLine($"  · 调 onStart(Empty)，限 {_c.MaxFrames} 帧 / {_c.TimeoutSec} 秒");

        // 收流中途注入一次 swipe：设备是「按屏幕变化推流」，若随后帧数增长，即闭环证据
        // （注入次数预算：本 swipe + 阶段D 的 click = 2 次，未超 3 次上限）
        string? kickInfo = null;
        int framesAtKick = -1;
        Task? kick = null;
        if (_c.Interact)
        {
            kick = Task.Run(async () =>
            {
                await Task.Delay(2000).ConfigureAwait(false);
                framesAtKick = frames;
                var (kc, ko, _) = Hdc("shell \"uitest uiInput swipe 630 1900 630 1300 600\"");
                kickInfo = $"swipe (630,1900)→(630,1300) exit={kc} out={ko.Trim()}";
            });
        }

        try
        {
            while (await call.ResponseStream.MoveNext(cts.Token).ConfigureAwait(false))
            {
                var msg = call.ResponseStream.Current;
                frames++;
                double ms = sw.Elapsed.TotalMilliseconds;
                if (tFirst < 0) { tFirst = ms; Console.WriteLine($"  ★ 首帧到达 {ms:F0} ms（建流+首帧）"); }

                var map = Proto.ParseMap(msg);
                var data = Proto.GetBlob(map, "data");
                long? flags = Proto.GetNum(map, "flags");
                long? pts = Proto.GetNum(map, "pts");
                long? len = Proto.GetNum(map, "len");

                // 记录 ParamValue 的 tag 结构（用于反向校正 .proto）
                foreach (var kv in map)
                    if (kv.Value.TagMap.Count > 0)
                        tagReport[kv.Key] = string.Join(", ", kv.Value.TagMap.Select(t => $"f{t.Field}/{t.WireType}"));

                bytesVideo += data.Length;
                bodySizes.Add(data.Length);

                string head = data.Length >= 5 ? BitConverter.ToString(data, 0, Math.Min(5, data.Length)) : BitConverter.ToString(data);
                Console.WriteLine($"  #{frames,-3} len={data.Length,-7} flags={(flags?.ToString() ?? "-"),-4} pts={(pts?.ToString() ?? "-"),-8} seq_head={head}{(data.Length >= 5 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1 ? "  [AnnexB]" : "")}");

                if (frames == 1) { fsExtra.Write(data); fsExtra.Flush(); Console.WriteLine($"       → 首条消息(SPS+PPS) 存入 extradata.bin"); }
                else { fsStream.Write(data); fsStream.Flush(); }

                if (frames == 1) scenarios.Add($"首条 {data.Length}B = SPS+PPS 合包（实测 32B：SPS 23 + PPS 9）");
                if (flags == 8) scenarios.Add($"#{frames} flags=8 配置帧");
                if (flags == 2) scenarios.Add($"#{frames} flags=2 IDR");

                if (_c.Dump && frames <= 3)
                {
                    Console.WriteLine("       ── 原始 protobuf（前 96 字节）──");
                    Console.WriteLine("       " + BitConverter.ToString(msg, 0, Math.Min(96, msg.Length)).Replace("-", " "));
                    foreach (var kv in map)
                        Console.WriteLine($"       payload[\"{kv.Key}\"] blob={kv.Value.Blob?.Length ?? 0}B num={kv.Value.Num?.ToString() ?? "-"} tags=[{string.Join(",", kv.Value.TagMap.Select(t => $"f{t.Field}/wt{t.WireType}"))}]");
                }

                if (frames >= _c.MaxFrames) { Console.WriteLine($"  · 已收满 {_c.MaxFrames} 帧，停止"); break; }
            }
        }
        catch (OperationCanceledException) { Console.WriteLine($"  · {_c.TimeoutSec} 秒超时，正常停止"); }
        catch (RpcException ex)
        {
            Console.WriteLine($"  ✗ RpcException status={ex.StatusCode} detail={ex.Status.Detail}");
            if (frames == 0) return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ 流异常 {ex.GetType().Name}: {ex.Message}");
            if (frames == 0) return false;
        }
        if (kick != null)
        {
            try { await kick.ConfigureAwait(false); } catch { }
            Console.WriteLine($"  · 收流中途注入: {kickInfo}");
            if (framesAtKick >= 0)
                Console.WriteLine($"  · 注入时刻已收 {framesAtKick} 帧 → 结束时 {frames} 帧，增量 {frames - framesAtKick} 帧");
        }

        swConn.Stop();

        Console.WriteLine($"  · 共收 {frames} 条消息 / 视频 {bytesVideo:N0} B / 时长 {sw.Elapsed.TotalSeconds:F1}s");
        if (bodySizes.Count > 0)
            Console.WriteLine($"  · 帧长 min={bodySizes.Min()} max={bodySizes.Max()} avg={bodySizes.Average():F0}");

        // 优雅结束
        try
        {
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var endReply = invoker.BlockingUnaryCall(onEnd, null, new CallOptions(cancellationToken: cts2.Token), Array.Empty<byte>());
            Console.WriteLine($"  · onEnd 已调用（回包 {endReply?.Length ?? 0} B）");
        }
        catch (Exception ex) { Console.WriteLine("  · onEnd 调用失败（不影响结论）: " + ex.Message); }

        if (frames == 0)
        {
            Console.WriteLine("  ✗ 未收到任何消息");
            return false;
        }

        // 落盘校验
        long eLen = new FileInfo(extradataPath).Length;
        long sLen = new FileInfo(streamPath).Length;
        Console.WriteLine($"  ✔ extradata.bin = {eLen} B   stream.h264 = {sLen:N0} B");
        Console.WriteLine($"  ✔ 首帧耗时 {tFirst:F0} ms");
        Console.WriteLine("  · ParamValue tag 实测（用于校正 .proto）:");
        foreach (var kv in tagReport) Console.WriteLine($"      {kv.Key,-6} → {kv.Value}");
        foreach (var s in scenarios.Distinct()) Console.WriteLine("  · " + s);
        return true;
    }

    // ─────────────────────────── D: 输入注入 ───────────────────────────

    private static bool PhaseD_Input(ref List<string> notes)
    {
        Section("D", "点击注入 + 截图比对");

        var (_, dimOut, _) = Hdc("shell \"snapshot_display -f /data/local/tmp/_poc_probe.jpeg 2>&1 | grep -iE 'width|height'\"");
        int w = 1260, h = 2720;
        var m = System.Text.RegularExpressions.Regex.Match(dimOut, @"width:\s*(\d+),\s*height:\s*(\d+)");
        if (m.Success) { w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value); }
        int cx = w / 2, cy = h / 2;
        Console.WriteLine($"  · 屏幕 {w}x{h}，点击点 = ({cx},{cy})");

        string before = Path.Combine(_c.Samples, "shot_before.jpeg");
        string after = Path.Combine(_c.Samples, "shot_after.jpeg");

        if (!GrabShot("hx.jpeg", before)) { Console.WriteLine("  ✗ 点击前截图失败"); return false; }
        string md5a = Md5File(before);
        Console.WriteLine($"  · 点击前截图 → {before}  md5={md5a}");

        var sw = Stopwatch.StartNew();
        var (cc, co, ce) = Hdc($"shell \"uitest uiInput click {cx} {cy}\"");
        sw.Stop();
        Console.WriteLine($"  · uitest uiInput click {cx} {cy} → exit={cc} {sw.Elapsed.TotalMilliseconds:F0} ms out={(co + ce).Replace("\r", "").Replace("\n", " ").Trim()}");
        Thread.Sleep(1200);

        if (!GrabShot("hx2.jpeg", after)) { Console.WriteLine("  ✗ 点击后截图失败"); return false; }
        string md5b = Md5File(after);
        Console.WriteLine($"  · 点击后截图 → {after}  md5={md5b}");

        bool changed = !string.Equals(md5a, md5b, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  {(changed ? "✔" : "✗")} MD5 {(changed ? "不同 → 屏幕内容已改变，注入生效" : "相同 → 该点点击未引起可见变化")}");

        if (!changed)
        {
            // 第二次尝试：改成一次短 swipe（仍在 ≤3 次注入的预算内），用来排除"点了但页面本就静止"
            Console.WriteLine("  · 追加一次 swipe 作为第二判据（预算内，第 2 次注入）…");
            string mid = Path.Combine(_c.Samples, "shot_mid.jpeg");
            var (sc, _, _) = Hdc($"shell \"uitest uiInput swipe {cx} {cy + 300} {cx} {cy - 300} 800\"");
            Thread.Sleep(1200);
            if (GrabShot("hx3.jpeg", mid))
            {
                string md5c = Md5File(mid);
                bool changed2 = !string.Equals(md5b, md5c, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"  · swipe 后 md5={md5c} → {(changed2 ? "✔ 改变，注入生效" : "✗ 仍未改变")}");
                changed = changed2;
                notes.Add($"点击点未引起变化，改由 swipe 证明注入（exit={cc}/{sc}）");
            }
        }
        return changed;
    }

    private static bool GrabShot(string remoteName, string localPath)
    {
        string remote = RemoteDir + "/" + remoteName;
        var (c1, o1, e1) = Hdc($"shell \"snapshot_display -f {remote}\"");
        if (c1 != 0) { Console.WriteLine($"    · snapshot_display exit={c1} {(o1 + e1).Trim()}"); }
        var (c2, o2, e2) = Hdc($"file recv {remote} \"{localPath}\"");
        if (c2 != 0) { Console.WriteLine($"    · file recv exit={c2} {(o2 + e2).Trim()}"); return false; }
        return File.Exists(localPath) && new FileInfo(localPath).Length > 1024;
    }

    // ─────────────────────────── 清理 ───────────────────────────

    private static void Cleanup()
    {
        Section("E", "清理临时文件");
        foreach (var f in new[] { "hx.jpeg", "hx2.jpeg", "hx3.jpeg", "_poc_probe.jpeg" })
            Hdc($"shell \"rm -f {RemoteDir}/{f}\"");
        var (_, o, _) = Hdc($"shell \"ls {RemoteDir}/hx.jpeg {RemoteDir}/hx2.jpeg {RemoteDir}/_poc_probe.jpeg 2>&1\"");
        Console.WriteLine("  · 设备端残留检查: " + o.Replace("\r", "").Replace("\n", " ").Trim());

        Hdc($"fport rm tcp:{_c.LocalPort} localabstract:{AbstractSocket}");
        Console.WriteLine($"  · 已撤 fport tcp:{_c.LocalPort}");

        if (!_c.KeepDaemon && _daemonPid > 0)
        {
            Hdc($"shell \"kill -9 {_daemonPid}\"");
            Console.WriteLine($"  · 已 kill -9 本 POC 的 daemon pid={_daemonPid}（SIGTERM 被设备拒绝，必须 -9）");
        }
        else if (_daemonPid > 0)
        {
            Console.WriteLine($"  · 保留 daemon pid={_daemonPid}（--keep-daemon）");
        }
    }

    // ─────────────────────────── 基础设施 ───────────────────────────

    private static (int code, string stdout, string stderr) Hdc(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _c.Hdc,
            Arguments = $"-t {_c.Device} {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        try
        {
            using var p = Process.Start(psi)!;
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return (-1, so, se + " [timeout 30s]"); }
            return (p.ExitCode, so, se);
        }
        catch (Exception ex) { return (-2, "", ex.Message); }
    }

    private static string Md5File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
    }

    // ─────────────────────────── 离线分析模式（不碰设备） ───────────────────────────

    private static int AnalyzeMode()
    {
        Section("A", "离线分析 samples/（不访问设备）");
        string ex = Path.Combine(_c.Samples, "extradata.bin");
        string st = Path.Combine(_c.Samples, "stream.h264");
        if (!File.Exists(ex) || !File.Exists(st))
        {
            Console.WriteLine("  ✗ 缺少 samples/extradata.bin 或 stream.h264，先跑一次完整 POC");
            return 2;
        }

        byte[] extra = File.ReadAllBytes(ex);
        byte[] body = File.ReadAllBytes(st);
        var full = new byte[extra.Length + body.Length];
        Buffer.BlockCopy(extra, 0, full, 0, extra.Length);
        Buffer.BlockCopy(body, 0, full, extra.Length, body.Length);

        Console.WriteLine($"  · extradata.bin = {extra.Length} B  md5={Md5File(ex)}");
        Console.WriteLine($"  · stream.h264   = {body.Length:N0} B  md5={Md5File(st)}");
        Console.WriteLine($"  · 合并 full.h264= {full.Length:N0} B");

        // 起始码统计 + NAL 类型分布
        var hist = new SortedDictionary<int, int>();
        var starts = new List<int>();
        for (int i = 0; i + 4 < full.Length; i++)
            if (full[i] == 0 && full[i + 1] == 0 && full[i + 2] == 0 && full[i + 3] == 1)
            { starts.Add(i); hist[(full[i + 4] & 0x1F)] = hist.GetValueOrDefault(full[i + 4] & 0x1F) + 1; }

        string NalName(int t) => t switch
        {
            1 => "非IDR切片(P)", 5 => "IDR切片", 6 => "SEI", 7 => "SPS", 8 => "PPS", 9 => "AUD", _ => "其他"
        };
        Console.WriteLine($"  · 起始码 {starts.Count} 个，NAL 分布：");
        foreach (var kv in hist) Console.WriteLine($"      type {kv.Key,-2} {NalName(kv.Key),-12} × {kv.Value}");

        // 解 SPS
        int spsIdx = -1;
        for (int i = 0; i < starts.Count; i++)
        {
            int off = starts[i] + 4;
            if (off < full.Length && (full[off] & 0x1F) == 7) { spsIdx = i; break; }
        }
        if (spsIdx >= 0)
        {
            int s = starts[spsIdx] + 4;
            int e = spsIdx + 1 < starts.Count ? starts[spsIdx + 1] : full.Length;
            var nal = full[s..e];
            try
            {
                var sps = Sps.Parse(nal);
                Console.WriteLine("  · SPS 解析：");
                Console.WriteLine($"      profile_idc={sps.ProfileIdc} ({Sps.ProfileName(sps.ProfileIdc)})  level_idc={sps.LevelIdc} (Level {sps.LevelIdc / 10.0:F1})");
                Console.WriteLine($"      chroma_format_idc={sps.ChromaFormatIdc}  bit_depth_luma={sps.BitDepthLuma}  bit_depth_chroma={sps.BitDepthChroma}");
                Console.WriteLine($"      frame_mbs_only={sps.FrameMbsOnly}  crop L/R/T/B={sps.CropLeft}/{sps.CropRight}/{sps.CropTop}/{sps.CropBottom}");
                Console.WriteLine($"      ★ 分辨率 = {sps.Width} x {sps.Height}");
                Console.WriteLine("      → 与设备 snapshot_display 的 1260x2720 对比，若一致即证明码流真实可用");
            }
            catch (Exception ex2) { Console.WriteLine("  ✗ SPS 解析失败: " + ex2.Message); }
        }
        else Console.WriteLine("  ✗ 未找到 SPS NAL");

        // 时间戳单调性 / IDR 位置
        Console.WriteLine($"  · IDR 数量 = {hist.GetValueOrDefault(5)}, P 数量 = {hist.GetValueOrDefault(1)}");
        Console.WriteLine("  ✔ 离线分析完成");
        return 0;
    }

    private static void Section(string tag, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── [{tag}] {title} ─────────────────────────────");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private static Cfg ParseArgs(string[] a)
    {
        var c = new Cfg();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--device": c.Device = a[++i]; break;
                case "--local-port": c.LocalPort = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--hdc": c.Hdc = a[++i]; break;
                case "--jar": c.Jar = a[++i]; break;
                case "--samples": c.Samples = a[++i]; break;
                case "--max-frames": c.MaxFrames = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--timeout": c.TimeoutSec = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--so-entry": c.SoEntry = a[++i]; break;
                case "--dump": c.Dump = true; break;
                case "--keep-daemon": c.KeepDaemon = true; break;
                case "--no-interact": c.Interact = false; break;
                case "--analyze": c.Analyze = true; break;
                case "-h":
                case "--help": PrintHelp(); Environment.Exit(0); break;
            }
        }
        return c;
    }

    private static void PrintHelp() => Console.WriteLine(
        "hncast-poc [--device ip:port] [--local-port N] [--hdc path] [--jar path] [--samples dir]\n" +
        "           [--max-frames N] [--timeout sec] [--so-entry zip/name.so]\n" +
        "           [--dump] [--keep-daemon] [--no-interact] [--analyze]\n" +
        "\n" +
        "  --analyze    仅离线分析 samples/ 下已落盘的数据（解 SPS、统计 NAL），不访问设备\n" +
        "  --no-interact  收流中途不做 swipe 注入\n" +
        "  --keep-daemon  结束后不 kill 本 POC 起的 uitest daemon");
}

// ─────────────────────────── 手写 protobuf wire 解析 ───────────────────────────
// 说明：上游 protobuf 生成类被 ProGuard 混淆，reference/ 内无 .proto，字段号无法引用。
// 本类按 protobuf wire format 直接解析，字段号由 2026-11-11 真机 dump 实测确定：
//
//   ReplyMessage.payload          = field 3, wire 2   （不是文档推测的 1）
//   map entry.key                 = field 1, wire 2   (string)
//   map entry.value               = field 2, wire 2   (ParamValue)
//   ParamValue 的整型载荷          = field 1, wire 0   (varint) → flags / pts / len
//   ParamValue 的字节载荷          = field 5, wire 2   (bytes)  → data (Annex B H.264)
//
// 实测报文（首条）：
//   1A 0B 0A 05 "flags" 12 02 08 08
//   1A 2A 0A 04 "data"  12 22 2A 20 <32B: 00000001 67... 00000001 68...>
//   1A 0E 0A 03 "pts"   12 07 08 AF F7 EC B1 9C 34
//   1A 09 0A 03 "len"   12 02 08 20
internal static class Proto
{
    private const int FieldPayload = 3;      // ReplyMessage.payload
    private const int FieldEntryKey = 1;     // map entry key
    private const int FieldEntryValue = 2;   // map entry value
    private const int FieldValLong = 1;      // ParamValue 整型
    private const int FieldValBytes = 5;     // ParamValue 字节

    internal readonly record struct Tag(int Field, int WireType);
    internal sealed class ParamValue
    {
        public byte[]? Blob;
        public long? Num;
        public List<Tag> TagMap = new();
    }

    internal static Dictionary<string, ParamValue> ParseMap(byte[] buf)
    {
        var result = new Dictionary<string, ParamValue>();
        foreach (var (field, wire, payload) in Walk(buf))
        {
            if (field != FieldPayload || wire != 2 || payload == null) continue;
            string? key = null; byte[]? valBytes = null;
            foreach (var (f2, w2, p2) in Walk(payload))
            {
                if (f2 == FieldEntryKey && w2 == 2) key = Encoding.UTF8.GetString(p2!);
                else if (f2 == FieldEntryValue && w2 == 2) valBytes = p2;
            }
            if (key == null) continue;

            var pv = new ParamValue();
            if (valBytes != null)
            {
                foreach (var (f3, w3, p3) in Walk(valBytes))
                {
                    pv.TagMap.Add(new Tag(f3, w3));
                    if (f3 == FieldValBytes && w3 == 2 && p3 != null) pv.Blob = p3;
                    else if (f3 == FieldValLong && w3 == 0 && p3 != null) pv.Num = ReadVarint(p3);
                }
            }
            result[key] = pv;
        }
        return result;
    }

    internal static byte[] GetBlob(Dictionary<string, ParamValue> m, string key)
        => m.TryGetValue(key, out var v) && v.Blob != null ? v.Blob : Array.Empty<byte>();

    internal static long? GetNum(Dictionary<string, ParamValue> m, string key)
        => m.TryGetValue(key, out var v) ? v.Num : null;

    /// 遍历一层字段：返回 (field, wireType, payload)，varint 型 payload = 该 varint 的原始字节
    private static List<(int field, int wire, byte[]? payload)> Walk(byte[] b)
    {
        var list = new List<(int, int, byte[]?)>();
        int i = 0;
        while (i < b.Length)
        {
            ulong key = ReadVarintAt(b, ref i);
            if (key == 0 && i >= b.Length) break;
            int field = (int)(key >> 3), wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                {
                    int start = i;
                    ReadVarintAt(b, ref i);
                    list.Add((field, wire, b[start..i]));
                    break;
                }
                case 1: if (i + 8 > b.Length) return list; list.Add((field, wire, b[i..(i + 8)])); i += 8; break;
                case 2:
                {
                    ulong lenU = ReadVarintAt(b, ref i);
                    int len = (int)lenU;
                    if (len < 0 || i + len > b.Length) return list;
                    list.Add((field, wire, b[i..(i + len)]));
                    i += len;
                    break;
                }
                case 5: if (i + 4 > b.Length) return list; list.Add((field, wire, b[i..(i + 4)])); i += 4; break;
                default: return list;   // 3/4 group 不支持，直接停
            }
        }
        return list;
    }

    private static ulong ReadVarintAt(byte[] b, ref int i)
    {
        ulong v = 0; int shift = 0;
        while (i < b.Length && shift < 64)
        {
            byte x = b[i++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }

    private static long ReadVarint(byte[] b) { int i = 0; return (long)ReadVarintAt(b, ref i); }
}

// ─────────────────────────── 最小 SPS 解析（H.264） ───────────────────────────
// 用途：不依赖 ffmpeg / 解码器即可从码流本身拿到分辨率与 profile，
// 既用于 POC 自证（码流真实可解），也是正式客户端建解码器前必须的一步。
internal sealed class Sps
{
    public int ProfileIdc, LevelIdc, ChromaFormatIdc = 1;
    public int BitDepthLuma = 8, BitDepthChroma = 8;
    public int Width, Height;
    public bool FrameMbsOnly = true;
    public int CropLeft, CropRight, CropTop, CropBottom;

    public static string ProfileName(int p) => p switch
    {
        66 => "Baseline", 77 => "Main", 88 => "Extended",
        100 => "High", 110 => "High 10", 122 => "High 4:2:2", 244 => "High 4:4:4",
        _ => "other"
    };

    public static Sps Parse(byte[] nal)
    {
        // nal[0] 是 NAL header（0x67），RBSP 从 nal[1] 开始
        // 去 emulation prevention (00 00 03 → 00 00)
        var rbsp = new List<byte>(nal.Length);
        int zeros = 0;
        for (int k = 1; k < nal.Length; k++)
        {
            byte b = nal[k];
            if (zeros >= 2 && b == 0x03) { zeros = 0; continue; }
            rbsp.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        var r = new BitReader(rbsp.ToArray());

        var sps = new Sps();
        sps.ProfileIdc = (int)r.U(8);
        r.U(8);                                  // constraint flags
        sps.LevelIdc = (int)r.U(8);
        r.Ue();                                  // seq_parameter_set_id

        if (sps.ProfileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            sps.ChromaFormatIdc = (int)r.Ue();
            if (sps.ChromaFormatIdc == 3) r.U(1);            // separate_colour_plane_flag
            sps.BitDepthLuma = (int)r.Ue() + 8;
            sps.BitDepthChroma = (int)r.Ue() + 8;
            r.U(1);                                          // qpprime_y_zero_transform_bypass_flag
            if (r.U(1) == 1)                                 // seq_scaling_matrix_present_flag
            {
                int count = sps.ChromaFormatIdc != 3 ? 8 : 12;
                for (int i = 0; i < count; i++)
                    if (r.U(1) == 1) SkipScalingList(r, i < 6 ? 16 : 64);
            }
        }

        r.Ue();                                              // log2_max_frame_num_minus4
        int pocType = (int)r.Ue();
        if (pocType == 0) r.Ue();                            // log2_max_pic_order_cnt_lsb_minus4
        else if (pocType == 1)
        {
            r.U(1); r.Se(); r.Se();
            int n = (int)r.Ue();
            for (int i = 0; i < n; i++) r.Se();
        }
        r.Ue();                                              // max_num_ref_frames
        r.U(1);                                              // gaps_in_frame_num_value_allowed_flag
        int wMbs = (int)r.Ue() + 1;
        int hMapUnits = (int)r.Ue() + 1;
        sps.FrameMbsOnly = r.U(1) == 1;
        if (!sps.FrameMbsOnly) r.U(1);                       // mb_adaptive_frame_field_flag
        r.U(1);                                              // direct_8x8_inference_flag
        if (r.U(1) == 1)
        {
            sps.CropLeft = (int)r.Ue();
            sps.CropRight = (int)r.Ue();
            sps.CropTop = (int)r.Ue();
            sps.CropBottom = (int)r.Ue();
        }

        int subW = sps.ChromaFormatIdc == 3 ? 1 : 2;
        int subH = sps.ChromaFormatIdc == 1 ? 2 : 1;
        int cropUnitX = sps.ChromaFormatIdc == 0 ? 1 : subW;
        int cropUnitY = (sps.ChromaFormatIdc == 0 ? 1 : subH) * (2 - (sps.FrameMbsOnly ? 1 : 0));

        sps.Width = wMbs * 16 - cropUnitX * (sps.CropLeft + sps.CropRight);
        sps.Height = (2 - (sps.FrameMbsOnly ? 1 : 0)) * hMapUnits * 16 - cropUnitY * (sps.CropTop + sps.CropBottom);
        return sps;
    }

    private static void SkipScalingList(BitReader r, int size)
    {
        int last = 8, next = 8;
        for (int j = 0; j < size; j++)
        {
            if (next != 0)
            {
                int delta = (int)r.Se();
                next = (last + delta + 256) % 256;
            }
            last = next == 0 ? last : next;
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _d; private int _bit;
        public BitReader(byte[] d) { _d = d; }
        public uint U(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                int byteIdx = _bit >> 3, bitIdx = 7 - (_bit & 7);
                if (byteIdx >= _d.Length) throw new EndOfStreamException("SPS 越界");
                v = (v << 1) | (uint)((_d[byteIdx] >> bitIdx) & 1);
                _bit++;
            }
            return v;
        }
        public uint Ue()
        {
            int zeros = 0;
            while (U(1) == 0) { zeros++; if (zeros > 32) throw new InvalidDataException("ue 过大"); }
            return zeros == 0 ? 0 : ((1u << zeros) - 1) + U(zeros);
        }
        public int Se()
        {
            uint k = Ue();
            int v = (int)((k + 1) >> 1);
            return (k & 1) == 1 ? v : -v;
        }
    }
}
