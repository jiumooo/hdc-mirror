using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HdcMirror.Core;
using HdcMirror.Core.Diagnostics;
using HdcMirror.Core.Hdc;

namespace HdcMirror.App.Services;

/// <summary>
/// 定时从手机抓一张当前画面，存成 <c>lockscreen.png</c>，供主窗口与镜像窗口在「未连接 / 无画面」时
/// 当作毛玻璃背景用。
///
/// 设计取舍：
/// <list type="bullet">
///   <item>默认 60 秒一次 —— 鸿蒙锁屏壁纸会轮换，抓太稀就永远停在旧画面；抓太密则设备侧 <c>uitest</c>
///   每轮要起一个约 2.6 秒的进程，白耗电。</item>
///   <item>**不点亮屏幕**：屏幕熄灭时 <c>uitest screenCap</c> 会返回
///   <c>ScreenCap failed: Failed to get display pixelMap</c>，这时只记调试日志，等下一轮，
///   绝不为了抓图去唤醒用户手机。</item>
///   <item>只在「未镜像」时抓 —— 镜像中画面本来就在传，再抓一张纯属浪费。</item>
/// </list>
/// </summary>
public sealed class LockScreenCapture : IDisposable
{
    /// <summary>设备侧临时文件（每次覆盖，抓完即删）。</summary>
    private const string RemotePath = "/data/local/tmp/hdcm-lock.jpeg";

    /// <summary>小于这个字节数基本可以断定不是有效照片。</summary>
    private const long MinBytes = 8 * 1024;

    private readonly ILogSink _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private string? _endpoint;

    /// <summary>初始化抓取器。</summary>
    public LockScreenCapture(ILogSink log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Directory.CreateDirectory(AppPaths.DataRoot);
    }

    /// <summary>抓到的文件路径（每成功一次触发一次）。</summary>
    public event Action<string>? Captured;

    /// <summary>缓存文件位置。</summary>
    public static string TargetPath => Path.Combine(AppPaths.DataRoot, "lockscreen.png");

    /// <summary>是否已经有可用的缓存图。</summary>
    public static bool HasCache => File.Exists(TargetPath);

    /// <summary>对指定设备开始定时抓取（重复调用会切到新设备）。</summary>
    public void Start(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(_endpoint, endpoint, StringComparison.OrdinalIgnoreCase) && _cts is not null)
            {
                return;
            }

            StopCore();
            _endpoint = endpoint;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token), CancellationToken.None);
            _log.Write(LogLevel.Debug, "LockShot", "开始定时抓取锁屏画面：" + endpoint);
        }
    }

    /// <summary>停止定时抓取。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    /// <summary>立刻抓一张（不看定时器，不受「是否镜像中」限制）。</summary>
    public async Task<bool> CaptureOnceAsync(CancellationToken cancellationToken = default)
    {
        string? endpoint;
        lock (_gate)
        {
            endpoint = _endpoint;
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var runner = new HdcRunner(deviceSerial: endpoint, log: _log, commandTimeout: TimeSpan.FromSeconds(15));

        var cap = await runner.ShellAsync("uitest screenCap -p " + RemotePath, cancellationToken).ConfigureAwait(false);
        if (!cap.Succeeded || cap.CombinedOutput.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            _log.Write(LogLevel.Debug, "LockShot", "截图未成功（多半是手机熄屏）：" + cap.CombinedOutput);
            return false;
        }

        var temp = Path.Combine(Path.GetTempPath(), "hdcm-lock-" + Guid.NewGuid().ToString("N") + ".jpeg");
        try
        {
            var recv = await runner.RunAsync(
                new[] { "file", "recv", RemotePath, temp },
                cancellationToken).ConfigureAwait(false);
            if (!recv.Succeeded || !File.Exists(temp))
            {
                _log.Write(LogLevel.Debug, "LockShot", "拉取截图失败：" + recv.CombinedOutput);
                return false;
            }

            var info = new FileInfo(temp);
            if (info.Length < MinBytes)
            {
                _log.Write(LogLevel.Debug, "LockShot", "截图过小，判定无效：" + info.Length + " B");
                return false;
            }

            // 换个可写名字再覆盖，避免 WPF 正在解码时被写坏。
            var staging = TargetPath + ".new";
            File.Copy(temp, staging, overwrite: true);
            File.Move(staging, TargetPath, overwrite: true);

            _log.Write(LogLevel.Info, "LockShot", "锁屏画面已更新：" + info.Length + " B");
            Captured?.Invoke(TargetPath);
            return true;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warn, "LockShot", "抓取锁屏画面异常：" + ex.Message);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // 临时文件删不掉不影响主流程。
            }

            _ = runner.ShellAsync("rm -f " + RemotePath, CancellationToken.None);
        }
    }

    /// <summary>释放定时器。</summary>
    public void Dispose()
    {
        Stop();
        _log.Write(LogLevel.Debug, "LockShot", "锁屏抓取已释放");
    }

    private void StopCore()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var seconds = AppSettings.Current.LockScreenIntervalSeconds;
            if (seconds < 15)
            {
                seconds = 15;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!AppSettings.Current.LockScreenCapture)
            {
                continue;
            }

            try
            {
                await CaptureOnceAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Warn, "LockShot", "定时抓取失败：" + ex.Message);
            }
        }
    }
}
