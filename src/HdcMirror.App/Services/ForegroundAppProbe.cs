using System;
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
/// 判据与镜像窗口一致：<c>aa dump -a</c> 里**进程级**的 <c>state #FOREGROUND</c>，且不是系统壳。
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
                var result = await runner.ShellAsync("aa dump -a", cancellationToken).ConfigureAwait(false);
                string? package = ExtractForegroundPackage(result.CombinedOutput);
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

    /// <summary>从 <c>aa dump -a</c> 的进程级 <c>state #FOREGROUND</c> 里挑出真实应用的包名。</summary>
    private static string? ExtractForegroundPackage(string? output)
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
