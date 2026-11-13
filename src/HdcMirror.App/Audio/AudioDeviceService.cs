// =============================================================================
// 播放设备：枚举 + 「只给本程序的接收端换输出设备」。
//
// 两件事各自独立：
//   1) 枚举：CoreAudio 的 IMMDeviceEnumerator → EnumAudioEndpoints(eRender) →
//      IMMDevice 的 OpenPropertyStore 读 PKEY_Device_FriendlyName，
//      得到「设备名 + 端点 ID」。
//   2) 路由：用 Windows 未公开的 IAudioPolicyConfigFactory
//      （激活类名 "Windows.Media.Internal.AudioPolicyConfig"），
//      只把 HdcMirror.Audio.exe 这个进程的声音切到指定端点。
//      **不改系统默认设备** —— 那会把用户其它软件的声音一起搬走。
//
// 为什么必须这么绕：AudioPlaybackConnection 把音频交给 Windows 音频引擎后，
// 输出走的是系统默认端点。想「本程序单独走某个设备」，只有按进程路由这一条路。
//
// 全部手写 interop，不引第三方包（本机没有 NuGet 源）。
//
// 线程模型：CoreAudio 与激活工厂都在专门的 MTA 线程上跑。WPF 主线程是 STA，
// 在它上面 CoInitializeEx(COINIT_MULTITHREADED) 会拿到 RPC_E_CHANGED_MODE，
// 所以一次都不在主线程初始化 COM。
//
// 失败一律不抛异常：枚举不到就返回空清单，路由失败只写日志并返回 false，
// 播放链路照旧从默认设备出声。用户看到的是「该功能在当前系统不可用」。
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.App.Audio;

/// <summary>一个可选的音频播放设备（渲染端点）。</summary>
public sealed class AudioOutputDevice
{
    /// <summary>创建一个设备条目。</summary>
    /// <param name="id">端点 ID。</param>
    /// <param name="name">用户可读的设备名。</param>
    public AudioOutputDevice(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>端点 ID，形如 <c>{0.0.0.00000000}.{...}</c>，音频策略接口直接吃这个字符串。</summary>
    public string Id { get; }

    /// <summary>Windows 声音设置里显示的设备名。</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>枚举播放设备，并把指定进程的声音路由到其中某一个。</summary>
public static class AudioDeviceService
{
    /// <summary>EDataFlow::eRender —— 播放（输出）方向。</summary>
    private const int EDataFlowRender = 0;

    /// <summary>ERole::eMultimedia —— 多媒体角色，音乐/视频走这一档。</summary>
    private const int ERoleMultimedia = 1;

    /// <summary>DEVICE_STATE_ACTIVE。</summary>
    private const int DeviceStateActive = 0x00000001;

    /// <summary>STGM_READ，打开属性存储时只读。</summary>
    private const int StgmRead = 0;

    /// <summary>MTA 线程最长等多久；超时就放弃，绝不拖住 UI。</summary>
    private const int MtaTimeoutMs = 5000;

    /// <summary>
    /// 按应用音频策略工厂的 IID 候选。系统在不同 Windows 版本里换了 IID：
    /// ・AB3D4648-E242-459F-B02F-541C70306324：Windows 10 1903 及以后（本机 Win11 属这一档）
    /// ・2A59116D-6C4F-45E0-A74F-707E3FEF9258：Windows 10 1803 / 1809
    /// 两个接口的方法签名与顺序一致，只有激活 IID 不同，所以逐个试。
    /// </summary>
    private static readonly Guid[] PolicyConfigIids =
    [
        new("AB3D4648-E242-459F-B02F-541C70306324"),
        new("2A59116D-6C4F-45E0-A74F-707E3FEF9258"),
    ];

    /// <summary>未公开的激活类名。</summary>
    private const string PolicyConfigClassName = "Windows.Media.Internal.AudioPolicyConfig";

    /// <summary>PROPVARIANT 里的 VT_LPWSTR。</summary>
    private const ushort VtLpwstr = 31;

    /// <summary>PROPVARIANT 里的 VT_BSTR。</summary>
    private const ushort VtBstr = 8;

    /// <summary>PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, 14。</summary>
    private static readonly PROPERTYKEY PkeyDeviceFriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14,
    };

    /// <summary>列出当前处于活动状态的播放设备（顺序与系统一致）。</summary>
    /// <param name="log">可选日志出口；为 null 时静默。</param>
    /// <returns>设备清单；枚举失败返回空清单，不抛异常。</returns>
    public static IReadOnlyList<AudioOutputDevice> GetOutputDevices(ILogSink? log = null)
    {
        return RunOnMta(() => EnumerateCore(log), Array.Empty<AudioOutputDevice>());
    }

    /// <summary>
    /// 取系统当前默认播放设备（多媒体角色）的端点 ID；取不到返回空串。
    /// 用于「跟随系统默认输出」——用户在 Windows 里换输出设备后，我们据此重新路由子进程。
    /// </summary>
    /// <param name="log">可选日志出口。</param>
    /// <returns>端点 ID；失败为空串（不抛异常）。</returns>
    public static string GetDefaultRenderDeviceId(ILogSink? log = null)
    {
        return RunOnMta(() => DefaultDeviceCore(log), string.Empty);
    }

    private static string DefaultDeviceCore(ILogSink? log)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid)!)!;
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out device);
            if (hr != 0 || device is null)
            {
                log?.Write(LogLevel.Debug, "audio", "读取默认播放设备失败，HRESULT=" + Hr(hr));
                return string.Empty;
            }

            if (device.GetId(out string id) != 0 || string.IsNullOrWhiteSpace(id))
            {
                return string.Empty;
            }

            return id.Trim();
        }
        catch (COMException ex)
        {
            log?.Write(LogLevel.Debug, "audio", "读取默认播放设备异常：" + ex.Message, ex);
            return string.Empty;
        }
        finally
        {
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>
    /// 把指定进程的声音输出切到 <paramref name="deviceId"/> 对应的播放设备。
    /// 只影响该进程，不改系统默认设备。
    /// </summary>
    /// <param name="processId">目标进程 PID（这里是 HdcMirror.Audio.exe 的 PID）。</param>
    /// <param name="deviceId">端点 ID；空串表示跟随系统默认，此时不做任何事。</param>
    /// <param name="log">可选日志出口。</param>
    /// <returns>路由成功返回 true；接口不可用或调用被拒返回 false（不抛异常）。</returns>
    public static bool TryRouteProcessToDevice(int processId, string deviceId, ILogSink? log = null)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            // 空 = 跟随系统默认。子进程新起时本来就跟随默认，不需要调接口。
            log?.Write(LogLevel.Info, "audio", "输出设备：跟随系统默认");
            return false;
        }

        string id = deviceId.Trim();
        bool ok = RunOnMta(() => RouteCore(processId, id, log), false);
        if (!ok)
        {
            log?.Write(
                LogLevel.Warn,
                "audio",
                "按应用指定播放设备失败：该功能在当前系统不可用，声音仍从默认设备播放（PID "
                    + processId.ToString(CultureInfo.InvariantCulture) + "）");
        }

        return ok;
    }

    /// <summary>
    /// 读取某个进程当前被持久化的输出设备 ID（仅用于诊断/验证）；读不到返回空串。
    /// </summary>
    public static string GetPersistedDeviceId(int processId, ILogSink? log = null)
        => RunOnMta(() => ReadPersistedCore(processId, log), string.Empty);

    private static string ReadPersistedCore(int processId, ILogSink? log)
    {
        IntPtr classHString = IntPtr.Zero;
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            if (WindowsCreateString(PolicyConfigClassName, PolicyConfigClassName.Length, out classHString) != 0)
            {
                return string.Empty;
            }

            int hr = -1;
            foreach (Guid candidate in PolicyConfigIids)
            {
                Guid iid = candidate;
                hr = RoGetActivationFactory(classHString, ref iid, out factoryPtr);
                if (hr == 0 && factoryPtr != IntPtr.Zero)
                {
                    break;
                }

                factoryPtr = IntPtr.Zero;
            }

            if (hr != 0 || factoryPtr == IntPtr.Zero)
            {
                return string.Empty;
            }

            // .NET 不支持把 IInspectable 接口做 COM 互操作（PlatformNotSupportedException），
            // 所以不走接口转换，直接按 vtable 顺序调用：IInspectable 占 0~2，Get 是第 4 个槽。
            int getHr = InvokeGetPersisted(factoryPtr, (uint)processId, EDataFlowRender, ERoleMultimedia, out IntPtr deviceHString);
            if (getHr != 0 || deviceHString == IntPtr.Zero)
            {
                log?.Write(LogLevel.Debug, "audio", "读取按应用路由失败，HRESULT=" + Hr(getHr));
                return string.Empty;
            }

            return Marshal.PtrToStringUni(deviceHString) ?? string.Empty;
        }
        catch (COMException ex)
        {
            log?.Write(LogLevel.Debug, "audio", "读取按应用路由异常：" + ex.Message, ex);
            return string.Empty;
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }

            if (classHString != IntPtr.Zero)
            {
                WindowsDeleteString(classHString);
            }
        }
    }

    // ── 枚举 ────────────────────────────────────────────────────────────

    private static IReadOnlyList<AudioOutputDevice> EnumerateCore(ILogSink? log)
    {
        var result = new List<AudioOutputDevice>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid)!)!;
            int hr = enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out collection);
            if (hr != 0 || collection is null)
            {
                log?.Write(LogLevel.Warn, "audio", "枚举播放设备失败，HRESULT=" + Hr(hr));
                return result;
            }

            if (collection.GetCount(out int count) != 0)
            {
                return result;
            }

            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDevice device) != 0 || device is null)
                {
                    continue;
                }

                try
                {
                    if (device.GetId(out string id) != 0 || string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    string name = ReadFriendlyName(device);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        // 读不到友好名时退回端点 ID，至少让用户能选。
                        name = id;
                    }

                    result.Add(new AudioOutputDevice(id, name));
                }
                finally
                {
                    Release(device);
                }
            }
        }
        catch (COMException ex)
        {
            log?.Write(LogLevel.Warn, "audio", "枚举播放设备异常：" + ex.Message, ex);
        }
        catch (InvalidCastException ex)
        {
            log?.Write(LogLevel.Warn, "audio", "CoreAudio 设备枚举器不可用：" + ex.Message, ex);
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }

        return result;
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out IPropertyStore store) != 0 || store is null)
        {
            return string.Empty;
        }

        try
        {
            // 静态只读字段不能取 ref，复制一份。
            PROPERTYKEY key = PkeyDeviceFriendlyName;
            if (store.GetValue(ref key, out PROPVARIANT value) != 0)
            {
                return string.Empty;
            }

            try
            {
                if ((value.vt == VtLpwstr || value.vt == VtBstr) && value.pointerValue != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(value.pointerValue) ?? string.Empty;
                }

                return string.Empty;
            }
            finally
            {
                // PROPVARIANT 里的字符串归 COM 所有，必须还回去。
                _ = PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    // ── 路由 ────────────────────────────────────────────────────────────

    private static bool RouteCore(int processId, string deviceId, ILogSink? log)
    {
        IntPtr classHString = IntPtr.Zero;
        IntPtr deviceHString = IntPtr.Zero;
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            if (WindowsCreateString(PolicyConfigClassName, PolicyConfigClassName.Length, out classHString) != 0
                || classHString == IntPtr.Zero)
            {
                log?.Write(LogLevel.Warn, "audio", "创建激活类名 HSTRING 失败");
                return false;
            }

            int hr = -1;
            foreach (Guid candidate in PolicyConfigIids)
            {
                Guid iid = candidate;
                hr = RoGetActivationFactory(classHString, ref iid, out factoryPtr);
                if (hr == 0 && factoryPtr != IntPtr.Zero)
                {
                    log?.Write(LogLevel.Info, "audio", "按应用路由启用：策略工厂接口 " + candidate.ToString("B"));
                    break;
                }

                factoryPtr = IntPtr.Zero;
            }

            if (hr != 0 || factoryPtr == IntPtr.Zero)
            {
                log?.Write(LogLevel.Warn, "audio", "取不到音频策略工厂，HRESULT=" + Hr(hr)
                    + "（两个 IID 都不接受，本机可能不支持按应用路由）");
                return false;
            }

            if (WindowsCreateString(deviceId, deviceId.Length, out deviceHString) != 0
                || deviceHString == IntPtr.Zero)
            {
                log?.Write(LogLevel.Warn, "audio", "创建端点 ID HSTRING 失败");
                return false;
            }

            // 同上：裸 vtable 调用，Set 是第 4 个槽（0~2 是 IInspectable 的三个方法）。
            int setHr = InvokeSetPersisted(factoryPtr, (uint)processId, EDataFlowRender, ERoleMultimedia, deviceHString);
            if (setHr != 0)
            {
                log?.Write(LogLevel.Warn, "audio", "设置按应用播放设备被拒，HRESULT=" + Hr(setHr));
                return false;
            }

            return true;
        }
        catch (COMException ex)
        {
            log?.Write(LogLevel.Warn, "audio", "音频策略调用异常：" + ex.Message, ex);
            return false;
        }
        catch (InvalidCastException ex)
        {
            log?.Write(LogLevel.Warn, "audio", "音频策略接口转换失败：" + ex.Message, ex);
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            log?.Write(LogLevel.Warn, "audio", "系统缺少 RoGetActivationFactory：" + ex.Message, ex);
            return false;
        }
        catch (DllNotFoundException ex)
        {
            log?.Write(LogLevel.Warn, "audio", "系统缺少 combase.dll：" + ex.Message, ex);
            return false;
        }
        finally
        {
            if (deviceHString != IntPtr.Zero)
            {
                _ = WindowsDeleteString(deviceHString);
            }

            if (classHString != IntPtr.Zero)
            {
                _ = WindowsDeleteString(classHString);
            }

            if (factoryPtr != IntPtr.Zero)
            {
                _ = Marshal.Release(factoryPtr);
            }
        }
    }

    // ── 工具 ────────────────────────────────────────────────────────────

    /// <summary>在专用 MTA 线程上跑一段 COM 代码，返回值；超时返回兜底值。</summary>
    private static T RunOnMta<T>(Func<T> work, T fallback)
    {
        T result = fallback;
        var thread = new Thread(() =>
        {
            // .NET 默认把新线程初始化成 MTA；这里再显式调一次，S_FALSE 也算「我们要负责收尾」。
            int hr = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
            bool owns = hr >= 0;
            try
            {
                result = work();
            }
            finally
            {
                if (owns)
                {
                    CoUninitialize();
                }
            }
        })
        {
            IsBackground = true,
            Name = "hdcmirror-audio-mta",
        };

        try
        {
            thread.SetApartmentState(ApartmentState.MTA);
        }
        catch (PlatformNotSupportedException)
        {
            // 理论上不会发生（只在 Windows 跑），发生也不影响后面的执行。
        }

        thread.Start();
        if (!thread.Join(MtaTimeoutMs))
        {
            return fallback;
        }

        return result;
    }

    /// <summary>IAudioPolicyConfigFactory::SetPersistedDefaultAudioEndpoint 的裸签名（stdcall）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedFn(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);

    /// <summary>IAudioPolicyConfigFactory::GetPersistedDefaultAudioEndpoint 的裸签名（stdcall）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedFn(IntPtr self, uint processId, int flow, int role, out IntPtr deviceId);

    /// <summary>裸调 Set（vtable 第 4 槽：IInspectable 的前三个是 QueryInterface/AddRef/Release）。</summary>
    private static int InvokeSetPersisted(IntPtr factory, uint processId, int flow, int role, IntPtr deviceId)
    {
        IntPtr vtable = Marshal.ReadIntPtr(factory);
        IntPtr slot = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var call = (SetPersistedFn)Marshal.GetDelegateForFunctionPointer(slot, typeof(SetPersistedFn));
        return call(factory, processId, flow, role, deviceId);
    }

    /// <summary>裸调 Get（vtable 第 5 槽）。</summary>
    private static int InvokeGetPersisted(IntPtr factory, uint processId, int flow, int role, out IntPtr deviceId)
    {
        IntPtr vtable = Marshal.ReadIntPtr(factory);
        IntPtr slot = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
        var call = (GetPersistedFn)Marshal.GetDelegateForFunctionPointer(slot, typeof(GetPersistedFn));
        return call(factory, processId, flow, role, out deviceId);
    }

    /// <summary>MMDeviceEnumerator 的 CLSID（{BCDE0395-E52F-467C-8E3D-C4579291692E}）。用 CLSID 现建实例、不再声明 [ComImport] coclass：
    /// 两个文件里各有一份同名嵌套 coclass 会让 CLR 的 interop 类型缓存串味，导致 InvalidCastException（对象与目标同名却转不过去）。</summary>
    private static readonly Guid MmDeviceEnumeratorClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private static string Hr(int hr) => "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);

    private static void Release(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                _ = Marshal.ReleaseComObject(comObject);
            }
        }
        catch (ArgumentException)
        {
            // 不是 RCW，忽略。
        }
        catch (InvalidOperationException)
        {
            // 已经释放过，忽略。
        }
    }

    // ───────────────────────── COM / WinRT 声明 ─────────────────────────

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;

        public int pid;
    }

    /// <summary>
    /// PROPVARIANT 的托管形状。只要 24 字节对齐正确、指针位置正确，
    /// 内容由 <see cref="PropVariantClear"/> 负责释放。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PROPVARIANT
    {
        public ushort vt;

        public ushort wReserved1;

        public ushort wReserved2;

        public ushort wReserved3;

        public IntPtr pointerValue;

        public int padding1;

        public int padding2;
    }


    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // vtable 顺序必须与原生一致，否则后面的方法全部错位。
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);

        [PreserveSig]
        int OpenPropertyStore(int access, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetAt(int index, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

        [PreserveSig]
        int Commit();
    }

    /// <summary>
    /// 未公开的按应用音频策略接口。IID 与激活类名都来自系统内部实现，
    /// 方法顺序就是 vtable 顺序：三个方法谁挪了位置都会调用错位。
    /// </summary>
    [ComImport]
    [Guid("2A59116D-6C4F-45E0-A74F-707E3FEF9258")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IAudioPolicyConfigFactory
    {
        [PreserveSig]
        int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);

        [PreserveSig]
        int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);

        [PreserveSig]
        int ClearAllPersistedApplicationDefaultEndpoints();
    }
}
