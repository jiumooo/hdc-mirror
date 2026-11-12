using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using HdcMirror.Core;
using System.Windows;
using System.Windows.Controls;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;

namespace HdcMirror.App.Views;

/// <summary>
/// 通用设置页：行为（关闭窗口时最小化到托盘）。
/// 改动即存，写进 <c>%LOCALAPPDATA%\HdcMirror\settings.json</c>。
/// 画质与音频在「音画与音频」页（<see cref="AvPage"/>）。
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;
    private bool _loading;

    /// <summary>初始化通用设置页，并把当前配置回填到控件。</summary>
    public SettingsPage()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            CloseToTrayBox.IsChecked = _settings.CloseToTray;
            AutoScanBox.IsChecked = _settings.AutoScanOnStart;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnCloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.Save();
        SavedText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0}：关闭到托盘 {1}",
            Strings.Settings_EffectiveHint,
            _settings.CloseToTray ? "是" : "否");
        SavedText.Opacity = 0.65;
    }

    private void OnAutoScanChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AutoScanOnStart = AutoScanBox.IsChecked == true;
        _settings.Save();
        SavedText.Text = Strings.Settings_EffectiveHint;
        SavedText.Opacity = 0.65;
    }

    /// <summary>在资源管理器里打开本机数据目录（设备列表、设置、日志、锁屏缓存都在这）。</summary>
    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        string root = AppPaths.DataRoot;
        Directory.CreateDirectory(root);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SavedText.Text = ex.Message;
            SavedText.Opacity = 0.65;
        }
    }
}
