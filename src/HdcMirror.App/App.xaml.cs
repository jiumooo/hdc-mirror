using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HdcMirror.App.Resources;
using HdcMirror.App.Views;
using H.NotifyIcon;
using Wpf.Ui.Appearance;

// 只引用这一个类型：整体 using Wpf.Ui.Controls 会与 System.Windows.Controls
// 在 MenuItem / ContextMenu 等名字上产生 CS0104 歧义
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace HdcMirror.App;

/// <summary>
/// 应用外壳：单实例闸门、主题跟随系统、主窗口、托盘图标的生命周期都在这里。
/// M0 不含任何设备/协议逻辑。
/// </summary>
public partial class App : Application
{
    // 会话级命名对象（Local\ 前缀），同一用户会话内第二次启动即可被识别
    private const string SingleInstanceMutexName = @"Local\HdcMirror.SingleInstance";
    private const string ActivateSignalName = @"Local\HdcMirror.Activate";
    private const string AppIconUri = "pack://application:,,,/Assets/app.ico";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;
    private RegisteredWaitHandle? _activateRegistration;
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;

    /// <summary>是否已由托盘「完全退出」发起关闭；为 <see langword="true"/> 时主窗口不再拦截关闭事件。</summary>
    public bool IsExitRequested { get; private set; }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 必须在任何界面代码之前装好：M0 第一次启动时因导航时机问题在构造函数里
        // 抛了 NullReferenceException，进程直接消失、托盘无图标、连日志目录都没建出来，
        // 用户看到的就是「双击了没反应」。这道守卫保证任何未处理异常都留下证据。
        InstallCrashGuard();

        // 关闭窗口只隐藏，进程存活 → 退出必须显式调用 Shutdown()
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!TryAcquireSingleInstance())
        {
            // 已有实例：把它唤到前台，本次进程直接退出（不弹任何提示框）
            SignalRunningInstance();
            Shutdown();
            return;
        }

        try
        {
            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;

            // 主题跟随系统深浅色（Mica 背景；需求 §3.1）
            SystemThemeWatcher.Watch(_mainWindow, WindowBackdropType.Mica, true);
            _mainWindow.Show();

            CreateTrayIcon();
        }
        catch (Exception ex)
        {
            ReportCrash(ex, "startup");
            Shutdown(1);
        }
    }

    /// <summary>日志目录：<c>%LOCALAPPDATA%\HdcMirror\logs</c>。</summary>
    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HdcMirror",
        "logs");

    private void InstallCrashGuard()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            ReportCrash(args.Exception, "dispatcher");
            args.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception, "appdomain");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception, "task");
            args.SetObserved();
        };
    }

    /// <summary>写崩溃日志，并尽量弹一次说明框（弹框失败也不影响已落盘的日志）。</summary>
    private static void ReportCrash(Exception? ex, string source)
    {
        string? logPath = WriteCrashLog(ex, source);

        try
        {
            string summary = ex?.Message ?? string.Empty;
            string detail = string.IsNullOrEmpty(summary)
                ? string.Format(Strings.Err_LogFolderOpen, LogDirectory)
                : string.Format(Strings.Err_UnhandledBody, summary, Environment.NewLine + Environment.NewLine);
            MessageBox.Show(detail, Strings.Err_UnhandledTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 界面线程已不可用时不强求弹框：日志已经落盘，日志路径见下方返回值
        }

        _ = logPath;
    }

    /// <summary>把异常与堆栈写入 <c>logs\crash-yyyyMMdd-HHmmss.log</c>，返回文件路径。</summary>
    private static string? WriteCrashLog(Exception? ex, string source)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            string fileName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            string path = Path.Combine(LogDirectory, fileName);

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] source={source}");
            sb.AppendLine($"assembly={typeof(App).Assembly.GetName().Version}");
            sb.AppendLine($"os={Environment.OSVersion}");
            sb.AppendLine($"runtime={RuntimeInformation.FrameworkDescription}");
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _activateRegistration?.Unregister(null);
        _activateSignal?.Dispose();
        _instanceMutex?.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnExit(e);
    }

    /// <summary>建立托盘图标：左键单击切换主窗口，右键菜单四项（需求 §3.5 的 M0 子集）。</summary>
    private void CreateTrayIcon()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateTrayMenuItem(Strings.Tray_ShowWindow, OnShowWindowClick));
        menu.Items.Add(CreateTrayMenuItem(Strings.Tray_Devices, OnOpenDevicesClick));
        menu.Items.Add(CreateTrayMenuItem(Strings.Tray_BasicSettings, OnOpenSettingsClick));
        menu.Items.Add(CreateTrayMenuItem(Strings.Tray_CheckUpdate, OnCheckUpdateClick));
        menu.Items.Add(CreateTrayMenuItem(Strings.Tray_About, OnOpenAboutClick));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateTrayMenuItem(Strings.Tray_Exit, OnExitClick));

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = Strings.Tray_ToolTip,
            IconSource = BitmapFrame.Create(
                new Uri(AppIconUri, UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad),
            ContextMenu = menu,
        };

        // 左键单击 = 显示/隐藏主窗口；右键菜单由 ContextMenu 自动弹出（MenuActivation 默认 RightClick）
        _trayIcon.TrayLeftMouseUp += OnTrayLeftMouseUp;

        // enableEfficiencyMode = false：窗口需要随时响应托盘单击，不要让系统降频
        _trayIcon.ForceCreate(false);
    }

    private static MenuItem CreateTrayMenuItem(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private void OnTrayLeftMouseUp(object sender, RoutedEventArgs e) => ToggleMainWindow();

    private void OnShowWindowClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => ShowPage(typeof(SettingsPage), Strings.Nav_Settings);

    private void OnOpenAboutClick(object sender, RoutedEventArgs e) => ShowPage(typeof(AboutPage), Strings.Nav_About);

    /// <summary>托盘里的「设备管理」：直接把主窗口叫出来并切到设备管理页。</summary>
    private void OnOpenDevicesClick(object sender, RoutedEventArgs e)
    {
        ShowMainWindow();
        _mainWindow?.ShowManagePage();
    }

    /// <summary>托盘里的「检查更新」：叫出主窗口再走同一套检查逻辑，结果写在状态栏。</summary>
    private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_mainWindow is null)
        {
            return;
        }

        ShowMainWindow();
        _mainWindow.CheckForUpdatesFromTray();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitApplication();

    private void ToggleMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (_mainWindow.IsVisible)
        {
            _mainWindow.Hide();
            return;
        }

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        WindowActivation.BringToForeground(_mainWindow);
    }

    private void ShowPage(Type pageType, string title)
    {
        ShowMainWindow();
        _mainWindow?.ShowPage(pageType, title);
    }

    /// <summary>供菜单「退出」与 Ctrl+Q 调用：与托盘「完全退出」走同一条路径。</summary>
    internal void RequestExit() => ExitApplication();

    /// <summary>托盘「完全退出」：真正结束进程，而不是隐藏窗口。</summary>
    private void ExitApplication()
    {
        IsExitRequested = true;
        _mainWindow?.Close();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Shutdown();
    }

    private bool TryAcquireSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }

        _activateSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateSignalName);
        _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateSignal,
            static (state, timedOut) =>
            {
                if (timedOut || state is not App app)
                {
                    return;
                }

                app.Dispatcher.Invoke(app.ShowMainWindow);
            },
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);

        return true;
    }

    private static void SignalRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(ActivateSignalName, out EventWaitHandle? signal))
        {
            return;
        }

        using (signal)
        {
            signal.Set();
        }
    }
}
