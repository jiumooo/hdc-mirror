using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HdcMirror.App;

/// <summary>
/// 窗口置前辅助：托盘单击唤回窗口时，仅 <see cref="Window.Activate"/> 常被系统拒绝，
/// 需要额外请求前台焦点（例如从托盘唤回、第二次启动唤醒已有实例时）。
/// </summary>
internal static class WindowActivation
{
    /// <summary>请求把指定窗口带到前台。</summary>
    internal static void BringToForeground(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = SetForegroundWindow(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
