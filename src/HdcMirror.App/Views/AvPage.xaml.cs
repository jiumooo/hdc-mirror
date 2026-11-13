using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HdcMirror.App.Audio;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.App.Views;

/// <summary>
/// 音频页：手机 → 电脑的蓝牙接收（A2DP Sink）。
/// 音量统一交给 Windows 音量合成器 / 系统音量键，本页不再提供重复的滑杆（滑杆对蓝牙端点无效）。
/// </summary>
public partial class AvPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;

    /// <summary>与接收器共用同一份日志出口，避免开两个句柄写同一个日志文件。</summary>
    private readonly ILogSink _log = AudioSinkHub.Instance.Log;

    /// <summary>下拉里除「跟随系统默认」之外的设备，下标 = 下拉下标 - 1。</summary>
    private readonly List<AudioOutputDevice> _devices = new();

    private bool _loading;

    /// <summary>初始化音频页，并把当前配置回填到控件。</summary>
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
            AutoAudioBox.IsChecked = _settings.AutoStartAudio;
        }
        finally
        {
            _loading = false;
        }

        BuildOutputDeviceChoices();
        RefreshAudioState();
    }

    // ── 音频接收（手机 → 电脑 → 所选输出设备）─────────────────

    private void OnAudioToggleClick(object sender, RoutedEventArgs e)
    {
        AudioSinkHost sink = AudioSinkHub.Instance;
        if (sink.IsRunning)
        {
            sink.Stop();
            RefreshAudioState();
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
    /// 重新枚举播放设备并回填下拉：第 0 项固定是「跟随系统默认」，其余按系统顺序。
    /// 枚举失败只留第 0 项，不影响其它设置。
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
            OutputDeviceBox.ToolTip = "选中哪台设备，点「开始接收」后手机声音就从哪台出；停止接收会还原你原来的默认输出设备。";
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 设置值 → 下拉序号：0 是「跟随系统默认」。
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

    /// <summary>选中即存；正在接收时立刻切过去，不必先停止再开始。</summary>
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

        AudioSinkHub.Instance.ApplyOutputDevice(_settings.AudioOutputDeviceId);
    }

    private void OnAutoAudioChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AutoStartAudio = AutoAudioBox.IsChecked == true;
        _settings.Save();
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

    /// <summary>把接收器状态翻译成人话，顺带把按钮文案切成「开始接收 / 停止接收」。</summary>
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
}