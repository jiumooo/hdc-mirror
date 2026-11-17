using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HdcMirror.Core.Diagnostics;
using HdcMirror.Core.Hdc;

namespace HdcMirror.App.Services;

/// <summary>前台应用的包名与显示名。</summary>
/// <param name="Package">包名，例如 <c>com.kylin.read</c>。</param>
/// <param name="Label">应用显示名；读不到时为空串。</param>
public sealed record ForegroundApp(string Package, string Label)
{
    /// <summary>界面展示用：优先应用名，读不到就用包名。</summary>
    public string Display => string.IsNullOrWhiteSpace(Label) ? Package : Label;
}

/// <summary>
/// 读手机当前前台正在使用的应用。
/// <para>
/// 判据与镜像窗口一致：<c>aa dump -r</c> 里**进程级**的 <c>state #FOREGROUND</c>，且不是系统壳。
/// mission 列表不能用来判前台 —— 实测（HarmonyOS NEXT）人已经在桌面时，mission 里仍留着上一次
/// 用过的应用，而它的进程早就没了；只有 sceneboard 等系统壳进程是 FOREGROUND。
/// </para>
/// <para>
/// 应用显示名只能靠 <c>bm dump -n &lt;包名&gt; -l</c> 一行拿到。不带 <c>-l</c> 的输出是上千行 JSON，
/// 里面的 <c>label</c> 是资源名 <c>$string:app_name</c>，不是能显示给人看的文字。
/// </para>
/// </summary>
public static class ForegroundAppProbe
{
    /// <summary>系统壳进程名片段：出现在 state #FOREGROUND 里也不算「用户正在用的应用」。</summary>
    private static readonly string[] ShellNames =
    {
        "sceneboard",
        "launcher",
        "SystemUI",
        "ailifesvc",
        "hiview",
        "shell_assistant",
        "mediacontroller",
    };

    /// <summary>读一次前台应用；没有设备、或读不出来时返回 null。</summary>
    /// <param name="log">日志出口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>前台应用；读不到返回 null。</returns>
    public static async Task<ForegroundApp?> ProbeAsync(ILogSink log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);

        foreach (SavedDevice saved in new DeviceStore().Load())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(saved.Endpoint))
            {
                continue;
            }

            try
            {
                var runner = new HdcRunner(
                    deviceSerial: saved.Endpoint,
                    log: log,
                    commandTimeout: TimeSpan.FromSeconds(6));
                var result = await runner.ShellAsync("aa dump -r", cancellationToken).ConfigureAwait(false);
                string? package = ExtractForegroundPackage(result.CombinedOutput);
                if (string.IsNullOrEmpty(package))
                {
                    // 快应用 / 元服务在进程级列表里完全不出现（实测 com.kylin.read 连 BACKGROUND 都没有），
                    // 只有 mission 列表能看出它在屏。
                    package = await ProbeMissionBundleAsync(runner, log, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(package))
                {
                    continue;
                }

                string label = await ReadLabelAsync(runner, package, cancellationToken).ConfigureAwait(false);
                return new ForegroundApp(package, label);
            }
            catch (HdcException ex)
            {
                log.Debug("foreground", "读前台应用失败：" + ex.Message);
            }
        }

        return null;
    }

    /// <summary>单独查一个已记设备的包名对应应用名（给设置页的「应用专属滑动」名单用）；读不到返回空串。</summary>
    /// <param name="package">包名，例如 <c>com.phoenix.read.next</c>。</param>
    /// <param name="log">日志出口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用显示名；读不到返回空串。</returns>
    public static async Task<string> ReadLabelForPackageAsync(string package, ILogSink log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (string.IsNullOrWhiteSpace(package))
        {
            return string.Empty;
        }

        foreach (SavedDevice saved in new DeviceStore().Load())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(saved.Endpoint))
            {
                continue;
            }

            try
            {
                var runner = new HdcRunner(
                    deviceSerial: saved.Endpoint,
                    log: log,
                    commandTimeout: TimeSpan.FromSeconds(6));
                string label = await ReadLabelAsync(runner, package, cancellationToken).ConfigureAwait(false);
                if (label.Length > 0)
                {
                    return label;
                }
            }
            catch (HdcException ex)
            {
                log.Debug("foreground", "读应用名失败：" + ex.Message);
            }
        }

        return string.Empty;
    }

    /// <summary>应用显示名；读不到返回空串（调用方退回显示包名）。</summary>
    private static async Task<string> ReadLabelAsync(HdcRunner runner, string package, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.ShellAsync("bm dump -n " + package + " -l", cancellationToken).ConfigureAwait(false);
            string label = result.CombinedOutput.Trim();

            // 读失败时 bm 会把 "error: ..." 打到输出里，别把它当成应用名显示出去。
            if (!result.Succeeded || label.Length == 0 || label.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return label;
        }
        catch (HdcException)
        {
            return string.Empty;
        }
    }

    /// <summary>从 <c>aa dump -r</c> 的进程级 <c>state #FOREGROUND</c> 里挑出真实应用的包名。</summary>
    /// <param name="output"><c>aa dump -r</c> 的原始输出。</param>
    /// <returns>前台真实应用的包名；只有系统壳在前台时返回 <c>null</c>。</returns>
    internal static string? ExtractForegroundPackage(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        string? current = null;
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("process name [", StringComparison.Ordinal))
            {
                int end = line.IndexOf(']', 14);
                current = end > 14 ? line.Substring(14, end - 14).Trim() : null;
                continue;
            }

            if (current is null || !line.StartsWith("state #FOREGROUND", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsShellProcess(current))
            {
                continue;
            }

            // 进程名可能带子进程后缀（com.foo:remote），取冒号前的包名。
            int colon = current.IndexOf(':');
            return colon > 0 ? current.Substring(0, colon) : current;
        }

        return null;
    }

    /// <summary>用 <c>aa dump -a</c> 的 mission 状态读「屏幕上此刻真正显示的应用」的包名。</summary>
    /// <remarks>
    /// 快应用 / 元服务在 <c>aa dump -r</c> 里根本没有自己的进程：实测屏幕上正播着
    /// <c>com.kylin.read</c>，而 <c>-r</c> 的 43 个进程里连 BACKGROUND 都没有它，
    /// 只有 <c>aa dump -a</c> 的 mission 列表能看到它 <c>state #FOREGROUND</c>。
    /// 桌面态实测：没有任何 mission 是 FOREGROUND（最多一条 ACTIVE，如应用市场），
    /// 所以「有非系统壳的 FOREGROUND mission」＝在应用里，否则＝在桌面。
    /// 一次调用约 340~380 毫秒（输出 98 KB）。不要换回 uitest dumpLayout：
    /// 那个接口在投屏守护进程运行时实测会挂到 30 秒以上超时。
    /// </remarks>
    /// <param name="runner">已绑定设备的 hdc 通道。</param>
    /// <param name="log">日志出口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>屏幕上真实应用的包名；在桌面时返回 <c>null</c>。</returns>
    public static async Task<string?> ProbeMissionBundleAsync(HdcRunner runner, ILogSink log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(log);

        HdcResult result = await runner.ShellAsync("aa dump -a", cancellationToken).ConfigureAwait(false);
        string? package = ExtractForegroundMission(result.CombinedOutput);
        if (package is null)
        {
            log.Debug("foreground", $"aa dump -a 里没有前台 mission（输出 {result.CombinedOutput.Length} 字符）");
        }

        return package;
    }

    /// <summary>从 <c>aa dump -a</c> 的 mission 列表里挑出 <c>state #FOREGROUND</c> 的那个应用包名。</summary>
    /// <param name="output"><c>aa dump -a</c> 的原始输出。</param>
    /// <returns>包名；没有任何 mission 处于前台，或前台的是系统壳时返回 <c>null</c>。</returns>
    internal static string? ExtractForegroundMission(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        const string marker = "mission name #[#";
        string? current = null;
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                current = null;
                int start = at + marker.Length;
                int colon = line.IndexOf(':', start);
                if (colon > start)
                {
                    current = line.Substring(start, colon - start).Trim();
                }

                continue;
            }

            if (current is null || !line.StartsWith("state #", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.StartsWith("state #FOREGROUND", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }

            if (!IsShellProcess(current))
            {
                return current;
            }

            current = null;
        }

        return null;
    }

    /// <summary>系统壳进程：桌面、系统界面、常驻服务子能力，都不算「用户正在用的应用」。</summary>
    private static bool IsShellProcess(string name)
    {
        foreach (string shell in ShellNames)
        {
            if (name.Contains(shell, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
