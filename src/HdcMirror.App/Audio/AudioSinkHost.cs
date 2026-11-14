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

    /// <summary>上一次成功路由到的端点 ID；用来发现“系统默认输出变了”。</summary>
    private string? _appliedDeviceId;
    private string? _savedDefault;
    private string? _switchedTo;

    /// <summary>监听系统默认输出变化的定时器（仅连接期间工作，2.5 秒一次）。</summary>
    private System.Threading.Timer? _deviceWatch;

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
        ApplySelectedDevice();

        // 用户要求：软件不得修改系统默认输出设备。这里只记录所选设备，不做切换。

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

            // 只有自包含安装时程序目录里才有 hostfxr.dll。框架依赖安装若也设 DOTNET_ROOT，
            // apphost 会去这个目录找运行时、找不到就直接退出（表现为子进程秒退、状态一直 Starting）。
            if (System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "hostfxr.dll")))
            {
                startInfo.Environment["DOTNET_ROOT"] = AppContext.BaseDirectory;
            }

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

        _deviceWatch?.Dispose();
        _deviceWatch = null;
        _appliedDeviceId = null;
        RestoreDefaultDevice();

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
                    // 子进程会把 AudioPlaybackConnection.State 的枚举名打出来：
                    //   Opened = 手机已经把音频推过来了（哪怕不是我们这次 Open 触发的，
                    //            比如手机自动连回上次的电脑），这时就该算「已连接」，
                    //            否则界面会一直停在「正在等手机把音频输出切到电脑」。
                    string value = line["STATE ".Length..].Trim();
                    if (value.Equals("Opened", StringComparison.OrdinalIgnoreCase))
                    {
                        if (State != AudioSinkState.Connected)
                        {
                            LastError = null;
                            ApplyVolumePolicy();
                            ApplyOutputDevice();
                            SetState(AudioSinkState.Connected, string.Empty);
                        }
                    }
                    else if (value.Equals("Closed", StringComparison.OrdinalIgnoreCase)
                             && State == AudioSinkState.Connected)
                    {
                        SetState(AudioSinkState.WaitingForPhone, string.Empty);
                    }
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
    /// 输出设备策略：本机（Win11 26300 / .NET 8）**不支持按应用路由** ——
    /// Windows.Media.Internal.AudioPolicyConfig 是 IInspectable 接口，.NET 无法做互操作，
    /// 裸 vtable 调用会直接 AccessViolation。因此改为「监听系统默认输出，一变就把接收端重启一次」，
    /// 新会话自然落在新的默认设备上。切换代价是一次重连（约 1~3 秒）。
    /// </summary>

    /// <summary>停止接收时把系统默认输出还原成接收前的设备。</summary>
    /// <summary>
    /// 用户在设置里选了输出设备：把系统默认播放设备临时切到它。
    /// 传空串表示「跟随系统默认」，会立刻还原成接收前的设备。
    /// </summary>
    public void ApplyOutputDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            RestoreDefaultDevice();
            return;
        }

        _savedDefault ??= AudioDeviceService.GetDefaultRenderDeviceId(Log);
        if (string.Equals(deviceId, _switchedTo, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (DefaultDeviceSwitcher.TrySetDefault(deviceId, out string error))
        {
            _switchedTo = deviceId;
            Log.Write(LogLevel.Info, "audio", "已切换到所选输出设备：" + deviceId + "（停止接收后还原）");
        }
        else
        {
            Log.Write(LogLevel.Warn, "audio", "切换输出设备失败：" + error);
        }
    }

    private void ApplySelectedDevice()
    {
        string wanted = Services.AppSettings.Current.AudioOutputDeviceId;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            ApplyOutputDevice(wanted);
        }
    }

    /// <summary>停止接收时把系统默认输出还原成接收前的设备。</summary>
    private void RestoreDefaultDevice()
    {
        if (string.IsNullOrWhiteSpace(_savedDefault))
        {
            _switchedTo = null;
            return;
        }

        if (DefaultDeviceSwitcher.TrySetDefault(_savedDefault, out string error))
        {
            Log.Write(LogLevel.Info, "audio", "已还原系统默认输出：" + _savedDefault);
        }
        else
        {
            Log.Write(LogLevel.Warn, "audio", "还原默认输出失败：" + error);
        }

        _savedDefault = null;
        _switchedTo = null;
    }

    private void ApplyOutputDevice()
    {
        try
        {
            int pid = ProcessId;
            if (pid <= 0)
            {
                return;
            }
    
            string current = AudioDeviceService.GetDefaultRenderDeviceId(Log);
            if (string.IsNullOrEmpty(current))
            {
                Log.Write(LogLevel.Info, "audio", "输出设备：读不到系统默认设备，交由系统自动处理");
                return;
            }
    
            bool changed = _appliedDeviceId is not null
                && !string.Equals(current, _appliedDeviceId, StringComparison.OrdinalIgnoreCase);
            _appliedDeviceId = current;
    
            if (changed)
            {
                // 系统默认设备变了：Windows 会自己把已有的渲染会话迁过去（用户听到的「断一下就继续」就是它）。
                // 我们不重启接收端 —— 重启会多出 1~3 秒静音，还会把按应用锁定的设备设置冲掉。
                Log.Write(
                    LogLevel.Info,
                    "audio",
                    "系统默认输出已变化，交由 Windows 迁移会话（PID "
                        + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）");
                return;
            }
    
            Log.Write(LogLevel.Info, "audio", "输出设备：跟随系统默认 / 按应用锁定（已记录当前设备）");
            StartDeviceWatch();
        }
        catch (Exception ex)
        {
            // 音频链路上的任何异常都不许冒到界面上（会把整个程序带崩）。
            Log.Write(LogLevel.Warn, "audio", "读取/应用输出设备失败：" + ex.Message, ex);
        }
    }

    /// <summary>重启接收端子进程：新会话会落在当前系统默认输出设备上。</summary>
    private void RestartSink()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Stop();
                await Task.Delay(400).ConfigureAwait(false);
                Start();
            }
            catch (Exception ex)
            {
                Log.Write(LogLevel.Warn, "audio", "重启接收端失败：" + ex.Message, ex);
            }
        });
    }

    /// <summary>开启默认设备监听（重复调用无副作用）。</summary>
    private void StartDeviceWatch()
    {
        if (_deviceWatch is not null)
        {
            return;
        }

        _deviceWatch = new System.Threading.Timer(_ => WatchDefaultDevice(), null, 2500, 2500);
    }

    private void WatchDefaultDevice()
    {
        try
        {
            if (State != AudioSinkState.Connected || ProcessId <= 0)
            {
                return;
            }

            string current = AudioDeviceService.GetDefaultRenderDeviceId(Log);
            if (string.IsNullOrEmpty(current)
                || string.Equals(current, _appliedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyOutputDevice();
        }
        catch (Exception ex)
        {
            Log.Write(LogLevel.Warn, "audio", "监听默认输出变化失败：" + ex.Message, ex);
        }
    }


    private void SetState(AudioSinkState state, string detail)
    {
        Log.Write(LogLevel.Info, "audio", "状态 -> " + state + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）"));
        StateChanged?.Invoke(state, detail);
    }
}
