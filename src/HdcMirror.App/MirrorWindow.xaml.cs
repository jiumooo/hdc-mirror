using System.Text;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HdcMirror.App.Audio;
using HdcMirror.App.Resources;
using HdcMirror.App.Video;
using HdcMirror.Core.Diagnostics;
using HdcMirror.Core.Hdc;
using HdcMirror.Core.Input;

namespace HdcMirror.App;

/// <summary>
/// 独立手机窗口：一条自绘控制条 + 一块严格按设备分辨率显示的镜像画面。
/// 竖屏时控制条在上方，横屏时在右侧（内容整体旋转 90°）；窗口比例始终锁死为设备比例，
/// 最大化也只是按比例放大到桌面边界，不铺满整屏。
/// </summary>
public partial class MirrorWindow
{
    /// <summary>竖屏控制条高度。</summary>
    private const double BarPortrait = 32d;

    /// <summary>横屏控制条宽度。</summary>
    private const double BarLandscape = 32d;

    private readonly ILogSink _log;
    private readonly DispatcherTimer _frameTimer;

    private MirrorSession? _session;
    private WriteableBitmap? _screenBitmap;
    private int _screenWidth;
    private int _screenHeight;

    private long _fpsWindowStart;
    private long _fpsWindowFrames;
    private int _fps;

    /// <summary>当前画面宽高比（宽 ÷ 高），随真实帧更新。</summary>
    private double _aspect = 1260d / 2720d;

    /// <summary>true = 横屏（宽大于高）：控制条在右，窗口按宽>高布局。</summary>
    private bool _landscape;

    /// <summary>防重入：代码在改窗口尺寸时不要再触发比例校正。</summary>
    private bool _fitting;

    private UitestInputChannel? _input;
    private HdcRunner? _inputRunner;
    private readonly HdcDevice _device;

    /// <summary>蓝牙音频接收器（独立子进程，见 Audio\AudioSinkHost.cs）。</summary>
    private readonly AudioSinkHost _audio = new();

    /// <summary>按下时的窗口内坐标（DIP），用于判断是点击还是拖拽。</summary>
    private Point? _pressPoint;

    /// <summary>上一次投递触摸移动的时间戳（毫秒），做 16ms 节流。</summary>
    private long _lastMoveTicks;

    /// <summary>窗口句柄来源，用来挂 WM_SIZING（比例锁 + 桌面边界钳制）。</summary>
    private HwndSource? _hwndSource;

    /// <summary>窗口最小宽度（物理像素），保证画面还有意义。</summary>
    private const int MinWindowWidthPx = 220;

    /// <summary>窗口最小高度（物理像素）。</summary>
    private const int MinWindowHeightPx = 220;

    /// <summary>被镜像设备的端点，用于窗口标题兜底与日志。</summary>
    public string DeviceSerial { get; }

    private double _lastWidth;
    private double _lastHeight;

    /// <summary>构造手机窗口。</summary>
    /// <param name="device">要镜像的设备。</param>
    /// <param name="title">控制条标题（设备名）。</param>
    /// <param name="log">日志出口。</param>
    public MirrorWindow(HdcDevice device, string? title, ILogSink log)
    {
        _log = log;
        _device = device;

        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? device.Serial : title!;
        DeviceSerial = device.Serial;
        Title = TitleText.Text;

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _frameTimer.Tick += OnFrameTick;

        ScreenHost.MouseLeftButtonDown += OnScreenLeftDown;
        ScreenHost.MouseMove += OnScreenMouseMove;
        ScreenHost.MouseLeftButtonUp += OnScreenLeftUp;
        ScreenHost.MouseWheel += OnScreenWheel;
        ScreenHost.MouseRightButtonUp += OnScreenRightUp;
        ScreenHost.Focusable = true;
        ScreenHost.PreviewTextInput += OnScreenTextInput;
        ScreenHost.PreviewKeyDown += OnScreenKeyDown;

        _audio.StateChanged += OnAudioStateChanged;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
    }

    // ── 生命周期 ──────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyOrientation();
        FitWindowToAspect(keepWidth: true);
        ClampWindowToWorkArea();
        StartMirror();
        _ = ConnectInputAsync();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _frameTimer.Stop();
        MirrorSession? session = _session;
        _session = null;
        session?.Dispose();
        _ = DisposeInputAsync();
        _audio.Dispose();
    }

    /// <summary>启动镜像会话：解码在后台，UI 线程只按定时器取最新一帧。</summary>
    private void StartMirror()
    {
        if (_session is { IsRunning: true })
        {
            return;
        }

        _session?.Dispose();
        _session = new MirrorSession(_log, OnMirrorStatus);
        _session.Start();

        _fps = 0;
        _fpsWindowStart = Environment.TickCount64;
        _fpsWindowFrames = 0;

        _frameTimer.Start();
        ShowStatus(Strings.Status_Mirror_Starting);
    }

    // ── 帧渲染 ────────────────────────────────────────────────────

    private void OnFrameTick(object? sender, EventArgs e)
    {
        MirrorSession? session = _session;
        if (session is null)
        {
            _frameTimer.Stop();
            return;
        }

        session.Frames.Consume(RenderFrame);
        UpdateStatus(session);
    }

    private void RenderFrame(byte[] pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride <= 0)
        {
            return;
        }

        bool geometryChanged = _screenBitmap is null
            || _screenBitmap.PixelWidth != width
            || _screenBitmap.PixelHeight != height;

        if (geometryChanged)
        {
            _screenBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            ScreenImage.Source = _screenBitmap;
            _screenWidth = width;
            _screenHeight = height;

            // 画面比例跟着真实帧走：设备转成横屏后窗口也跟着换成横向布局。
            double aspect = width / (double)height;
            if (Math.Abs(aspect - _aspect) > 0.001)
            {
                _aspect = aspect;
                bool landscape = aspect > 1d;
                if (landscape != _landscape)
                {
                    _landscape = landscape;
                    ApplyOrientation();
                }

                FitWindowToAspect(keepWidth: false);
                ClampWindowToWorkArea();
            }
        }

        WriteableBitmap? bitmap = _screenBitmap;
        if (bitmap is null)
        {
            return;
        }
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.WritePixels(
                new Int32Rect(0, 0, width, height),
                handle.AddrOfPinnedObject(),
                stride * height,
                stride,
                0,
                0);
        }
        finally
        {
            handle.Free();
        }

        UpdateZoomText();
    }

    private void UpdateStatus(MirrorSession session)
    {
        long decoded = session.DecodedFrames;
        long now = Environment.TickCount64;

        if (now - _fpsWindowStart >= 1000)
        {
            double seconds = Math.Max(0.001, (now - _fpsWindowStart) / 1000d);
            _fps = (int)Math.Round((decoded - _fpsWindowFrames) / seconds, MidpointRounding.AwayFromZero);
            _fpsWindowStart = now;
            _fpsWindowFrames = decoded;
        }

        if (decoded == 0)
        {
            ShowStatus(Strings.Status_Mirror_Starting);
            return;
        }

        long? since = session.MillisecondsSinceLastFrame;
        if (since is null || since >= 1500)
        {
            ShowStatus(Strings.Status_Mirror_Idle);
            return;
        }

        ShowStatus(string.Format(
            CultureInfo.CurrentCulture,
            Strings.Status_Mirror_Streaming,
            _screenWidth,
            _screenHeight,
            _fps));
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusHost.Visibility = Visibility.Visible;
    }

    private void OnMirrorStatus(MirrorStatus status, string? message)
    {
        switch (status)
        {
            case MirrorStatus.Failed:
                _frameTimer.Stop();
                ShowStatus(message ?? Strings.Err_Mirror_NoDevice);
                break;

            case MirrorStatus.Starting:
                ShowStatus(Strings.Status_Mirror_Starting);
                break;

            default:
                ShowStatus(Strings.Status_Mirror_Stopped);
                break;
        }
    }

    // ── 系统功能键（uitest keyEvent）────────────────────────────

    private void OnKeyHomeClick(object sender, RoutedEventArgs e) => SendKeyEvent("Home");

    private void OnKeyBackClick(object sender, RoutedEventArgs e) => SendKeyEvent("Back");

    private void OnKeyRecentClick(object sender, RoutedEventArgs e) => SendKeyEvent("2720");

    private void OnKeyPowerClick(object sender, RoutedEventArgs e) => SendKeyEvent("18");

    /// <summary>
    /// 走 uitest keyEvent 发系统键：uinput -K 对 Home/Back/Power 只回显不生效（实测），
    /// 只有 uitest 的 AAMS 通道能真正触发。
    /// </summary>
    private void SendKeyEvent(string keyName)
    {
        // 输入通道还没建好时按键也得能用：临时按设备序列号起一个 runner（命令本身是独立进程）。
        var runner = _inputRunner ?? new HdcRunner(deviceSerial: _device.Serial, log: _log);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await runner.ShellAsync($"uitest uiInput keyEvent {keyName}", CancellationToken.None)
                    .ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    _log.Warn("App.Input", $"keyEvent {keyName} 失败：{result.StandardError.Trim()}");
                }
            }
            catch (Exception ex)
            {
                _log.Error("App.Input", $"keyEvent {keyName} 异常。", ex);
            }
        });
    }
    // ── 音频（蓝牙 A2DP Sink）────────────────────────────────────

    /// <summary>控制条上的音频按钮：开＝把手机声音接到电脑，关＝断开。</summary>
    private void OnAudioToggleClick(object sender, RoutedEventArgs e)
    {
        if (_audio.IsRunning)
        {
            _audio.Stop();
            return;
        }

        ShowStatus(Strings.Status_Audio_Waiting);
        if (!_audio.Start())
        {
            ShowStatus(string.Format(
                CultureInfo.CurrentCulture,
                Strings.Status_Audio_FailedFormat,
                _audio.LastError ?? "audio"));
        }
    }

    /// <summary>子进程状态回传：读在后台线程，必须切回 UI 线程再动控件。</summary>
    private void OnAudioStateChanged(AudioSinkState state, string detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAudioStateChanged(state, detail)));
            return;
        }

        // 镜像状态每帧都刷 StatusText，音频状态不能抢同一条线（会被覆盖掉），
        // 所以常态只改图标 + 悬停提示；NoDevice / Failed 这种要用户动手的才写一次状态栏。
        string tooltip;
        switch (state)
        {
            case AudioSinkState.Starting:
            case AudioSinkState.WaitingForPhone:
                // 静音字形：还没接通
                AudioButton.Content = "\uE74F";
                tooltip = Strings.Status_Audio_Waiting;
                break;

            case AudioSinkState.Connected:
                AudioButton.Content = "\uE767";
                tooltip = Strings.Status_Audio_On;
                break;

            case AudioSinkState.NoDevice:
                AudioButton.Content = "\uE767";
                tooltip = Strings.Status_Audio_NoDevice;
                ShowStatus(tooltip);
                break;

            case AudioSinkState.Failed:
                AudioButton.Content = "\uE767";
                tooltip = string.Format(CultureInfo.CurrentCulture, Strings.Status_Audio_FailedFormat, detail);
                ShowStatus(tooltip);
                break;

            default:
                AudioButton.Content = "\uE767";
                tooltip = Strings.Status_Audio_Off;
                break;
        }

        AudioButton.ToolTip = tooltip;
        _log.Write(LogLevel.Info, "App.Audio", "音频状态=" + state
            + (string.IsNullOrEmpty(detail) ? string.Empty : " 详情=" + detail));
    }

    // ── 朝向与比例 ────────────────────────────────────────────────

    /// <summary>按当前朝向把控制条搬到上方或右侧，并把内容旋转到可读方向。</summary>
    private void ApplyOrientation()
    {
        if (_landscape)
        {
            DockPanel.SetDock(ControlBar, Dock.Right);
            ControlBar.Width = BarLandscape;
            ControlBar.Height = double.NaN;
            ControlBar.Padding = new Thickness(0, 6, 0, 6);
            ControlBar.BorderThickness = new Thickness(1, 0, 0, 0);
            BarContent.LayoutTransform = new RotateTransform(90);

            // 旋转后垂直方向排布：竖屏是「标题 … 缩放 按钮」，横屏改为从下往上读。
            BarContent.Margin = new Thickness(0, 6, 0, 6);
        }
        else
        {
            DockPanel.SetDock(ControlBar, Dock.Top);
            ControlBar.Height = BarPortrait;
            ControlBar.Width = double.NaN;
            ControlBar.Padding = new Thickness(6, 0, 6, 0);
            ControlBar.BorderThickness = new Thickness(0, 0, 0, 1);
            BarContent.LayoutTransform = Transform.Identity;
            BarContent.Margin = new Thickness(0);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitting)
        {
            return;
        }

        double deltaWidth = Math.Abs(e.NewSize.Width - _lastWidth);
        double deltaHeight = Math.Abs(e.NewSize.Height - _lastHeight);
        _lastWidth = e.NewSize.Width;
        _lastHeight = e.NewSize.Height;

        FitWindowToAspect(keepWidth: deltaWidth >= deltaHeight);
        UpdateZoomText();
    }

    /// <summary>
    /// 按比例校正窗口尺寸：<paramref name="keepWidth"/> 为 true 时以宽度为基准改高度，否则反过来。
    /// 校正过程用 <see cref="_fitting"/> 挡住重入。
    /// </summary>
    private void FitWindowToAspect(bool keepWidth)
    {
        if (_fitting)
        {
            return;
        }

        _fitting = true;
        try
        {
            if (keepWidth)
            {
                double screenWidth = Math.Max(120d, ActualWidth - (_landscape ? BarLandscape : 0d));
                double targetHeight = (screenWidth / _aspect) + (_landscape ? 0d : BarPortrait);
                if (Math.Abs(targetHeight - ActualHeight) > 0.5)
                {
                    Height = targetHeight;
                }
            }
            else
            {
                double screenHeight = Math.Max(120d, ActualHeight - (_landscape ? 0d : BarPortrait));
                double targetWidth = (screenHeight * _aspect) + (_landscape ? BarLandscape : 0d);
                if (Math.Abs(targetWidth - ActualWidth) > 0.5)
                {
                    Width = targetWidth;
                }
            }
        }
        finally
        {
            _fitting = false;
        }
    }

    private void UpdateZoomText()
    {
        double shown = ScreenHost.ActualHeight;
        if (shown <= 0 || _screenHeight <= 0)
        {
            ZoomText.Text = string.Empty;
            return;
        }

        double zoom = shown / _screenHeight;
        ZoomText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Ui_ZoomFormat, zoom);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        if (WindowState != WindowState.Maximized)
        {
            return;
        }

        // WPF 的最大化会铺满工作区，破坏设备比例：改回普通状态，再按比例取工作区能容下的最大尺寸。
        WindowState = WindowState.Normal;
        ApplyMaximizedFit();
    }

    /// <summary>按比例放大到桌面边界：取工作区能容纳的最大屏幕尺寸，居中摆放。</summary>
    private void ApplyMaximizedFit()
    {
        Rect work = SystemParameters.WorkArea;

        double chromeWidth = _landscape ? BarLandscape : 0d;
        double chromeHeight = _landscape ? 0d : BarPortrait;

        double maxScreenWidth = Math.Max(120d, work.Width - chromeWidth - 8d);
        double maxScreenHeight = Math.Max(120d, work.Height - chromeHeight - 8d);

        double screenWidth = Math.Min(maxScreenWidth, maxScreenHeight * _aspect);
        double screenHeight = screenWidth / _aspect;

        _fitting = true;
        try
        {
            Width = screenWidth + chromeWidth;
            Height = screenHeight + chromeHeight;
            Left = work.Left + ((work.Width - Width) / 2d);
            Top = work.Top + ((work.Height - Height) / 2d);

            if (_lastWidth == 0)
            {
                _lastWidth = Width;
                _lastHeight = Height;
            }
        }
        finally
        {
            _fitting = false;
        }

        UpdateZoomText();
    }

    // ── 控制条交互 ────────────────────────────────────────────────

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            return;
        }

        WindowState = WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // 控制条空白处可拖动窗口；双击切最大化。按钮点击由按钮自己处理，不会走到这里。
        if (e.OriginalSource is DependencyObject source && IsInsideControlBar(source))
        {
            if (e.ClickCount == 2)
            {
                OnMaximizeClick(this, new RoutedEventArgs());
                return;
            }

            DragMove();
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    private static bool IsInsideControlBar(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Border { Name: "ControlBar" })
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
    // ── 窗口边界：比例锁死 + 不允许超出桌面 ─────────────────────────

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSizing)
        {
            handled = ResizeWithAspect(wParam.ToInt32(), lParam);
        }
        else if (msg == WmMoving)
        {
            var rect = Marshal.PtrToStructure<Win32Rect>(lParam);
            ClampToWorkArea(ref rect);
            Marshal.StructureToPtr(rect, lParam, false);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private const int WmSizing = 0x0214;

    private const int WmMoving = 0x0216;

    /// <summary>
    /// 用户拖边框改大小时：按设备比例换算另一维，并把窗口整体压进当前显示器的工作区，
    /// 保证任何一边都拖不出桌面。
    /// </summary>
    /// <param name="edge">WM_SIZING 的方向码。</param>
    /// <param name="lParam">指向 RECT 的指针。</param>
    /// <returns>是否已处理。</returns>
    private bool ResizeWithAspect(int edge, IntPtr lParam)
    {
        var rect = Marshal.PtrToStructure<Win32Rect>(lParam);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX <= 0 ? 1d : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1d : dpi.DpiScaleY;

        double chromeWidth = (_landscape ? BarLandscape : 0d) * scaleX;
        double chromeHeight = (_landscape ? 0d : BarPortrait) * scaleY;

        bool horizontal = edge is 1 or 2 or 4 or 5 or 7 or 8;
        bool fromLeft = edge is 1 or 4 or 7;
        bool fromTop = edge is 3;

        int width = Math.Max(MinWindowWidthPx, rect.Right - rect.Left);
        int height = Math.Max(MinWindowHeightPx, rect.Bottom - rect.Top);

        if (horizontal)
        {
            double screenWidth = Math.Max(80d * scaleX, width - chromeWidth);
            height = (int)Math.Round((screenWidth / _aspect) + chromeHeight);
        }
        else
        {
            double screenHeight = Math.Max(80d * scaleY, height - chromeHeight);
            width = (int)Math.Round((screenHeight * _aspect) + chromeWidth);
        }

        if (fromLeft)
        {
            rect.Left = rect.Right - width;
        }
        else
        {
            rect.Right = rect.Left + width;
        }

        if (fromTop)
        {
            rect.Top = rect.Bottom - height;
        }
        else
        {
            rect.Bottom = rect.Top + height;
        }

        ClampToWorkArea(ref rect);
        Marshal.StructureToPtr(rect, lParam, false);
        return true;
    }

    /// <summary>把窗口矩形压进当前显示器工作区；放不下时按比例缩到能放下。</summary>
    /// <param name="rect">待修正的窗口矩形（物理像素）。</param>
    private void ClampToWorkArea(ref Win32Rect rect)
    {
        Win32Rect work = GetWorkAreaPixels();
        int workWidth = work.Right - work.Left;
        int workHeight = work.Bottom - work.Top;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width > workWidth || height > workHeight)
        {
            double shrink = Math.Min((double)workWidth / width, (double)workHeight / height);
            width = (int)Math.Floor(width * shrink);
            height = (int)Math.Floor(height * shrink);
            rect.Right = rect.Left + width;
            rect.Bottom = rect.Top + height;
        }

        if (rect.Right > work.Right)
        {
            rect.Left -= rect.Right - work.Right;
            rect.Right = work.Right;
        }

        if (rect.Bottom > work.Bottom)
        {
            rect.Top -= rect.Bottom - work.Bottom;
            rect.Bottom = work.Bottom;
        }

        if (rect.Left < work.Left)
        {
            rect.Right += work.Left - rect.Left;
            rect.Left = work.Left;
        }

        if (rect.Top < work.Top)
        {
            rect.Bottom += work.Top - rect.Top;
            rect.Top = work.Top;
        }
    }

    /// <summary>取当前显示器工作区（物理像素）。</summary>
    /// <returns>工作区矩形。</returns>
    private Win32Rect GetWorkAreaPixels()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        IntPtr monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return info.rcWork;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Rect area = SystemParameters.WorkArea;
        return new Win32Rect
        {
            Left = (int)Math.Round(area.Left * dpi.DpiScaleX),
            Top = (int)Math.Round(area.Top * dpi.DpiScaleY),
            Right = (int)Math.Round(area.Right * dpi.DpiScaleX),
            Bottom = (int)Math.Round(area.Bottom * dpi.DpiScaleY),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Win32Rect rcMonitor;
        public Win32Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    /// <summary>
    /// 把当前窗口尺寸压进桌面工作区：先按设备比例换算出「桌面能容下的最大画面」，
    /// 再取窗口当前尺寸与该上限的较小值。打开窗口、切朝向、跨屏时都要过一遍。
    /// </summary>
    private void ClampWindowToWorkArea()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Rect work = SystemParameters.WorkArea;

        double chromeWidth = _landscape ? BarLandscape : 0d;
        double chromeHeight = _landscape ? 0d : BarPortrait;

        double maxScreenWidth = Math.Max(120d, Math.Min(
            work.Width - chromeWidth - 16d,
            ((work.Height - chromeHeight - 16d) * _aspect)));
        double maxScreenHeight = maxScreenWidth / _aspect;

        double targetWidth = Math.Min(Width <= 0 ? maxScreenWidth + chromeWidth : Width, maxScreenWidth + chromeWidth);
        double targetHeight = Math.Min(Height <= 0 ? maxScreenHeight + chromeHeight : Height, maxScreenHeight + chromeHeight);

        _fitting = true;
        try
        {
            Width = targetWidth;
            Height = targetHeight;

            if (Left + Width > work.Right)
            {
                Left = Math.Max(work.Left, work.Right - Width);
            }

            if (Top + Height > work.Bottom)
            {
                Top = Math.Max(work.Top, work.Bottom - Height);
            }

            if (Left < work.Left)
            {
                Left = work.Left;
            }

            if (Top < work.Top)
            {
                Top = work.Top;
            }

            _lastWidth = ActualWidth;
            _lastHeight = ActualHeight;
            _ = dpi;
        }
        finally
        {
            _fitting = false;
        }

        UpdateZoomText();
    }
    // ── 输入：触摸 / 滚轮 / 返回 ────────────────────────────────────

    /// <summary>建立到设备 uitest daemon 的输入通道；失败只记日志，画面继续可用。</summary>
    /// <returns>异步任务。</returns>
    private async Task ConnectInputAsync()
    {
        try
        {
            _inputRunner ??= new HdcRunner(deviceSerial: _device.Serial, log: _log);
            _input = await UitestInputChannel.ConnectAsync(_inputRunner, _log).ConfigureAwait(true);
        }
        catch (HdcException ex)
        {
            _log.Warn("input", $"输入通道不可用：{ex.Message}");
        }
    }

    private async Task DisposeInputAsync()
    {
        UitestInputChannel? input = _input;
        _input = null;
        if (input is not null)
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>把窗口内坐标换算成设备像素坐标。</summary>
    /// <param name="point">窗口内坐标（DIP）。</param>
    /// <returns>设备像素坐标；画面还没就绪时返回 null。</returns>
    private System.Drawing.Point? ToDevice(Point point)
    {
        double width = ScreenHost.ActualWidth;
        double height = ScreenHost.ActualHeight;
        if (width <= 0 || height <= 0 || _screenWidth <= 0 || _screenHeight <= 0)
        {
            return null;
        }

        int x = (int)Math.Round(Math.Clamp(point.X / width, 0d, 1d) * _screenWidth);
        int y = (int)Math.Round(Math.Clamp(point.Y / height, 0d, 1d) * _screenHeight);
        return new System.Drawing.Point(
            Math.Clamp(x, 0, Math.Max(0, _screenWidth - 1)),
            Math.Clamp(y, 0, Math.Max(0, _screenHeight - 1)));
    }

    private void OnScreenLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_input is not { IsConnected: true })
        {
            return;
        }

        System.Drawing.Point? device = ToDevice(e.GetPosition(ScreenHost));
        if (device is null)
        {
            return;
        }

        _pressPoint = e.GetPosition(ScreenHost);
        _lastMoveTicks = 0;
        _input.TouchDown(device.Value.X, device.Value.Y);
        ScreenHost.CaptureMouse();
        ScreenHost.Focus();
        e.Handled = true;
    }

    private void OnScreenMouseMove(object sender, MouseEventArgs e)
    {
        if (_input is not { IsConnected: true } || _pressPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastMoveTicks < 16)
        {
            return;
        }

        _lastMoveTicks = now;
        System.Drawing.Point? device = ToDevice(e.GetPosition(ScreenHost));
        if (device is not null)
        {
            _input.TouchMove(device.Value.X, device.Value.Y);
        }
    }

    private void OnScreenLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressPoint is null)
        {
            return;
        }

        ScreenHost.ReleaseMouseCapture();
        _pressPoint = null;

        if (_input is { IsConnected: true })
        {
            _input.TouchUp();
        }

        e.Handled = true;
    }

    /// <summary>滚轮：在指针位置做一次短距离上下滑动（鸿蒙多数应用只认触摸滑动，不认鼠标滚轮）。</summary>
    private void OnScreenWheel(object sender, MouseWheelEventArgs e)
    {
        if (_input is not { IsConnected: true })
        {
            return;
        }

        System.Drawing.Point? center = ToDevice(e.GetPosition(ScreenHost));
        if (center is null)
        {
            return;
        }

        int step = e.Delta > 0 ? -180 : 180;
        int startY = Math.Clamp(center.Value.Y, 40, Math.Max(41, _screenHeight - 40));
        int endY = Math.Clamp(startY + step, 10, Math.Max(11, _screenHeight - 10));

        _input.TouchDown(center.Value.X, startY);
        for (int i = 1; i <= 6; i++)
        {
            _input.TouchMove(center.Value.X, startY + ((endY - startY) * i / 6));
        }

        _input.TouchUp();
        e.Handled = true;
    }

    /// <summary>右键 = 返回键。</summary>
    private async void OnScreenRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_inputRunner is null)
        {
            return;
        }

        try
        {
            await _inputRunner.ShellAsync("uitest uiInput keyEvent Back").ConfigureAwait(true);
        }
        catch (HdcException ex)
        {
            _log.Warn("input", $"返回键注入失败：{ex.Message}");
        }
    }
    // ── 键盘输入 ──────────────────────────────────────────────────
    // 手机侧没有可写的常驻 shell（hdc 3.2.0c 不支持 TTY），每条注入都是一次进程启动（约 400 ms），
    // 所以字符先攒在本地，停顿 700 ms 或遇到回车/退格时整批发一次，避免一个字一次进程。
    private readonly StringBuilder _typing = new();
    private DispatcherTimer? _typingTimer;

    /// <summary>把攒下的字符整批发给手机输入框。</summary>
    private async Task FlushTypingAsync()
    {
        _typingTimer?.Stop();
        if (_inputRunner is null || _typing.Length == 0)
        {
            return;
        }

        string text = _typing.ToString();
        _typing.Clear();

        try
        {
            await _inputRunner.ShellAsync("uitest uiInput text '" + text.Replace("'", "'\\''") + "'").ConfigureAwait(true);
        }
        catch (HdcException ex)
        {
            _log.Warn("input", $"文本注入失败：{ex.Message}");
        }
    }

    private void RestartTypingTimer()
    {
        _typingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _typingTimer.Tick -= OnTypingIdle;
        _typingTimer.Tick += OnTypingIdle;
        _typingTimer.Stop();
        _typingTimer.Start();
    }

    private async void OnTypingIdle(object? sender, EventArgs e) => await FlushTypingAsync().ConfigureAwait(true);

    private void OnScreenTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
        {
            return;
        }

        _typing.Append(e.Text);
        RestartTypingTimer();
        e.Handled = true;
    }

    private async void OnScreenKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // Ctrl 组合键 = 手机系统键。系统键必须走 uitest uiInput keyEvent：
        // uinput -K 对 Home/Back/Power 这类系统键是「假成功」（回显正常、屏幕无反应，真机实测过）。
        if (ctrl)
        {
            string? systemKey = e.Key switch
            {
                Key.H => "Home",
                Key.B => "Back",
                Key.J => "2720",
                Key.P => "Power",
                Key.Up => "16",
                Key.Down => "17",
                _ => null,
            };

            if (systemKey is not null)
            {
                e.Handled = true;
                await FlushTypingAsync().ConfigureAwait(true);
                await SendShellAsync("uitest uiInput keyEvent " + systemKey).ConfigureAwait(true);
                return;
            }
        }

        int keyCode = e.Key switch
        {
            Key.Enter => 2054,
            Key.Back => 2055,
            Key.Tab => 2049,
            Key.Left => 2012,
            Key.Right => 2013,
            Key.Up => 2014,
            Key.Down => 2015,
            _ => 0,
        };

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await FlushTypingAsync().ConfigureAwait(true);
            await SendShellAsync("uitest uiInput keyEvent Back").ConfigureAwait(true);
            return;
        }

        if (keyCode == 0)
        {
            return;
        }

        e.Handled = true;
        await FlushTypingAsync().ConfigureAwait(true);
        await SendShellAsync($"uinput -K -d {keyCode} -u {keyCode}").ConfigureAwait(true);
    }

    private async Task SendShellAsync(string command)
    {
        if (_inputRunner is null)
        {
            return;
        }

        try
        {
            await _inputRunner.ShellAsync(command).ConfigureAwait(true);
        }
        catch (HdcException ex)
        {
            _log.Warn("input", $"按键注入失败：{ex.Message}");
        }
    }
}