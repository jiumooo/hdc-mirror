// =============================================================================
// 蓝牙音频接收子进程的父侧管家。
//
// 子进程（HdcMirror.Audio.exe）负责真正去 Open AudioPlaybackConnection，
// 我们在这一侧只做三件事：拉起、读状态行、必要时杀掉重来。
// 为什么要独立进程见 src\HdcMirror.Audio\Program.cs 头部注释。
// =============================================================================

using System.Diagnostics;
using System.IO;

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

    /// <summary>状态变化回调：参数为「新状态」与「本机默认播放设备提示 / 错误码」。</summary>
    public event Action<AudioSinkState, string>? StateChanged;

    /// <summary>当前状态。</summary>
    public AudioSinkState State { get; private set; } = AudioSinkState.Idle;

    /// <summary>最近一次失败的错误码或说明，成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>子进程可执行文件的绝对路径（随程序一起分发在 <c>audio\</c> 下）。</summary>
    public static string ExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, "audio", "HdcMirror.Audio.exe");

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

                break;
        }
    }

    private void SetState(AudioSinkState state, string detail)
    {
        State = state;
        StateChanged?.Invoke(state, detail);
    }
}
