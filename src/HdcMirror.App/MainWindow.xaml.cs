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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;
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
    /// <summary>
    /// 手机屏幕宽高比。设备可能竖屏（1260×2720）也可能横屏（2720×1260），
    /// 所以初值只是占位，第一帧解码出来后按真实尺寸校正。
    /// </summary>
    private double _phoneAspect = 1260d / 2720d;

    /// <summary>手机屏幕四周统一留白（px）。</summary>
    private const double PhoneMargin = 5d;

    private bool _fittingPhone;

    private readonly AppPageProvider _pages = AppPageProvider.Instance;

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

    /// <summary>正在读设备信息；启动流程与选中事件会同时触发，用它挡住重复绘制。</summary>
    private bool _infoLoading;

    /// <summary>设备管理页，首次打开时创建，之后复用。</summary>
    private DeviceManagePage? _managePage;

    /// <summary>镜像状态浮层当前显示的内容，避免每帧重复写同一个字符串。</summary>
    private string? _statusShown;

    /// <summary>设备下拉的数据源。</summary>
    public ObservableCollection<DeviceEntry> Devices { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

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
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += OnWindowStateChanged;
        DevicePicker.DropDownClosed += OnDevicePickerDropDownClosed;
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        UpdatePhoneFrameSize();

        // 启动时若手机已经在无线调试里连着，就先读设备信息铺满屏幕，再开始投屏；
        // 没有设备就停在「尚未连接设备」，不空起一条会话。
        _ = AutoStartAsync();

        // 启动时静默查一次更新：只有真发现新版本才提示，未配置 / 404 / 断网都只写日志。
        _ = CheckForUpdatesAsync(manual: false);
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
                // 还没连过设备：直接走一次扫描，用户打开软件就能看到自己的手机。
                await RefreshDevicesAsync().ConfigureAwait(true);
                return;
            }

            HdcDevice device = devices[0];
            foreach (HdcDevice candidate in devices)
            {
                AddDeviceDistinct(new DeviceEntry(candidate));
                _store.Remember(candidate.Serial, null);
            }

            DevicePicker.SelectedItem = Devices.Count > 0 ? Devices[0] : null;
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
    /// 在独立窗口里打开一个页面（设置 / 关于）。
    /// 主界面本身就是手机屏幕，不再占用内容宿主，所以页面走弹出窗口。
    /// </summary>
    internal void ShowPage(Type pageType, string title)
    {
        var frame = new System.Windows.Controls.Frame
        {
            NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden,
        };

        var window = new Window
        {
            Owner = this,
            Title = title,
            Width = 560,
            Height = 640,
            MinWidth = 480,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = frame,
            FontFamily = FontFamily,
            FontSize = FontSize,
        };

        frame.Navigate(_pages.GetPage(pageType));
        window.ShowDialog();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Q && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            RequestExit();
        }
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
        bool deep)
    {
        var options = new HdcDiscoveryOptions
        {
            KnownEndpoints = hints,
            EnableBroadcast = !deep,
            BroadcastTimeout = deep ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(7),
            DeepScan = deep && hints.Count > 0,
            DeepScanConcurrency = 192,
            ConnectTimeoutMilliseconds = deep ? 150 : 300,
        };

        return new HdcDeviceDiscovery(log: log, options: options).ScanAsync();
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

    private async void OnScanClick(object sender, RoutedEventArgs e) => await RefreshDevicesAsync().ConfigureAwait(true);

    private async Task RefreshDevicesAsync()
    {
        ScanButton.IsEnabled = false;
        ShowStatus(Strings.Status_Scanning);
        try
        {
            var log = new FileLogSink();
            var runner = new HdcRunner(log: log);
            var enumerator = new HdcDeviceEnumerator(runner, log);

            // ① 已连接设备：这些可以直接镜像，优先级最高。
            IReadOnlyList<HdcDevice> connected = await enumerator.EnumerateAsync().ConfigureAwait(true);

            // ② 局域网发现：把尚未连接的设备也列出来，点「连接」时再做 tconn。
            //    线索库里的端点优先探测——「忘记设备」之后端口还得靠它找回来。
            IReadOnlyList<string> hints = _hints.Endpoints();
            IReadOnlyList<HdcDiscoveredDevice> discovered = Array.Empty<HdcDiscoveredDevice>();
            try
            {
                discovered = await ScanAsync(log, hints, deep: false).ConfigureAwait(true);

                // 一次没扫到，但以前连过：无线调试端口可能被系统换了号，对这几台机器做端口细扫。
                if (discovered.Count == 0 && hints.Count > 0)
                {
                    string first = hints[0];
                    ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Status_ScanDeepFormat, first));
                    _log.Write(LogLevel.Info, "discovery", "常规扫描没结果，对线索主机做端口细扫：" + string.Join(", ", hints));
                    discovered = await ScanAsync(log, hints, deep: true).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is HdcException or IOException or InvalidOperationException)
            {
                // 发现失败不影响已连接设备的使用。
                _log.Write(LogLevel.Warn, "discovery", "局域网扫描失败：" + ex.Message);
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
                _hints.Remember(found.Endpoint, found.DisplayName);
            }

            ConnectButton.IsEnabled = Devices.Count > 0;
            if (Devices.Count > 0)
            {
                DevicePicker.SelectedIndex = 0;

                // 可编辑下拉的收起态文字来自 Text；ItemsSource 刚换过时立刻赋值会被清掉，
                // 所以排到消息队列尾部再补一次。
                _ = Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (DevicePicker.SelectedItem is DeviceEntry first)
                        {
                            DevicePicker.Text = string.IsNullOrWhiteSpace(first.Name) ? first.Endpoint : first.Name;
                        }
                    }),
                    System.Windows.Threading.DispatcherPriority.Background);
            }

            ShowStatus(Devices.Count > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Status_DeviceFoundFormat, Devices.Count)
                : Strings.Status_NoDeviceFound);
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
        }

        Devices.Add(candidate);
    }

    private static string EndpointOf(DeviceEntry entry)
        => entry.Endpoint ?? entry.Device?.Serial ?? string.Empty;

    /// <summary>切回「设备简介」视图（手机屏里显示九项信息）。</summary>
    private void ShowInfoView()
    {
        if (InfoList.Children.Count == 0)
        {
            return;
        }

        ManageHost.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Visible;
    }

    /// <summary>下拉收起时回到简介视图：再选同一台设备不会触发 SelectionChanged，靠这里兜住。</summary>
    private void OnDevicePickerDropDownClosed(object? sender, EventArgs e) => ShowInfoView();

    private async void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not DeviceEntry entry)
        {
            ConnectButton.IsEnabled = false;
            return;
        }

        ConnectButton.IsEnabled = true;

        // 选中即回到「设备简介」视图：否则停在设备管理页时，点下拉看不到任何变化，
        // 用户会以为下拉框没用（见用户反馈「点开之后用不上」）。
        ShowInfoView();

        // 已连接的设备可以直接读简介；仅发现的设备要等点了「连接」才拿到序列号。
        if (entry.Device is { } device)
        {
            await ShowDeviceInfoAsync(device).ConfigureAwait(true);
        }
        else
        {
            InfoList.Children.Clear();
            AddInfoRow(Strings.Dev_Info_Name, entry.Name);
            AddInfoRow(Strings.Dev_Info_Address, EndpointOf(entry));
            ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Status_DeviceSelectedFormat, entry.Name));
        }
    }

    /// <summary>下拉项右侧的「忘记」按钮：断开这台设备的连接，并从本机列表移除。</summary>
    private async void OnForgetDeviceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceEntry entry)
        {
            return;
        }

        DevicePicker.IsDropDownOpen = false;
        await ForgetDeviceAsync(entry).ConfigureAwait(true);
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
            DevicePicker.SelectedIndex = Math.Clamp(removedIndex, 0, Devices.Count - 1);
        }
        else
        {
            DevicePicker.SelectedItem = null;
            ConnectButton.IsEnabled = false;
            InfoList.Children.Clear();
            InfoPanel.Visibility = Visibility.Collapsed;
            ManageHost.Visibility = Visibility.Collapsed;
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
        if (DevicePicker.SelectedItem is not DeviceEntry entry)
        {
            return;
        }

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
            _hints.Remember(endpoint, null);
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

        var window = new MirrorWindow(device, title, _log) { Owner = this };
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
    internal void ShowManagePage()
    {
        _managePage ??= CreateManagePage();
        ManageHost.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        MirrorStatusHost.Visibility = Visibility.Collapsed;
        _managePage.Refresh();
    }

    private DeviceManagePage CreateManagePage()
    {
        var page = new DeviceManagePage(_store);
        page.ConnectRequested += async (_, endpoint) => await ConnectEndpointAsync(endpoint).ConfigureAwait(true);
        page.Deleted += (_, endpoint) => RemoveDeviceByEndpoint(endpoint);
        ManageHost.Children.Add(page);
        return page;
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
            DevicePicker.SelectedIndex = 0;
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

    private void OnMenuDevicesClick(object sender, RoutedEventArgs e) => ShowManagePage();

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

        _lastInfo = info;

        // 读到真实名称后回填下拉条目，手机上改了名也能立刻看到。
        if (DevicePicker.SelectedItem is DeviceEntry selected
            && string.Equals(selected.Endpoint, device.Serial, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(info.Name))
        {
            selected.Name = info.Name;
            DevicePicker.Text = info.Name;
        }

        // 读到名称后把显示名写进本机设备清单，避免「设备管理」里只显示 IP:端口。
        _hints.Remember(device.Serial, info.Name);
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
            return;
        }

        _statusShown = text;
        MirrorStatusText.Text = text;
        MirrorStatusHost.Visibility = Visibility.Visible;
    }

    /// <summary>把画面层裁成与 <see cref="PhoneFrame"/> 一致的圆角。</summary>
    private void OnMenuAboutClick(object sender, RoutedEventArgs e) => ShowPage(typeof(AboutPage), Strings.Nav_About);

    private void OnMenuSettingsClick(object sender, RoutedEventArgs e) => ShowPage(typeof(SettingsPage), Strings.Nav_Settings);

    private void OnMenuKeysClick(object sender, RoutedEventArgs e) => ShowPage(typeof(KeysPage), Strings.Keys_Title);

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

                    ShowStatus(Strings.Upd_Downloading);
                    try
                    {
                        string path = await UpdateService.DownloadAsync(result.DownloadUrl!).ConfigureAwait(true);
                        _log.Write(LogLevel.Info, "App.Update", "安装包已下载：" + path);
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        RequestExit();
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
                    {
                        ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.Upd_DownloadFailedFormat, ex.Message));
                    }

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
    private void OnMenuExitClick(object sender, RoutedEventArgs e) => RequestExit();

    private void OnMenuDocsClick(object sender, RoutedEventArgs e) => OpenExternal(Strings.Link_Docs);

    private void OnMenuReportClick(object sender, RoutedEventArgs e) => OpenExternal(Strings.Link_Issues);

    private void OnMenuSupportClick(object sender, RoutedEventArgs e)
    {
        var window = new SupporterWindow { Owner = this };
        window.ShowDialog();
    }

    private void OnMenuLogFolderClick(object sender, RoutedEventArgs e)
    {
        string logs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HdcMirror",
            "logs");

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
        }

        base.OnClosing(e);
    }
}

/// <summary>设备下拉条目：显示名 + IP/端口，供 <see cref="MainWindow"/> 绑定。</summary>
public sealed class DeviceEntry : INotifyPropertyChanged
{
    private string _name;

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
        }
    }

    /// <summary>一行显示文本，兼容屏幕阅读器与调试输出。</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(_name) ? Endpoint ?? string.Empty : _name;

    /// <summary>下拉框折叠时显示设备名，屏幕阅读器读到的也是它而不是类型名。</summary>
    /// <returns>显示文本。</returns>
    public override string ToString() => DisplayText;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
