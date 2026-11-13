using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.App.Views;

/// <summary>
/// 画面页：清晰度与流畅度预设、帧率、码率、锁屏画面。
/// 改动即存，写进 <c>%APPDATA%\HdcMirror\settings.json</c>；帧率与码率在下次建立镜像会话时生效。
/// </summary>
public partial class VideoPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;

    private bool _loading;

    /// <summary>初始化画面页，并把当前配置回填到控件。</summary>
    public VideoPage()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            BuildFrameRateChoices();
            FrameRateBox.SelectedIndex = IndexOfFrameRate(_settings.FrameRate);
            FrameRateDetail.Text = DisplayModes.Describe();
            BitRateBox.SelectedIndex = IndexOfBitRate(_settings.BitRateMbps);
            PresetBox.SelectedIndex = IndexOfPreset();
            UpdatePresetDetail();
            LockShotBox.IsChecked = _settings.LockScreenCapture;
        }
        finally
        {
            _loading = false;
        }

        _ = RefreshFrameRateAsync();
    }

    /// <summary>
    /// 打开本页时顺手读一次手机刷新率：没连接过也能显示真机档位（30/60/90/120）。
    /// 读不到就保持「自动 + 30/60」，不装作知道。
    /// </summary>
    private async Task RefreshFrameRateAsync()
    {
        try
        {
            if (!await DisplayModes.EnsureLoadedAsync(new FileLogSink()).ConfigureAwait(true))
            {
                return;
            }

            _loading = true;
            try
            {
                int keep = _settings.FrameRate;
                BuildFrameRateChoices();
                FrameRateBox.SelectedIndex = IndexOfFrameRate(keep);
                FrameRateDetail.Text = DisplayModes.Describe();
            }
            finally
            {
                _loading = false;
            }
        }
        catch (Exception ex) when (ex is HdcMirror.Core.Hdc.HdcException or InvalidOperationException)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    /// <summary>按设备支持的最高刷新率、以 30 为一档填档位；第 0 项固定是「自动」。</summary>
    private void BuildFrameRateChoices()
    {
        FrameRateBox.Items.Clear();
        FrameRateBox.Items.Add(new ComboBoxItem { Content = Strings.Settings_FrameRateAuto });
        foreach (int rate in DisplayModes.Choices())
        {
            FrameRateBox.Items.Add(new ComboBoxItem
            {
                Content = rate.ToString(CultureInfo.CurrentCulture) + " Hz",
            });
        }
    }

    private static int IndexOfFrameRate(int frameRate)
    {
        if (frameRate <= 0)
        {
            return 0;
        }

        IReadOnlyList<int> choices = DisplayModes.Choices();
        for (int i = 0; i < choices.Count; i++)
        {
            if (choices[i] == frameRate)
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static int FrameRateAt(int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        IReadOnlyList<int> choices = DisplayModes.Choices();
        int position = index - 1;
        return position < choices.Count ? choices[position] : 0;
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

        _settings.FrameRate = FrameRateAt(FrameRateBox.SelectedIndex);
        SyncPresetFromSettings();
        Persist();
    }

    private void OnBitRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.BitRateMbps = BitRateAt(BitRateBox.SelectedIndex);
        SyncPresetFromSettings();
        Persist();
    }

    private void OnLockShotChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.LockScreenCapture = LockShotBox.IsChecked == true;
        _settings.Save();
        LockShotText.Text = _settings.LockScreenCapture
            ? Strings.Settings_LockShotOn
            : Strings.Settings_LockShotOff;
    }

    /// <summary>立刻抓一张（不受定时器与「是否镜像中」限制），结果显示在按钮右边。</summary>
    private async void OnCaptureNowClick(object sender, RoutedEventArgs e)
    {
        LockShotText.Text = Strings.Settings_LockShotBusy;
        string message;
        try
        {
            message = Window.GetWindow(this) is MainWindow owner
                ? await owner.CaptureLockScreenNowAsync()
                : Strings.Settings_LockShotNoDevice;
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        LockShotText.Text = message;
    }

    private void Persist()
    {
        _settings.Save();
        string rate = _settings.EffectiveFrameRate > 0
            ? _settings.EffectiveFrameRate.ToString(CultureInfo.CurrentCulture) + " Hz"
            : Strings.Settings_FrameRateAuto;
        SavedText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Settings_EffectiveSummaryFormat,
            Strings.Settings_EffectiveHint,
            rate,
            _settings.BitRateMbps);
        SavedText.Opacity = 0.65;
    }

    /// <summary>
    /// 四个预设的取值：(码率 Mbps, 帧率上限 Hz；0 = 自动跟随手机)。
    /// 顺序必须与 <c>PresetBox</c> 的前四项一致：超清 / 高清 / 均衡 / 流畅。
    /// </summary>
    private static readonly (int Mbps, int FrameRate)[] Presets =
    {
        (24, 0),
        (16, 0),
        (8, 0),
        (4, 30),
    };

    /// <summary>选预设：一次改帧率与码率，并回填两个下拉。</summary>
    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetBox.SelectedIndex < 0)
        {
            return;
        }

        int index = PresetBox.SelectedIndex;
        if (index >= Presets.Length)
        {
            UpdatePresetDetail();
            return;
        }

        var preset = Presets[index];
        _settings.BitRateMbps = preset.Mbps;
        _settings.FrameRate = preset.FrameRate;

        _loading = true;
        try
        {
            BitRateBox.SelectedIndex = IndexOfBitRate(preset.Mbps);
            FrameRateBox.SelectedIndex = IndexOfFrameRate(preset.FrameRate);
        }
        finally
        {
            _loading = false;
        }

        Persist();
    }

    /// <summary>当前帧率与码率命中了哪个预设；都不命中就返回「自定义」的下标。</summary>
    private int IndexOfPreset()
    {
        for (int i = 0; i < Presets.Length; i++)
        {
            if (Presets[i].Mbps == _settings.BitRateMbps && Presets[i].FrameRate == _settings.FrameRate)
            {
                return i;
            }
        }

        return Presets.Length;
    }

    /// <summary>把当前档位的实际数值写到下拉下面，避免"档位名看不到具体参数"。</summary>
    private void UpdatePresetDetail()
    {
        string rate = _settings.EffectiveFrameRate > 0
            ? _settings.EffectiveFrameRate.ToString(CultureInfo.CurrentCulture) + " Hz"
            : Strings.Settings_FrameRateAuto;
        PresetDetail.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0} Mbps · {1}",
            _settings.BitRateMbps,
            rate);
    }

    /// <summary>手动改帧率 / 码率后，把预设下拉同步到对应档或「自定义」。</summary>
    private void SyncPresetFromSettings()
    {
        int index = IndexOfPreset();
        UpdatePresetDetail();
        if (PresetBox.SelectedIndex == index)
        {
            return;
        }

        _loading = true;
        try
        {
            PresetBox.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }

        UpdatePresetDetail();
    }
}