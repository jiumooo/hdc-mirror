using System.Collections.Generic;
using System;
using System.Globalization;
using System.Windows;
using System.Diagnostics;
using System.Threading.Tasks;
using HdcMirror.Core.Diagnostics;
using System.Windows.Controls;
using HdcMirror.App.Audio;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;

namespace HdcMirror.App.Views;

/// <summary>
/// 音画与音频页：画质（帧率 / 码率）与音频（连接后自动开）。
/// 改动即存，写进 <c>%LOCALAPPDATA%\HdcMirror\settings.json</c>；
/// 帧率与码率在下次建立镜像会话时生效（设备侧 daemon 不接受热改）。
/// </summary>
public partial class AvPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;

    /// <summary>与接收器共用同一份日志出口，避免开两个句柄写同一个日志文件。</summary>
    private readonly ILogSink _log = AudioSinkHub.Instance.Log;

    /// <summary>下拉里除「系统默认输出」之外的设备，下标 = 下拉下标 - 1。</summary>
    private readonly List<AudioOutputDevice> _devices = new();

    private bool _loading;

    /// <summary>初始化音画页，并把当前配置回填到控件。</summary>
    public AvPage()
    {
        InitializeComponent();
        AudioSinkHub.Instance.StateChanged += OnAudioStateChanged;
        Unloaded += OnPageUnloaded;
        Load();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        AudioSinkHub.Instance.StateChanged -= OnAudioStateChanged;
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
            AutoAudioBox.IsChecked = _settings.AutoStartAudio;
            LockShotBox.IsChecked = _settings.LockScreenCapture;
            LockShotText.Text = _settings.LockScreenCapture
                ? Strings.Settings_LockShotOn
                : Strings.Settings_LockShotOff;

            FollowPhoneBox.IsChecked = _settings.AudioFollowPhone;
            VolumeSlider.Value = Math.Clamp(_settings.AudioAppVolume * 100d, 0d, 100d);
            UpdateVolumeHint();
        }
        finally
        {
            _loading = false;
        }

        BuildOutputDeviceChoices();
        RefreshAudioState();
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

    /// <summary>设置值 → 下拉序号：0 为自动，其余按档位序位。</summary>
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

    /// <summary>下拉序号 → 设置值：0 表示自动（存 0）。</summary>
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

    private void OnAutoAudioChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AutoStartAudio = AutoAudioBox.IsChecked == true;
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

    // ── 音频接收（手机 → 电脑 → 电脑默认输出设备）─────────────────

    private void OnAudioToggleClick(object sender, RoutedEventArgs e)
    {
        AudioSinkHost sink = AudioSinkHub.Instance;
        if (sink.IsRunning)
        {
            sink.Stop();
            return;
        }

        _ = sink.Start();
        RefreshAudioState();
    }

    private void OnOpenSoundSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AudioStateText.Text = ex.Message;
        }
    }

    /// <summary>
    /// 重新枚举播放设备并回填下拉：第 0 项固定是「系统默认输出」，其余按系统顺序。
    /// 枚举失败只留「系统默认输出」一项，不影响其它设置。
    /// </summary>
    private void BuildOutputDeviceChoices()
    {
        _loading = true;
        try
        {
            OutputDeviceBox.Items.Clear();
            _devices.Clear();

            OutputDeviceBox.Items.Add(new ComboBoxItem { Content = Strings.Settings_AudioOutputDefault });
            foreach (AudioOutputDevice device in AudioDeviceService.GetOutputDevices(_log))
            {
                _devices.Add(device);
                OutputDeviceBox.Items.Add(new ComboBoxItem { Content = device.Name });
            }

            OutputDeviceBox.SelectedIndex = IndexOfOutputDevice(_settings.AudioOutputDeviceId);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 设置值 → 下拉序号：0 是「系统默认输出」。
    /// 存的设备已经拔掉时也退回 0，但**不**清掉已存的 ID —— 插回来还能自动选中。
    /// </summary>
    private int IndexOfOutputDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return 0;
        }

        for (int i = 0; i < _devices.Count; i++)
        {
            if (string.Equals(_devices[i].Id, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        BuildOutputDeviceChoices();
    }

    /// <summary>选中即存；音频已连接就对当前子进程立刻生效，没连接只存着等下次 OPENED。</summary>
    private void OnOutputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        int index = OutputDeviceBox.SelectedIndex;
        _settings.AudioOutputDeviceId = index > 0 && index - 1 < _devices.Count
            ? _devices[index - 1].Id
            : string.Empty;
        _settings.Save();
        ApplyOutputDevice();
    }

    /// <summary>把当前选择的输出设备应用到接收子进程；未运行时只记一行日志。</summary>
    private void ApplyOutputDevice()
    {
        int pid = AudioSinkHub.Instance.ProcessId;
        if (pid <= 0)
        {
            _log.Write(LogLevel.Info, "audio", "输出设备已保存，接收端未运行，下次开启接收时生效");
            return;
        }

        string deviceId = _settings.AudioOutputDeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            // 系统会把「按进程指定输出」持久化在注册表里，切回默认时清不掉；
            // 重启一次接收子进程即可让新进程回到系统默认端点 —— 用户点一下就能真回到默认。
            _log.Write(LogLevel.Info, "audio", "输出设备：跟随系统默认（正在重启接收端以生效）");
            _ = RestartSinkAsync();
            return;
        }

        bool ok = AudioDeviceService.TryRouteProcessToDevice(pid, deviceId, _log);
        _log.Write(
            ok ? LogLevel.Info : LogLevel.Warn,
            "audio",
            ok ? "接收端输出已切到所选设备" : "该功能在当前系统不可用，声音仍从默认设备播放");
    }

    /// <summary>切回「系统默认输出」时重启接收子进程：新进程不会继承旧的按进程路由。</summary>
    private async Task RestartSinkAsync()
    {
        AudioSinkHub.Instance.Stop();
        await Task.Delay(600).ConfigureAwait(true);
        if (!AudioSinkHub.Instance.Start())
        {
            _log.Write(LogLevel.Warn, "audio", "重启接收端失败，请手动点一次「开启接收」");
        }
    }

    private void OnAudioStateChanged(AudioSinkState state, string detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAudioStateChanged(state, detail)));
            return;
        }

        RefreshAudioState();
    }
    /// <summary>把接收器状态翻译成人话，顺带把按钮文案切成「开启 / 停止」。</summary>
    /// <summary>「以手机音量为主」开关：开启时电脑侧固定 100%，关掉才用滑杆。</summary>
    private void OnFollowPhoneChanged(object sender, RoutedEventArgs e)
    {
        bool follow = FollowPhoneBox.IsChecked == true;
        _settings.AudioFollowPhone = follow;
        _settings.Save();

        ApplyVolume();
        UpdateVolumeHint();
    }

    /// <summary>滑杆：只在「以手机音量为主」关闭时生效。</summary>
    private void OnVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (FollowPhoneBox.IsChecked == true)
        {
            // 滑杆处于禁用态，这里是程序回填值触发的，不回写设置。
            return;
        }

        _settings.AudioAppVolume = Math.Clamp(e.NewValue / 100d, 0d, 1d);
        _settings.Save();
        VolumeText.Text = (int)Math.Round(e.NewValue) + "%";
        ApplyVolume();
        UpdateVolumeHint();
    }

    /// <summary>把当前策略应用到音频接收子进程的会话音量上。</summary>
    private void ApplyVolume()
    {
        int pid = AudioSinkHub.Instance.ProcessId;
        if (pid <= 0)
        {
            return;
        }

        double level = _settings.AudioFollowPhone ? 1d : _settings.AudioAppVolume;
        AudioVolumeService.SetProcessVolume(pid, level);
    }

    private void UpdateVolumeHint()
    {
        bool follow = FollowPhoneBox.IsChecked == true;
        VolumeSlider.IsEnabled = !follow;
        VolumeText.Text = follow
            ? "100%"
            : (int)Math.Round(VolumeSlider.Value) + "%";
        VolumeHintText.Text = follow ? Strings.Settings_VolumeHintOn : Strings.Settings_VolumeHintOff;
    }
    private void RefreshAudioState()
    {
        AudioSinkHost sink = AudioSinkHub.Instance;
        AudioToggleButton.Content = sink.IsRunning
            ? Strings.Settings_AudioStop
            : Strings.Settings_AudioStart;

        AudioStateText.Text = sink.State switch
        {
            AudioSinkState.Starting => Strings.Settings_AudioState_Starting,
            AudioSinkState.WaitingForPhone => Strings.Settings_AudioState_Waiting,
            AudioSinkState.Connected => string.Format(
                CultureInfo.CurrentCulture,
                Strings.Settings_AudioState_ConnectedFormat,
                sink.LastError ?? string.Empty).Replace("（）", string.Empty, StringComparison.Ordinal),
            AudioSinkState.NoDevice => Strings.Settings_AudioState_NoDevice,
            AudioSinkState.Failed => string.Format(
                CultureInfo.CurrentCulture,
                Strings.Settings_AudioState_FailedFormat,
                sink.LastError ?? "-"),
            _ => Strings.Settings_AudioState_Idle,
        };
    }

    private void Persist()
    {
        _settings.Save();
        string rate = _settings.EffectiveFrameRate > 0
            ? _settings.EffectiveFrameRate.ToString(CultureInfo.CurrentCulture) + " Hz"
            : Strings.Settings_FrameRateAuto;
        SavedText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0}：帧率 {1} · 码率 {2} Mbps",
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
            // 「自定义」只是展示态，选中它不覆盖用户已调好的数值。
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

    /// <summary>手动改帧率 / 码率 / 画质后，把预设下拉同步到对应档或「自定义」。</summary>
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
