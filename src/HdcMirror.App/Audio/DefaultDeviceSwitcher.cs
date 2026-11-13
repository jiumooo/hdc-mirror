using System;
using System.Runtime.InteropServices;

namespace HdcMirror.App.Audio;

/// <summary>
/// 把系统默认播放设备临时切到指定端点（IPolicyConfig::SetDefaultEndpoint）。
///
/// 用途：用户要求「在软件里选输出设备，点音频就用这台设备出声」。
/// 手机声音由 Windows 蓝牙 A2DP 接收通道渲染到「当前默认播放设备」，而 .NET 无法给这条通道
/// 单独指定设备（AudioPolicyConfig 是 IInspectable 接口），所以只能临时改系统默认；
/// 停止接收或退出程序时由调用方还原，用户原本的设置不会被永久改动。
/// </summary>
public static class DefaultDeviceSwitcher
{
    private const string PolicyConfigClientClassId = "870af99c-171d-4f9e-af0d-e63df40c2bc9";
    private const string PolicyConfigInterfaceId = "f8679f50-850a-41cf-9c72-430f290290c8";

    /// <summary>eConsole / eMultimedia / eCommunications 三个角色都要设，否则部分程序仍走旧设备。</summary>
    private static readonly int[] Roles = { 0, 1, 2 };

    /// <summary>把默认播放设备切到 <paramref name="deviceId"/>（端点 ID，形如 {0.0.0.00000000}.{guid}）。</summary>
    public static bool TrySetDefault(string deviceId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            error = "设备 ID 为空";
            return false;
        }

        object? client = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("PolicyConfig.Client")
                ?? Type.GetTypeFromCLSID(new Guid(PolicyConfigClientClassId));
            if (type is null)
            {
                error = "创建 PolicyConfig 客户端失败";
                return false;
            }

            client = Activator.CreateInstance(type);
            if (client is null)
            {
                error = "PolicyConfig 客户端为空";
                return false;
            }

            var policy = (IPolicyConfig)client;
            foreach (int role in Roles)
            {
                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr != 0)
                {
                    error = "SetDefaultEndpoint 失败，HRESULT=0x" + hr.ToString("X8");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (client is not null && Marshal.IsComObject(client))
            {
                Marshal.ReleaseComObject(client);
            }
        }
    }

    [ComImport]
    [Guid(PolicyConfigInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long defaultPeriodValue, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, out IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);

        /// <summary>role：0=eConsole，1=eMultimedia，2=eCommunications。</summary>
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}