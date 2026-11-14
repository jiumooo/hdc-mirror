using System.Text;
using System.Text.RegularExpressions;
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
using KeyBinding = HdcMirror.App.Services.KeyBinding;
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
    private const double ThumbTargetWidth = 128d;

    /// <summary>悬浮窗高度固定按竖屏比例，保证横竖屏一致。</summary>
    private const double ThumbTargetHeight = 276d;

    /// <summary>缩小 / 还原动画时长（毫秒）。</summary>
    private const double ScreenAnimationMs = 180d;

    /// <summary>触摸移动节流（毫秒）。</summary>

    /// <summary>鼠标按下后位移小于该值（DIP）视为点击，不进设备做 down/move/up。</summary>
    // 拖动限流：设备侧手势回放约 30 次/秒（实测 -frameRate 请求 60/90 都只出 30fps），
    // 16 ms 已对齐一帧；打开「快速甩动」后放宽到 8 ms，让轨迹更密、起停更贴手。
    // 注意：这里是按设置实时求值，不能写成 const —— 用户改了设置要立刻生效。
    private static int MoveThrottleMs => AppSettings.Current.SwipeFastMode ? 8 : 16;
    private const double ClickMaxDistance = 8d;

    /// <summary>拖动分段下发的最小位移（设备像素）：太碎会把 uitest 命令排满，太粗会一顿一顿。</summary>
    private const int DragChunkPixels = 24;

    /// <summary>拖动速度（px/s）：越高越跟手，4000 以上容易被当成甩动。</summary>
    private const int DragVelocity = 3000;

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
    private readonly ILogSink _log;

    // 诊断：画面「发布 → 渲染」延迟
    private double _lagSum;
    private long _lagMax;
    private long _lagCount;
    private double _tickGapSum;
    private long _tickGapMax;
    private double _renderSum;
    private long _lastTickTicks;
    private long _lastSampledPublished;
    private long _lastMoveTicks;


    private MirrorSession? _session;
    private WriteableBitmap? _screenBitmap;

    /// <summary>上次扫描黑边的时刻（节流用）。</summary>
    private long _cropStamp;

    /// <summary>当前的黑边放大系数（1 = 不需要放大）。</summary>
    private double _cropScale = 1d;

    /// <summary>上一次测到的黑边宽度，用来要求连续两次一致再动手，避免画面轻微变化时抖动。</summary>
    private int _cropBars;
    private int _screenWidth;
    private int _screenHeight;

    /// <summary>设备真实画面宽度（来自设备信息的分辨率，用于剔除编码补边；0 = 未知）。</summary>
    private int _contentWidth;

    /// <summary>设备真实画面高度（来自设备信息的分辨率，用于剔除编码补边；0 = 未知）。</summary>
    private int _contentHeight;

    private long _fpsWindowStart;
    private long _fpsWindowFrames;
    private int _fps;

    /// <summary>上次按画面亮度调整底部功能键颜色的时刻（节流用）。</summary>
    private long _navColorStamp;

    /// <summary>底部功能键当前是否为浅色（true = 白，适合深色画面）。</summary>
    private bool _navLight = true;

    /// <summary>当前画面宽高比（宽 ÷ 高），随真实帧更新。</summary>
    /// <summary>开窗前由主窗口指定的初始位置与大小（DIP）；为空则沿用 XAML 里的默认尺寸。</summary>
    public Rect? InitialBounds { get; set; }

    private double _aspect = 1260d / 2720d;

    /// <summary>转横屏前记住的竖屏窗口（位置+大小），转回竖屏时原样还原。</summary>
    private Rect? _portraitRestore;

    /// <summary>横屏时窗口被 Math.Clamp 搬进屏幕的临时位移，恢复竖屏时要补回去。</summary>
    private Point _landscapeOffset;

    /// <summary>转横屏后窗口实际落点（用来判断用户有没有自己再拖过）。</summary>
    private Point? _landscapePlaced;

    /// <summary>防重入：代码在改窗口尺寸时不要再触发比例校正。</summary>
    private bool _fitting;

    /// <summary>输入通道重建锁，防止多路手势同时重建。</summary>

    /// <summary>每 3 秒看一眼输入通道，断了就重建（不打扰用户）。</summary>

    /// <summary>连续重建失败次数，用于指数退避。</summary>

    /// <summary>下一次允许尝试重建的时间戳（TickCount64）。</summary>
    private HdcRunner? _inputRunner;

    /// <summary>常驻控制通道：连接设备后就一直保持，不等要操作才去建。</summary>
    private UitestInputChannel? _input;

    /// <summary>这一笔是否正走常驻通道（true = 逐帧 touchMove，画面实时跟着走）。</summary>
    private bool _streaming;

    /// <summary>设备固件拒绝 Gestures 连续触摸（一收 touchDown 就断链）时置真，本会话不再尝试流式。</summary>
    private bool _streamUnusable;

    /// <summary>常驻通道的保活/重建计时器。</summary>
    private DispatcherTimer? _inputKeepAlive;
    private readonly HdcDevice _device;

    /// <summary>蓝牙音频接收器：与「音画与音频」设置页共用同一实例（见 Audio\AudioSinkHub.cs）。</summary>
    private readonly AudioSinkHost _audio = AudioSinkHub.Instance;
    private bool _audioStartedByWindow;

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

    /// <summary>长按计时器：按住不动超过 400ms 才补发一次 touchDown。</summary>
    private DispatcherTimer? _longPressTimer;

    /// <summary>滚轮累计余量（设备像素），保证小幅度滚动也能累加成一次滑动。</summary>


    /// <summary>正在跑的滑动发送循环；非 null 表示还有滑动在路上。</summary>

    /// <summary>滚轮锚点：鼠标最后一次落在画面上的设备像素坐标。</summary>
    private System.Drawing.Point _wheelAnchor;
    private double _wheelPixels;
    private long _wheelBurstStart;
    private long _wheelBurstEnd;
    private DispatcherTimer? _wheelFlush;
    private long _lastWheelTicks;

    /// <summary>防止 <see cref="ClampWindowToWorkArea"/> 在自己改尺寸引发的回调里重入。</summary>
    private bool _clamping;

    /// <summary>尺寸拖拽去抖：连续事件只做最后一次校正，避免不停重建渲染目标（Win32 1816「配额不足」的根源）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _fitTimer = new()
    {
        Interval = System.TimeSpan.FromMilliseconds(90),
    };

    /// <summary>去抖期间记下最后一次拖拽的主方向：true = 以宽为准。</summary>
    private bool _fitKeepWidth = true;

    /// <summary>上一次尺寸夹取的时间戳，用来给夹取限频。</summary>
    private long _lastClampTicks;

    /// <summary>上一次桌面翻页的时间戳，用于翻页冷却。</summary>
    private long _lastPageFlipTicks;

    /// <summary>上一次因画幅变化换方向的时间戳，防止比例抖动来回切。</summary>
    private long _lastOrientTicks;

    /// <summary>界面线程心跳（毫秒时间戳），卡住时由看门狗发现并记日志。</summary>
    private long _uiBeat;

    /// <summary>窗口已关闭。</summary>
    private bool _closed;

    /// <summary>心跳计时器（界面线程）。</summary>
    private System.Windows.Threading.DispatcherTimer? _uiHeartbeat;

    /// <summary>看门狗线程。</summary>
    private Task? _uiWatchdog;

    /// <summary>设备侧当前是否有一个未抬起的触点：新一轮手势前必须先补一次 touchUp。</summary>

    /// <summary>本次按下后鼠标是否移动超过点击容差（决定抬手发 swipe 还是 click）。</summary>
    private bool _moved;

    /// <summary>本次手势最后一个位置对应的设备坐标。</summary>
    private System.Drawing.Point _lastDevice;

    /// <summary>上一次已经发给设备的拖动终点：只把新增的那一段位移补发出去。</summary>
    private System.Drawing.Point _lastSent;

    /// <summary>是否有一条拖动命令正在设备侧执行（同一时刻只允许一条）。</summary>
    private bool _dragInFlight;

    /// <summary>前台是否桌面（Launcher）：null = 还没拿到可靠信号。</summary>

    private DispatcherTimer? _launcherPoll;
    private bool? _launcher;

    /// <summary>上次探测前台包名的时间戳（毫秒）。</summary>
    private long _launcherStamp;

    /// <summary>探测是否在跑，避免滚轮每格都发一次进程。</summary>
    private bool _launcherProbeRunning;

    /// <summary>探测连续失败次数：连续失败说明这台机器上拿不到包名，放弃并回退成上下滑动。</summary>
    private int _launcherFailures;

    /// <summary>aa dump -a 第一条 mission 的包名（= 用户眼前的应用）。</summary>
    private string? _foregroundPackage;

    /// <summary>上面那个包名的采集时刻。</summary>
    private long _foregroundStamp;

    /// <summary>滚轮翻集的节流时刻。</summary>
    private long _lastWheelFlingTicks;

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

        // 和主窗口（FluentWindow + Round）保持同一套圆角。
        SourceInitialized += (_, __) => ApplyRoundedCorners();

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? device.Serial : title!;
        DeviceSerial = device.Serial;
        Title = TitleText.Text;

        // 用合成帧回调而不是 DispatcherTimer：前者跟显示器垂直同步走（≤16.7ms），
        // 定时器在界面忙时会被排到 25ms 以上，画面就慢半拍。
        System.Windows.Media.CompositionTarget.Rendering += OnCompositionRender;

        ScreenHost.MouseLeftButtonDown += OnScreenLeftDown;
        ScreenHost.MouseMove += OnScreenMouseMove;
        ScreenHost.MouseLeftButtonUp += OnScreenLeftUp;
        ScreenHost.MouseLeave += OnScreenMouseLeave;
        ScreenHost.LostMouseCapture += OnScreenLostMouseCapture;
        Deactivated += (_, __) => CancelGesture();
        ScreenHost.MouseWheel += OnScreenWheel;
        ScreenHost.MouseRightButtonUp += OnScreenRightUp;
        ScreenHost.Focusable = true;
        ScreenHost.PreviewTextInput += OnScreenTextInput;
        ScreenHost.PreviewKeyDown += OnScreenKeyDown;

        // 圆角裁剪：虚拟屏四角按真实尺寸生成 Clip，保证画面不会漏出直角。
        FrameSurface.SizeChanged += OnFrameSurfaceSizeChanged;

        _audio.StateChanged += OnAudioStateChanged;

        // 订阅完立刻按当前状态刷一次：音频可能在窗口创建前就已经接通（自动开启），
        // 那时事件已经发过了，不补这一下按钮就一直是未选中态。
        OnAudioStateChanged(_audio.State, string.Empty);

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        _fitTimer.Tick += OnFitTimerTick;
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
    }

    // ── 生命周期 ──────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetLinkBadge();
        ApplyFrameClip();

        // 还没出画面：先把锁屏 / 最后一帧 + 设备信息铺上，别只给一块纯黑。
        ShowIdleLayer();
        _ = FillIdleInfoAsync();

        ApplyInitialBounds();
        FitWindowToAspect(keepWidth: true);
        ClampWindowToWorkArea();

        // 位置必须和主窗口一致：等比换算会改高度，钳制又可能把窗口挪走，
        // 所以最后再按调用方给的 Left/Top 摆一次（只有放不下时才让钳制挪动）。
        if (InitialBounds is { } posed)
        {
            Left = posed.Left;
            Top = posed.Top;
            ClampWindowToWorkArea();
        }
        StartMirror();
        _ = ConnectInputAsync();

        ProbeLauncherAsync();

        // 常驻轮询前台：700ms 一次，让「桌面/应用」判断始终新鲜（滚轮据此决定翻页还是滚动）。
        if (_launcherPoll is null)
        {
            _launcherPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _launcherPoll.Tick += (_, __) => ProbeLauncherAsync();
            _launcherPoll.Start();
        }
        StartUiWatchdog();

        // 「连接设备后音频使用电脑输出」：勾上就在连接成功（镜像窗打开）时直接把接收端拉起来，
        // 不再要求用户去点控制条上的喇叭。蓝牙 A2DP Sink 起来后手机端会自动把媒体声音投过来。
        if (AppSettings.Current.AutoStartAudio && !_audio.IsRunning)
        {
            _ = StartAutoAudioAsync();
        }

        // 底部功能键：鼠标靠近窗口底部才滑出，离开后 1 秒自动收起
        _navHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _navHideTimer.Tick += (_, __) =>
        {
            _navHideTimer!.Stop();

            // 光标停着不动时不会再有 MouseMove，NavBar.IsMouseOver 此时仍是 false（还没对它做命中测试），
            // 所以必须直接问「光标现在在哪」：仍在底部热区就继续等，不能收起。
            Point cursor = default;
            try
            {
                cursor = Mouse.GetPosition(this);
            }
            catch (InvalidOperationException)
            {
                HideNavBar();
                return;
            }

            if (NavBar.IsMouseOver || InNavHotZone(cursor))
            {
                _navHideTimer.Start();
                return;
            }

            HideNavBar();
        };
        // 画面区域会 e.Handled 掉 MouseMove，所以用隧道事件 PreviewMouseMove 才能收到
        PreviewMouseMove += OnWindowMouseMove;
        MouseLeave += OnPreviewMouseLeave;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        System.Windows.Media.CompositionTarget.Rendering -= OnCompositionRender;
        StopUiWatchdog();

        _longPressTimer?.Stop();
        _inputKeepAlive?.Stop();
        _launcherPoll?.Stop();
        UitestInputChannel? closing = _input;
        _input = null;
        if (closing is not null)
        {
            _ = closing.DisposeAsync();
        }
        MirrorSession? session = _session;
        _session = null;
        session?.Dispose();
        // 不 Dispose：接收器是进程级单例，设置页还在用它；只退订事件，避免窗口对象被吊住。
        _audio.StateChanged -= OnAudioStateChanged;

        // 关掉连接就得断开音频：否则手机声音会一直从电脑出。
        // 只停「本窗口自动开的那次」——用户在设置页手动开的，不归窗口管。
        if (_audioStartedByWindow && _audio.IsRunning)
        {
            _audio.Stop();
            _audioStartedByWindow = false;
            _log.Info("audio", "镜像窗已关闭：停止音频接收并还原系统默认输出");
        }

        RememberGeometry();
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

        ShowStatus(Strings.Status_Mirror_Starting);
    }

    // ── 帧渲染 ────────────────────────────────────────────────────

    private void OnCompositionRender(object? sender, EventArgs e)
    {
        MirrorSession? session = _session;
        if (session is null)
        {
            return;
        }

        long tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastTickTicks != 0)
        {
            double gap = (tickStart - _lastTickTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;
            _tickGapSum += gap;
            if (gap > _tickGapMax)
            {
                _tickGapMax = (long)Math.Ceiling(gap);
            }
        }

        _lastTickTicks = tickStart;

        session.Frames.Consume(RenderFrame);
        _renderSum += (System.Diagnostics.Stopwatch.GetTimestamp() - tickStart) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        ReportDisplayLag(session);
        UpdateStatus(session);    }

    /// <summary>诊断：解码线程发布图像的瞬间，到界面真正画上去，中间隔了多久。</summary>
    private void ReportDisplayLag(MirrorSession session)
    {
        long published = session.Frames.LastPublishTicks;
        long publishedCount = session.Frames.Published;
        if (publishedCount == _lastSampledPublished)
        {
            return;
        }

        _lastSampledPublished = publishedCount;
        if (published == 0)
        {
            return;
        }

        double lag = (System.Diagnostics.Stopwatch.GetTimestamp() - published) * 1000d
                     / System.Diagnostics.Stopwatch.Frequency;
        _lagSum += lag;
        if (lag > _lagMax)
        {
            _lagMax = (long)Math.Ceiling(lag);
        }

        long count = ++_lagCount;
        if (count % 60 == 0)
        {
            _log.Info("lag", string.Format(
                CultureInfo.InvariantCulture,
                "画面延迟：发布→渲染 平均 {0:0.0} ms，最大 {1} ms；界面帧间隔平均 {2:0.0} ms，最大 {3} ms；渲染耗时平均 {4:0.0} ms",
                _lagSum / 60d,
                _lagMax,
                _tickGapSum / 60d,
                _tickGapMax,
                _renderSum / 60d));
            _lagSum = 0;
            _lagMax = 0;
            _tickGapSum = 0;
            _tickGapMax = 0;
            _renderSum = 0;
        }
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

            // 画面比例跟着真实帧走：设备转屏时按新比例换尺寸，中心点保持不动。
            double aspect = visibleWidth / (double)visibleHeight;
            long orientNow = System.Environment.TickCount64;
            bool aspectJumped = Math.Abs(aspect - _aspect) / Math.Max(0.001d, _aspect) > 0.02d;
            if (aspectJumped && orientNow - _lastOrientTicks > 800)
            {
                _lastOrientTicks = orientNow;
                _aspect = aspect;
                SwitchOrientation();
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

            // 顺手判定「画面左右有没有黑边」：有就整体放大到看不见黑边（窗口大小不动）。
            UpdateContentCrop(handle.AddrOfPinnedObject(), stride, visibleWidth, visibleHeight);

            // 底部功能键压在画面之上：白底界面里白键看不见，所以按画面亮度自动换深/浅色。
            UpdateNavBarColor(handle.AddrOfPinnedObject(), stride, visibleWidth, visibleHeight);
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

            // 首帧到达说明推流守护进程已经起来了 —— 它在启动时会把已建立的 8012 连接顶掉，
            // 所以这一刻重建一次输入通道，用户的第一个手势就能生效。
            _ = EnsureInputAsync();
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
            // 首帧还没到：这一行先显示准备信息（此时 fps 没有意义）。
            ShowStatus(Strings.Status_Mirror_Starting);
            return;
        }

        // 有帧之后左下角固定成「无线/有线 · N fps」。
        // 画面静止时 fps 会掉到 0，也照样显示，不再换成「已连接 · …」这类提示。
        StatusText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Status_Mirror_Streaming,
            _screenWidth,
            _screenHeight,
            _fps);
        StatusHost.ToolTip = null;
        if (!_overlayOpen)
        {
            StatusHost.Visibility = Visibility.Visible;
        }
    }

    /// <summary>左下角一直显示连接方式（无线 / 有线），帧率紧随其后。</summary>
    private void SetLinkBadge()
    {
        LinkText.Text = _device.IsWireless ? Strings.Link_Wireless : Strings.Link_Usb;
    }
    private void ShowStatus(string text)
    {
        // 临时提示不抢左下角那一行（那里固定显示连接方式 + fps），只挂成悬浮提示；
        // 首帧还没到的阶段才直接写进去。
        StatusHost.ToolTip = text;
        if (_fps <= 0)
        {
            StatusText.Text = text;
        }
    }
    private void OnMirrorStatus(MirrorStatus status, string? message)
    {
        switch (status)
        {
            case MirrorStatus.Failed:
                ShowStatus(message ?? Strings.Err_Mirror_NoDevice);
                ShowIdleLayer();
                break;

            case MirrorStatus.Starting:
                ShowStatus(Strings.Status_Mirror_Starting);
                break;

            case MirrorStatus.Streaming:
                // 开始出帧：把锁屏层收掉。状态栏交给 UpdateStatus 按帧数刷新，
                // 这里不要再写文案——Status_Mirror_Streaming 是带 {0}{1}{2} 的格式串。
                IdleHost.Visibility = Visibility.Collapsed;
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
    /// <summary>连接成功时按设置自动开启音频接收；失败只落日志 + 状态栏，不弹框。</summary>
    private async Task StartAutoAudioAsync()
    {
        // 给蓝牙栈一点时间：镜像窗刚开就抢 A2DP 端点容易拿到「设备忙」。
        await Task.Delay(700).ConfigureAwait(true);

        // 再等画面出第一帧：原来固定 700ms 就开音频，蓝牙端点往往比画面先通，
        // 结果就是「画面还没出来声音先到」。改成等首帧后再开，最多等 8 秒。
        await WaitForFirstFrameAsync().ConfigureAwait(true);

        if (_audio.IsRunning || !AppSettings.Current.AutoStartAudio)
        {
            return;
        }

        ShowStatus(Strings.Status_Audio_Waiting);
        if (_audio.Start())
        {
            _audioStartedByWindow = true;
            _log.Info("audio", "连接设备后已自动开启音频接收（等待手机投射）");
            return;
        }

        _log.Warn("audio", $"连接设备后自动开启音频失败：{_audio.LastError}");
        ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Status_Audio_FailedFormat, _audio.LastError ?? "audio"));
    }

    /// <summary>
    /// 等镜像会话出第一帧；超时或会话没起来就直接返回，不阻塞音频。
    /// 用 session.DecodedFrames 判断：解码成功一帧就 +1，静止画面也照样算。
    /// </summary>
    private async Task WaitForFirstFrameAsync()
    {
        const int timeoutMilliseconds = 8000;
        const int pollMilliseconds = 100;

        for (int waited = 0; waited < timeoutMilliseconds; waited += pollMilliseconds)
        {
            MirrorSession? session = _session;
            if (session is null || session.DecodedFrames > 0)
            {
                return;
            }

            await Task.Delay(pollMilliseconds).ConfigureAwait(true);
        }

        _log.Warn("audio", "等首帧超时（8 秒），仍按设置开启音频");
    }

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
            // 事件是从子进程 stdout 的读取线程发上来的：必须切回 UI 线程，
            // 之前这里写的是「不是 UI 线程就直接 return」，于是按钮永远不变色。
            Dispatcher.BeginInvoke(new Action<AudioSinkState, string>(OnAudioStateChanged), state, detail);
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
                AudioButton.Content = "\uE767";   // 等待阶段也用喇叭字形，不闪「静音」图标
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
        // 选中态：真的在电脑上出声（Connected）才高亮，未接通保持普通样式。
        bool active = state == AudioSinkState.Connected;
        AudioButton.Background = active
            ? (Brush)new BrushConverter().ConvertFrom("#CC5EA9FF")!
            : Brushes.Transparent;
        AudioButton.Foreground = active
            ? (Brush)new BrushConverter().ConvertFrom("#FFFFFFFF")!
            : (Brush)new BrushConverter().ConvertFrom("#CCFFFFFF")!;
        _log.Write(LogLevel.Info, "App.Audio", "音频状态=" + state
            + (string.IsNullOrEmpty(detail) ? string.Empty : " 详情=" + detail));
    }

    // ── 置顶图钉 ──────────────────────────────────────────────────

    private void OnPinChecked(object sender, RoutedEventArgs e)
    {
        bool pinned = PinButton.IsChecked == true;
        Topmost = pinned;
        // 未置顶用空心图钉（E718），置顶用实心图钉（E840）—— 一眼能看出状态，不用靠颜色猜。
        PinButton.Content = pinned ? "\uE840" : "\uE718";
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

    private void OnDevicesPageClick(object sender, RoutedEventArgs e) => OpenOverlayTab("devices");

    private void OnSettingsPageClick(object sender, RoutedEventArgs e) => OpenOverlayTab("general");

    private void OnOverlayCloseClick(object sender, RoutedEventArgs e) => ClosePageOverlay();

    /// <summary>覆盖层顶部的四个页签：与主窗口「设置」下的分栏一一对应。</summary>
    private void OnOverlayTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key })
        {
            OpenOverlayTab(key);
        }
    }

    /// <summary>
    /// 按页签键打开对应页面。页面每次新建：主窗口改完设置再回镜像窗，这里拿到的一定是新实例，
    /// 不会出现「界面上改了、镜像窗还显示旧值」。
    /// </summary>
    private DispatcherTimer? _navHideTimer;
    private bool _navVisible;

    /// <summary>鼠标靠近窗口底部（或已在功能键上）时滑出底部功能键，离开后延时收起。</summary>
    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (_overlayOpen)
        {
            return;
        }

        Point point;
        try
        {
            point = e.GetPosition(this);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (InNavHotZone(point) || NavBar.IsMouseOver)
        {
            ShowNavBar();
        }
        else
        {
            // 用户要求「鼠标移走就立马消失」：不走延时器，直接 140ms 动画收起
            HideNavBar();
        }
    }

    /// <summary>
    /// 热区 = 底部功能键自身占的那块矩形再向外放 6px（用户要求「就按红框那部分区域」），
    /// 不再用「距底部 N 像素」这种整条横幅判定。
    /// </summary>
    private bool InNavHotZone(Point point)
    {
        try
        {
            if (NavBar.ActualWidth <= 0d || NavBar.ActualHeight <= 0d)
            {
                return false;
            }

            Point origin = NavBar.TranslatePoint(new Point(0d, 0d), this);
            var box = new Rect(origin, new Size(NavBar.ActualWidth, NavBar.ActualHeight));
            box.Inflate(6d, 6d);
            return box.Contains(point);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnPreviewMouseLeave(object sender, MouseEventArgs e) => HideNavBar();

    private void ShowNavBar()
    {
        _navHideTimer?.Stop();
        if (_navVisible)
        {
            return;
        }

        _navVisible = true;
        NavBar.IsHitTestVisible = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        NavBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        NavBarShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18d, 0d, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private void HideNavBar(bool immediate = false)
    {
        if (!_navVisible && !immediate)
        {
            return;
        }

        _navVisible = false;
        NavBar.IsHitTestVisible = false;
        TimeSpan duration = immediate ? TimeSpan.Zero : TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        NavBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1d, 0d, duration) { EasingFunction = ease });
        NavBarShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0d, 18d, duration) { EasingFunction = ease });
    }

    private void OpenOverlayTab(string key)
    {
        object page;
        string title;

        switch (key)
        {
            case "keys":
                page = new KeysPage();
                title = Strings.Menu_Settings_Keys;
                break;
            case "audio":
                page = new AvPage();
                title = Strings.Menu_Settings_Audio;
                break;
            case "video":
                page = new VideoPage();
                title = Strings.Menu_Settings_Video;
                break;
            case "av":
                page = new AvPage();
                title = Strings.Menu_Settings_Av;
                break;
            case "general":
                page = new SettingsPage();
                title = Strings.Menu_Settings_General;
                break;
            default:
                key = "devices";
                page = new DeviceManagePage(new DeviceStore());
                title = Strings.Menu_Settings_Devices;
                break;
        }

        UpdateOverlayTabs(key);
        OpenPageOverlay(title, page);
    }

    /// <summary>高亮当前页签。</summary>
    private void UpdateOverlayTabs(string key)
    {
        foreach ((Button button, string tag) in new[]
        {
            (OverlayTabDevices, "devices"),
            (OverlayTabGeneral, "general"),
            (OverlayTabKeys, "keys"),
            (OverlayTabVideo, "video"),
            (OverlayTabAudio, "audio"),
        })
        {
            bool active = string.Equals(tag, key, StringComparison.Ordinal);
            button.Foreground = active
                ? (Brush)new BrushConverter().ConvertFrom("#FFFFFFFF")!
                : (Brush)new BrushConverter().ConvertFrom("#99FFFFFF")!;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>打开窗口内页面：虚拟屏带动画缩到左下角，空出的区域放页面内容。</summary>
    /// <param name="title">页面标题。</param>
    /// <param name="page">页面实例。</param>
    private void OpenPageOverlay(string title, object page)
    {
        OverlayTitle.Text = title;
        PageHost.Content = page;

        // 页面自带 PageRootStyle 的底部留白（给主窗左下缩略屏让位）在镜像窗里会让滚动条
        // 页内容根（ScrollViewer 里的 StackPanel）自带 PageRootStyle 的 168px 底边距，
        // 那是给主窗左下缩略屏让位的；镜像窗里这个留白会让滚动条提前结束、右下空一条。
        if (FindScrollContent(page) is { } content)
        {
            content.Margin = new Thickness(content.Margin.Left, content.Margin.Top, content.Margin.Right, 16d);
        }

        if (_overlayOpen)
        {
            return;
        }

        // 打开页面时先掐掉在途手势，避免残留 touchDown。
        CancelGesture();
        HideNavBar(immediate: true);
        _overlayOpen = true;
        OverlayHost.Visibility = Visibility.Visible;
        IdleHost.Visibility = Visibility.Collapsed;   // 页面打开时缩略屏只显示最后一帧，不显示设备信息列表
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

        double availableHeight = Math.Max(120d, ScreenHost.ActualHeight);

        // 悬浮窗固定用「竖屏」的宽高比，横竖屏一致：128 x 276（不足时等比缩小）。
        bool landscape = _aspect > 1d;
        double targetWidth = landscape ? ThumbTargetHeight : ThumbTargetWidth;
        double targetHeight = landscape ? ThumbTargetWidth : ThumbTargetHeight;
        double maxHeight = Math.Max(120d, availableHeight - 32d);
        if (targetHeight > maxHeight)
        {
            double ratio = maxHeight / targetHeight;
            targetHeight = maxHeight;
            targetWidth = Math.Max(96d, targetWidth * ratio);
        }

        // 页面始终铺满：悬浮窗不占位，只浮在上面。
        PageHost.Margin = new Thickness(12, 2, 12, 10);

        ScreenFrame.HorizontalAlignment = HorizontalAlignment.Right;
        ScreenFrame.VerticalAlignment = VerticalAlignment.Bottom;
        ScreenFrame.Margin = new Thickness(0, 0, 16, 16);

        // 缩略时按等比缩放显示（横屏就留上下黑边），不裁切、不变形。
        ScreenImage.Stretch = Stretch.Uniform;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(ScreenAnimationMs);
        ScreenFrame.BeginAnimation(
            FrameworkElement.WidthProperty,
            new DoubleAnimation(fromWidth, targetWidth, duration) { EasingFunction = ease });
        ScreenFrame.BeginAnimation(
            FrameworkElement.HeightProperty,
            new DoubleAnimation(fromHeight, targetHeight, duration) { EasingFunction = ease });
    }

    /// <summary>取页面里第一个 ScrollViewer 的内容元素（用来调整它的底边距）。</summary>
    private static FrameworkElement? FindScrollContent(object page)
    {
        if (page is not DependencyObject root)
        {
            return null;
        }

        ScrollViewer? viewer = root as ScrollViewer ?? FindChild<ScrollViewer>(root);
        return viewer?.Content as FrameworkElement;
    }

    /// <summary>在可视树里按类型找第一个子元素。</summary>
    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }


    /// <summary>把缩小的虚拟屏动画还原，动画结束后重新交回 Stretch 布局。</summary>
    /// <summary>页面开着时转屏：把悬浮窗的宽高按新方向重新对调（窄/宽基准保持同一套）。</summary>
    private void RefreshThumbnailSize()
    {
        if (!_overlayOpen)
        {
            return;
        }

        bool landscape = _aspect > 1d;
        double targetWidth = landscape ? ThumbTargetHeight : ThumbTargetWidth;
        double targetHeight = landscape ? ThumbTargetWidth : ThumbTargetHeight;

        double maxHeight = Math.Max(120d, ScreenHost.ActualHeight - 32d);
        if (targetHeight > maxHeight)
        {
            double ratio = maxHeight / targetHeight;
            targetHeight = maxHeight;
            targetWidth = Math.Max(96d, targetWidth * ratio);
        }

        ScreenFrame.BeginAnimation(FrameworkElement.WidthProperty, null);
        ScreenFrame.BeginAnimation(FrameworkElement.HeightProperty, null);
        ScreenFrame.Width = targetWidth;
        ScreenFrame.Height = targetHeight;
        ScreenImage.Stretch = Stretch.Uniform;
    }


    private void RestoreScreenFromThumbnail()
    {
        ScreenImage.Stretch = Stretch.Fill;   // 还原成全屏拉伸（窗口锁了手机比例，不会变形）
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
        IdleHost.Visibility = _hasFrame ? Visibility.Collapsed : Visibility.Visible;
        StatusHost.Visibility = Visibility.Visible;
    }

    // ── 比例 / 最大化 ─────────────────────────────────────────────

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 先更新基准：内部调整（_fitting）时也必须记下新尺寸，
        // 否则下一次用户拖拽会拿旧基准算增量，选错"以宽为准 / 以高为准"，画面就跳出比例。
        double deltaWidth = Math.Abs(e.NewSize.Width - _lastWidth);
        double deltaHeight = Math.Abs(e.NewSize.Height - _lastHeight);
        _lastWidth = e.NewSize.Width;
        _lastHeight = e.NewSize.Height;

        if (_fitting)
        {
            return;
        }

        // 拖拽会连发几十个 SizeChanged；每次立刻改尺寸等于每次重建渲染目标，
        // 攒到一定次数就撞 Win32 1816（配额不足），界面直接卡死。这里只记方向，去抖后再校一次。
        _fitKeepWidth = deltaWidth >= deltaHeight;
        _fitTimer.Stop();
        _fitTimer.Start();
    }

    /// <summary>去抖到点：按最后的主方向做一次比例校正。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnFitTimerTick(object? sender, EventArgs e)
    {
        _fitTimer.Stop();
        FitWindowToAspect(_fitKeepWidth);
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

        // 拖到桌面外时把窗口「整块搬回」可见范围（优先搬，搬不动才等比缩小），
        // 保证任何一次拖拽结束后：比例 = 手机分辨率，且窗口完整落在桌面里。
        ClampWindowToWorkArea();
    }

    /// <summary>
    /// 设备转屏时的窗口切换：<b>中心点不变</b>，尺寸优先用该朝向自己的记忆，
    /// 没有记忆就沿用切换前的宽度（于是横屏与竖屏看起来一样大，只是按比例重新算高）。
    /// </summary>
    private void SwitchOrientation()
    {
        if (_fitting)
        {
            // 正好在一次内部调整中：此刻改尺寸会被 _fitting 吞掉，比例就永久留在旧值上。
            // 排到本轮布局结束再切一次，保证转屏一定落在最后。
            Dispatcher.BeginInvoke(new Action(SwitchOrientation), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        bool landscape = _aspect > 1d;

        if (_overlayOpen)
        {
            // 页面开着时转屏：悬浮窗立刻按新方向对调宽高，不用等下次打开页面。
            _ = Dispatcher.BeginInvoke(new Action(RefreshThumbnailSize), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (landscape)
        {
            // 竖屏 -> 横屏：把竖屏的「位置 + 客户区宽高」整份记下来（恢复时要用）。
            double oldClientW = Math.Max(120d, ActualWidth);
            double oldClientH = Math.Max(120d, ActualHeight - BarHeight);
            _portraitRestore = new Rect(Left, Top, oldClientW, oldClientH);

            // 尺寸就是宽高对调：竖屏 512x1105 的客户区 -> 横屏 1105x512，比例正好是 2720:1260，
            // 所以画面既不会黑边也不会被裁；窗口保持原中心点「转」过去。
            double newWidth = oldClientH;
            double newHeight = oldClientW + BarHeight;
            double centerX = Left + (oldClientW / 2d);
            double centerY = Top + ((oldClientH + BarHeight) / 2d);
            double wantLeft = centerX - (newWidth / 2d);
            double wantTop = centerY - (newHeight / 2d);

            _fitting = true;
            try
            {
                Width = newWidth;
                Height = newHeight;
                Left = wantLeft;
                Top = wantTop;
            }
            finally
            {
                _fitting = false;
            }

            // 超出桌面：整块往屏幕内搬（不缩放）。搬动的距离记下来，恢复竖屏时补回去。
            ClampWindowToWorkArea();
            _landscapePlaced = new Point(Left, Top);
            _landscapeOffset = new Point(Left - wantLeft, Top - wantTop);

            _log.Info("orient", $"转横屏：竖屏 {oldClientW:0}x{oldClientH:0} -> 横屏 {newWidth:0}x{newHeight:0}，搬动 ({_landscapeOffset.X:0},{_landscapeOffset.Y:0})");
            return;
        }

        if (_portraitRestore is { Width: > 1d } saved)
        {
            bool userMoved = _landscapePlaced is { } placed
                && (Math.Abs(Left - placed.X) > 2d || Math.Abs(Top - placed.Y) > 2d);

            double left;
            double top;
            if (userMoved)
            {
                // 横屏时用户自己拖过窗口：按现在的中心点还原成竖屏尺寸。
                left = Left + ((ActualWidth - saved.Width) / 2d);
                top = Top + ((ActualHeight - saved.Height) / 2d);
            }
            else
            {
                // 没拖过：回到原来竖屏的位置，并把横屏时为「搬进屏幕」临时位移的距离补上。
                left = saved.Left + _landscapeOffset.X;
                top = saved.Top + _landscapeOffset.Y;
            }

            double targetHeight = saved.Height + BarHeight;
            _fitting = true;
            try
            {
                Width = saved.Width;
                Height = targetHeight;
                Left = left;
                Top = top;
            }
            finally
            {
                _fitting = false;
            }

            _portraitRestore = null;
            _landscapePlaced = null;
            _landscapeOffset = default;

            ClampWindowToWorkArea();
            _log.Info("orient", $"回竖屏：{saved.Width:0}x{targetHeight:0} @ ({Left:0},{Top:0})，用户拖过={userMoved}");
            return;
        }

        // 没有竖屏记忆（例如一开始就是横屏流）：保持当前宽度、按比例改高度。
        FitWindowToAspect(keepWidth: true);
        ClampWindowToWorkArea();
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
        long clampNow = System.Environment.TickCount64;
        if (clampNow - _lastClampTicks < 120)
        {
            // 120ms 内重复夹取没有意义，只会白白重建渲染目标。
            return;
        }

        _lastClampTicks = clampNow;

        if (_clamping)
        {
            return;
        }

        Rect work = WindowPlacement.WorkArea(this);

        double maxScreenWidth = Math.Max(120d, Math.Min(
            work.Width - 16d,
            (work.Height - BarHeight - 16d) * _aspect));
        double maxScreenHeight = maxScreenWidth / _aspect;

        // 关键：这里是**等比缩放**，不是把宽高各自砍一刀。
        // 以前 Width/Height 分别取 min，竖屏高度（1020）转到横屏后会被单独压到 941，
        // 宽度还留在 409 —— 窗口比例与画面比例不一致，两侧就露出黑边。
        double screenWidth = Math.Max(120d, Width <= 0 ? maxScreenWidth : Width);
        double screenHeight = Math.Max(120d, (Height <= 0 ? maxScreenHeight + BarHeight : Height) - BarHeight);
        double scale = Math.Min(1d, Math.Min(maxScreenWidth / screenWidth, maxScreenHeight / screenHeight));

        double targetWidth = screenWidth * scale;
        double targetHeight = (screenHeight * scale) + BarHeight;
        double targetLeft = Left;
        double targetTop = Top;

        if (targetLeft + targetWidth > work.Right)
        {
            targetLeft = Math.Max(work.Left, work.Right - targetWidth);
        }

        if (targetTop + targetHeight > work.Bottom)
        {
            targetTop = Math.Max(work.Top, work.Bottom - targetHeight);
        }

        if (targetLeft < work.Left)
        {
            targetLeft = work.Left;
        }

        if (targetTop < work.Top)
        {
            targetTop = work.Top;
        }

        // 只在真的需要变的时候才写属性：无条件赋值会让 WPF 每次都重建渲染目标，
        // 连续几十次就会撞上 Win32 1816「配额不足」（见 App.IsTransientRenderFailure）。
        if (Math.Abs(Left - targetLeft) <= 0.5
            && Math.Abs(Top - targetTop) <= 0.5
            && Math.Abs(Width - targetWidth) <= 0.5
            && Math.Abs(Height - targetHeight) <= 0.5)
        {
            return;
        }

        _clamping = true;
        _fitting = true;
        try
        {
            if (Math.Abs(Width - targetWidth) > 0.5)
            {
                Width = targetWidth;
            }

            if (Math.Abs(Height - targetHeight) > 0.5)
            {
                Height = targetHeight;
            }

            if (Math.Abs(Left - targetLeft) > 0.5)
            {
                Left = targetLeft;
            }

            if (Math.Abs(Top - targetTop) > 0.5)
            {
                Top = targetTop;
            }

            _lastWidth = ActualWidth;
            _lastHeight = ActualHeight;
        }
        finally
        {
            _fitting = false;
            _clamping = false;
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

            // 设备侧 hypium RPC 服务（8012）不常驻：投屏 daemon 只服务画面 socket。
            // 没有它连 touchDown 都发不出去，所以每次连上先确认，不在就拉起。
            bool rpcReady = await UitestRpcDaemon.EnsureAsync(_inputRunner, _log, CancellationToken.None).ConfigureAwait(true);
            if (rpcReady)
            {
                _streamUnusable = false;
            }

            // 提前探一次桌面状态：第一次滚轮就能按「桌面横滑 / 应用竖滑」走对分支。
            ProbeLauncherAsync();

            // 连上就先把常驻控制通道建起来，不等用户操作才建；断了自己重连。
            _ = EnsureInputAsync();
            _inputKeepAlive ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _inputKeepAlive.Tick -= OnInputKeepAliveTick;
            _inputKeepAlive.Tick += OnInputKeepAliveTick;
            _inputKeepAlive.Start();
        }
        catch (HdcException ex)
        {
            _log.Warn("input", $"输入通道不可用：{ex.Message}");
        }
    }

    /// <summary>常驻通道保活：断了就在后台重连一次，两秒一轮，不打扰用户。</summary>
    private void OnInputKeepAliveTick(object? sender, EventArgs e)
    {
        if (_input is { IsConnected: true })
        {
            return;
        }

        _ = EnsureInputAsync();
    }
    /// <summary>
    /// 保证输入通道可用：已连通直接返回；断链（设备侧 hypium 服务重启、线被掐）就重建。
    /// 设备一启动推流守护进程会顶掉已建立的 8012 连接，所以这里不能只信 TcpClient.Connected。
    /// </summary>
    /// <returns>通道可用返回 <c>true</c>。</returns>
    private async Task<bool> EnsureInputAsync()
    {
        if (_input is { IsConnected: true })
        {
            return true;
        }

        _inputRunner ??= new HdcRunner(deviceSerial: _device.Serial, log: _log);
        try
        {
            _input = await UitestInputChannel.ConnectAsync(_inputRunner, _log).ConfigureAwait(true);
            _log.Info("input", "常驻控制通道已就绪（连接期间一直保持）");
            _streamUnusable = false;   // 新通道可用：清掉上一次被掐断留下的旧结论
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("input", $"常驻控制通道建立失败，暂时用命令方式：{ex.Message}");
            return false;
        }
    }
    /// <summary>看门狗：通道已停用，这里只把自己停掉。</summary>
    /// <param name="sender">事件源。</param>
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

        // 横屏黑边被横向拉伸后，显示坐标要先按中心反算回原始画面坐标，否则点击会左右偏。
        double surfaceWidth = FrameSurface.ActualWidth;
        double px = point.X;
        if (_cropScale > 1.0001d && surfaceWidth > 0d)
        {
            double centerX = surfaceWidth / 2d;
            px = centerX + ((px - centerX) / _cropScale);
        }

        double deviceX = (px - offsetX) / scale;
        double deviceY = (point.Y - offsetY) / scale;
        x = (int)Math.Round(Math.Clamp(deviceX, 0d, Math.Max(0d, _screenWidth - 1d)));
        y = (int)Math.Round(Math.Clamp(deviceY, 0d, Math.Max(0d, _screenHeight - 1d)));
        return true;
    }

    private void OnScreenLeftDown(object sender, MouseButtonEventArgs e)
    {
        InvalidateLauncherCache();
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

        Point surface = e.GetPosition(FrameSurface);
        if (!TryMapToDevice(surface, out int deviceX, out int deviceY))
        {
            return;
        }
        _pressed = true;
        _downSent = false;
        _moved = false;
        _pressSurface = surface;
        _pressDevice = new System.Drawing.Point(deviceX, deviceY);
        _lastSent = _pressDevice;
        _pressTicks = Environment.TickCount64;


        // 常驻通道可用就直接把按下送下去：之后的每一次移动都实时下发，画面跟着鼠标走。
        _streaming = false;
        // 注意：这里不立刻发 TouchDown。按下就发会被设备当成「手指落点后停顿」，
        // 先闪一下点击/长按反馈，等真正拖动时才开始滑——就是你看到的现象。
        // 改为等第一次真实位移再补 TouchDown，纯拖动从第一步就是拖拽。
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
            // 抬起事件丢了（比如在窗口外松手）：按取消处理。
            CancelGesture();
            return;
        }

        Point surface = e.GetPosition(FrameSurface);

        // 只记录轨迹终点，不逐帧下发：设备侧的 hypium 手势通道收到 touchDown 就断链，
        // 所以统一改成 uitest 的 click / longClick / swipe 一条命令完成。
        double dx = surface.X - _pressSurface.X;
        double dy = surface.Y - _pressSurface.Y;
        if (Math.Sqrt((dx * dx) + (dy * dy)) >= ClickMaxDistance)
        {
            TryMapToDevice(surface, out int deviceX, out int deviceY);
            _lastDevice = new System.Drawing.Point(deviceX, deviceY);
            _moved = true;

            // 已经动了就不可能是长按：立刻取消计时器，否则慢速拖动会先弹出长按菜单。
            StopLongPressTimer();

            // 第一次真实位移：现在才真正按下手指，从按下点开始拖，
            // 这样设备拿到的是「按下→连续移动」，不会先当成点击。
            if (!_streaming && _input is { IsConnected: true } starter)
            {
                starter.TouchDown(_pressDevice.X, _pressDevice.Y);
                _streaming = starter.IsConnected;
            }

            if (_streaming)
            {
                if (_input is not { IsConnected: true })
                {
                    // 通道刚被设备掐断：本会话不再试流式，剩下的路程改用分段下发，保证拖动过程中画面在动。
                    _streaming = false;
                    _streamUnusable = true;
                    _log.Warn("input", "连续触摸被设备中断，改用分段下发");
                }
                else
                {
                    // 鼠标事件率可到 125~1000 次/秒，而设备侧手势回放只有约 60 次/秒。
                    // 不在这里限流，快速拖动会在通道里堆出几百帧，抬手后手机还在慢慢滑。
                    long nowTicks = Environment.TickCount64;
                    if (nowTicks - _lastMoveTicks >= MoveThrottleMs)
                    {
                        _lastMoveTicks = nowTicks;
                        _input.TouchMove(deviceX, deviceY);
                        _lastSent = _lastDevice;
                    }

                    // 太密的那一帧不发，位置已记在 _lastDevice，抬手前会补发最后一帧。
                    return;
                }
            }

            // 不再分段下发：分段会让每一步都变成一次独立的「按下-抬起」，
            // 应用里看到的就是先点一下再滑、而且每段都要等 hdc 起进程（约 0.5 秒）。
            // 流式可用时靠上面的 TouchMove 跟手；万一通道断了，抬手时补一条整段 drag。

        }
    }

    private void OnScreenLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        e.Handled = true;

        System.Drawing.Point pressDevice = _pressDevice;
        bool moved = _moved;
        bool longPressed = _downSent;

        StopLongPressTimer();

        _pressed = false;
        _downSent = false;
        _moved = false;
        ScreenHost.ReleaseMouseCapture();

        if (longPressed)
        {
            // 长按已经由 OnLongPressTick 发过 longClick，抬手什么都不用做。
            return;
        }

        if (_streaming)
        {
            _streaming = false;
            if (_input is { IsConnected: true })
            {
                _input.DropPending();

                // 抬手前把最后一帧补上，设备停在鼠标最终位置。
                if (moved && (_lastDevice.X != _lastSent.X || _lastDevice.Y != _lastSent.Y))
                {
                    _input.TouchMove(_lastDevice.X, _lastDevice.Y);
                }

                // 快速甩动：抬手前顺着拖动方向再送一小段（位移的 18%），设备侧按速度判定的
                // 甩动才够格触发「翻页 / 翻集」。关掉开关则保持原样，停在哪就是哪。
                if (moved && AppSettings.Current.SwipeFastMode)
                {
                    SendFastTail(pressDevice);
                }

                _input.TouchUp();
                return;
            }

            // 通道在半路被设备掐断：补一条命令把这一笔画完，不让画面停在半路。
            if (moved)
            {
                SendSwipe(pressDevice.X, pressDevice.Y, _lastDevice.X, _lastDevice.Y, DragVelocity);
            }
            else
            {
                SendClick(pressDevice.X, pressDevice.Y);
            }

            return;
        }

        if (moved)
        {
            // 流式没走成：抬手时补一条整段拖拽，速度按这次拖动的真实速度换算。
            long heldMs = Math.Max(40, Environment.TickCount64 - _pressTicks);
            double dist = Math.Sqrt(Math.Pow(_lastDevice.X - pressDevice.X, 2) + Math.Pow(_lastDevice.Y - pressDevice.Y, 2));
            int velocity = (int)Math.Clamp(dist / (heldMs / 1000d), 800d, 12000d);
            SendDrag(pressDevice.X, pressDevice.Y, _lastDevice.X, _lastDevice.Y, velocity);
            return;
        }

        SendClick(pressDevice.X, pressDevice.Y);
    }

    /// <summary>鼠标离开画面：当作手势结束，已发的 down 必须补一次 up，之后不再发 move。</summary>
    private void OnScreenMouseLeave(object sender, MouseEventArgs e)
    {
        CancelGesture();
    }

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

        _downSent = false;

        if (_streaming)
        {
            _streaming = false;
            if (_input is { IsConnected: true })
            {
                _input.TouchUp();
            }
        }
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

    /// <summary>按住不动超过 400ms：发一条设备侧 longClick，抬手不再重复点击。</summary>
    private void OnLongPressTick(object? sender, EventArgs e)
    {
        StopLongPressTimer();
        if (!_pressed || _downSent || _moved)
        {
            return;
        }

        if (!_streamUnusable && _input is { IsConnected: true } holder)
        {
            // 通道可用：就地按下，设备自己会判长按（手指确实停在同一位置）。
            holder.TouchDown(_pressDevice.X, _pressDevice.Y);
            _streaming = holder.IsConnected;
            _downSent = true;
            return;
        }

        // 通道不可用：退回 shell 的长按命令。
        _downSent = true;
        RunShell($"uitest uiInput longClick {_pressDevice.X} {_pressDevice.Y}");
    }
    private void InvalidateLauncherCache()
    {
        _launcher = null;
        _launcherStamp = 0;
    }

    /// <summary>纯点击：发一条 <c>uitest uiInput click</c>。</summary>
    /// <param name="x">设备像素 X。</param>
    /// <param name="y">设备像素 Y。</param>
    private void SendClick(int x, int y)
    {
        InvalidateLauncherCache();

        // 常驻 8012 通道写一条手势只要 0.1 ms，而走 hdc shell 每条命令都要付一次
        // WiFi 往返（实测平均 148.6 ms）—— 这就是「点一下要等一下才有反应」的主因。
        // 通道可用时直接用它发「按下 + 抬起」，点哪儿立刻响应。
        if (!_streamUnusable && _input is { IsConnected: true } channel)
        {
            channel.Tap(x, y);
            return;
        }

        RunShell($"uitest uiInput click {x} {y}");
    }

    /// <summary>从按下点滑到松手点：一条 <c>uitest uiInput swipe</c>，最后一位是速度（px/s）。</summary>
    /// <summary>精确拖拽（不带抬手惯性）：应用内滚动与拖动收尾用它。</summary>
    private void SendDrag(int fromX, int fromY, int toX, int toY, int velocity)
    {
        int v = Math.Clamp(velocity, 200, 40_000);
        RunShell($"uitest uiInput drag {fromX} {fromY} {toX} {toY} {v}");
    }

    private void SendSwipe(int fromX, int fromY, int toX, int toY, int velocity)
    {
        InvalidateLauncherCache();

        // uitest 的最后一个参数是速度（200~40000 px/s），不是时长：
        // 速度越大画面跟得越快，之前按「按住时长」传，等于每次都按最慢档跑。
        velocity = Math.Clamp(velocity, 200, 40_000);
        RunShell($"uitest uiInput swipe {fromX} {fromY} {toX} {toY} {velocity}");
    }

    /// <summary>
    /// 拖动过程中分段下发：一次只发「上次发过之后新走的那一段」，
    /// 速度给高，这样手机画面基本跟着鼠标走，而不是松手才整段滑过去。
    /// </summary>
    private void MaybeSendDragChunk()
    {
        if (_streaming || !_moved || _dragInFlight)
        {
            return;
        }

        System.Drawing.Point from = _lastSent;
        System.Drawing.Point to = _lastDevice;
        int dx = to.X - from.X;
        int dy = to.Y - from.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < DragChunkPixels)
        {
            return;
        }

        // 同一时刻只允许一条设备侧命令在跑：uitest 一次约 600ms，
        // 并发下发只会把 hdc 排队排满，反而更不跟手。
        _dragInFlight = true;
        _lastSent = to;
        _ = Task.Run(async () =>
        {
            try
            {
                HdcRunner? runner = _inputRunner;
                if (runner is not null)
                {
                    await runner.ShellAsync(
                        $"uitest uiInput swipe {from.X} {from.Y} {to.X} {to.Y} {DragVelocity}",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("input", $"拖动分段失败：{ex.Message}");
            }
            finally
            {
                _dragInFlight = false;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 上一条发完时鼠标可能又走了一段：立刻补上，保持贴合。
                    if (_pressed && _moved)
                    {
                        MaybeSendDragChunk();
                    }
                }), DispatcherPriority.Background);
            }
        });
    }
    /// <summary>抬手时补最后余量：不足最小分段的也算上，避免结尾少走一截。</summary>
    /// <remarks>
    /// TODO(0.2.4): 死代码，全仓没有任何调用点（抬手补帧已由 <see cref="SendFastTail"/> 与
    /// OnScreenLeftUp 的流式分支承担）。0.2.3 按所有者要求先保留，下个版本删除。
    /// </remarks>
    private void SendDragTail()
    {
        System.Drawing.Point from = _lastSent;
        System.Drawing.Point to = _lastDevice;
        if (from.X == to.X && from.Y == to.Y)
        {
            return;
        }

        _lastSent = to;
        SendSwipe(from.X, from.Y, to.X, to.Y, DragVelocity);
    }

    /// <summary>
    /// 快速甩动的尾巴：沿「按下点 → 当前点」的方向再补 18% 位移，速度按拖动真实速度放大。
    /// <para>
    /// 为什么需要它：设备侧把「甩动」和「慢慢拖」区分得很死，只有抬手瞬间还有速度才算甩动；
    /// 流式触摸在抬手前刚好停在原地（最后一帧补的是同一位置），速度判为 0，翻页就不触发。
    /// 补这一小段等于人为保留速度。仅在 <see cref="AppSettings.SwipeFastMode"/> 打开时调用。
    /// </para>
    /// </summary>
    /// <param name="pressDevice">本轮手势的按下点（设备坐标）。</param>
    private void SendFastTail(System.Drawing.Point pressDevice)
    {
        int dx = _lastDevice.X - pressDevice.X;
        int dy = _lastDevice.Y - pressDevice.Y;
        double distance = Math.Sqrt((dx * (double)dx) + (dy * (double)dy));
        if (distance < 1d)
        {
            return;
        }

        // 尾巴长度按位移的 18%，至少 12 像素，最多 160 像素：
        // 太短设备不认速度，太长会滑过头、把下一屏的内容也带进来。
        double tail = Math.Clamp(distance * 0.18d, 12d, 160d);
        int tailX = (int)Math.Round(_lastDevice.X + (dx / distance * tail));
        int tailY = (int)Math.Round(_lastDevice.Y + (dy / distance * tail));
        tailX = Math.Clamp(tailX, 0, Math.Max(0, _screenWidth - 1));
        tailY = Math.Clamp(tailY, 0, Math.Max(0, _screenHeight - 1));
        if (tailX == _lastDevice.X && tailY == _lastDevice.Y)
        {
            return;
        }

        if (_input is { IsConnected: true } channel)
        {
            channel.TouchMove(tailX, tailY);
        }

        _lastSent = new System.Drawing.Point(tailX, tailY);
    }

    /// <summary>把一条设备侧命令丢到后台跑，失败写日志，不阻塞界面线程。</summary>
    private void RunShell(string command)
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
                var result = await runner.ShellAsync(command, CancellationToken.None).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    _log.Warn("input", $"注入失败（{command}）：{result.StandardError.Trim()}");
                }
            }
            catch (Exception ex)
            {
                _log.Error("input", $"注入异常（{command}）。", ex);
            }
        });
    }

    /// <summary>
    /// 滚轮：累加位移后发一条 <c>uitest uiInput swipe</c>（设备侧真实滑动，按速度执行）。
    /// 起点取鼠标在画面上的位置 —— 鼠标停在哪就从哪滑，不再固定从屏幕边缘开始。
    /// </summary>
    private void OnScreenWheel(object sender, MouseWheelEventArgs e)
    {
        if (_overlayOpen || _inputRunner is null)
        {
            return;
        }

        if (!TryMapToDevice(e.GetPosition(FrameSurface), out int centerX, out int centerY))
        {
            return;
        }

        e.Handled = true;

        // 应用专属滑动：视频/短剧类只认甩动，滚轮直接翻一集（不累加像素）。
        if (AppSettings.Current.ForceFlingWheel || NativeSwipeForForeground())
        {
            long flingNow = Environment.TickCount64;
            if (flingNow - _lastWheelFlingTicks >= 350 && _screenWidth > 0 && _screenHeight > 0)
            {
                _lastWheelFlingTicks = flingNow;
                int flingX = _screenWidth / 2;
                int flingTop = Math.Max(80, _screenHeight / 4);
                int flingBottom = _screenHeight - flingTop;
                bool nextEpisode = e.Delta < 0;
                string flingCmd = nextEpisode
                    ? $"uitest uiInput fling {flingX} {flingBottom} {flingX} {flingTop} 12000 60"
                    : $"uitest uiInput fling {flingX} {flingTop} {flingX} {flingBottom} 12000 60";
                _ = _inputRunner.ShellAsync(flingCmd, CancellationToken.None);
                _log.Info("wheel", $"滚轮翻集（{(nextEpisode ? "下一集" : "上一集")}）：{_foregroundPackage}");
            }

            return;
        }

        // 驱动常把一格滚轮拆成多条 MouseWheel（实测一格 4 条、间隔 16ms 内），
        // 45ms 内只认第一条，避免一格滚轮被当成好几次。
        long now = Environment.TickCount64;
        if (now - _lastWheelTicks < 25)
        {
            return;
        }

        _lastWheelTicks = now;

        // 滚轮刻度换算成设备像素：一格约 1/18 屏高；连滚多格就累加成一段。
        double perNotch = Math.Max(20d, _screenHeight / 18d);
        _wheelPixels += (e.Delta / 120d) * perNotch;
        _wheelAnchor = new System.Drawing.Point(centerX, centerY);

        if (_wheelBurstStart == 0)
        {
            _wheelBurstStart = now;
        }

        _wheelBurstEnd = now;

        // 滚轮一停就结算：80ms 没有新的滚轮事件就把这一段一次性发出去（既合并连滚，又不拖响应）。
        _wheelFlush ??= CreateWheelFlushTimer();
        _wheelFlush.Stop();
        _wheelFlush.Start();
    }

    private DispatcherTimer CreateWheelFlushTimer()
    {
        DispatcherTimer timer = new(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(70),
        };

        timer.Tick += OnWheelFlushTick;
        return timer;
    }

    private void OnWheelFlushTick(object? sender, EventArgs e)
    {
        _wheelFlush?.Stop();

        double pixels = _wheelPixels;
        long start = _wheelBurstStart;
        long end = _wheelBurstEnd;
        _wheelPixels = 0d;
        _wheelBurstStart = 0;
        _wheelBurstEnd = 0;

        if (Math.Abs(pixels) < 1)
        {
            return;
        }

        long elapsed = Math.Max(40, end - start);
        _ = ScrollBurstAsync(pixels, elapsed);
    }

    /// <summary>
    /// 一次滚轮爆发只发一条设备命令：距离由累计刻度决定，速度由「这段时间滚了多少」决定。
    /// 不排队、不回填余量——滚轮一停就停，绝不出现第二段。
    /// </summary>
    private async Task ScrollBurstAsync(double pixels, long elapsedMs)
    {
        HdcRunner? runner = _inputRunner;
        int width = _screenWidth;
        int height = _screenHeight;
        if (runner is null || width <= 0 || height <= 0)
        {
            return;
        }

        int dir = Math.Sign(pixels);
        int remaining = (int)Math.Round(Math.Abs(pixels));
        if (dir == 0 || remaining < 1)
        {
            return;
        }

        try
        {
            // 桌面 → 滚轮翻页；应用内 → 滚轮纵向滚动。自动判断，不需要按任何修饰键。
            if (IsLauncherActive() == true)
            {
                long flipNow = Environment.TickCount64;
                if (flipNow - _lastPageFlipTicks < 400)
                {
                    return;
                }

                _lastPageFlipTicks = flipNow;
                int flipDir = dir < 0 ? 0 : 1;

                if (!_streamUnusable && _input is { IsConnected: true } flipChannel)
                {
                    int cy = height / 2;
                    int fromX = flipDir == 0 ? width - 60 : 60;
                    int toX = flipDir == 0 ? 60 : width - 60;
                    flipChannel.TouchDown(fromX, cy);
                    const int flipFrames = 8;
                    for (int i = 1; i <= flipFrames; i++)
                    {
                        flipChannel.TouchMove(fromX + (int)Math.Round((toX - fromX) * (i / (double)flipFrames)), cy);
                        await Task.Delay(5).ConfigureAwait(true);
                    }

                    flipChannel.TouchUp();
                    _log.Info("wheel", $"桌面翻页：{fromX} → {toX}");
                    return;
                }

                await runner.ShellAsync($"uitest uiInput dircFling {flipDir} 1500").ConfigureAwait(true);
                _log.Info("wheel", $"桌面翻页：dircFling {flipDir}");
                return;
            }

            int minY = SwipeMargin;
            int maxY = Math.Max(SwipeMargin + 1, height - SwipeMargin);
            int minX = SwipeMargin + 1;
            int maxX = Math.Max(minX + 1, width - SwipeMargin);
            // 固定从屏幕正中滑动：跟着鼠标位置走会导致「滚轮时鼠标一动就滑错地方」。
            int x = width / 2;
            int y = height / 2;
            int targetY = Math.Clamp(y + (dir * remaining), minY, maxY);
            if (targetY == y)
            {
                return;
            }

            // 滚轮优先走常驻通道：TouchDown → 若干帧 TouchMove → 停一帧 → TouchUp。
            // 走 shell 要 hdc + uitest 起两次进程（>500ms），那就是「滚一下要等半天」的来源。
            int delta = targetY - y;
            // 帧数由「你这次滚了多久」决定：滚得慢 → 时长长 → 帧多、每帧位移小（慢速跟上）；
            // 滚得快 → 时长短 → 帧少、每帧位移大（快速甩到位）。总位移始终等于你滚出来的像素。
            int durationMs = (int)Math.Clamp(elapsedMs, 80d, 600d);
            int frames = Math.Max(3, durationMs / 12);
            const int stepMs = 12;   // 24 像素 / 12ms ≈ 2000 px/s：跟手又不被判成甩动

            _log.Info("wheel", $"滚动（流式）：{x},{y} → {targetY}（{Math.Abs(delta)} 像素，{frames} 帧，速度约 {Math.Abs(delta) * 1000 / (frames * stepMs)} px/s）");

            if (!_streamUnusable && _input is { IsConnected: true } wheelChannel)
            {
                wheelChannel.TouchDown(x, y);
                for (int i = 1; i <= frames; i++)
                {
                    if (!wheelChannel.IsConnected)
                    {
                        break;
                    }

                    wheelChannel.TouchMove(x, y + (delta * i / frames));
                    await Task.Delay(stepMs).ConfigureAwait(true);
                }
                // 尾部减速：最后三帧把间隔拉到 45ms 且停在终点，抬手速度≈0，
                // 应用就不会把这次滚动判成「甩动」，松手后不会自己再滑一段。
                for (int k = 0; k < 3; k++)
                {
                    wheelChannel.TouchMove(x, y + delta);
                    await Task.Delay(45).ConfigureAwait(true);
                }

                await Task.Delay(120).ConfigureAwait(true);
                wheelChannel.TouchUp();
                return;
            }

            // 通道不可用才退回 shell drag。
            double speed = Math.Abs(delta) / (elapsedMs / 1000d);
            int velocity = (int)Math.Clamp(speed, 600d, 6000d);
            await runner.ShellAsync($"uitest uiInput drag {x} {y} {x} {targetY} {velocity}").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warn("input", $"滚动注入失败：{ex.Message}");
        }
    }
    private bool? IsLauncherActive()
    {
        long now = Environment.TickCount64;

        // 缓存超过 600ms 就不再信任：宁可先按「应用内」走纵向滚动，
        // 也不能拿桌面的旧状态去横滑（微信里横滑就是灾难）。
        // 轮询每 700ms 一次，缓存有效期必须比它长，否则永远判“未知”→ 桌面上也只会纵向滚。
        if (now - _launcherStamp > 2000)
        {
            ProbeLauncherAsync();
            return null;
        }

        return _launcher;
    }

    /// <summary>后台探测前台进程（<c>aa dump -r</c> 里 state 含 #FOREGROUND 的记录）。</summary>
    private void ProbeLauncherAsync()
    {
        if (_launcherProbeRunning)
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
                var result = await runner.ShellAsync("aa dump -a", CancellationToken.None).ConfigureAwait(false);
                string? topPkg = ExtractTopMissionPackage(result.CombinedOutput);
                if (!string.IsNullOrEmpty(topPkg))
                {
                    _foregroundPackage = topPkg;
                    _foregroundStamp = Environment.TickCount64;
                }

                // 桌面判据必须走「进程级 state」，不能走 mission 列表。
                // 实测（HarmonyOS NEXT）：人已经在桌面时，current mission lists 里仍然留着上一次用过的应用
                // （比如 com.kylin.read），而该应用的进程早就没了；只有 com.ohos.sceneboard 等系统壳进程是
                // state #FOREGROUND。所以按 mission 判前台会一直判成「在应用里」，桌面上滚轮永远只能上下滑。
                // 现在改成：优先用进程级（DetectLauncher），mission 只作为进程列表读不出来时的兜底。
                bool? detected = DetectLauncher(result.CombinedOutput);
                if (detected is null && !string.IsNullOrEmpty(topPkg))
                {
                    detected = IsShellProcess(topPkg);
                }
                if (detected is null)
                {
                    NoteLauncherProbeFailed("输出里没有前台状态");
                    return;
                }

                _launcherFailures = 0;
                _launcher = detected.Value;
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

        _log.Warn("input", $"前台包名探测失败（第 {_launcherFailures} 次）：{reason}");
    }

    /// <summary>
    /// 从 <c>aa dump -r</c> 的 AppRunningRecords 里挑出 state #FOREGROUND 的进程；
    /// 只剩系统壳（桌面 / 系统界面 / 服务子进程）时判定为「在桌面」。
    /// </summary>
    /// <param name="output">命令输出。</param>
    /// <returns>true = 桌面；false = 有真实应用在前台；null = 读不出前台状态。</returns>
    /// <summary>取 aa dump -a 里 current mission lists 中 state #FOREGROUND 的 mission 包名（当前前台应用）。</summary>
    private static string? ExtractTopMissionPackage(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        string? currentPkg = null;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("state #FOREGROUND", StringComparison.Ordinal))
            {
                return currentPkg;
            }

            Match match = Regex.Match(line, @"mission name #\[#([A-Za-z0-9_.]+):");
            if (match.Success)
            {
                currentPkg = match.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>当前前台是否命中「应用专属滑动」名单（只认 5 秒内采集的包名，避免沿用上一个应用）。</summary>
    private bool NativeSwipeForForeground()
    {
        string list = AppSettings.Current.SwipeNativeApps;
        if (string.IsNullOrWhiteSpace(list))
        {
            return false;
        }

        string? pkg = _foregroundPackage;
        if (string.IsNullOrEmpty(pkg) || Environment.TickCount64 - _foregroundStamp > 5000)
        {
            ProbeLauncherAsync();
            return false;
        }

        foreach (string raw in list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string item = raw.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            if (item == "*" || string.Equals(item, pkg, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
    private static bool? DetectLauncher(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        string? current = null;
        bool sawForeground = false;
        bool sawRealApp = false;

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("process name [", StringComparison.Ordinal))
            {
                int end = line.IndexOf(']', 14);
                current = end > 14 ? line.Substring(14, end - 14).Trim() : null;
                continue;
            }

            if (current is null || !line.StartsWith("state #FOREGROUND", StringComparison.Ordinal))
            {
                continue;
            }

            sawForeground = true;
            if (!IsShellProcess(current))
            {
                sawRealApp = true;
            }
        }

        return sawForeground ? !sawRealApp : null;
    }

    /// <summary>系统壳进程：桌面、系统界面、常驻服务子能力，都不算「用户正在用的应用」。</summary>
    /// <param name="name">进程名。</param>
    /// <returns>是系统壳返回 true。</returns>
    private static bool IsShellProcess(string name) =>
        name.Contains("sceneboard", StringComparison.OrdinalIgnoreCase)
        || name.Contains("launcher", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SystemUI", StringComparison.OrdinalIgnoreCase)
        || name.Contains("ailifesvc", StringComparison.OrdinalIgnoreCase)
        || name.Contains("hiview", StringComparison.OrdinalIgnoreCase)
        // 下面两个在桌面上同样是 state #FOREGROUND：小艺语音助手（shell_assistant）与
        // 媒体控制卡片（mediacontroller:UIExtAbility）。漏掉它们会让「人在桌面」被判成
        // 「有真实应用在前台」，桌面滚轮就又退回上下滑了。
        || name.Contains("shell_assistant", StringComparison.OrdinalIgnoreCase)
        || name.Contains("mediacontroller", StringComparison.OrdinalIgnoreCase);

    // ── 界面卡死看门狗 ────────────────────────────────────────────

    /// <summary>
    /// 界面线程心跳 + 看门狗：每 500ms 打一次心跳，后台线程发现超过 5 秒没动就记一条 Warn。
    /// 只读诊断，不改变任何行为；卡死时日志里能直接看到从什么时刻开始卡。
    /// </summary>
    private void StartUiWatchdog()
    {
        if (_uiWatchdog is not null)
        {
            return;
        }

        _uiBeat = System.Environment.TickCount64;
        _uiHeartbeat = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(500),
        };
        _uiHeartbeat.Tick += (_, _) => Interlocked.Exchange(ref _uiBeat, System.Environment.TickCount64);
        _uiHeartbeat.Start();

        _uiWatchdog = Task.Run(async () =>
        {
            while (!_closed)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                long gap = System.Environment.TickCount64 - Interlocked.Read(ref _uiBeat);
                if (gap <= 5000)
                {
                    continue;
                }

                _log.Warn("ui", $"界面线程已连续 {gap} ms 无响应（画面尺寸 {_screenWidth}×{_screenHeight}，窗口 {ActualWidth:0}×{ActualHeight:0}）");
                Interlocked.Exchange(ref _uiBeat, System.Environment.TickCount64);
            }
        });
    }

    /// <summary>停掉看门狗与心跳。</summary>
    private void StopUiWatchdog()
    {
        _closed = true;
        _uiHeartbeat?.Stop();
        _uiHeartbeat = null;
        ScreenImage.Source = null;
        _screenBitmap = null;
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

        // 用户自定义映射优先：按下的组合键若在「设置 → 按键映射」里，就翻译成对应手机动作。
        // 表是每次按键现读 AppSettings 的，所以在设置页保存后立刻生效，不需要重连。
        string gesture = KeyActions.Describe(e);
        if (gesture.Length > 0 && TryResolveGesture(gesture, out string action))
        {
            e.Handled = true;
            await FlushTypingAsync().ConfigureAwait(true);
            await ExecuteKeyActionAsync(action).ConfigureAwait(true);
            return;
        }

        // 没映射时只保留一个通用逃生键：Esc 等于返回。
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await FlushTypingAsync().ConfigureAwait(true);
            await ExecuteKeyActionAsync(KeyActions.Back).ConfigureAwait(true);
        }
    }

    /// <summary>在用户自定义映射里查这条组合键该做哪个动作；表为空即什么也不拦。</summary>
    private static bool TryResolveGesture(string gesture, out string action)
    {
        foreach (KeyBinding binding in AppSettings.Current.KeyBindings)
        {
            if (string.Equals(binding.Gesture, gesture, StringComparison.OrdinalIgnoreCase))
            {
                action = binding.Action;
                return true;
            }
        }

        action = string.Empty;
        return false;
    }

    /// <summary>
    /// 把一个动作发到手机。系统键（Home/Back/最近任务/电源/音量）必须走
    /// <c>uitest uiInput keyEvent</c>；编辑键（回车/退格/制表/方向键）走 <c>uinput -K</c>。
    /// </summary>
    private async Task ExecuteKeyActionAsync(string action)
    {
        switch (action)
        {
            case KeyActions.Home:
                await SendShellAsync("uitest uiInput keyEvent Home").ConfigureAwait(true);
                return;
            case KeyActions.Back:
                await SendShellAsync("uitest uiInput keyEvent Back").ConfigureAwait(true);
                return;
            case KeyActions.Recent:
                await SendShellAsync("uitest uiInput keyEvent 2720").ConfigureAwait(true);
                return;
            case KeyActions.Power:
                await SendShellAsync("uitest uiInput keyEvent Power").ConfigureAwait(true);
                return;
            case KeyActions.VolumeUp:
                await SendShellAsync("uitest uiInput keyEvent 16").ConfigureAwait(true);
                return;
            case KeyActions.VolumeDown:
                await SendShellAsync("uitest uiInput keyEvent 17").ConfigureAwait(true);
                return;
            default:
                break;
        }

        int code = action switch
        {
            KeyActions.Enter => 2054,
            KeyActions.Backspace => 2055,
            KeyActions.Tab => 2049,
            KeyActions.Left => 2012,
            KeyActions.Right => 2013,
            KeyActions.Up => 2014,
            KeyActions.Down => 2015,
            _ => 0,
        };

        if (code == 0)
        {
            return;
        }

        await SendShellAsync($"uinput -K -d {code} -u {code}").ConfigureAwait(true);
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
    /// <summary>
    /// 开窗时主窗口给的初始矩形：有竖屏记忆就用记忆，否则就用主窗口当前的位置与大小。
    /// 不在这里做比例校正，交给 <see cref="FitWindowToAspect"/>。
    /// </summary>
    private void ApplyInitialBounds()
    {
        if (InitialBounds is not { Width: > 1d, Height: > 1d } bounds)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    /// <summary>
    /// 关窗时记住当前朝向的位置与大小（竖屏、横屏各存一份），
    /// 并让主窗口挪到同一位置出现 —— <b>只挪位置，绝不改主窗口的大小</b>。
    /// </summary>
    /// <summary>
    /// 检测画面左右是否带黑边（手机横屏看剧时影片比屏幕窄），若有则等比放大画面把黑边挤出可视区，
    /// 窗口尺寸不动 —— 用户要求「横屏看剧时旁边的两条黑边自动去掉，同时保持窗口大小」。
    /// </summary>
    /// <summary>
    /// 底部三个功能键是画在画面之上的，颜色写死成白以后，遇到白底界面（微信、设置、阅读器）就完全看不见。
    /// 这里按画面底部那一条的平均亮度决定用白键还是深色键：深底配白键、浅底配深键。
    /// </summary>
    private void UpdateNavBarColor(IntPtr data, int stride, int width, int height)
    {
        long now = System.Environment.TickCount64;
        if (now - _navColorStamp < 400 || width < 40 || height < 40)
        {
            return;
        }

        _navColorStamp = now;

        // 只采样底部 1/6 高度：功能键就压在这块区域上，采别处会跟着页面中部颜色乱跳。
        int bandTop = Math.Max(0, height - Math.Max(24, height / 6));
        int stepY = Math.Max(1, (height - bandTop) / 12);
        int stepX = Math.Max(1, width / 24);

        long sum = 0;
        int count = 0;
        for (int y = bandTop; y < height; y += stepY)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x += stepX)
            {
                int offset = rowOffset + (x * 4);
                byte b = System.Runtime.InteropServices.Marshal.ReadByte(data, offset);
                byte g = System.Runtime.InteropServices.Marshal.ReadByte(data, offset + 1);
                byte r = System.Runtime.InteropServices.Marshal.ReadByte(data, offset + 2);

                // 感知亮度：人眼对绿色最敏感，直接取 RGB 平均值会让亮黄色被误判成暗色。
                sum += ((299L * r) + (587L * g) + (114L * b)) / 1000L;
                count++;
            }
        }

        if (count == 0)
        {
            return;
        }

        bool light = sum / count < 150;
        if (light == _navLight)
        {
            return;
        }

        _navLight = light;
        Color glyph = light ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF2, 0x1A, 0x1A, 0x1A);
        var fill = new SolidColorBrush(glyph);
        fill.Freeze();

        BackGlyph.Fill = fill;
        BackGlyph.Stroke = fill;
        HomeGlyph.Fill = fill;
        RecentGlyph.Fill = fill;
        _log.Info("navbar", $"画面底部偏{(light ? "暗" : "亮")}，功能键切换为{(light ? "白色" : "深色")}");
    }

    private void UpdateContentCrop(IntPtr data, int stride, int width, int height)
    {
        long now = System.Environment.TickCount64;
        if (now - _cropStamp < 500 || width < 120 || height < 120)
        {
            return;
        }

        _cropStamp = now;

        const int LumaThreshold = 26;
        int stepY = Math.Max(1, height / 24);

        bool ColumnHasContent(int x)
        {
            for (int y = stepY; y < height; y += stepY)
            {
                int offset = (y * stride) + (x * 4);
                byte b = System.Runtime.InteropServices.Marshal.ReadByte(data, offset);
                byte g = System.Runtime.InteropServices.Marshal.ReadByte(data, offset + 1);
                byte r = System.Runtime.InteropServices.Marshal.ReadByte(data, offset + 2);
                if (r > LumaThreshold || g > LumaThreshold || b > LumaThreshold)
                {
                    return true;
                }
            }

            return false;
        }

        int limit = Math.Max(8, width / 3);
        int left = 0;
        while (left < limit && !ColumnHasContent(left))
        {
            left++;
        }

        int right = 0;
        while (right < limit && !ColumnHasContent(width - 1 - right))
        {
            right++;
        }

        int bars = Math.Max(left, right);
        if (bars >= limit)
        {
            // 整幅都很暗（黑屏 / 熄屏），别当成黑边。
            bars = 0;
        }

        // 要求连续两次测量结果接近，避免个别暗帧导致反复缩放。
        if (Math.Abs(bars - _cropBars) > Math.Max(6, width / 60))
        {
            _cropBars = bars;
            return;
        }

        _cropBars = bars;
        double scale = bars < 4 ? 1d : Math.Min(1.8d, width / (double)Math.Max(1, width - (2 * bars)));
        if (Math.Abs(scale - _cropScale) < 0.01d)
        {
            return;
        }

        _cropScale = scale;
        ScreenImage.RenderTransformOrigin = new Point(0.5d, 0.5d);
        ScreenImage.RenderTransform = scale <= 1.001d ? Transform.Identity : new ScaleTransform(scale, 1d);
        _log.Info("crop", $"左右黑边 {bars}px，横向拉伸 {scale:0.###}（只去左右，不动上下）");
    }

    private void RememberGeometry()
    {
        AppSettings settings = AppSettings.Current;
        bool landscape = _aspect > 1d;

        // 竖屏该落在哪：横屏下关窗时，用户看到的是「手机横过来了」，
        // 主窗必须回到竖屏那个位置与尺寸，而不是横屏那块又宽又矮的矩形。
        double portraitLeft = Left;
        double portraitTop = Top;
        double portraitWidth = ActualWidth;
        double portraitHeight = ActualHeight;

        if (landscape && _portraitRestore is { Width: > 1d } portrait)
        {
            portraitWidth = portrait.Width;
            portraitHeight = portrait.Height + BarHeight;

            bool userMoved = _landscapePlaced is { } placed
                && (Math.Abs(Left - placed.X) > 2d || Math.Abs(Top - placed.Y) > 2d);
            if (userMoved)
            {
                // 横屏里用户自己拖过：按当前中心点还原成竖屏尺寸。
                portraitLeft = Left + ((ActualWidth - portraitWidth) / 2d);
                portraitTop = Top + ((ActualHeight - portraitHeight) / 2d);
            }
            else
            {
                // 没拖过：回到原竖屏位置，并补上横屏时被迫搬动的距离。
                portraitLeft = portrait.Left + _landscapeOffset.X;
                portraitTop = portrait.Top + _landscapeOffset.Y;
            }
        }

        if (WindowState == WindowState.Normal && portraitWidth > 200 && portraitHeight > 200)
        {
            // 只存竖屏档位：横屏尺寸是竖屏对调出来的，不需要单独记忆。
            settings.MirrorPortraitLeft = portraitLeft;
            settings.MirrorPortraitTop = portraitTop;
            settings.MirrorPortraitWidth = portraitWidth;
            settings.MirrorPortraitHeight = portraitHeight;
        }

        if (Owner is Window owner && owner.WindowState == WindowState.Normal)
        {
            Rect work = WindowPlacement.WorkArea(owner);
            double left = Math.Clamp(portraitLeft, work.Left, Math.Max(work.Left, work.Right - owner.Width));
            double top = Math.Clamp(portraitTop, work.Top, Math.Max(work.Top, work.Bottom - owner.Height));
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            owner.Left = left;
            owner.Top = top;
        }

        settings.Save();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 用 DWM 给本窗口上 Win11 圆角（DWMWCP_ROUND = 2）。
    /// WindowStyle=None 的自绘窗口不会自动获得系统圆角，所以必须显式设置，
    /// 否则主窗口是圆角、这个窗口是直角，看起来像两套界面。
    /// </summary>
    private void ApplyRoundedCorners()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int round = 2;
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

}
