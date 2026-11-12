using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HdcMirror.App.Services;

/// <summary>
/// 取显示器可用区域与窗口真实外框。
/// <para>
/// <see cref="SystemParameters.WorkArea"/> 与本窗口的 <c>Height</c> 都不可靠：前者在部分机器上
/// 报出的高度比真实可用区多 80 像素；后者不含 DWM 扩展边框（实测比可视外框矮 79 像素）。
/// 因此落位与居中一律以 Win32 / DWM 的实测矩形为准。
/// </para>
/// </summary>
internal static class WindowPlacement
{
    private const int MonitorDefaultToNearest = 2;
    private const int DwmExtendedFrameBounds = 9;

    /// <summary>当前窗口所在显示器的可用区域（DIP，已排除任务栏）。</summary>
    /// <param name="window">任意窗口，用于确定显示器与 DPI。</param>
    public static Rect WorkArea(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        return Scale(handle, info.Work);
    }

    /// <summary>窗口真正画在屏幕上的外框（DIP，含标题栏与边框，不含投影）。</summary>
    /// <param name="window">目标窗口。</param>
    public static Rect Bounds(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return new Rect(window.Left, window.Top, window.Width, window.Height);
        }

        if (DwmGetWindowAttribute(handle, DwmExtendedFrameBounds, out NativeRect rect, Marshal.SizeOf<NativeRect>()) != 0
            && !GetWindowRect(handle, out rect))
        {
            return new Rect(window.Left, window.Top, window.Width, window.Height);
        }

        return Scale(handle, rect);
    }

    private static Rect Scale(IntPtr handle, NativeRect rect)
    {
        double scale = 1d;
        if (handle != IntPtr.Zero && Environment.OSVersion.Version.Build >= 14393)
        {
            uint dpi = GetDpiForWindow(handle);
            if (dpi > 0)
            {
                scale = 96d / dpi;
            }
        }

        Rect raw = rect.ToRect();
        return new Rect(raw.Left * scale, raw.Top * scale, raw.Width * scale, raw.Height * scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out NativeRect value, int size);
}
