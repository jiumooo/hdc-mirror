using System.Text;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace HdcMirror.UpdatePoc;

/// <summary>
/// M5「应用内更新」可行性 POC。
///
/// 目的不是跑通一次真实更新，而是把 <b>四件事钉死</b>：
///   1. 真实部署路径下 <c>new UpdateManager(...)</c> 在「未经 vpk 安装的开发态」抛什么异常；
///   2. 借 <c>TestVelopackLocator</c>（官方定位为 CI/单测用的假 locator）绕开安装检查，
///      真正打到 GitHub，拿到「仓库还不存在」时 CheckForUpdatesAsync 的<b>真实异常类型与消息</b>；
///   3. CheckForUpdatesAsync / DownloadUpdatesAsync / ApplyUpdatesAndRestart 的真实签名；
///   4. HTTP 层旁证（GitHub API 与 releases.win.json 的状态码），用于判定正式版静默策略。
///
/// 全部异常原样打印类型全名 + Message + 内层链，不做包装、不吞。
/// 运行：C:\dotnet8\dotnet.exe run --project src\UpdatePoc\UpdatePoc.csproj
/// </summary>
internal static class Program
{
    /// <summary>占位仓库。正式接入时替换为真实 owner，且必须与 vpk pack 的 --channel 一致（默认 win）。</summary>
    private const string PlaceholderRepoUrl = "https://github.com/PLACEHOLDER_OWNER/hdc-mirror";

    /// <summary>Velopack 更新清单固定叫 releases.{channel}.json；Windows 通道即 releases.win.json。</summary>
    private const string ManifestUrl =
        PlaceholderRepoUrl + "/releases/latest/download/releases.win.json";

    private static readonly StringBuilder Log = new();

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Write("=== HdcMirror M5 应用内更新 POC ===");
        Write($"运行时版本 : {Environment.Version}");
        Write($"进程架构   : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Write($"占位仓库   : {PlaceholderRepoUrl}");
        Write("");

        // ---------------------------------------------------------------
        // 0. VelopackApp 必须在任何 UpdateManager 之前调用。
        //    安装/更新钩子被触发时它自行接管并退出；无钩子参数时立即返回。
        // ---------------------------------------------------------------
        Write("[0] VelopackApp.Build().Run()");
        try
        {
            VelopackApp.Build().Run();
            Write("    结果: 返回（无安装/更新钩子参数，符合预期）");
        }
        catch (Exception ex)
        {
            DumpException("    VelopackApp.Run 抛出", ex);
        }
        Write("");

        // ---------------------------------------------------------------
        // 1A. 真实部署路径：不注入 locator，按「正式版会怎么写」来构造
        // ---------------------------------------------------------------
        Write("[1A] new UpdateManager(new GithubSource(...))  —— 真实部署路径");
        var source = new GithubSource(PlaceholderRepoUrl, accessToken: null, prerelease: false);
        UpdateManager? prodManager = null;
        try
        {
            prodManager = new UpdateManager(source);
            Write("    结果: 构造成功");
            Describe(prodManager);
        }
        catch (Exception ex)
        {
            DumpException("    构造抛出", ex);
        }
        Write("");

        // ---------------------------------------------------------------
        // 1B. 诊断路径：注入 TestVelopackLocator，绕开「必须已安装」的前置，
        //     以便真正发起一次 GitHub 检查。正式版绝不这样写。
        // ---------------------------------------------------------------
        Write("[1B] new UpdateManager(source, null, new TestVelopackLocator(...))  —— 仅诊断用");
        UpdateManager? diagManager = null;
        var probeRoot = Path.Combine(Path.GetTempPath(), "HdcMirror-UpdatePoc");
        try
        {
            Directory.CreateDirectory(probeRoot);
            var packagesDir = Path.Combine(probeRoot, "packages");
            Directory.CreateDirectory(packagesDir);

            // 位置参数语义（实测）：appId, version, packagesDir, logger
            // 第 2 个参数会被语义版本解析，传目录会抛 ArgumentException("... is not a valid semantic version")
            var locator = new TestVelopackLocator("HdcMirror", "0.1.0", packagesDir, logger: null!);
            diagManager = new UpdateManager(source, options: null, locator: locator);
            Write("    结果: 构造成功");
            Describe(diagManager);
        }
        catch (Exception ex)
        {
            DumpException("    构造抛出", ex);
        }
        Write("");

        // ---------------------------------------------------------------
        // 2. CheckForUpdatesAsync —— 核心取证点
        // ---------------------------------------------------------------
        Write("[2] CheckForUpdatesAsync()");
        var candidates = new (string Label, UpdateManager? Manager)[]
        {
            ("prod · 真实 locator（未经 Setup.exe 安装的开发态）", prodManager),
            ("diag · TestVelopackLocator（仅诊断，正式版不用）", diagManager),
        };

        UpdateManager? manager = prodManager ?? diagManager;
        UpdateInfo? update = null;
        foreach (var (label, mgr) in candidates)
        {
            Write($"  ── {label}");
            if (mgr is null)
            {
                Write("    跳过: 该路径未取得 manager 实例");
                continue;
            }

            try
            {
                var found = await mgr.CheckForUpdatesAsync();
                if (found is null)
                {
                    Write("    结果: null —— 检查成功但无可用更新（已是最新）");
                }
                else
                {
                    Write($"    结果: 发现新版本 {found.TargetFullRelease.Version}");
                    Write($"    IsDowngrade: {found.IsDowngrade}");
                    Write($"    增量包 DeltasToTarget: {found.DeltasToTarget}");
                    update ??= found;
                    manager = mgr;
                }
            }
            catch (Exception ex)
            {
                DumpException("    检查抛出", ex);
                Write($"    异常归属判定: {Classify(ex)}");
            }
        }
        Write("");

        // ---------------------------------------------------------------
        // 3. 下载 + 应用（骨架）。占位仓库下走不到这里，代码保留为正式版接线模板。
        // ---------------------------------------------------------------
        Write("[3] DownloadUpdatesAsync / ApplyUpdatesAndRestart（骨架）");
        if (update is null || manager is null)
        {
            Write("    跳过: 没有 UpdateInfo，无法演示下载与重启");
            Write("    正式版接线形如:");
            Write("      if (info is not null) {");
            Write("          await mgr.DownloadUpdatesAsync(info, p => Report(p));      // Action<int> 0..100");
            Write("          mgr.ApplyUpdatesAndRestart(info);                          // 重启进新版本");
            Write("          // 或 mgr.ApplyUpdatesAndExit(info);                       // 只装不启，下次启动生效");
            Write("      }");
        }
        else
        {
            try
            {
                await manager.DownloadUpdatesAsync(
                    update,
                    p => Console.WriteLine($"    下载进度: {p}%"));
                Write("    下载完成，执行 ApplyUpdatesAndRestart");
                manager.ApplyUpdatesAndRestart(update);
                Write("    （进程通常在此被 Update.exe 接管并退出）");
            }
            catch (Exception ex)
            {
                DumpException("    下载/应用抛出", ex);
            }
        }
        Write("");

        // ---------------------------------------------------------------
        // 4. 旁证：不经 Velopack，直接打 GitHub API / 清单地址
        // ---------------------------------------------------------------
        await ProbeHttpAsync();
        Write("");

        Write("=== POC 结束 ===");

        var text = Log.ToString();
        Console.Write(text);
        TryPersistLog(text);
        return 0;
    }

    private static void Describe(UpdateManager mgr)
    {
        Write($"    IsInstalled       : {mgr.IsInstalled}");
        Write($"    IsPortable        : {mgr.IsPortable}");
        Write($"    AppId             : {mgr.AppId ?? "(null)"}");
        Write($"    CurrentVersion    : {mgr.CurrentVersion?.ToString() ?? "(null)"}");
        Write($"    UpdatePendingRestart: {mgr.UpdatePendingRestart?.Version?.ToString() ?? "(无)"}");
    }

    /// <summary>把异常归到「能拿来定策略」的类别上。</summary>
    private static string Classify(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            var n = cur.GetType().Name;
            switch (n)
            {
                case "NotInstalledException":
                    return "未安装态（Velopack 特有）→ 正式版必须先经 Setup.exe 安装才谈检查更新";
                case "HttpRequestException":
                    return "网络/HTTP 失败（含 404 仓库不存在）→ 属可预期失败，宜静默";
                case "TaskCanceledException":
                    return "超时/取消 → 属可预期失败，宜静默";
                case "AcquireLockFailedException":
                    return "更新锁被占用 → 静默跳过本轮";
                case "ChecksumFailedException":
                    return "校验失败 → 需保留旧版本并提示";
                default:
                    break;
            }
        }

        return "未归类 → 需人工看原文";
    }

    /// <summary>直接请求 GitHub API 与更新清单，记录 HTTP 层事实。</summary>
    private static async Task ProbeHttpAsync()
    {
        Write("[4] HTTP 旁证（不经 Velopack）");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HdcMirror-UpdatePoc/0.1");

        await ProbeOneAsync(http, "GitHub API releases/latest",
            "https://api.github.com/repos/PLACEHOLDER_OWNER/hdc-mirror/releases/latest");
        await ProbeOneAsync(http, "更新清单 releases.win.json", ManifestUrl);
    }

    private static async Task ProbeOneAsync(HttpClient http, string label, string url)
    {
        Write($"    → {label}");
        Write($"      {url}");
        try
        {
            using var resp = await http.GetAsync(url);
            Write($"      状态码: {(int)resp.StatusCode} {resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync();
            body = body.Replace("\r", " ").Replace("\n", " ").Trim();
            if (body.Length > 240)
            {
                body = body[..240] + "…";
            }

            Write($"      响应体: {body}");
        }
        catch (Exception ex)
        {
            DumpException("      请求抛出", ex);
        }
    }

    /// <summary>把异常的类型全名、Message、内层链原样打出来。</summary>
    private static void DumpException(string title, Exception ex)
    {
        Write($"{title}:");
        var depth = 0;
        for (var cur = ex; cur is not null && depth < 8; cur = cur.InnerException, depth++)
        {
            var pad = new string(' ', 4 + depth * 2);
            Write($"{pad}类型 : {cur.GetType().FullName}");
            Write($"{pad}消息 : {cur.Message}");
            if (cur is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    Write($"{pad}聚合 : {inner.GetType().FullName} / {inner.Message}");
                }
            }
        }
    }

    private static void Write(string line) => Log.AppendLine(line);

    /// <summary>日志同时落盘一份，便于把原文贴进报告；失败不影响退出码。</summary>
    private static void TryPersistLog(string text)
    {
        try
        {
            var dir = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "pack"));
            if (!Directory.Exists(dir))
            {
                return;
            }

            File.WriteAllText(Path.Combine(dir, "updatepoc-run.log"), text, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 落盘失败与结论无关
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
