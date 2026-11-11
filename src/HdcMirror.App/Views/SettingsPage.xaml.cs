using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;

namespace HdcMirror.App.Views;

/// <summary>
/// 设置页：画质（帧率 / 码率）、行为（关闭到托盘）、音频（连接后自动开）。
/// 所有改动即改即存，写进 <c>%LOCALAPPDATA%\HdcMirror\settings.json</c>，
/// 帧率与码率在下次建立镜像会话时生效（设备侧 daemon 不接受热改）。
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;
    private bool _loading;

    /// <summary>初始化设置页，并把当前配置回填到控件。</summary>
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
            FrameRateBox.SelectedIndex = _settings.EffectiveFrameRate >= 60 ? 1 : 0;
            BitRateBox.SelectedIndex = IndexOfBitRate(_settings.BitRateMbps);
            CloseToTrayBox.IsChecked = _settings.CloseToTray;
            AutoAudioBox.IsChecked = _settings.AutoStartAudio;
        }
        finally
        {
            _loading = false;
        }
    }

    private static int IndexOfBitRate(int mbps) => mbps switch
    {
        4 => 0,
        16 => 2,
        24 => 3,
        _ => 1,
    };

    private static int BitRateAt(int index) => index switch
    {
        0 => 4,
        2 => 16,
        3 => 24,
        _ => 8,
    };

    private void OnFrameRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.FrameRate = FrameRateBox.SelectedIndex == 0 ? 30 : 60;
        Persist();
    }

    private void OnBitRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.BitRateMbps = BitRateAt(BitRateBox.SelectedIndex);
        Persist();
    }

    private void OnCloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        Persist();
    }

    private void OnAutoAudioChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AutoStartAudio = AutoAudioBox.IsChecked == true;
        Persist();
    }

    private void Persist()
    {
        _settings.Save();
        SavedText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0}：帧率 {1} · 码率 {2} Mbps",
            Strings.Settings_EffectiveHint,
            _settings.EffectiveFrameRate,
            _settings.BitRateMbps);
        SavedText.Opacity = 0.65;
    }
}
