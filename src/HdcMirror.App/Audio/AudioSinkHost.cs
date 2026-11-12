// =============================================================================
// 蓝牙音频接收子进程的父侧管家。
//
// 子进程（HdcMirror.Audio.exe）负责真正去 Open AudioPlaybackConnection，
// 我们在这一侧只做三件事：拉起、读状态行、必要时杀掉重来。
// 为什么要独立进程见 src\HdcMirror.Audio\Program.cs 头部注释。
// =============================================================================

using System.Diagnostics;
using System.IO;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.App.Audio;

/// <summary>音频接收链路的状态。</summary>
public enum AudioSinkState
{
    /// <summary>未启用。</summary>
    Idle,

    /// <summary>子进程已拉起，正在建立连接。</summary>
    Starting,

    /// <summary>已 Start()，等手机把音频输出切到本机（最长约 30 秒）。</summary>
    WaitingForPhone,

    /// <summary>已连上：手机声音从本机默认播放设备出来。</summary>
    Connected,

    /// <summary>系统里没有可用的已配对蓝牙设备。</summary>
    NoDevice,

    /// <summary>失败（错误码见 <see cref="LastError"/>）。</summary>
    Failed,
}

/// <summary>管理 HdcMirror.Audio 子进程的生命周期与状态。</summary>
public sealed class AudioSinkHost : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;

    /// <summary>
    /// 诊断日志出口。默认写本机滚动日志文件（<c>%LOCALAPPDATA%\HdcMirror\logs</c>），
    /// 设置页与这里共用一份，避免开多个写同一文件的句柄。
    /// </summary>
    public ILogSink Log { get; set; } = new FileLogSink();

    /// <summary>状态变化回调：参数为「新状态」与「本机默认播放设备提示 / 错误码」。</summary>
    public event Action<AudioSinkState, string>? StateChanged;

    /// <summary>当前状态。</summary>
    public AudioSinkState State { get; private set; } = AudioSinkState.Idle;

    /// <summary>最近一次失败的错误码或说明，成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>子进程可执行文件的绝对路径（随程序一起分发在 <c>audio\</c> 下）。</summary>
    public static string ExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, "audio", "HdcMirror.Audio.exe");

    /// <summary>音频接收子进程的 PID；未运行时为 0。用来把本应用音量钉到 100%。</summary>
    public int ProcessId
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _process is { HasExited: false } ? _process.Id : 0;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
        }
    }

    /// <summary>子进程是否还在跑。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>拉起子进程开始接收音频；已有实例时直接返回。</summary>
    /// <returns>子进程是否成功启动。</returns>
    public bool Start()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                return true;
            }

            string exe = ExecutablePath;
            if (!File.Exists(exe))
            {
                LastError = "audio-missing";
                SetState(AudioSinkState.Failed, LastError);
                return false;
            }

            var startInfo = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += OnOutputLine;
            process.ErrorDataReceived += OnErrorLine;
            process.Exited += OnExited;

            if (!process.Start())
            {
                LastError = "audio-start-failed";
                SetState(AudioSinkState.Failed, LastError);
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
        }

        LastError = null;
        SetState(AudioSinkState.Starting, string.Empty);
        return true;
    }

    /// <summary>关掉子进程（父进程只发 quit + 关 stdin，让子进程自己退）。</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            SetState(AudioSinkState.Idle, string.Empty);
            return;
        }

        try
        {
            process.StandardInput.WriteLine("quit");
            process.StandardInput.Close();
            if (!process.WaitForExit(2500))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程已经没了就算了。
        }
        finally
        {
            process.Dispose();
        }

        SetState(AudioSinkState.Idle, string.Empty);
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void OnExited(object? sender, EventArgs e)
    {
        // 子进程自己退出（崩溃或被杀）：回收句柄并把状态落回 Idle。
        lock (_gate)
        {
            if (ReferenceEquals(sender, _process))
            {
                _process = null;
            }
        }

        if (State is not (AudioSinkState.Idle or AudioSinkState.Failed))
        {
            SetState(AudioSinkState.Idle, string.Empty);
        }
    }

    private void OnErrorLine(object? sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            LastError = e.Data.Trim();
        }
    }

    private void OnOutputLine(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        string line = e.Data.Trim();
        switch (line)
        {
            case "READY":
                break;
            case "NO_DEVICE":
                LastError = "no-device";
                SetState(AudioSinkState.NoDevice, string.Empty);
                break;
            case "OPENING":
                SetState(AudioSinkState.WaitingForPhone, string.Empty);
                break;
            case "OPENED":
                LastError = null;
                ApplyVolumePolicy();
                ApplyOutputDevice();
                SetState(AudioSinkState.Connected, string.Empty);
                break;
            case "CLOSED":
                SetState(AudioSinkState.WaitingForPhone, string.Empty);
                break;
            case "EXIT":
                SetState(AudioSinkState.Idle, string.Empty);
                break;
            default:
                if (line.StartsWith("DEVICE ", StringComparison.Ordinal))
                {
                    // 只记名字，界面不显示（避免把设备昵称写进 UI 截图）。
                    LastError = null;
                }
                else if (line.StartsWith("STATE ", StringComparison.Ordinal))
                {
                    // Windows.Media.Audio.AudioPlaybackConnectionState 的枚举名，仅用于日志排查。
                }
                else if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                {
                    LastError = line["ERROR ".Length..].Trim();
                    SetState(AudioSinkState.Failed, LastError);
                }
                else if (line.StartsWith("DENIED ", StringComparison.Ordinal))
                {
                    // 系统级拒绝（DeniedBySystem）：重试没有意义，只能去蓝牙设置里处理。
                    LastError = line["DENIED ".Length..].Trim();
                    SetState(AudioSinkState.Failed, LastError);
                }
                else if (line.StartsWith("LOG ", StringComparison.Ordinal))
                {
                    // 子进程的自述日志（例如 StartAsync 失败但继续尝试），不改变状态。
                }

                break;
        }
    }

    /// <summary>
    /// 音量策略：默认「以手机音量为主」—— 本应用会话钉在 100%，音量完全由手机侧决定，
    /// 避免手机与电脑两级衰减。用户关掉这个开关时才用自己设的电脑侧音量。
    /// </summary>
    private void ApplyVolumePolicy()
    {
        int pid = ProcessId;
        if (pid <= 0)
        {
            return;
        }

        double level = Services.AppSettings.Current.AudioFollowPhone
            ? 1d
            : Services.AppSettings.Current.AudioAppVolume;

        AudioVolumeService.SetProcessVolume(pid, level);
    }

    /// <summary>
    /// 输出设备策略：用户选了具体设备就只把这一个子进程切过去，
    /// 没选（空串）就跟随系统默认，什么都不做。
    /// 接口不可用时只写日志，播放链路照旧从默认设备出声。
    /// </summary>
    private void ApplyOutputDevice()
    {
        int pid = ProcessId;
        if (pid <= 0)
        {
            return;
        }

        string deviceId = Services.AppSettings.Current.AudioOutputDeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            Log.Write(LogLevel.Info, "audio", "输出设备：跟随系统默认");
            return;
        }

        if (AudioDeviceService.TryRouteProcessToDevice(pid, deviceId, Log))
        {
            Log.Write(LogLevel.Info, "audio", "接收端输出已切到所选设备（PID "
                + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）");
        }
    }

    private void SetState(AudioSinkState state, string detail)
    {
        State = state;
        StateChanged?.Invoke(state, detail);
    }
}
