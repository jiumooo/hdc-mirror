using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;
using Velopack;
using HdcMirror.App.Video;
using HdcMirror.App.Views;
using HdcMirror.Core.Diagnostics;
using HdcMirror.Core.Hdc;

namespace HdcMirror.App;

/// <summary>
/// 主窗口：标题栏（菜单 + 窗口按钮）/ 设备栏 / 手机屏幕。
/// 只负责界面编排与设备枚举，投屏与输入逻辑都在 Core。
/// </summary>
public partial class MainWindow
{
    /// <summary>可视外框相对 WPF Height 多出的高度（DWM 扩展边框，实测 79 像素）。</summary>
    private const double VisualFrameInflation = 79d;

    /// <summary>
    /// 手机屏幕宽高比。设备可能竖屏（1260×2720）也可能横屏（2720×1260），
    /// 所以初值只是占位，第一帧解码出来后按真实尺寸校正。
    /// </summary>
    private double _phoneAspect = 1260d / 2720d;

    /// <summary>手机屏幕四周统一留白（px）。</summary>
    private const double PhoneMargin = 5d;

    private bool _fittingPhone;

    /// <summary>内容模式开关：true 时手机屏缩成左下角缩略屏，内容页占满其余空间。</summary>
    private bool _contentOpen;

    /// <summary>缩略屏宽度（px）；高度按手机真实宽高比自动算。</summary>
    private const double ThumbnailWidth = 64d;

    /// <summary>缩略屏距窗口左边与底边的距离（px）。</summary>
    private const double ThumbnailMargin = 12d;

    /// <summary>缩略屏下方那行提示文字占用的高度，避免缩略屏压到文字上。</summary>
    private const double ThumbnailCaptionHeight = 20d;

    /// <summary>页面缓存：同一种页面只建一次，切换菜单时就地显示/隐藏。</summary>
    private readonly Dictionary<Type, UIElement> _pageCache = new();

    /// <summary>UI 线程上唯一的日志出口。</summary>
    private readonly FileLogSink _log = new();

    /// <summary>已记住设备的本地清单（只有显示名与端点，不含序列号）。</summary>
    private readonly DeviceStore _store = new();

    /// <summary>
    /// 端点线索库。「忘记设备」会清掉设备清单，但无线调试端口是随机分配的，
    /// 端口一旦丢了就再难扫到，所以线索单独留一份，扫描时优先用它。
    /// </summary>
    private readonly EndpointHints _hints = new();

    /// <summary>当前选中设备最近一次读到的信息，用于给手机窗口取标题。</summary>
    private HdcDeviceInfo _lastInfo = HdcDeviceInfo.Empty;

    /// <summary>定时抓手机画面做背景（见 Services\LockScreenCapture.cs）；没有设备前为 null。</summary>
    private LockScreenCapture? _lockShot;

    /// <summary>设备栏当前选中的设备。</summary>
    private DeviceEntry? _selected;

    /// <summary>正在读设备信息；启动流程与选中事件会同时触发，用它挡住重复绘制。</summary>
    private bool _infoLoading;

    /// <summary>设备管理页，首次打开时创建，之后复用。</summary>
    private DeviceManagePage? _managePage;

    /// <summary>镜像状态浮层当前显示的内容，避免每帧重复写同一个字符串。</summary>
    private string? _statusShown;
    private DispatcherTimer? _statusTimer;

    /// <summary>设备下拉的数据源。</summary>
    public ObservableCollection<DeviceEntry> Devices { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        RegisterTopMenus();

        // 带产品名的菜单项在代码里拼，避免在 XAML 里写死名称（需求 §6.4）
        MenuApp.Header = Strings.Menu_App;
        MenuAppAbout.Header = string.Format(Strings.Menu_App_AboutFormat, Strings.Menu_App);
        MenuAppExit.Header = string.Format(Strings.Menu_App_ExitFormat, Strings.Menu_App);
        MenuAppCheckUpdate.Header = Strings.Menu_App_CheckUpdate;
        MenuSettings.Header = Strings.Menu_Settings;
        MenuHelp.Header = Strings.Menu_Help;

        DataContext = this;

        // 首屏必须在 Loaded 之后设置内容：窗口未加载时控件模板尚未套用，
        // 提前操作内容宿主会在 WPF 内部抛 NullReferenceException（见 docs\10-缺陷记录.md BUG-001）。
        Loaded += OnFirstLoaded;
        StateChanged += OnWindowStateChanged;
    }

    /// <summary>设备栏当前选中的设备；切换时同步刷新下拉里每一项的按钮形态。</summary>
    private DeviceEntry? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            foreach (DeviceEntry item in Devices)
            {
                item.IsCurrent = ReferenceEquals(item, value);
            }
        }
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;

        // 先落位再算手机框：位置定了，UpdatePhoneFrameSize 才知道可用高度有多少。
        PlaceAtRightColumn();
        UpdatePhoneFrameSize();

        // 布局完成后再按真实外框高度居中：初次落位时 ActualHeight 还没算出来。
        CenterVertically();

        // 全新安装（还没记住过任何设备）时，手机屏中央直接给手动连接表单。

        // 有上次抓下来的手机画面就先铺上去，别一开就是纯黑。
        UpdatePhoneBackground();

        // 启动时若手机已经在无线调试里连着，就先读设备信息铺满屏幕，再开始投屏；
        // 没有设备就停在「尚未连接设备」，不空起一条会话。
        _ = AutoStartAsync();

        // 启动时静默查一次更新：只有真发现新版本才提示，未配置 / 404 / 断网都只写日志。
        _ = CheckForUpdatesAsync(manual: false);
    }
    /// <summary>
    /// 启动落位：记住过就回到上次关闭的位置（越界自动收回工作区）；
    /// 第一次启动则贴在屏幕最右边、上下居中。
    /// </summary>
    private void PlaceAtRightColumn()
    {
        Rect work = WindowPlacement.WorkArea(this);
        AppSettings settings = AppSettings.Current;

        if (settings.HasWindowPlacement)
        {
            Width = Math.Clamp(settings.WindowWidth, MinWidth, work.Width);
            Height = Math.Clamp(settings.WindowHeight, MinHeight, work.Height);
            Left = Math.Clamp(settings.WindowLeft, work.Left, Math.Max(work.Left, work.Right - Width));
            Top = Math.Clamp(settings.WindowTop, work.Top, Math.Max(work.Top, work.Bottom - Height));
            return;
        }

        double column = work.Width / 5d;
        double wanted = Math.Max(MinWidth, Math.Min(column, work.Width * 0.45));
        double tall = Math.Min(work.Height, wanted / _phoneAspect);

        Width = wanted;
        Height = Math.Max(MinHeight, tall);
        Left = Math.Max(work.Left, work.Right - wanted - 8d);
        Top = work.Top + Math.Max(0d, (work.Height - Height) / 2d);
    }

    /// <summary>把当前窗口的位置与大小记进设置，下次启动原样回来。</summary>
    private void SaveWindowPlacement()
    {
        AppSettings settings = AppSettings.Current;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
            settings.WindowWidth = ActualWidth > 0 ? ActualWidth : Width;
            settings.WindowHeight = ActualHeight > 0 ? ActualHeight : Height;
        }
        else
        {
            Rect restore = RestoreBounds;
            if (restore.Width > 200 && restore.Height > 200)
            {
                settings.WindowLeft = restore.Left;
                settings.WindowTop = restore.Top;
                settings.WindowWidth = restore.Width;
                settings.WindowHeight = restore.Height;
            }
        }

        settings.Save();
    }

    /// <summary>按真实外框高度把窗口在可用区域内上下居中（初次落位后补一次）。</summary>
    private void CenterVertically()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        Rect work = WindowPlacement.WorkArea(this);
        // WPF 的 Height 不含 DWM 扩展边框：实测可视外框比 Height 高 79 像素（截图逐像素核对过），
        // 按 Height 居中会整体偏低约 40 像素，所以这里用 Height + 79 参与计算。
        double visualHeight = Height + VisualFrameInflation;
        double target = work.Top + Math.Max(0d, (work.Height - visualHeight) / 2d);
        Top = Math.Min(target, Math.Max(work.Top, work.Bottom - visualHeight));
    }


    /// <summary>启动自检：有已连接设备就直接显示设备简介，没有就保持空态。</summary>
    private async Task AutoStartAsync()
    {
        try
        {
            var enumerator = new HdcDeviceEnumerator(new HdcRunner(log: _log), _log);
            IReadOnlyList<HdcDevice> devices = await enumerator.EnumerateAsync().ConfigureAwait(true);
            if (devices.Count == 0)
            {
                // 没有已连接的设备时不再自动扫局域网：只把用过的端点填进设备栏，点一下就连。
                // 想找新设备的人自己点「扫描」。
                RestoreRememberedDevices();
                return;
            }

            HdcDevice device = devices[0];
            foreach (HdcDevice candidate in devices)
            {
                AddDeviceDistinct(new DeviceEntry(candidate));
                _store.Remember(candidate.Serial, null);
            }

            SelectDevice(Devices.Count > 0 ? Devices[0] : null);
            await ShowDeviceInfoAsync(device).ConfigureAwait(true);
            ConnectButton.IsEnabled = true;
        }
        catch (HdcException ex)
        {
            Debug.WriteLine(ex.Message);
            ShowStatus(Strings.Status_HdcUnavailable);
        }
    }

    /// <summary>
    /// 启动时把线索库里用过的端点填进设备栏（不联网、不扫描）：
    /// 用户开机就能看到上次连过的设备，点「连接」直接 tconn，省掉一轮局域网扫描。
    /// </summary>
    private void RestoreRememberedDevices()
    {
        IReadOnlyList<EndpointHint> hints = _hints.List();
        bool hasVerified = false;
        foreach (EndpointHint hint in hints)
        {
            if (hint.Verified)
            {
                hasVerified = true;
                break;
            }
        }

        foreach (EndpointHint hint in hints)
        {
            // 有验证过的线索就只显示验证过的：没连成功过的端点多半是扫描误报。
            if (hasVerified && !hint.Verified)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(hint.Endpoint))
            {
                continue;
            }

            var entry = new DeviceEntry(hint.Endpoint);
            if (!string.IsNullOrWhiteSpace(hint.Name))
            {
                entry.Name = hint.Name!;
            }

            AddDeviceDistinct(entry);
        }

        ConnectButton.IsEnabled = Devices.Count > 0;
        if (Devices.Count > 0)
        {
            SelectDevice(Devices[0]);
        }
    }

    /// <summary>
    /// 在独立窗口里打开一个页面（设置 / 关于）。
    /// 主界面本身就是手机屏幕，不再占用内容宿主，所以页面走弹出窗口。
    /// </summary>
    internal void ShowPage(Type pageType, string title)
    {
        _log.Write(LogLevel.Debug, "App.Main", "在主窗口内打开页面：" + title);
        // 非设置页（关于 / 支持作者 / 使用说明）不挂设置分栏，否则会「人在支持作者页、下面却是设置分栏」。
        SettingsTabs.Visibility = Visibility.Collapsed;
        ShowContentPage(GetOrCreatePage(pageType));
    }

    /// <summary>按类型取页面实例，第一次取时创建并挂到内容宿主上。</summary>
    private UIElement GetOrCreatePage(Type pageType)
    {
        if (_pageCache.TryGetValue(pageType, out UIElement? cached))
        {
            return cached;
        }

        if (Activator.CreateInstance(pageType) is not UserControl page)
        {
            throw new InvalidOperationException("无法创建页面：" + pageType.Name);
        }

        page.Visibility = Visibility.Collapsed;
        ManageHost.Children.Add(page);
        _pageCache[pageType] = page;
        return page;
    }

    /// <summary>把某一页显示到内容宿主，其余页收起；同时把手机屏切成缩略屏。</summary>
    private void ShowContentPage(UIElement page)
    {
        foreach (UIElement child in ManageHost.Children)
        {
            child.Visibility = ReferenceEquals(child, page) ? Visibility.Visible : Visibility.Collapsed;
        }

        SetContentMode(true);
    }

    /// <summary>内容开关：手机屏在「正常大小」与「左下角缩略屏」之间做 260ms 缓动。</summary>
    /// <param name="open">true 进入内容模式，false 回到手机屏。</param>
    private void SetContentMode(bool open)
    {
        if (_contentOpen == open)
        {
            return;
        }

        _contentOpen = open;
        ThumbnailHint.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (open)
        {
            ContentHost.Visibility = Visibility.Visible;
            ApplyPhoneTransform(true, null);
            return;
        }

        ApplyPhoneTransform(true, () =>
        {
            if (_contentOpen)
            {
                return;
            }

            ContentHost.Visibility = Visibility.Collapsed;
            foreach (UIElement child in ManageHost.Children)
            {
                child.Visibility = Visibility.Collapsed;
            }
        });
    }

    /// <summary>
    /// 计算并应用手机屏的缩放/位移：内容模式时缩到左下角当缩略屏，正常模式时复位。
    /// 缩略屏固定 84px 宽，高度按真实宽高比自动得到。
    /// </summary>
    /// <param name="animate">是否走动画；布局变化时用 false 直接对齐。</param>
    /// <param name="restore">动画结束后的回调。</param>
    private void ApplyPhoneTransform(bool animate, Action? restore)
    {
        double frameWidth = PhoneFrame.ActualWidth;
        double frameHeight = PhoneFrame.ActualHeight;
        double target = _contentOpen && frameWidth > 0 ? ThumbnailWidth / frameWidth : 1d;
        double scale = double.IsFinite(target) && target > 0 ? target : 1d;

        double shiftX = 0d;
        double shiftY = 0d;
        if (_contentOpen)
        {
            // PhoneFrame 在 PhoneArea 里是居中放置的，缩放原点是左上角（RenderTransformOrigin=0,0），
            // 所以位移必须先把居中带来的左上偏移扣掉，否则缩略屏会整体偏右下、底边被窗口切掉。
            double offsetX = (PhoneArea.ActualWidth - frameWidth) / 2d;
            double offsetY = (PhoneArea.ActualHeight - frameHeight) / 2d;
            double shrunkHeight = frameHeight * scale;
            shiftX = ThumbnailMargin - offsetX;
            shiftY = PhoneArea.ActualHeight - ThumbnailMargin - ThumbnailCaptionHeight - shrunkHeight - offsetY;
        }

        if (!animate)
        {
            PhoneScale.ScaleX = scale;
            PhoneScale.ScaleY = scale;
            PhoneShift.X = shiftX;
            PhoneShift.Y = shiftY;
            restore?.Invoke();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var scaleX = new DoubleAnimation(scale, duration) { EasingFunction = ease };
        var scaleY = new DoubleAnimation(scale, duration) { EasingFunction = ease };
        var shiftAnimationX = new DoubleAnimation(shiftX, duration) { EasingFunction = ease };
        var shiftAnimationY = new DoubleAnimation(shiftY, duration) { EasingFunction = ease };

        if (restore is not null)
        {
            shiftAnimationY.Completed += (_, _) => restore();
        }

        PhoneScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        PhoneScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        PhoneShift.BeginAnimation(TranslateTransform.XProperty, shiftAnimationX);
        PhoneShift.BeginAnimation(TranslateTransform.YProperty, shiftAnimationY);
    }

    private void OnContentBackClick(object sender, RoutedEventArgs e)
    {
        if (_managePage is not null)
        {
            _managePage.Refresh();
        }

        SetContentMode(false);
    }


    private static void RequestExit()
    {
        if (Application.Current is App app)
        {
            app.RequestExit();
        }
    }

    // ── 标题栏按钮 ────────────────────────────────────────────────

    private void OnCaptionMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCaptionMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCaptionClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // 最大化后换成「还原」字形 E923，与系统行为一致
        CaptionMaximize.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        UpdatePhoneFrameSize();
    }

    // ── 手机屏幕尺寸 ──────────────────────────────────────────────

    private void OnPhoneAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePhoneFrameSize();

    /// <summary>
    /// 手机屏幕四边统一 <see cref="PhoneMargin"/> 留白：
    /// 先按 1260:2720 等比算出能塞进「可用区域减两倍边距」的最大尺寸，
    /// 再把窗口高度收敛到刚好容纳它，这样左/右/下（以及设备栏下方）的间距全部相同，
    /// 不会出现左右 40px、底部 12px 这种不一致。
    /// </summary>
    private void UpdatePhoneFrameSize()
    {
        if (PhoneArea is null || PhoneFrame is null || _fittingPhone)
        {
            return;
        }

        double availableWidth = PhoneArea.ActualWidth - (PhoneMargin * 2);
        double availableHeight = PhoneArea.ActualHeight - (PhoneMargin * 2);
        if (availableWidth <= 1 || availableHeight <= 1)
        {
            return;
        }

        double width = Math.Min(availableWidth, availableHeight * _phoneAspect);
        double height = width / _phoneAspect;

        PhoneFrame.Width = width;
        PhoneFrame.Height = height;

        // 让「宽度」成为唯一约束：目标可用高度 = 可用宽度 / 宽高比。
        // 可用高度比目标大就收矮窗口，比目标小就长高窗口，
        // 于是左 / 右 / 下三边留白在任何窗口尺寸下都恰好等于 PhoneMargin。
        if (WindowState == WindowState.Normal)
        {
            double targetAvailableHeight = availableWidth / _phoneAspect;
            double surplus = availableHeight - targetAvailableHeight;
            if (Math.Abs(surplus) > 1)
            {
                _fittingPhone = true;
                try
                {
                    double workArea = SystemParameters.WorkArea.Height;
                    double wanted = ActualHeight - surplus;
                    Height = Math.Max(MinHeight, Math.Min(workArea, wanted));
                }
                finally
                {
                    _fittingPhone = false;
                }
            }
        }
    }

    // ── 设备栏 ────────────────────────────────────────────────────

    /// <summary>
    /// 按线索做一次局域网发现。<paramref name="deep"/> 为真时关掉广播、对线索主机做全端口细扫，
    /// 用来应付「无线调试端口被系统换号」的情况（慢，但只针对已知的那几台机器）。
    /// </summary>
    private static Task<IReadOnlyList<HdcDiscoveredDevice>> ScanAsync(
        FileLogSink log,
        IReadOnlyList<string> hints,
        bool deep,
        CancellationToken cancellationToken = default,
        IProgress<HdcDiscoveryProgress>? progress = null)
    {
        var options = new HdcDiscoveryOptions
        {
            KnownEndpoints = hints,
            EnableBroadcast = !deep,
            // 广播实测固定 6 秒才返回，且本机常被防火墙挡住：常规档给 4 秒，细扫档不给广播。
            BroadcastTimeout = deep ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(4),
            DeepScan = deep && hints.Count > 0,
            // 细扫档只针对线索主机：跳过 /24 全网段探测（实测省约 4 秒）。
            ProbeLocalSubnets = !deep,
            // 细扫是「主机 × 6 万个端口」，并发拉高、超时压短，才不至于让用户干等一分钟。
            DeepScanConcurrency = 768,
            MaxConcurrency = deep ? 128 : 64,
            ConnectTimeoutMilliseconds = deep ? 80 : 250,
            HdcSilenceWindowMilliseconds = deep ? 250 : 400,
        };

        return new HdcDeviceDiscovery(log: log, options: options).ScanAsync(progress, cancellationToken);
    }

    /// <summary>细扫进度回调：把「主机 · 百分比」写到状态栏，别让用户以为卡死了。</summary>
    private void OnDeepScanProgress(HdcDiscoveryProgress progress)
    {
        if (progress.Stage != HdcDiscoveryStage.DeepScanning)
        {
            return;
        }

        int percent = (int)Math.Round(progress.Ratio * 100d);
        ShowStatus(string.Format(
            CultureInfo.CurrentCulture,
            Strings.Status_ScanDeepProgressFormat,
            string.IsNullOrEmpty(progress.Detail) ? "-" : progress.Detail,
            percent));
    }

    /// <summary>从线索库里取这台设备上次用的显示名；没有就返回 <see langword="null"/>。</summary>
    private string? HintName(string endpoint)
    {
        foreach (EndpointHint hint in _hints.List())
        {
            if (string.Equals(hint.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            {
                return hint.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// 扫描按钮一身两用：没有设备就直接去局域网里找；已经连着设备就先刷新这台的信息，
    /// 再顺带扫一遍局域网里的其它设备（用户要求「扫描兼刷新，不再单独留刷新按钮」）。
    /// </summary>
    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (Selected is DeviceEntry { Device: { } device })
        {
            await ShowDeviceInfoAsync(device).ConfigureAwait(true);
        }

        await RefreshDevicesAsync().ConfigureAwait(true);
    }

    /// <summary>扫描并用结果填充设备下拉；已连接设备与局域网发现并行跑。</summary>
    private async Task RefreshDevicesAsync()
    {
        ScanButton.IsEnabled = false;
        ShowStatus(Strings.Status_Scanning);
        try
        {
            var log = new FileLogSink();
            var runner = new HdcRunner(log: log);
            var enumerator = new HdcDeviceEnumerator(runner, log);

            // 线索库里的端点最可能对上同一台手机——「忘记设备」之后端口还得靠它找回来。
            IReadOnlyList<string> hints = _hints.Endpoints();
            if (hints.Count == 0)
            {
                // 还没有任何「验证过」的线索时退回全部，否则第一次换端口就无据可查。
                hints = _hints.Endpoints(verifiedOnly: false);
            }

            // 已连接设备与局域网发现同时进行：前者读 hdc list targets，后者要等广播与端口探测。
            Task<IReadOnlyList<HdcDevice>> connectedTask = enumerator.EnumerateAsync();

            // 线索主机并行做端口细扫：无线调试端口被系统换号时，只有全端口扫描能把它找回来。
            using var deepCts = new CancellationTokenSource();
            var deepProgress = new Progress<HdcDiscoveryProgress>(OnDeepScanProgress);
            Task<IReadOnlyList<HdcDiscoveredDevice>> deepTask = hints.Count > 0
                ? ScanAsync(log, hints, deep: true, deepCts.Token, deepProgress)
                : Task.FromResult<IReadOnlyList<HdcDiscoveredDevice>>(Array.Empty<HdcDiscoveredDevice>());

            IReadOnlyList<HdcDiscoveredDevice> discovered = Array.Empty<HdcDiscoveredDevice>();
            try
            {
                discovered = await ScanAsync(log, hints, deep: false).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HdcException or IOException or InvalidOperationException)
            {
                // 发现失败不影响已连接设备的使用。
                _log.Write(LogLevel.Warn, "discovery", "局域网扫描失败：" + ex.Message);
            }

            if (discovered.Count == 0 && hints.Count > 0)
            {
                // 常规扫描（广播 + 固定端口）扫不到，基本就是端口被换号了：等细扫结果。
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Status_ScanDeepFormat, hints[0]));
                _log.Write(LogLevel.Info, "discovery", "常规扫描没结果，等待线索主机的端口细扫：" + string.Join(", ", hints));
                try
                {
                    discovered = await deepTask.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (ex is HdcException or IOException or InvalidOperationException)
                {
                    _log.Write(LogLevel.Warn, "discovery", "端口细扫失败：" + ex.Message);
                }
            }
            else
            {
                // 常规扫描已经找到设备，后台细扫没必要继续占用端口。
                deepCts.Cancel();
            }

            // 已连接设备这一路最后收，保证上面两条路径都不会把它漏掉。
            IReadOnlyList<HdcDevice> connected;
            try
            {
                connected = await connectedTask.ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HdcException or IOException or InvalidOperationException)
            {
                _log.Write(LogLevel.Warn, "discovery", "枚举已连接设备失败：" + ex.Message);
                connected = Array.Empty<HdcDevice>();
            }

            Devices.Clear();

            foreach (HdcDevice device in connected)
            {
                AddDeviceDistinct(new DeviceEntry(device));
                _store.Remember(device.Serial, null);
                _hints.Remember(device.Serial, null);
            }

            foreach (HdcDiscoveredDevice found in discovered)
            {
                if (found.IsConnected)
                {
                    continue; // 已连接的那批上面已经加过了
                }

                var item = new DeviceEntry(found);
                string? hint = HintName(found.Endpoint);
                if (!string.IsNullOrWhiteSpace(hint)
                    && (string.IsNullOrWhiteSpace(item.Name)
                        || string.Equals(item.Name, found.Endpoint, StringComparison.OrdinalIgnoreCase)))
                {
                    // 靠线索找回来的设备，hdc 只给 IP:端口；名称从线索库补上，别让用户看成一串地址。
                    item.Name = hint;
                }

                AddDeviceDistinct(item);
                // 扫描出来的候选还没连上过，不写线索库：避免把 5555 之类的误报当成"上次用过的端口"。
            }

            ConnectButton.IsEnabled = Devices.Count > 0;
            if (Devices.Count > 0)
            {
                SelectDevice(Devices.Count > 0 ? Devices[0] : null);

            }

            ShowStatus(Devices.Count > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Status_DeviceFoundFormat, Devices.Count)
                : Strings.Status_NoDeviceFound);

            // hdc 的局域网扫描只给 IP:端口，机型要连上才读得到；这里补一次，用户看到的就不是一串地址。
            _ = FillDeviceNamesAsync();
        }
        catch (HdcException ex)
        {
            // hdc 找不到或拒绝执行：把原因留在日志里，界面保持空列表
            Devices.Clear();
            ConnectButton.IsEnabled = false;
            ShowStatus(Strings.Status_HdcUnavailable);
            Debug.WriteLine(ex.Message);
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    /// <summary>按端点去重后加入列表：同一台设备在「已记住」与「当前枚举」里只留一条。</summary>
    private void AddDeviceDistinct(DeviceEntry candidate)
    {
        string key = EndpointOf(candidate);
        if (!string.IsNullOrEmpty(key))
        {
            foreach (DeviceEntry existing in Devices)
            {
                if (string.Equals(EndpointOf(existing), key, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            // 本机已经记住过这台设备时直接用记住的名字，省掉一次连设备读机型的往返。
            if (string.IsNullOrWhiteSpace(candidate.Name) || string.Equals(candidate.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                string? saved = SavedNameFor(key);
                if (saved is not null)
                {
                    candidate.Name = saved;
                }
            }
        }

        Devices.Add(candidate);
    }

    /// <summary>从本机设备清单里按端点找已记住的名字，没记住返回 null。</summary>
    private string? SavedNameFor(string endpoint)
    {
        foreach (SavedDevice saved in _store.Load())
        {
            if (string.Equals(saved.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            {
                string title = saved.Title;
                return string.IsNullOrWhiteSpace(title) || string.Equals(title, endpoint, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : title;
            }
        }

        return null;
    }

    /// <summary>
    /// hdc 的局域网发现只返回 IP:端口，机型要连上才读得到。
    /// 扫描结束后对「名字还等于地址」的条目补读一次 const.product.model；
    /// 读不到就保持地址显示，不弹错、不影响其它设备。
    /// </summary>
    private async Task FillDeviceNamesAsync()
    {
        foreach (DeviceEntry entry in Devices.ToList())
        {
            string endpoint = EndpointOf(entry);
            if (endpoint.Length == 0)
            {
                continue;
            }

            bool stillAddress = string.IsNullOrWhiteSpace(entry.Name)
                || string.Equals(entry.Name, endpoint, StringComparison.OrdinalIgnoreCase);
            if (!stillAddress)
            {
                continue;
            }

            try
            {
                var runner = new HdcRunner(
                    log: _log,
                    deviceSerial: endpoint,
                    commandTimeout: TimeSpan.FromSeconds(5));
                HdcResult result = await runner.ShellAsync("param get const.product.model").ConfigureAwait(true);
                string model = result.StandardOutput.Trim();
                if (IsPlausibleModel(model, endpoint))
                {
                    entry.Name = model;
                    _hints.Remember(endpoint, model, verified: true);
                    _log.Write(LogLevel.Info, "device", "补读到设备名称：" + model + "（" + endpoint + "）");
                }
            }
            catch (HdcException ex)
            {
                _log.Write(LogLevel.Debug, "device", "读取设备名称失败：" + endpoint + " → " + ex.Message);
            }
        }
    }

    private static string EndpointOf(DeviceEntry entry)
        => entry.Endpoint ?? entry.Device?.Serial ?? string.Empty;

    /// <summary>
    /// hdc 失败时会把「[Fail]IE0010051 Device not found」这类文本打到标准输出，
    /// 直接拿去当机型会显示在设备下拉里（实测过），所以只接受「像机型的一行短文本」。
    /// </summary>
    private static bool IsPlausibleModel(string model, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        if (model.Length > 40 || model.IndexOf('\n') >= 0 || model.IndexOf('\r') >= 0)
        {
            return false;
        }

        if (model.StartsWith("[", StringComparison.Ordinal) || model.StartsWith("Fail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] noise = { "fail", "error", "not found", "not exist", "permission denied", "unknown" };
        foreach (string word in noise)
        {
            if (model.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return !string.Equals(model, endpoint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>切回「设备简介」视图（手机屏里显示九项信息）。</summary>
    private void ShowInfoView()
    {
        if (InfoList.Children.Count == 0)
        {
            return;
        }

        SetContentMode(false);
        EmptyState.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Visible;
    }

    /// <summary>下拉收起时回到简介视图：再选同一台设备不会触发 SelectionChanged，靠这里兜住。</summary>
    /// <summary>设备框：有设备就展开列表；一台都没有时不再自动扫描（按用户要求），只提示去点「扫描」。</summary>
    private void OnDeviceBoxClick(object sender, RoutedEventArgs e)
    {
        if (Devices.Count == 0)
        {
            CloseDevicePopup();
            return;
        }

        if (DevicePopup.IsOpen)
        {
            CloseDevicePopup();
            return;
        }

        DeviceList.ItemsSource = Devices;
        DevicePopupPanel.Width = Math.Max(240d, DeviceBox.ActualWidth);
        DevicePopup.IsOpen = true;
    }

    private void CloseDevicePopup() => DevicePopup.IsOpen = false;

    /// <summary>点下拉里的某一行：选中它并显示简介。</summary>
    private void OnDeviceRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceEntry entry)
        {
            return;
        }

        e.Handled = true;
        CloseDevicePopup();
        SelectDevice(entry);
    }

    /// <summary>下拉里未连接设备的「连接」按钮。</summary>
    private async void OnDeviceItemConnectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceEntry entry)
        {
            return;
        }

        e.Handled = true;
        CloseDevicePopup();
        SelectDevice(entry);
        await ConnectEntryAsync(entry).ConfigureAwait(true);
    }

    /// <summary>下拉里当前设备的「忘记」按钮。</summary>
    private async void OnDeviceItemForgetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceEntry entry)
        {
            return;
        }

        e.Handled = true;
        CloseDevicePopup();
        await ForgetDeviceAsync(entry).ConfigureAwait(true);
    }

    /// <summary>选中一台设备（不联网）：更新设备框文字、按钮状态与简介视图。</summary>
    /// <param name="entry">要选中的设备；null 表示清空。</param>
    private void SelectDevice(DeviceEntry? entry)
    {
        Selected = entry;
        DeviceBoxText.Text = entry?.Name ?? Strings.Dev_NoDevice;

        if (entry is null)
        {
            ConnectButton.IsEnabled = false;
            return;
        }

        ConnectButton.IsEnabled = true;
        ShowInfoView();

        // 已连接的设备直接读简介；仅发现的设备要等点了「连接」才拿到序列号。
        if (entry.Device is { } device)
        {
            _ = ShowDeviceInfoAsync(device);
            return;
        }

        InfoList.Children.Clear();
        AddInfoRow(Strings.Dev_Info_Name, entry.Name);
        AddInfoRow(Strings.Dev_Info_Address, EndpointOf(entry));
        ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Status_DeviceSelectedFormat, entry.Name));
    }


    /// <summary>
    /// 「忘记设备」＝删掉本机保存的这条记录 + 断开当前 hdc 连接。
    /// 注意：手机会一直记着本机的 hdc 公钥（<c>%USERPROFILE%\.harmony\hdckey.pub</c>），
    /// 所以下次扫描到再点连接不会再弹一次授权确认；想重新配对必须在手机上撤销调试授权。
    /// </summary>
    private async Task ForgetDeviceAsync(DeviceEntry entry)
    {
        string endpoint = EndpointOf(entry);
        if (endpoint.Length == 0)
        {
            return;
        }

        _log.Write(LogLevel.Info, "App.Main", "忘记设备请求：" + endpoint);

        string name = string.IsNullOrWhiteSpace(entry.Name) ? endpoint : entry.Name;
        bool confirmed = ConfirmWindow.Ask(
            this,
            Strings.Confirm_DeleteTitle,
            string.Format(CultureInfo.CurrentCulture, Strings.Confirm_DeleteBodyFormat, name),
            Strings.Confirm_DeleteOk);
        if (!confirmed)
        {
            return;
        }

        _store.Remove(endpoint);
        await DisconnectAsync(endpoint).ConfigureAwait(true);

        int removedIndex = Devices.IndexOf(entry);
        Devices.Remove(entry);

        if (Devices.Count > 0)
        {
            SelectDevice(Devices[Math.Clamp(removedIndex, 0, Devices.Count - 1)]);
        }
        else
        {
            SelectDevice(null);
            ConnectButton.IsEnabled = false;
            InfoList.Children.Clear();
            InfoPanel.Visibility = Visibility.Collapsed;
            SetContentMode(false);
            MirrorStatusHost.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }

        if (_managePage is not null)
        {
            _managePage.Refresh();
        }

        ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Status_DeviceRemovedFormat, name));
        _log.Write(LogLevel.Info, "App.Main", "已忘记设备：" + name + "（" + endpoint + "）");
    }

    /// <summary>若这台设备当前在 hdc 连接列表里，就 <c>tconn ... -remove</c> 断开。</summary>
    /// <param name="endpoint">形如 <c>192.0.2.10:5555</c> 的设备标识。</param>
    /// <returns>异步任务。</returns>
    private async Task DisconnectAsync(string endpoint)
    {
        try
        {
            // 必须用不带 -t 的执行器：tconn 的目标就是参数里的端点。
            var runner = new HdcRunner(log: _log);
            HdcResult targets = await runner.RunAsync(new[] { "list", "targets" }).ConfigureAwait(true);
            bool connected = targets.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(line => string.Equals(line, endpoint, StringComparison.OrdinalIgnoreCase));

            if (!connected)
            {
                return;
            }

            HdcResult result = await runner.RunAsync(new[] { "tconn", endpoint, "-remove" }).ConfigureAwait(true);
            _log.Write(
                LogLevel.Info,
                "App.Main",
                "已断开 hdc 连接：" + endpoint + " → " + result.StandardOutput.Trim());
        }
        catch (HdcException ex)
        {
            _log.Write(LogLevel.Warn, "App.Main", "断开连接失败：" + ex.Message);
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not DeviceEntry entry)
        {
            return;
        }

        await ConnectEntryAsync(entry).ConfigureAwait(true);
    }

    /// <summary>把某个条目真正连上并打开镜像窗（下拉项里的「连接」也走这里）。</summary>
    /// <param name="entry">要连接的条目。</param>
    private async Task ConnectEntryAsync(DeviceEntry entry)
    {
        HdcDevice? device = await ResolveDeviceAsync(entry).ConfigureAwait(true);
        if (device is null)
        {
            return;
        }

        await ShowDeviceInfoAsync(device).ConfigureAwait(true);
        OpenMirrorWindow(device);
    }

    /// <summary>把下拉条目落实成一台可用设备：尚未连接的先 tconn，再重新枚举拿到序列号。</summary>
    /// <param name="entry">下拉条目。</param>
    /// <returns>可用设备；失败返回 null。</returns>
    private async Task<HdcDevice?> ResolveDeviceAsync(DeviceEntry entry)
    {
        if (entry.Device is { } connected)
        {
            return connected;
        }

        string? endpoint = entry.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        ConnectButton.IsEnabled = false;
        ShowStatus(Strings.Status_Connecting);
        try
        {
            var discovery = new HdcDeviceDiscovery(log: _log);
            if (!await discovery.ConnectAsync(endpoint).ConfigureAwait(true))
            {
                ShowStatus(Strings.Status_ConnectFailed);
                return null;
            }

            var enumerator = new HdcDeviceEnumerator(new HdcRunner(log: _log), _log);
            IReadOnlyList<HdcDevice> nowConnected = await enumerator.EnumerateAsync().ConfigureAwait(true);
            HdcDevice? match = nowConnected.FirstOrDefault(
                candidate => string.Equals(candidate.Serial, endpoint, StringComparison.OrdinalIgnoreCase));
            match ??= nowConnected.Count > 0 ? nowConnected[0] : null;

            // 连上了就把端点记进线索库：以后即使「忘记设备」，扫描也能凭它把端口找回来。
            _hints.Remember(endpoint, null, verified: true);
            return match;
        }
        catch (HdcException ex)
        {
            ShowStatus(Strings.Status_ConnectFailed);
            Debug.WriteLine(ex.Message);
            return null;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>打开独立手机窗口：主窗口先收起，手机窗口关掉后再回来。</summary>
    /// <param name="device">要镜像的设备。</param>
    private void OpenMirrorWindow(HdcDevice device)
    {
        string title = string.IsNullOrWhiteSpace(_lastInfo.Name) ? device.Serial : _lastInfo.Name;
        _store.Remember(device.Serial, title);

        // 出生位置永远跟随主窗口此刻的位置与大小 —— 用户要求「我把主窗挪到哪，
        // 手机窗就在哪出现」，所以不再用上次关窗时记下的坐标（那正是跑偏的原因）。
        // 高度由 MirrorWindow 里的 FitWindowToAspect 按手机比例重算，这里只给个初值。
        Rect initial = new Rect(
            Left,
            Top,
            Math.Max(320d, ActualWidth > 1d ? ActualWidth : Width),
            Math.Max(480d, ActualHeight > 1d ? ActualHeight : Height));

        var window = new MirrorWindow(device, title, _log)
        {
            Owner = this,
            InitialBounds = initial,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Closed += (_, _) =>
        {
            Show();
            Activate();
            _managePage?.Refresh();
            ShowStatus(Strings.Status_Mirror_Stopped);
        };

        Hide();
        window.Show();
    }

    /// <summary>在手机屏幕区域显示设备管理面板。</summary>
    internal void ShowManagePage() => ShowSettingsTab("devices");

    private DeviceManagePage CreateManagePage()
    {
        var page = new DeviceManagePage(_store);
        page.ConnectRequested += async (_, endpoint) => await ConnectEndpointAsync(endpoint).ConfigureAwait(true);
        page.Deleted += (_, endpoint) => RemoveDeviceByEndpoint(endpoint);
        ManageHost.Children.Add(page);
        return page;
    }

    /// <summary>
    /// 设置分栏就地切换：设备管理 / 通用设置 / 按键映射 / 音画与音频 都显示在手机屏这块区域里，不弹新窗口。
    /// </summary>
    /// <param name="key">devices / general / keys / video / audio。</param>
    private void ShowSettingsTab(string key)
    {
        SettingsTabs.Visibility = Visibility.Visible;

        if (key is "general" or "av" or "audio" or "video" or "keys")
        {
            Type pageType = key switch
            {
                "keys" => typeof(KeysPage),
                "av" => typeof(AvPage),
                "audio" => typeof(AvPage),
                "video" => typeof(VideoPage),
                _ => typeof(SettingsPage),
            };
            ShowContentPage(GetOrCreatePage(pageType));
            UpdateTabHighlight(key);
            return;
        }

        _managePage ??= CreateManagePage();
        _managePage.Refresh();
        ShowContentPage(_managePage);
        UpdateTabHighlight("devices");
    }

    /// <summary>顶部分栏按钮点击：直接切到对应页面。</summary>
    private void OnContentTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && key.Length > 0)
        {
            ShowSettingsTab(key);
        }
    }

    /// <summary>高亮当前分栏，其余保持普通样式。</summary>
    private void UpdateTabHighlight(string key)
    {
        (string Key, Wpf.Ui.Controls.Button Button)[] tabs =
        {
            ("devices", TabDevicesButton),
            ("general", TabGeneralButton),
            ("keys", TabKeysButton),
            ("video", TabVideoButton),
            ("audio", TabAudioButton),
        };

        foreach ((string tabKey, Wpf.Ui.Controls.Button button) in tabs)
        {
            bool active = string.Equals(tabKey, key, StringComparison.Ordinal);
            button.Appearance = active ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>设备管理页删掉一条记录后，同步从设备栏下拉里移除同一台设备。</summary>
    private void RemoveDeviceByEndpoint(string endpoint)
    {
        DeviceEntry? match = Devices.FirstOrDefault(
            item => string.Equals(EndpointOf(item), endpoint, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        Devices.Remove(match);
        if (Devices.Count == 0)
        {
            ConnectButton.IsEnabled = false;
        }
        else
        {
            SelectDevice(Devices.Count > 0 ? Devices[0] : null);
        }

        ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Status_DeviceRemovedFormat, endpoint));
    }

    /// <summary>设备管理页发起的连接：tconn → 读信息 → 打开手机窗口。</summary>
    /// <param name="endpoint">形如 192.0.2.10:5555 的端点。</param>
    /// <returns>异步任务。</returns>
    private async Task ConnectEndpointAsync(string endpoint)
    {
        DeviceEntry entry = Devices.FirstOrDefault(
                item => string.Equals(item.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            ?? new DeviceEntry(endpoint);

        HdcDevice? device = await ResolveDeviceAsync(entry).ConfigureAwait(true);
        if (device is null)
        {
            ShowStatus(Strings.Status_ConnectFailed);
            return;
        }

        await ShowDeviceInfoAsync(device).ConfigureAwait(true);
        OpenMirrorWindow(device);
    }

    /// <summary>当前在手机屏上展示信息的设备；双击手机屏直接连它。</summary>
    private HdcDevice? _infoDevice;

    /// <summary>双击手机屏：已经扫描/读取到设备信息时，直接打开镜像窗口。</summary>
    private void OnPhoneFrameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_contentOpen && e.ClickCount == 1)
        {
            e.Handled = true;
            SetContentMode(false);
            return;
        }

        if (e.ClickCount != 2 || _infoDevice is null)
        {
            return;
        }

        e.Handled = true;
        OpenMirrorWindow(_infoDevice);
    }


    private void OnMenuDevicesClick(object sender, RoutedEventArgs e) => ShowManagePage();

    /// <summary>
    /// 对这个端点开定时抓图。锁屏壁纸会轮换，所以按 <see cref="AppSettings.LockScreenIntervalSeconds"/>
    /// 周期性重抓；屏幕熄灭时抓不到，抓取器会静默跳过、等下一轮，不会去点亮手机。
    /// </summary>
    private void EnsureLockCapture(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        _lockShot ??= CreateLockCapture();
        _lockShot.Start(endpoint);
    }

    private LockScreenCapture CreateLockCapture()
    {
        var capture = new LockScreenCapture(_log);
        capture.Captured += OnLockScreenCaptured;
        return capture;
    }

    /// <summary>设置页「立即抓取」调这里：抓一张并把结果说给人听。</summary>
    internal async Task<string> CaptureLockScreenNowAsync()
    {
        string? endpoint = Selected?.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Strings.Settings_LockShotNoDevice;
        }

        _lockShot ??= CreateLockCapture();
        _lockShot.Start(endpoint);
        bool ok = await _lockShot.CaptureOnceAsync().ConfigureAwait(true);
        return ok
            ? string.Format(CultureInfo.CurrentCulture, Strings.Settings_LockShotOk, LockScreenCapture.TargetPath)
            : Strings.Settings_LockShotFail;
    }

    private void OnLockScreenCaptured(string path)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLockScreenCaptured(path)));
            return;
        }

        UpdatePhoneBackground();
    }

    /// <summary>把缓存图铺到手机屏当背景（压暗到 55% 保证白字可读），没有缓存就保持纯黑。</summary>
    private void UpdatePhoneBackground()
    {
        try
        {
            if (!LockScreenCapture.HasCache)
            {
                return;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(LockScreenCapture.TargetPath);
            image.EndInit();
            image.Freeze();

            PhoneFrame.Background = new ImageBrush(image)
            {
                Stretch = Stretch.UniformToFill,
                Opacity = 0.55,
            };
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warn, "App.Main", "加载锁屏背景失败：" + ex.Message);
        }
    }

    /// <summary>把设备信息面板显示出来：九项全部来自真机读取，读不到的留「—」。</summary>
    private async Task ShowDeviceInfoAsync(HdcDevice device)
    {
        if (_infoLoading)
        {
            return;
        }

        _infoLoading = true;
        try
        {
            await FillInfoAsync(device).ConfigureAwait(true);
        }
        finally
        {
            _infoLoading = false;
        }
    }

    /// <summary>真正去读设备信息并铺满手机屏幕。</summary>
    /// <param name="device">目标设备。</param>
    /// <returns>异步任务。</returns>
    private async Task FillInfoAsync(HdcDevice device)
    {
        _infoDevice = device;
        InfoList.Children.Clear();
        EmptyState.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Visible;

        HdcDeviceInfo info = HdcDeviceInfo.Empty;
        try
        {
            var reader = new HdcDeviceInfoReader(new HdcRunner(deviceSerial: device.Serial, log: _log), _log);
            info = await reader.ReadAsync().ConfigureAwait(true);
        }
        catch (HdcException ex)
        {
            Debug.WriteLine(ex.Message);
        }


        // 刷新率能力缓存：设置页的档位与「自动」档都靠它，读不到就退化为空清单。
        DisplayModes.Update(info);
        _lastInfo = info;

        // 读到真实名称后回填下拉条目，手机上改了名也能立刻看到。
        if (Selected is DeviceEntry selected
            && string.Equals(selected.Endpoint, device.Serial, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(info.Name))
        {
            selected.Name = info.Name;
        }

        // 设备可用就按分钟抓一张画面，给未连接时的毛玻璃背景用。
        EnsureLockCapture(device.Serial);

        // 读到名称后把显示名写进本机设备清单，避免「设备管理」里只显示 IP:端口。
        _hints.Remember(device.Serial, info.Name, verified: true);
        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            _store.Remember(device.Serial, info.Name);
            _managePage?.Refresh();
        }
        AddInfoRow(Strings.Dev_Info_Name, Value(info.Name, device.Serial));
        AddInfoRow(Strings.Dev_Info_Brand, Value(info.Brand));
        AddInfoRow(Strings.Dev_Info_Model, Value(info.Model));
        AddInfoRow(Strings.Dev_Info_Os, Value(info.HarmonyVersion));
        AddInfoRow(Strings.Dev_Info_Kernel, Value(info.KernelVersion));
        AddInfoRow(Strings.Dev_Info_Cpu, Value(info.Cpu));
        AddInfoRow(Strings.Dev_Info_Resolution, Value(info.Resolution));
        AddInfoRow(Strings.Dev_Info_Memory, Value(info.Memory));

        // 序列号（含 UDID）属于设备个人标识，界面上不展示；末尾只给连得上的地址。
        AddInfoRow(Strings.Dev_Info_Address, Value(device.Serial));
    }

    private static string Value(string? text, string? fallback = null) =>
        !string.IsNullOrWhiteSpace(text) ? text! : (!string.IsNullOrWhiteSpace(fallback) ? fallback! : "—");

    /// <summary>信息行：左侧灰标签 + 右侧白值，行距 12。</summary>
    private void AddInfoRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.6,
            Foreground = System.Windows.Media.Brushes.White,
        };
        var content = new TextBlock
        {
            Text = value,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.White,
        };

        Grid.SetColumn(caption, 0);
        Grid.SetColumn(content, 1);
        row.Children.Add(caption);
        row.Children.Add(content);
        InfoList.Children.Add(row);
    }

    // ── 镜像（M1b）────────────────────────────────────────────────

    /// <summary>启动镜像：解码在后台线程，UI 只按定时器取最新一帧上屏。</summary>
    private void ShowStatus(string text)
    {
        if (string.Equals(_statusShown, text, StringComparison.Ordinal))
        {
            MirrorStatusHost.Visibility = Visibility.Visible;
            ArmStatusTimer();
            return;
        }

        _statusShown = text;
        MirrorStatusText.Text = text;
        MirrorStatusHost.Visibility = Visibility.Visible;
        ArmStatusTimer();
    }

    /// <summary>状态提示只是过路消息，3 秒后自动收起，避免长期压在手机屏上。</summary>
    private void ArmStatusTimer()
    {
        _statusTimer?.Stop();
        _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick -= OnStatusTick;
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        MirrorStatusHost.Visibility = Visibility.Collapsed;
        _statusShown = null;
    }

    /// <summary>把画面层裁成与 <see cref="PhoneFrame"/> 一致的圆角。</summary>
    private void OnMenuAboutClick(object sender, RoutedEventArgs e) => ShowPage(typeof(AboutPage), Strings.Nav_About);

    /// <summary>当前展开的一级菜单。WPF 会在「按下」之前先把已展开的菜单关掉，
    /// 所以还要配合 _topClosedTicks 识别「这次按下就是把刚关掉的那个再点一下」。</summary>
    private MenuItem? _openTopMenu;

    /// <summary>上一次一级菜单被关掉的时刻，用于识别「同一个点击的关闭 + 按下」。</summary>
    private long _topClosedTicks;

    /// <summary>登记三个一级菜单，用打开/关闭事件维护真实状态（不靠 IsSubmenuOpen 猜）。</summary>
    private void RegisterTopMenus()
    {
        foreach (MenuItem item in new[] { MenuApp, MenuSettings, MenuHelp })
        {
            item.SubmenuOpened += (s, _) => _openTopMenu = (MenuItem)s;
            item.SubmenuClosed += (s, _) =>
            {
                _topClosedTicks = Environment.TickCount64;
                if (ReferenceEquals(_openTopMenu, s))
                {
                    _openTopMenu = null;
                }
            };
        }
    }

    /// <summary>一级菜单点击：没开就开，已开就收（WPF 默认「按下先关再开」，看起来永远只会重新弹出）。</summary>
    private void OnTopMenuPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not MenuItem item || !item.HasItems)
        {
            return;
        }

        // 关键：Popup 里的子项点击会「隧道」经过这个一级项，若一并处理，子项就永远点不动。
        // 用几何位置区分：点在自身标题条之外（子菜单区域内）就直接放行。
        Point local = e.GetPosition(item);
        if (local.X < 0d || local.X > item.ActualWidth || local.Y < 0d || local.Y > item.ActualHeight)
        {
            return;
        }

        // 刚被这次按下关掉（<300ms）也算「本来是开着的」→ 这一次就保持收起。
        bool wasOpen = ReferenceEquals(_openTopMenu, item)
            || Environment.TickCount64 - _topClosedTicks < 300L;

        e.Handled = true;

        foreach (MenuItem other in new[] { MenuApp, MenuSettings, MenuHelp })
        {
            if (!ReferenceEquals(other, item) && other.IsSubmenuOpen)
            {
                other.IsSubmenuOpen = false;
            }
        }

        item.IsSubmenuOpen = !wasOpen;
        if (!wasOpen)
        {
            _openTopMenu = item;
        }
    }

    /// <summary>「设置」下拉里的四项：直接跳到对应的设置分栏。</summary>
    private void OnMenuSettingsTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key })
        {
            ShowSettingsTab(key);
        }
    }
    private void OnMenuSettingsClick(object sender, RoutedEventArgs e) => ShowSettingsTab("general");

    private void OnMenuKeysClick(object sender, RoutedEventArgs e) => ShowSettingsTab("keys");

    /// <summary>供托盘调用：走与菜单同一套检查逻辑。</summary>
    internal void CheckForUpdatesFromTray() => _ = CheckForUpdatesAsync(manual: true);

    private async void OnMenuCheckUpdateClick(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true).ConfigureAwait(true);

    /// <summary>
    /// 检查更新。手动点击时无论结果如何都给反馈；启动时的自动检查只在「真的发现新版本」时提示，
    /// 未配置仓库 / 404 / 网络失败一律只写日志，避免每次开机弹一次没用的失败框。
    /// </summary>
    /// <param name="manual">是否由用户主动触发。</param>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (manual)
        {
            ShowStatus(Strings.Upd_Checking);
        }

        // 通过安装包装好的用户走 Velopack：增量包、断点续传、替换文件与重启都由它负责。
        if (VelopackUpdater.IsInstalled)
        {
            await CheckWithVelopackAsync(manual).ConfigureAwait(true);
            return;
        }

        await RunManualUpdateAsync(manual).ConfigureAwait(true);
    }

    /// <summary>
    /// 手动更新通道：直接下载 Setup 安装包再交给系统安装。
    /// Velopack 通道任何一步失败时也会退到这里，保证「总能更新」。
    /// </summary>
    /// <param name="manual">是否由用户主动触发。</param>
    private async Task RunManualUpdateAsync(bool manual)
    {
        UpdateCheckResult result = await UpdateService.CheckAsync().ConfigureAwait(true);
        _log.Write(LogLevel.Info, "App.Update", $"检查更新：{result.Status} 版本={result.Version ?? "-"} 详情={result.Detail ?? "-"}");

        switch (result.Status)
        {
            case UpdateCheckStatus.Available:
                {
                    bool install = ConfirmWindow.Ask(
                        this,
                        Strings.Upd_Title,
                        string.Format(CultureInfo.CurrentCulture, Strings.Upd_AvailableFormat, result.Version),
                        Strings.Upd_InstallNow);
                    if (!install || string.IsNullOrWhiteSpace(result.DownloadUrl))
                    {
                        return;
                    }

                    var progressWindow = new UpdateProgressWindow { Owner = this };
                    progressWindow.Show();
                    _log.Write(LogLevel.Info, "App.Update", "开始下载安装包：" + result.DownloadUrl);

                    string path;
                    try
                    {
                        path = await UpdateService
                            .DownloadAsync(result.DownloadUrl!, progressWindow.Progress, progressWindow.Token)
                            .ConfigureAwait(true);
                    }
                    catch (OperationCanceledException ex)
                    {
                        progressWindow.Close();
                        _log.Write(LogLevel.Info, "App.Update", "下载被取消（用户操作或连接中断）；已下载的分片保留，下次继续。" + ex.GetType().Name);
                        return;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
                    {
                        progressWindow.Close();
                        _log.Write(LogLevel.Warn, "App.Update", "下载失败：" + ex.ToString());
                        ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Upd_DownloadFailedFormat, ex.Message));
                        return;
                    }

                    _log.Write(LogLevel.Info, "App.Update", "安装包已下载：" + path);
                    progressWindow.SetPhase(Strings.Upd_Launching);

                    try
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        progressWindow.Close();
                        ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Upd_DownloadFailedFormat, ex.Message));
                        return;
                    }

                    await Task.Delay(500).ConfigureAwait(true);
                    RequestExit();

                    break;
                }

            case UpdateCheckStatus.UpToDate:
                if (manual)
                {
                    ShowStatus(Strings.Upd_UpToDate);
                }

                break;

            case UpdateCheckStatus.NotConfigured:
                ShowStatus(Strings.Upd_NotConfigured);
                break;

            case UpdateCheckStatus.NotFound:
                ShowStatus(Strings.Upd_NotFound);
                break;

            default:
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Upd_FailedFormat, result.Detail ?? "-"));
                break;
        }
    }
    /// <summary>
    /// Velopack 通道：检查 → 确认 → 下载（带百分比进度、可取消、断点续传）→ 应用并自动重启。
    /// 这条路径下用户看到的是一个进度窗口，装完自己回来，不需要再点安装包。
    /// </summary>
    /// <param name="manual">是否由用户主动触发。</param>
    private async Task CheckWithVelopackAsync(bool manual)
    {
        try
        {
            UpdateManager manager = VelopackUpdater.Create();
            UpdateInfo? info = await manager.CheckForUpdatesAsync().ConfigureAwait(true);

            string version = info?.TargetFullRelease.Version.ToString() ?? "-";
            _log.Write(
                LogLevel.Info,
                "App.Update",
                string.Format(CultureInfo.InvariantCulture, "检查更新（Velopack）：{0} 版本={1}", info is null ? "UpToDate" : "Available", version));

            if (info is null)
            {
                if (manual)
                {
                    ShowStatus(Strings.Upd_UpToDate);
                }

                return;
            }

            bool install = ConfirmWindow.Ask(
                this,
                Strings.Upd_Title,
                string.Format(CultureInfo.CurrentCulture, Strings.Upd_AvailableFormat, version),
                Strings.Upd_InstallNow);
            _log.Write(LogLevel.Info, "App.Update", "用户确认安装=" + install);
            if (!install)
            {
                return;
            }

            var progressWindow = new UpdateProgressWindow { Owner = this };
            progressWindow.Show();

            try
            {
                await manager
                    .DownloadUpdatesAsync(info, progressWindow.ReportPercent, progressWindow.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                progressWindow.Close();
                _log.Write(LogLevel.Info, "App.Update", "用户取消下载；已下载的分片保留，下次继续。");
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
            {
                // 增量通道失手时退回「下载 Setup」，用户仍然能拿到新版本。
                progressWindow.Close();
                _log.Write(LogLevel.Warn, "App.Update", "增量下载失败，退回安装包通道：" + ex);
                await RunManualUpdateAsync(manual).ConfigureAwait(true);
                return;
            }

            progressWindow.SetPhase(Strings.Upd_Restarting);
            _log.Write(LogLevel.Info, "App.Update", "下载完成，开始应用更新并重启。");
            await Task.Delay(400).ConfigureAwait(true);

            // 这一句会替换文件并重新拉起本程序，后面不要再写任何界面代码。
            manager.ApplyUpdatesAndRestart(info);
        }
        catch (Exception ex)
        {
            // 更新通道任何一项失败都不能把程序带走：记录全文，用户可见一行提示。
            _log.Write(LogLevel.Warn, "App.Update", "Velopack 更新失败：" + ex.ToString());
            if (manual)
            {
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Upd_FailedFormat, ex.Message));
            }
        }
    }
    private void OnMenuExitClick(object sender, RoutedEventArgs e) => RequestExit();

    private void OnMenuDocsClick(object sender, RoutedEventArgs e) => OpenExternal(Strings.Link_Docs);

    private void OnMenuReportClick(object sender, RoutedEventArgs e) => OpenExternal(Strings.Link_Issues);

    private void OnMenuSupportClick(object sender, RoutedEventArgs e) =>
        ShowPage(typeof(SupportPage), Strings.Supporter_Title);

    private void OnMenuLogFolderClick(object sender, RoutedEventArgs e)
    {
        string logs = HdcMirror.Core.AppPaths.Logs;
        Directory.CreateDirectory(logs);
        OpenExternal(logs);
    }

    private static void OpenExternal(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <inheritdoc />
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {

        // 关窗即记位置：最小化到托盘也算"离开"，下次回来还是这儿。
        SaveWindowPlacement();
        // TODO(M4): 关闭行为改读 AppSettings.MinimizeToTray；当前固定为最小化到托盘。
        const bool minimizeToTray = true;

        if (minimizeToTray && Application.Current is App { IsExitRequested: false })
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            // 真正退出：镜像会话由手机窗口自己释放，设备清单在记住时已即时落盘。
            _lockShot?.Dispose();
            _lockShot = null;
        }

        base.OnClosing(e);
    }
}

/// <summary>设备下拉条目：显示名 + IP/端口，供 <see cref="MainWindow"/> 绑定。</summary>
public sealed class DeviceEntry : INotifyPropertyChanged
{
    private string _name;

    private bool _isCurrent;

    public DeviceEntry(HdcDevice device)
    {
        Device = device;
        Endpoint = device.Serial;
        _name = device.Kind == ConnectionKind.Wireless
            ? device.Serial
            : string.Format(CultureInfo.CurrentCulture, "{0} (USB)", device.Serial);
    }

    public DeviceEntry(HdcDiscoveredDevice discovered)
    {
        Discovered = discovered;
        Endpoint = discovered.Endpoint;
        _name = string.IsNullOrWhiteSpace(discovered.DisplayName) ? discovered.Address : discovered.DisplayName;
    }

    public DeviceEntry(string endpoint)
    {
        Endpoint = endpoint;
        _name = endpoint;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>已连接设备（可直接镜像）；仅发现未连接时为 null。</summary>
    public HdcDevice? Device { get; }

    /// <summary>扫描结果条目（可能尚未连接）。</summary>
    public HdcDiscoveredDevice? Discovered { get; }

    /// <summary>形如 192.0.2.10:5555 的端点。</summary>
    public string? Endpoint { get; }

    /// <summary>设备显示名；读不到真实名称时退化为端点。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal))
            {
                return;
            }

            _name = value;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(ShowEndpoint));
        }
    }

    /// <summary>一行显示文本，兼容屏幕阅读器与调试输出。</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(_name) ? Endpoint ?? string.Empty : _name;

    /// <summary>名称和端点是否不同：相同时右侧不再重复显示同一串地址。</summary>
    public bool ShowEndpoint => !string.IsNullOrWhiteSpace(Endpoint)
        && !string.Equals(DisplayText, Endpoint, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否是设备栏当前选中的那台：下拉项据此决定显示「连接」还是「忘记」。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            OnPropertyChanged(nameof(IsCurrent));
            OnPropertyChanged(nameof(IsNotCurrent));
        }
    }

    /// <summary><see cref="IsCurrent"/> 的反面，给 XAML 的可见性绑定用。</summary>
    public bool IsNotCurrent => !_isCurrent;

    /// <summary>下拉框折叠时显示设备名，屏幕阅读器读到的也是它而不是类型名。</summary>
    /// <returns>显示文本。</returns>
    public override string ToString() => DisplayText;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
