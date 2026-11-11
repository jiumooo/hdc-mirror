using System.Text;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HdcMirror.App.Audio;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;
using HdcMirror.App.Video;
using HdcMirror.App.Views;
using HdcMirror.Core;
using HdcMirror.Core.Diagnostics;
using HdcMirror.Core.Hdc;
using HdcMirror.Core.Input;

namespace HdcMirror.App;

/// <summary>
/// 独立手机窗口：一条自绘控制条 + 一块严格按设备比例显示的镜像画面。
/// 控制条**一律固定在窗口上方横排**（横屏时窗口变宽、条子仍在上面，不旋转、不搬到右侧），
/// 窗口比例始终锁死为设备比例，最大化也只是按比例放大到桌面边界，不铺满整屏。
/// </summary>
public partial class MirrorWindow
{
    /// <summary>控制条高度（横竖屏一致）。</summary>
    private const double BarHeight = 32d;

    /// <summary>虚拟屏幕四角圆角半径。</summary>
    private const double ScreenCornerRadius = 10d;

    /// <summary>页面覆盖层打开时，缩小后的虚拟屏目标宽度。</summary>
    private const double ThumbTargetWidth = 180d;

    /// <summary>缩小 / 还原动画时长（毫秒）。</summary>
    private const double ScreenAnimationMs = 180d;

    /// <summary>触摸移动节流（毫秒）。</summary>
    private const int MoveThrottleMs = 16;

    /// <summary>鼠标按下后位移小于该值（DIP）视为点击，不进设备做 down/move/up。</summary>
    private const double ClickMaxDistance = 8d;

    /// <summary>按下超过该时长（毫秒）才补发 touchDown，让设备侧识别长按。</summary>
    private const int LongPressMs = 400;

    /// <summary>一次滑动拆成几段 move。</summary>
    private const int SwipeSteps = 6;

    /// <summary>滑动起点 / 终点离屏幕边缘的余量（设备像素）。</summary>
    private const int SwipeMargin = 6;

    /// <summary>解码帧最多允许多少像素的编码补边（右/下）被当作黑框裁掉。</summary>
    private const int MaxPaddingPixels = 96;

    /// <summary>壁纸 / 锁屏缓存文件名（数据目录下，缺省时回退到最后一帧模糊）。</summary>
    private static readonly string[] LockScreenFileNames =
    {
        "lockscreen.png",
        "lockscreen.jpg",
        "lockscreen.jpeg",
        "lock.png",
        "lock.jpg",
    };

    /// <summary>前台包名解析时认的字段名。</summary>
    private static readonly string[] BundleKeys =
    {
        "bundle name",
        "bundleName",
        "bundle_name",
    };

    private readonly ILogSink _log;
    private readonly DispatcherTimer _frameTimer;

    private MirrorSession? _session;
    private WriteableBitmap? _screenBitmap;
    private int _screenWidth;
    private int _screenHeight;

    /// <summary>设备真实画面宽度（来自设备信息的分辨率，用于剔除编码补边；0 = 未知）。</summary>
    private int _contentWidth;

    /// <summary>设备真实画面高度（来自设备信息的分辨率，用于剔除编码补边；0 = 未知）。</summary>
    private int _contentHeight;

    private long _fpsWindowStart;
    private long _fpsWindowFrames;
    private int _fps;

    /// <summary>当前画面宽高比（宽 ÷ 高），随真实帧更新。</summary>
    private double _aspect = 1260d / 2720d;

    /// <summary>防重入：代码在改窗口尺寸时不要再触发比例校正。</summary>
    private bool _fitting;

    private UitestInputChannel? _input;
    private HdcRunner? _inputRunner;
    private readonly HdcDevice _device;

    /// <summary>蓝牙音频接收器（独立子进程，见 Audio\AudioSinkHost.cs）。</summary>
    private readonly AudioSinkHost _audio = new();

    /// <summary>鼠标按住中（还没抬起、也没取消）。抬起后立刻置 false，之后的 move 一律不注入。</summary>
    private bool _pressed;

    /// <summary>已经把 touchDown 发给设备（拖拽或长按）。没发过就只发一条 click 命令。</summary>
    private bool _downSent;

    /// <summary>按下点在画面内的坐标（DIP，相对 <see cref="FrameSurface"/>）。</summary>
    private Point _pressSurface;

    /// <summary>按下点换算出的设备像素坐标。</summary>
    private System.Drawing.Point _pressDevice;

    /// <summary>按下的时间戳（毫秒），用于长按判定。</summary>
    private long _pressTicks;

    /// <summary>上一次投递触摸移动的时间戳（毫秒），做 16ms 节流。</summary>
    private long _lastMoveTicks;

    /// <summary>长按计时器：按住不动超过 400ms 才补发一次 touchDown。</summary>
    private DispatcherTimer? _longPressTimer;

    /// <summary>滚轮累计余量（设备像素），保证小幅度滚动也能累加成一次滑动。</summary>
    private double _wheelRemainder;

    /// <summary>前台是否桌面（Launcher）：null = 还没拿到可靠信号。</summary>
    private bool? _launcher;

    /// <summary>上次探测前台包名的时间戳（毫秒）。</summary>
    private long _launcherStamp;

    /// <summary>探测是否在跑，避免滚轮每格都发一次进程。</summary>
    private bool _launcherProbeRunning;

    /// <summary>探测连续失败次数：连续失败说明这台机器上拿不到包名，放弃并回退成上下滑动。</summary>
    private int _launcherFailures;

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

    /// <summary>true = 当前处于「放大」状态（按比例铺到工作区极限）。</summary>
    private bool _isMaximized;

    /// <summary>放大前的窗口 Left/Top/Width/Height，还原时精确恢复。</summary>
    private Rect? _restoreBounds;

    /// <summary>页面覆盖层是否打开（虚拟屏缩小状态）。</summary>
    private bool _overlayOpen;

    /// <summary>覆盖层打开前虚拟屏的宽度（DIP）。</summary>
    private double _screenRestoreWidth;

    /// <summary>覆盖层打开前虚拟屏的高度（DIP）。</summary>
    private double _screenRestoreHeight;

    /// <summary>是否已经收到过至少一帧真实画面。</summary>
    private bool _hasFrame;

    /// <summary>最后一帧的冻结副本，用作「停止后」的背景。</summary>
    private BitmapSource? _lastFrameSnapshot;

    /// <summary>是否已经找过锁屏缓存文件。</summary>
    private bool _lockScreenChecked;

    /// <summary>锁屏缓存图片（拿到才非 null）。</summary>
    private BitmapImage? _lockScreen;

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
        ScreenHost.MouseLeave += OnScreenMouseLeave;
        ScreenHost.LostMouseCapture += OnScreenLostMouseCapture;
        ScreenHost.MouseWheel += OnScreenWheel;
        ScreenHost.MouseRightButtonUp += OnScreenRightUp;
        ScreenHost.Focusable = true;
        ScreenHost.PreviewTextInput += OnScreenTextInput;
        ScreenHost.PreviewKeyDown += OnScreenKeyDown;

        // 圆角裁剪：虚拟屏四角按真实尺寸生成 Clip，保证画面不会漏出直角。
        FrameSurface.SizeChanged += OnFrameSurfaceSizeChanged;

        _audio.StateChanged += OnAudioStateChanged;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
    }

    // ── 生命周期 ──────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyFrameClip();

        // 还没出画面：先把锁屏 / 最后一帧 + 设备信息铺上，别只给一块纯黑。
        ShowIdleLayer();
        _ = FillIdleInfoAsync();

        FitWindowToAspect(keepWidth: true);
        ClampWindowToWorkArea();
        StartMirror();
        _ = ConnectInputAsync();
        ProbeLauncherAsync();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _frameTimer.Stop();
        _longPressTimer?.Stop();
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

        // 编码器常把宽度/高度补齐到 16 的倍数，多出来的右/下补边就是用户看到的黑框：
        // 只取左上角「真有内容」的那一块，几何与坐标换算都以这一块为准。
        int visibleWidth = width;
        int visibleHeight = height;
        if (TryGetContentSize(width, height, out int contentWidth, out int contentHeight))
        {
            visibleWidth = contentWidth;
            visibleHeight = contentHeight;
        }

        bool geometryChanged = _screenBitmap is null
            || _screenBitmap.PixelWidth != visibleWidth
            || _screenBitmap.PixelHeight != visibleHeight;

        if (geometryChanged)
        {
            _screenBitmap = new WriteableBitmap(visibleWidth, visibleHeight, 96, 96, PixelFormats.Bgra32, null);
            ScreenImage.Source = _screenBitmap;
            _screenWidth = visibleWidth;
            _screenHeight = visibleHeight;

            // 画面比例跟着真实帧走（设备转成横屏时窗口也跟着变宽，控制条仍在上面）。
            double aspect = visibleWidth / (double)visibleHeight;
            if (Math.Abs(aspect - _aspect) > 0.001)
            {
                _aspect = aspect;
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
            // 源缓冲区仍按原始 stride 读，但只写 visibleWidth×visibleHeight 这一块，
            // 因此补边像素永远进不了显示位图。
            bitmap.WritePixels(
                new Int32Rect(0, 0, visibleWidth, visibleHeight),
                handle.AddrOfPinnedObject(),
                stride * visibleHeight,
                stride,
                0,
                0);
        }
        finally
        {
            handle.Free();
        }

        // 首帧到达：收起锁屏 / 信息层，并留一份冻结副本当背景。
        if (!_hasFrame)
        {
            _hasFrame = true;
            IdleHost.Visibility = Visibility.Collapsed;
            CaptureLastFrame();
        }
    }

    /// <summary>
    /// 判断解码帧是不是带了补边（编码分辨率大于设备真实画面）。
    /// 只在「补边不超过 96px 且宽高比几乎不变」时才认定，避免把真正的画面裁掉。
    /// </summary>
    /// <param name="frameWidth">解码宽度。</param>
    /// <param name="frameHeight">解码高度。</param>
    /// <param name="contentWidth">真实内容宽度。</param>
    /// <param name="contentHeight">真实内容高度。</param>
    /// <returns>需要裁剪时为 <c>true</c>。</returns>
    private bool TryGetContentSize(int frameWidth, int frameHeight, out int contentWidth, out int contentHeight)
    {
        contentWidth = frameWidth;
        contentHeight = frameHeight;

        int knownWidth = _contentWidth;
        int knownHeight = _contentHeight;
        if (knownWidth <= 0 || knownHeight <= 0)
        {
            return false;
        }

        // 分辨率文本可能记的是竖屏（设备转横屏后帧是宽×矮），两种取向都试一次，取补边更少的那个。
        if (TryAcceptContent(frameWidth, frameHeight, knownWidth, knownHeight, out int padX, out int padY))
        {
            contentWidth = knownWidth;
            contentHeight = knownHeight;
            return padX > 0 || padY > 0;
        }

        if (TryAcceptContent(frameWidth, frameHeight, knownHeight, knownWidth, out padX, out padY))
        {
            contentWidth = knownHeight;
            contentHeight = knownWidth;
            return padX > 0 || padY > 0;
        }

        return false;
    }

    /// <summary>校验一组候选内容尺寸是否真的是「同一画面的补边版本」。</summary>
    /// <param name="frameWidth">解码宽度。</param>
    /// <param name="frameHeight">解码高度。</param>
    /// <param name="candidateWidth">候选内容宽度。</param>
    /// <param name="candidateHeight">候选内容高度。</param>
    /// <param name="padX">右侧补边像素。</param>
    /// <param name="padY">下方补边像素。</param>
    /// <returns>可用时返回 <c>true</c>。</returns>
    private static bool TryAcceptContent(
        int frameWidth,
        int frameHeight,
        int candidateWidth,
        int candidateHeight,
        out int padX,
        out int padY)
    {
        padX = 0;
        padY = 0;

        if (candidateWidth <= 0 || candidateHeight <= 0)
        {
            return false;
        }

        if (candidateWidth > frameWidth || candidateHeight > frameHeight)
        {
            return false;
        }

        padX = frameWidth - candidateWidth;
        padY = frameHeight - candidateHeight;
        if (padX > MaxPaddingPixels || padY > MaxPaddingPixels)
        {
            return false;
        }

        double frameAspect = frameWidth / (double)frameHeight;
        double contentAspect = candidateWidth / (double)candidateHeight;

        // 补边会轻微改变宽高比，但不会差到 2% 以上；差得多说明这是另一块画面，别裁。
        return Math.Abs(frameAspect - contentAspect) <= 0.02;
    }

    /// <summary>从「1260x2720 / 1260×2720 / 1260*2720」这类分辨率文本里取宽高。</summary>
    /// <param name="text">设备信息里的分辨率字段。</param>
    /// <param name="width">解析出的宽度。</param>
    /// <param name="height">解析出的高度。</param>
    /// <returns>解析成功返回 <c>true</c>。</returns>
    private static bool TryParseResolution(string? text, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text!.Split(new[] { 'x', 'X', '×', '*' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0].Trim(), out int parsedWidth) || !int.TryParse(parts[1].Trim(), out int parsedHeight))
        {
            return false;
        }

        if (parsedWidth <= 0 || parsedHeight <= 0)
        {
            return false;
        }

        width = parsedWidth;
        height = parsedHeight;
        return true;
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

        // 只留帧率：缩放倍率已按用户要求整段去掉（连布局占位一起）。
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
        if (!_overlayOpen)
        {
            StatusHost.Visibility = Visibility.Visible;
        }
    }

    private void OnMirrorStatus(MirrorStatus status, string? message)
    {
        switch (status)
        {
            case MirrorStatus.Failed:
                _frameTimer.Stop();
                ShowStatus(message ?? Strings.Err_Mirror_NoDevice);
                ShowIdleLayer();
                break;

            case MirrorStatus.Starting:
                ShowStatus(Strings.Status_Mirror_Starting);
                break;

            default:
                ShowStatus(Strings.Status_Mirror_Stopped);
                break;
        }
    }

    // ── 未开始镜像时的锁屏 + 设备信息 ─────────────────────────────

    /// <summary>把锁屏层铺出来：锁屏截图 → 最后一帧模糊 → 纯黑 + 提示。</summary>
    private void ShowIdleLayer()
    {
        IdleHost.Visibility = Visibility.Visible;
        RefreshIdleBackdrop();
    }

    /// <summary>背景优先级：数据目录里的锁屏缓存 &gt; 当前画面最后一帧（外层再做模糊）。</summary>
    private void RefreshIdleBackdrop()
    {
        if (!_lockScreenChecked)
        {
            _lockScreenChecked = true;
            _lockScreen = TryLoadLockScreen();
        }

        ImageSource? source = _lockScreen ?? _lastFrameSnapshot;
        IdleBackdrop.Source = source;
        IdleBackdrop.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        IdleHint.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>找锁屏截图缓存：只认数据目录（%APPDATA%\HdcMirror）下的几个固定文件名。</summary>
    /// <returns>解码后的图片；没有或读不了时返回 null。</returns>
    private BitmapImage? TryLoadLockScreen()
    {
        foreach (string name in LockScreenFileNames)
        {
            string path = Path.Combine(AppPaths.DataRoot, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException)
            {
                _log.Warn("idle", $"锁屏缓存不可用（{name}）：{ex.Message}");
            }
        }

        return null;
    }

    /// <summary>把当前这一帧拷一份冻结副本，供停止后当背景（只有第一次调用会拷）。</summary>
    private void CaptureLastFrame()
    {
        WriteableBitmap? bitmap = _screenBitmap;
        if (bitmap is null || _lastFrameSnapshot is not null)
        {
            return;
        }

        try
        {
            var copy = new WriteableBitmap(bitmap);
            copy.Freeze();
            _lastFrameSnapshot = copy;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            _log.Warn("render", $"缓存最后一帧失败：{ex.Message}");
        }
    }

    /// <summary>读九项设备信息，叠在锁屏画面上（文字在毛玻璃之上，保持清晰）。</summary>
    /// <returns>异步任务。</returns>
    private async Task FillIdleInfoAsync()
    {
        HdcDeviceInfo info = HdcDeviceInfo.Empty;
        try
        {
            HdcRunner runner = _inputRunner ?? new HdcRunner(deviceSerial: _device.Serial, log: _log);
            var reader = new HdcDeviceInfoReader(runner, _log);
            info = await reader.ReadAsync().ConfigureAwait(true);
        }
        catch (HdcException ex)
        {
            _log.Warn("device", $"读取设备信息失败：{ex.Message}");
        }

        // 拿到真实分辨率后，解码帧若带编码补边就能裁掉，画面不会出现右/下黑边。
        if (TryParseResolution(info.Resolution, out int contentWidth, out int contentHeight))
        {
            _contentWidth = contentWidth;
            _contentHeight = contentHeight;
        }

        IdleInfoList.Children.Clear();
        AddIdleInfoRow(Strings.Dev_Info_Name, Value(info.Name, _device.Serial));
        AddIdleInfoRow(Strings.Dev_Info_Brand, Value(info.Brand));
        AddIdleInfoRow(Strings.Dev_Info_Model, Value(info.Model));
        AddIdleInfoRow(Strings.Dev_Info_Os, Value(info.HarmonyVersion));
        AddIdleInfoRow(Strings.Dev_Info_Kernel, Value(info.KernelVersion));
        AddIdleInfoRow(Strings.Dev_Info_Cpu, Value(info.Cpu));
        AddIdleInfoRow(Strings.Dev_Info_Resolution, Value(info.Resolution, ResolutionFallback()));
        AddIdleInfoRow(Strings.Dev_Info_Memory, Value(info.Memory));
        AddIdleInfoRow(Strings.Dev_Info_Address, Value(_device.Serial));
    }

    /// <summary>设备信息里没给分辨率时的回退：优先用已知的设备真实画面尺寸，其次用当前解码尺寸。</summary>
    /// <returns>形如 <c>1260×2720</c> 的文本；都没有时返回 <c>null</c>。</returns>
    private string? ResolutionFallback()
    {
        if (_contentWidth > 0 && _contentHeight > 0)
        {
            return _contentWidth.ToString(CultureInfo.CurrentCulture)
                + "×"
                + _contentHeight.ToString(CultureInfo.CurrentCulture);
        }

        if (_screenWidth > 0 && _screenHeight > 0)
        {
            return _screenWidth.ToString(CultureInfo.CurrentCulture)
                + "×"
                + _screenHeight.ToString(CultureInfo.CurrentCulture);
        }

        return null;
    }

    private static string Value(string? text, string? fallback = null) =>
        !string.IsNullOrWhiteSpace(text) ? text! : (!string.IsNullOrWhiteSpace(fallback) ? fallback! : "—");

    /// <summary>信息行：左侧半透明标签 + 右侧白色值。</summary>
    /// <param name="label">标签文本。</param>
    /// <param name="value">值文本。</param>
    private void AddIdleInfoRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.72,
            Foreground = Brushes.White,
        };
        var content = new TextBlock
        {
            Text = value,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
        };

        Grid.SetColumn(caption, 0);
        Grid.SetColumn(content, 1);
        row.Children.Add(caption);
        row.Children.Add(content);
        IdleInfoList.Children.Add(row);
    }

    // ── 系统功能键（uitest keyEvent）────────────────────────────

    private void OnKeyHomeClick(object sender, RoutedEventArgs e) => SendKeyEvent("Home");

    private void OnKeyBackClick(object sender, RoutedEventArgs e) => SendKeyEvent("Back");

    private void OnKeyRecentClick(object sender, RoutedEventArgs e) => SendKeyEvent("2720");

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

    // ── 置顶图钉 ──────────────────────────────────────────────────

    private void OnPinChecked(object sender, RoutedEventArgs e)
    {
        bool pinned = PinButton.IsChecked == true;
        Topmost = pinned;
        PinButton.ToolTip = pinned ? Strings.Ui_PinOff : Strings.Ui_Pin;
        _log.Write(LogLevel.Info, "App.Window", pinned ? "窗口置顶=开" : "窗口置顶=关");
    }

    // ── 四角圆角裁剪 ──────────────────────────────────────────────

    private void OnFrameSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => ApplyFrameClip();

    /// <summary>按虚拟屏当前尺寸生成圆角矩形 Clip，保证画面四个角是圆的、不漏直角。</summary>
    private void ApplyFrameClip()
    {
        double width = FrameSurface.ActualWidth;
        double height = FrameSurface.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        FrameSurface.Clip = new RectangleGeometry(
            new Rect(0, 0, width, height),
            ScreenCornerRadius,
            ScreenCornerRadius);
    }

    // ── 页面覆盖层（设备管理 / 设置不再弹新窗口）───────────────────

    private void OnDevicesPageClick(object sender, RoutedEventArgs e) =>
        OpenPageOverlay(Strings.Ui_PageDevices, new DeviceManagePage(new DeviceStore()));

    private void OnSettingsPageClick(object sender, RoutedEventArgs e) =>
        OpenPageOverlay(Strings.Ui_PageSettings, new SettingsPage());

    private void OnOverlayCloseClick(object sender, RoutedEventArgs e) => ClosePageOverlay();

    /// <summary>打开窗口内页面：虚拟屏带动画缩到左下角，空出的区域放页面内容。</summary>
    /// <param name="title">页面标题。</param>
    /// <param name="page">页面实例。</param>
    private void OpenPageOverlay(string title, object page)
    {
        OverlayTitle.Text = title;
        PageHost.Content = page;

        if (_overlayOpen)
        {
            return;
        }

        // 打开页面时先掐掉在途手势，避免残留 touchDown。
        CancelGesture();
        _overlayOpen = true;
        OverlayHost.Visibility = Visibility.Visible;
        StatusHost.Visibility = Visibility.Collapsed;
        AnimateScreenToThumbnail();
    }

    /// <summary>收起页面：点左下角缩小的虚拟屏或覆盖层右上角按钮，都会回到原大小。</summary>
    private void ClosePageOverlay()
    {
        if (!_overlayOpen)
        {
            return;
        }

        _overlayOpen = false;
        RestoreScreenFromThumbnail();
    }

    /// <summary>把虚拟屏按 180ms 动画缩到左下角（宽约 180px，四角还是圆的）。</summary>
    private void AnimateScreenToThumbnail()
    {
        double fromWidth = ScreenFrame.ActualWidth;
        double fromHeight = ScreenFrame.ActualHeight;
        if (fromWidth <= 0 || fromHeight <= 0)
        {
            return;
        }

        _screenRestoreWidth = fromWidth;
        _screenRestoreHeight = fromHeight;

        double availableWidth = Math.Max(120d, ScreenHost.ActualWidth);
        double availableHeight = Math.Max(120d, ScreenHost.ActualHeight);
        double targetWidth = Math.Clamp(ThumbTargetWidth, 120d, Math.Max(120d, availableWidth * 0.5d));
        double targetHeight = targetWidth / _aspect;

        double maxHeight = Math.Max(120d, availableHeight - 24d);
        if (targetHeight > maxHeight)
        {
            targetHeight = maxHeight;
            targetWidth = targetHeight * _aspect;
        }

        // 缩略屏占左下角：给页面留出底部空间，免得内容被压在它下面。
        // 窗口很矮时缩略屏几乎占满高度，这时就不留了，否则页面区域会被压成一条缝。
        PageHost.Margin = targetHeight < availableHeight * 0.55d
            ? new Thickness(12, 2, 12, targetHeight + 22d)
            : new Thickness(12, 2, 12, 10);

        ScreenFrame.HorizontalAlignment = HorizontalAlignment.Left;
        ScreenFrame.VerticalAlignment = VerticalAlignment.Bottom;
        ScreenFrame.Margin = new Thickness(14, 0, 0, 14);

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(ScreenAnimationMs);
        ScreenFrame.BeginAnimation(
            FrameworkElement.WidthProperty,
            new DoubleAnimation(fromWidth, targetWidth, duration) { EasingFunction = ease });
        ScreenFrame.BeginAnimation(
            FrameworkElement.HeightProperty,
            new DoubleAnimation(fromHeight, targetHeight, duration) { EasingFunction = ease });
    }

    /// <summary>把缩小的虚拟屏动画还原，动画结束后重新交回 Stretch 布局。</summary>
    private void RestoreScreenFromThumbnail()
    {
        double fromWidth = ScreenFrame.ActualWidth;
        double fromHeight = ScreenFrame.ActualHeight;
        double targetWidth = _screenRestoreWidth > 0 ? _screenRestoreWidth : fromWidth;
        double targetHeight = _screenRestoreHeight > 0 ? _screenRestoreHeight : fromHeight;

        if (fromWidth <= 0 || fromHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            FinishOverlayRestore();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(ScreenAnimationMs);
        var widthAnimation = new DoubleAnimation(fromWidth, targetWidth, duration) { EasingFunction = ease };
        widthAnimation.Completed += OnRestoreAnimationCompleted;
        ScreenFrame.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation);
        ScreenFrame.BeginAnimation(
            FrameworkElement.HeightProperty,
            new DoubleAnimation(fromHeight, targetHeight, duration) { EasingFunction = ease });
    }

    private void OnRestoreAnimationCompleted(object? sender, EventArgs e)
    {
        if (_overlayOpen)
        {
            return;
        }

        FinishOverlayRestore();
    }

    /// <summary>还原动画收尾：清掉显式尺寸，让虚拟屏重新铺满，并收起覆盖层。</summary>
    private void FinishOverlayRestore()
    {
        ScreenFrame.BeginAnimation(FrameworkElement.WidthProperty, null);
        ScreenFrame.BeginAnimation(FrameworkElement.HeightProperty, null);
        ScreenFrame.Width = double.NaN;
        ScreenFrame.Height = double.NaN;
        ScreenFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
        ScreenFrame.VerticalAlignment = VerticalAlignment.Stretch;
        ScreenFrame.Margin = new Thickness(0);

        PageHost.Content = null;
        PageHost.Margin = new Thickness(12, 2, 12, 10);
        OverlayHost.Visibility = Visibility.Collapsed;
        StatusHost.Visibility = Visibility.Visible;
    }

    // ── 比例 / 最大化 ─────────────────────────────────────────────

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
    }

    /// <summary>
    /// 按比例校正窗口尺寸：<paramref name="keepWidth"/> 为 true 时以宽度为基准改高度，否则反过来。
    /// 控制条永远占据上方 <see cref="BarHeight"/> 的高度，横竖屏一样。
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
                double screenWidth = Math.Max(120d, ActualWidth);
                double targetHeight = (screenWidth / _aspect) + BarHeight;
                if (Math.Abs(targetHeight - ActualHeight) > 0.5)
                {
                    Height = targetHeight;
                }
            }
            else
            {
                double screenHeight = Math.Max(120d, ActualHeight - BarHeight);
                double targetWidth = screenHeight * _aspect;
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

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // 系统最大化会铺满工作区、破坏设备比例：改回普通状态，再按比例取工作区能容纳的最大尺寸。
            // 放大前的真实位置由系统的 RestoreBounds 记着，先取下来，还原时才能精确复原。
            if (!_isMaximized)
            {
                Rect restored = RestoreBounds;
                if (restored.Width > 1d && restored.Height > 1d)
                {
                    _restoreBounds = restored;
                }
            }

            WindowState = WindowState.Normal;
            MaximizeToWorkArea();
            return;
        }

        UpdateMaximizeGlyph();
    }

    /// <summary>双击 / 最大化按钮：在「放大」与「放大前的窗口尺寸位置」之间切换。</summary>
    private void ToggleMaximize()
    {
        if (_isMaximized)
        {
            RestoreFromMaximized();
            return;
        }

        MaximizeToWorkArea();
    }

    /// <summary>按比例放大到桌面边界，并把放大前的 Left/Top/Width/Height 记下来。</summary>
    private void MaximizeToWorkArea()
    {
        if (!_isMaximized)
        {
            // 只在第一次放大时记录；系统最大化那条路径可能已经先塞了更准的 RestoreBounds。
            if (_restoreBounds is not { Width: > 1d, Height: > 1d })
            {
                _restoreBounds = new Rect(Left, Top, Width, Height);
            }

            _isMaximized = true;
        }

        ApplyMaximizedFit();
        UpdateMaximizeGlyph();
    }

    /// <summary>还原到放大前的尺寸与位置，再重新等比钳制一次，避免还原后跑到桌面外。</summary>
    private void RestoreFromMaximized()
    {
        _isMaximized = false;

        Rect? bounds = _restoreBounds;
        if (bounds is { Width: > 1d, Height: > 1d })
        {
            _fitting = true;
            try
            {
                WindowState = WindowState.Normal;
                Left = bounds.Value.Left;
                Top = bounds.Value.Top;
                Width = bounds.Value.Width;
                Height = bounds.Value.Height;
            }
            finally
            {
                _fitting = false;
            }
        }

        ClampWindowToWorkArea();

        // 清掉旧还原点：下次放大重新按当前（可能被手动拖过的）位置记录。
        _restoreBounds = null;
        UpdateMaximizeGlyph();
    }

    /// <summary>按比例放大到桌面边界：取工作区能容纳的最大屏幕尺寸，居中摆放。</summary>
    private void ApplyMaximizedFit()
    {
        Rect work = SystemParameters.WorkArea;

        double maxScreenWidth = Math.Max(120d, work.Width - 8d);
        double maxScreenHeight = Math.Max(120d, work.Height - BarHeight - 8d);

        double screenWidth = Math.Min(maxScreenWidth, maxScreenHeight * _aspect);
        double screenHeight = screenWidth / _aspect;

        _fitting = true;
        try
        {
            Width = screenWidth;
            Height = screenHeight + BarHeight;
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
    }

    private void UpdateMaximizeGlyph()
    {
        MaximizeButton.Content = _isMaximized ? "\uE923" : "\uE922";
    }

    // ── 控制条交互 ────────────────────────────────────────────────

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // 控制条空白处可拖动窗口；双击切「放大 / 还原」。按钮点击由按钮自己处理，不会走到这里。
        if (e.OriginalSource is DependencyObject source && IsInside(source, ControlBar))
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            DragMove();
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    /// <summary>判断某个可视元素是否位于指定祖先之内（拖动窗口时区分控制条与控制条上的按钮）。</summary>
    private static bool IsInside(DependencyObject source, DependencyObject ancestor)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
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
    /// 保证任何一边都拖不出桌面。控制条固定在上方，只扣高度。
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

        double chromeHeight = BarHeight * scaleY;

        bool horizontal = edge is 1 or 2 or 4 or 5 or 7 or 8;
        bool fromLeft = edge is 1 or 4 or 7;
        bool fromTop = edge is 3;

        int width = Math.Max(MinWindowWidthPx, rect.Right - rect.Left);
        int height = Math.Max(MinWindowHeightPx, rect.Bottom - rect.Top);

        if (horizontal)
        {
            double screenWidth = Math.Max(80d * scaleX, width);
            height = (int)Math.Round((screenWidth / _aspect) + chromeHeight);
        }
        else
        {
            double screenHeight = Math.Max(80d * scaleY, height - chromeHeight);
            width = (int)Math.Round(screenHeight * _aspect);
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
    /// 再取窗口当前尺寸与该上限的较小值。打开窗口、帧比例变化、跨屏、还原时都要过一遍。
    /// </summary>
    private void ClampWindowToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;

        double maxScreenWidth = Math.Max(120d, Math.Min(
            work.Width - 16d,
            (work.Height - BarHeight - 16d) * _aspect));
        double maxScreenHeight = maxScreenWidth / _aspect;

        double targetWidth = Math.Min(Width <= 0 ? maxScreenWidth : Width, maxScreenWidth);
        double targetHeight = Math.Min(Height <= 0 ? maxScreenHeight + BarHeight : Height, maxScreenHeight + BarHeight);

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
        }
        finally
        {
            _fitting = false;
        }
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

            // 提前探一次桌面状态：第一次滚轮就能按「桌面横滑 / 应用竖滑」走对分支。
            ProbeLauncherAsync();
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

    /// <summary>
    /// 画面在虚拟屏里的实际排布：UniformToFill 意味着缩放比取「铺满」的那个较大值，
    /// 多出来的部分被居中裁掉。返回缩放比与画面左上角相对虚拟屏的偏移。
    /// </summary>
    /// <param name="scale">设备像素 → DIP 的缩放比。</param>
    /// <param name="offsetX">画面左边缘在虚拟屏里的 X 偏移（裁剪时为正，留边时为负）。</param>
    /// <param name="offsetY">画面上边缘在虚拟屏里的 Y 偏移。</param>
    /// <returns>几何是否可用。</returns>
    private bool TryGetFrameLayout(out double scale, out double offsetX, out double offsetY)
    {
        scale = 0d;
        offsetX = 0d;
        offsetY = 0d;

        double surfaceWidth = FrameSurface.ActualWidth;
        double surfaceHeight = FrameSurface.ActualHeight;
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || _screenWidth <= 0 || _screenHeight <= 0)
        {
            return false;
        }

        scale = Math.Max(surfaceWidth / _screenWidth, surfaceHeight / _screenHeight);
        if (scale <= 0)
        {
            return false;
        }

        offsetX = (surfaceWidth - (_screenWidth * scale)) / 2d;
        offsetY = (surfaceHeight - (_screenHeight * scale)) / 2d;
        return true;
    }

    /// <summary>
    /// 把画面内坐标换算成设备像素坐标：先按实际显示几何反算，再钳到 [0, 边长-1]，
    /// 绝不允许出现 (0,0) 兜底这种「拉到左上角」的假坐标。
    /// </summary>
    /// <param name="point">相对 <see cref="FrameSurface"/> 的坐标（DIP）。</param>
    /// <param name="x">设备像素 X。</param>
    /// <param name="y">设备像素 Y。</param>
    /// <returns>画面还没就绪时返回 false。</returns>
    private bool TryMapToDevice(Point point, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!TryGetFrameLayout(out double scale, out double offsetX, out double offsetY))
        {
            return false;
        }

        double deviceX = (point.X - offsetX) / scale;
        double deviceY = (point.Y - offsetY) / scale;
        x = (int)Math.Round(Math.Clamp(deviceX, 0d, Math.Max(0d, _screenWidth - 1d)));
        y = (int)Math.Round(Math.Clamp(deviceY, 0d, Math.Max(0d, _screenHeight - 1d)));
        return true;
    }

    private void OnScreenLeftDown(object sender, MouseButtonEventArgs e)
    {
        // 覆盖层打开时，画面是左下角那块缩略屏：点它就是「还原」。
        if (_overlayOpen)
        {
            if (e.OriginalSource is DependencyObject origin && IsInside(origin, FrameSurface))
            {
                e.Handled = true;
                ClosePageOverlay();
            }

            return;
        }

        if (e.OriginalSource is not DependencyObject source || !IsInside(source, FrameSurface))
        {
            return;
        }

        if (_input is not { IsConnected: true })
        {
            return;
        }

        Point surface = e.GetPosition(FrameSurface);
        if (!TryMapToDevice(surface, out int deviceX, out int deviceY))
        {
            return;
        }

        _pressed = true;
        _downSent = false;
        _pressSurface = surface;
        _pressDevice = new System.Drawing.Point(deviceX, deviceY);
        _pressTicks = Environment.TickCount64;
        _lastMoveTicks = 0;

        // 按下先什么都不发：纯点击最后只发一条 uitest click，避免 down/up 之间隔太久被判成长按。
        RestartLongPressTimer();

        ScreenHost.CaptureMouse();
        ScreenHost.Focus();
        e.Handled = true;
    }

    private void OnScreenMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || _overlayOpen)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // 抬起事件丢了（比如在窗口外松手）：按取消处理，并补一次 up。
            CancelGesture();
            return;
        }

        if (_input is not { IsConnected: true })
        {
            return;
        }

        Point surface = e.GetPosition(FrameSurface);

        if (!_downSent)
        {
            double dx = surface.X - _pressSurface.X;
            double dy = surface.Y - _pressSurface.Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) < ClickMaxDistance)
            {
                // 还在点击容差内，先不注入任何事件。
                return;
            }

            StopLongPressTimer();
            _input.TouchDown(_pressDevice.X, _pressDevice.Y);
            _downSent = true;
        }

        long now = Environment.TickCount64;
        if (now - _lastMoveTicks < MoveThrottleMs)
        {
            return;
        }

        _lastMoveTicks = now;
        if (TryMapToDevice(surface, out int deviceX, out int deviceY))
        {
            _input.TouchMove(deviceX, deviceY);
        }
    }

    private void OnScreenLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        e.Handled = true;

        bool downSent = _downSent;
        System.Drawing.Point pressDevice = _pressDevice;
        StopLongPressTimer();

        // 先清状态再放捕获：LostMouseCapture 回调里就不会再补一次 up。
        _pressed = false;
        _downSent = false;
        ScreenHost.ReleaseMouseCapture();

        if (_input is not { IsConnected: true })
        {
            return;
        }

        if (downSent)
        {
            _input.TouchUp();
            return;
        }

        // 纯点击：一条 uitest click 命令，设备侧就是一个干净的点击。
        SendClick(pressDevice.X, pressDevice.Y);
    }

    /// <summary>鼠标离开画面：当作手势结束，已发的 down 必须补一次 up，之后不再发 move。</summary>
    private void OnScreenMouseLeave(object sender, MouseEventArgs e) => CancelGesture();

    /// <summary>鼠标捕获丢失（Alt+Tab、弹窗抢焦点等）：同样收尾，避免设备侧卡在按下状态。</summary>
    private void OnScreenLostMouseCapture(object sender, MouseEventArgs e) => CancelGesture();

    /// <summary>结束当前手势：已发过 down 就补 up，然后彻底停止注入。</summary>
    private void CancelGesture()
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        StopLongPressTimer();

        if (_downSent && _input is { IsConnected: true })
        {
            _input.TouchUp();
        }

        _downSent = false;
    }

    private void RestartLongPressTimer()
    {
        _longPressTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressMs) };
        _longPressTimer.Tick -= OnLongPressTick;
        _longPressTimer.Tick += OnLongPressTick;
        _longPressTimer.Stop();
        _longPressTimer.Start();
    }

    private void StopLongPressTimer() => _longPressTimer?.Stop();

    /// <summary>按住不动超过 400ms：这时才补发 touchDown，设备侧就能识别成长按。</summary>
    private void OnLongPressTick(object? sender, EventArgs e)
    {
        StopLongPressTimer();
        if (!_pressed || _downSent || _input is not { IsConnected: true })
        {
            return;
        }

        _input.TouchDown(_pressDevice.X, _pressDevice.Y);
        _downSent = true;
    }

    /// <summary>纯点击：直接发一条 <c>uitest uiInput click</c>，不走 down/move/up 三件套。</summary>
    /// <param name="x">设备像素 X。</param>
    /// <param name="y">设备像素 Y。</param>
    private void SendClick(int x, int y)
    {
        var runner = _inputRunner;
        if (runner is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await runner.ShellAsync($"uitest uiInput click {x} {y}", CancellationToken.None)
                    .ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    _log.Warn("input", $"点击注入失败：{result.StandardError.Trim()}");
                }
            }
            catch (Exception ex)
            {
                _log.Error("input", $"点击注入异常（{x},{y}）。", ex);
            }
        });
    }

    /// <summary>
    /// 滚轮：按 <c>e.Delta</c> 的真实滚动量换算成设备像素（每格 ≈ 屏幕高度 1/12，保留累计余量），
    /// 方向按「滚轮向下 ⇒ 内容上移」；桌面（Launcher）上改成左右切页。
    /// </summary>
    private void OnScreenWheel(object sender, MouseWheelEventArgs e)
    {
        if (_overlayOpen)
        {
            // 覆盖层里的页面自己滚，不往手机注入。
            return;
        }

        if (_input is not { IsConnected: true } || _pressed)
        {
            return;
        }

        if (!TryMapToDevice(e.GetPosition(FrameSurface), out int centerX, out int centerY))
        {
            return;
        }

        // 每格 120：Delta/120 是格数，除以屏幕高度 1/12 得到设备像素位移；小数部分留着下次累加。
        double perNotch = Math.Max(24d, _screenHeight / 12d);
        _wheelRemainder += (e.Delta / 120d) * perNotch;

        int distance = (int)Math.Truncate(_wheelRemainder);
        if (distance == 0)
        {
            e.Handled = true;
            return;
        }

        _wheelRemainder -= distance;

        // 拿不到可靠的前台包名时退化成「横向滑动一次」，而不是报错或什么都不做。
        bool horizontal = IsLauncherActive() ?? true;
        if (horizontal)
        {
            SwipeHorizontally(distance, centerX, centerY);
        }
        else
        {
            SwipeVertically(distance, centerX, centerY);
        }

        e.Handled = true;
    }

    /// <summary>上下滑动：distance 为负表示内容上移（手指从下往上）。</summary>
    private void SwipeVertically(int distance, int centerX, int centerY)
    {
        UitestInputChannel? input = _input;
        if (input is null || _screenHeight <= 0)
        {
            return;
        }

        int minY = Math.Min(SwipeMargin, Math.Max(0, _screenHeight - 1));
        int maxY = Math.Max(minY + 1, _screenHeight - SwipeMargin);

        int startY = Math.Clamp(centerY, minY, maxY);
        int endY = Math.Clamp(startY + distance, minY, maxY);
        if (endY == startY)
        {
            return;
        }

        int x = Math.Clamp(centerX, 0, Math.Max(0, _screenWidth - 1));
        input.TouchDown(x, startY);
        for (int i = 1; i <= SwipeSteps; i++)
        {
            input.TouchMove(x, startY + (((endY - startY) * i) / SwipeSteps));
        }

        input.TouchUp();
    }

    /// <summary>左右滑动（桌面切页）：distance 为负表示往下一页翻。</summary>
    private void SwipeHorizontally(int distance, int centerX, int centerY)
    {
        UitestInputChannel? input = _input;
        if (input is null || _screenWidth <= 0)
        {
            return;
        }

        int minX = Math.Min(SwipeMargin, Math.Max(0, _screenWidth - 1));
        int maxX = Math.Max(minX + 1, _screenWidth - SwipeMargin);

        int startX = Math.Clamp(centerX, minX, maxX);
        int endX = Math.Clamp(startX + distance, minX, maxX);
        if (endX == startX)
        {
            return;
        }

        int y = Math.Clamp(centerY, 0, Math.Max(0, _screenHeight - 1));
        input.TouchDown(startX, y);
        for (int i = 1; i <= SwipeSteps; i++)
        {
            input.TouchMove(startX + (((endX - startX) * i) / SwipeSteps), y);
        }

        input.TouchUp();
    }

    // ── 桌面（Launcher）判定 ──────────────────────────────────────

    /// <summary>
    /// 读前台包名判断是不是桌面：命中缓存直接返回，过期在后台补探一次（滚轮不被命令卡住）。
    /// </summary>
    /// <returns>true = 桌面；false = 普通应用；null = 还没拿到可靠信号。</returns>
    private bool? IsLauncherActive()
    {
        long now = Environment.TickCount64;
        if (now - _launcherStamp > 2000)
        {
            ProbeLauncherAsync();
        }

        return _launcher;
    }

    /// <summary>后台探测前台包名（<c>aa dump -l</c> 的第一条 bundle name 即当前前台任务）。</summary>
    private void ProbeLauncherAsync()
    {
        if (_launcherProbeRunning || _launcherFailures >= 3)
        {
            return;
        }

        var runner = _inputRunner;
        if (runner is null)
        {
            return;
        }

        _launcherProbeRunning = true;
        _launcherStamp = Environment.TickCount64;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await runner.ShellAsync("aa dump -l", CancellationToken.None).ConfigureAwait(false);
                string? bundle = FirstBundleName(result.CombinedOutput);
                if (bundle is null)
                {
                    NoteLauncherProbeFailed("输出里没有包名");
                    return;
                }

                _launcherFailures = 0;
                _launcher = bundle.Contains("launcher", StringComparison.OrdinalIgnoreCase);
            }
            catch (HdcException ex)
            {
                NoteLauncherProbeFailed(ex.Message);
            }
            finally
            {
                _launcherProbeRunning = false;
            }
        });
    }

    /// <summary>探测失败：冷却重试，连续失败 3 次就放弃，滚轮回退成上下滑动。</summary>
    private void NoteLauncherProbeFailed(string reason)
    {
        _launcherFailures++;
        _launcherStamp = Environment.TickCount64;

        if (_launcherFailures >= 3)
        {
            // 拿不到前台包名：按「不是桌面」处理，保证普通应用里的滚轮还是上下滑动。
            _launcher = false;
        }

        _log.Warn("input", $"前台包名探测失败（第 {_launcherFailures} 次）：{reason}");
    }

    /// <summary>从 <c>aa dump -l</c> 输出里取第一条包名。</summary>
    /// <param name="output">命令输出。</param>
    /// <returns>包名；解析不出来时返回 null。</returns>
    private static string? FirstBundleName(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        foreach (string key in BundleKeys)
        {
            int index = output.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            int bracket = output.IndexOf('[', index);
            int bracketEnd = bracket >= 0 ? output.IndexOf(']', bracket + 1) : -1;
            if (bracket >= 0 && bracketEnd > bracket)
            {
                string value = output.Substring(bracket + 1, bracketEnd - bracket - 1).Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }

            int colon = output.IndexOf(':', index);
            if (colon < 0)
            {
                continue;
            }

            int lineEnd = output.IndexOf('\n', colon);
            string raw = lineEnd > colon
                ? output.Substring(colon + 1, lineEnd - colon - 1)
                : output.Substring(colon + 1);
            raw = raw.Trim();
            if (raw.Length > 0)
            {
                return raw;
            }
        }

        return null;
    }

    /// <summary>右键 = 返回键。</summary>
    private async void OnScreenRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_overlayOpen)
        {
            return;
        }

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
        // 覆盖层里的页面自己处理键盘（设置页有输入框）。
        if (_overlayOpen)
        {
            return;
        }

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
        if (_overlayOpen)
        {
            return;
        }

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
