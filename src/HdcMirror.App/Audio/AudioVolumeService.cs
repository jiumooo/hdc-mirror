using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HdcMirror.App.Audio;

/// <summary>
/// 本程序音频接收进程的「单应用音量」。
///
/// 为什么需要它：A2DP Sink 收到的是一条**已经被手机按手机音量衰减过**的音频流，电脑这边如果再把
/// 自己的音量压低，就变成两级衰减 —— 用户会觉得「手机上开到最大还是很小声」。
/// 所以本程序默认把这个会话的音量钉在 100%，让**手机音量成为唯一音量**；滑杆只作为用户自愿降低时的覆盖。
///
/// 实现走 CoreAudio 的会话接口（<c>IAudioSessionManager2</c> → <c>ISimpleAudioSessionControl2</c> →
/// <c>ISimpleAudioVolume</c>），只用 COM 互操作，不引第三方包。
/// </summary>
public static class AudioVolumeService
{
    /// <summary>MMDeviceEnumerator 的 CLSID；用 CLSID 现建实例，避免与 AudioDeviceService 里的同名嵌套 coclass 串味。</summary>
    private static readonly Guid MmDeviceEnumeratorClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>把指定进程的音频会话音量设为 <paramref name="level"/>（0~1）。</summary>
    /// <param name="processId">音频接收子进程的 PID。</param>
    /// <param name="level">目标音量，0 = 静音，1 = 100%。</param>
    /// <returns>找到并设置成功返回 true。</returns>
    public static bool SetProcessVolume(int processId, double level)
    {
        if (processId <= 0)
        {
            return false;
        }

        double clamped = Math.Clamp(level, 0d, 1d);
        try
        {
            foreach (ISimpleAudioVolume session in Sessions())
            {
                if (ProcessIdOf(session) != processId)
                {
                    continue;
                }

                Guid ctx = Guid.Empty;
                session.SetMasterVolume((float)clamped, ref ctx);
                return true;
            }
        }
        catch (COMException ex)
        {
            Debug.WriteLine("[audio] 设置会话音量失败：" + ex.Message);
        }
        catch (InvalidCastException ex)
        {
            Debug.WriteLine("[audio] 会话不是简单音频会话：" + ex.Message);
        }

        return false;
    }

    /// <summary>按进程名找音频会话并设音量（拿不到 PID 时的兜底）。</summary>
    /// <param name="processName">进程名，不带 .exe。</param>
    /// <param name="level">目标音量。</param>
    /// <returns>设置成功返回 true。</returns>
    public static bool SetProcessVolume(string processName, double level)
    {
        int pid = 0;
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            pid = process.Id;
            break;
        }

        return SetProcessVolume(pid, level);
    }

    /// <summary>读出指定进程当前音量；读不到返回 <c>null</c>。</summary>
    /// <param name="processId">进程 PID。</param>
    /// <returns>0~1 的音量，或 null。</returns>
    public static double? GetProcessVolume(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            foreach (ISimpleAudioVolume session in Sessions())
            {
                if (ProcessIdOf(session) != processId)
                {
                    continue;
                }

                session.GetMasterVolume(out float volume);
                return volume;
            }
        }
        catch (COMException ex)
        {
            Debug.WriteLine("[audio] 读取会话音量失败：" + ex.Message);
        }

        return null;
    }

    private static int ProcessIdOf(ISimpleAudioVolume session)
    {
        if (session is IAudioSessionControl2 control)
        {
            control.GetProcessId(out uint pid);
            return (int)pid;
        }

        return 0;
    }

    private static ISimpleAudioVolume[] Sessions()
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid)!)!;
        enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device);
        Guid iid = typeof(IAudioSessionManager2).GUID;
        device.Activate(ref iid, 23, IntPtr.Zero, out object managerObject);

        var manager = (IAudioSessionManager2)managerObject;
        manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        sessions.GetCount(out int count);

        var result = new System.Collections.Generic.List<ISimpleAudioVolume>(count);
        for (int i = 0; i < count; i++)
        {
            if (sessions.GetSession(i, out IAudioSessionControl control) != 0 || control is null)
            {
                continue;
            }

            if (control is ISimpleAudioVolume simple)
            {
                result.Add(simple);
            }
        }

        return result.ToArray();
    }

    // ───────────────────────── COM 声明 ─────────────────────────


    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager 的成员（vtable 顺序必须保留，否则调用会错位）
        int GetAudioSessionControl(ref Guid audioSessionGuid, int flags, out IntPtr sessionControl);

        int GetSimpleAudioVolume(ref Guid audioSessionGuid, int flags, out IntPtr audioVolume);

        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);

        int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        int GetState(out int state);

        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);

        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        int GetGroupingParam(out Guid groupingParam);

        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

        int RegisterAudioSessionNotification(IntPtr notifications);

        int UnregisterAudioSessionNotification(IntPtr notifications);
    }

    [ComImport]
    [Guid("BFB7FF10-BBA4-11DF-9690-F06A95AF9B0F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int state);

        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);

        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        int GetGroupingParam(out Guid groupingParam);

        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

        int RegisterAudioSessionNotification(IntPtr notifications);

        int UnregisterAudioSessionNotification(IntPtr notifications);

        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

        int GetProcessId(out uint processId);

        int IsSystemSoundsSession();

        int SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);

        int GetMasterVolume(out float level);

        int SetMute(bool mute, ref Guid eventContext);

        int GetMute(out bool mute);
    }
}
