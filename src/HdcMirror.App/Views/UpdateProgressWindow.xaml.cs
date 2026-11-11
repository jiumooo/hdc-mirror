using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;

namespace HdcMirror.App.Views;

/// <summary>
/// 更新下载进度窗口：把字节数、速度、剩余时间摊在界面上，
/// 同时把「取消」翻译成一个 <see cref="CancellationToken"/> 交给下载器。
/// 两种数据源都支持：自己下载时给字节数，Velopack 下载时只给百分比。
/// </summary>
public partial class UpdateProgressWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>创建一个进度窗口；<see cref="Progress"/> 已绑定到 UI 线程。</summary>
    public UpdateProgressWindow()
    {
        InitializeComponent();
        Progress = new Progress<UpdateService.DownloadProgress>(OnProgress);
    }

    /// <summary>交给下载器的字节级进度回调。</summary>
    public IProgress<UpdateService.DownloadProgress> Progress { get; }

    /// <summary>取消标记。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>用户是否按过取消。</summary>
    public bool Canceled => _cts.IsCancellationRequested;

    /// <summary>切换阶段文案（准备安装、重启）。</summary>
    /// <param name="text">要显示的文案。</param>
    public void SetPhase(string text)
    {
        DetailText.Text = text;
        SpeedText.Text = string.Empty;
        CancelButton.IsEnabled = false;
    }

    /// <summary>Velopack 只给百分比时用它画进度条。</summary>
    /// <param name="percent">0~100。</param>
    public void ReportPercent(int percent)
    {
        if (Canceled)
        {
            return;
        }

        int value = Math.Clamp(percent, 0, 100);
        Bar.Value = value;
        DetailText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Upd_ProgressOnlyPercentFormat,
            value);
        SpeedText.Text = string.Empty;
    }

    /// <summary>把一次字节级进度快照画到界面上。</summary>
    /// <param name="progress">下载器上报的快照。</param>
    private void OnProgress(UpdateService.DownloadProgress progress)
    {
        if (Canceled)
        {
            return;
        }

        Bar.Value = progress.Fraction * 100;

        string received = FormatBytes(progress.Received);
        string total = progress.Total > 0 ? FormatBytes(progress.Total) : "?";
        string percent = progress.Total > 0
            ? (progress.Fraction * 100).ToString("0", CultureInfo.CurrentCulture)
            : "?";

        DetailText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Upd_ProgressPercentFormat,
            received,
            total,
            percent);

        string speed = progress.BytesPerSecond > 0
            ? FormatBytes((long)progress.BytesPerSecond) + "/s"
            : "—";

        string remain = Strings.Upd_RemainUnknown;
        if (progress.BytesPerSecond > 1 && progress.Total > progress.Received)
        {
            var left = TimeSpan.FromSeconds((progress.Total - progress.Received) / progress.BytesPerSecond);
            remain = left.TotalMinutes >= 1
                ? string.Format(CultureInfo.CurrentCulture, "{0} 分 {1} 秒", (int)left.TotalMinutes, left.Seconds)
                : string.Format(CultureInfo.CurrentCulture, "{0} 秒", Math.Max(1, (int)left.TotalSeconds));
        }

        SpeedText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Upd_SpeedFormat, speed, remain);

        if (progress.ResumedFrom > 0)
        {
            HintText.Text = string.Format(
                CultureInfo.CurrentCulture,
                Strings.Upd_ResumeNoticeFormat,
                FormatBytes(progress.ResumedFrom));
        }
    }

    /// <summary>把字节数格式化成 MB / GB。</summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>形如 <c>82.2 MB</c> 的字符串。</returns>
    private static string FormatBytes(long bytes)
    {
        const double Megabyte = 1024d * 1024d;
        const double Gigabyte = Megabyte * 1024d;
        return bytes >= Gigabyte
            ? (bytes / Gigabyte).ToString("0.00", CultureInfo.CurrentCulture) + " GB"
            : (bytes / Megabyte).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
    }

    /// <summary>取消下载：只置标记，窗口由调用方关闭。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        DetailText.Text = Strings.Upd_CancelHint;
        SpeedText.Text = string.Empty;
    }
}
