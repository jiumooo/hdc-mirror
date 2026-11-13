using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using HdcMirror.App.Resources;
using HdcMirror.App.Services;

// System.Windows 里也有一个 Localization 类型，别名把它固定到我们自己的实现
using Localization = HdcMirror.App.Services.Localization;
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
    private bool _afterLanguageChange;
    private bool _languageRestartRequested;

    /// <summary>是否已由托盘「完全退出」发起关闭；为 <see langword="true"/> 时主窗口不再拦截关闭事件。</summary>
    public bool IsExitRequested { get; private set; }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        // 换语言重启拉起的新实例会带这个参数：抢单实例锁时多等一会儿
        _afterLanguageChange = HasLanguageChangeArgument(e.Args);

        // Velopack 安装钩子：安装/更新/卸载时的首启清理必须最先跑，失败也不能拖垮启动。
        try
        {
            Velopack.VelopackApp.Build().Run();
        }
        catch (Exception)
        {
            // 未安装（直接跑 Debug 输出）时会抛 NotInstalled 类异常，属正常，忽略。
        }

        // 数据目录与安装目录分离后（0.0.2 起数据在 %APPDATA%\HdcMirror），
        // 旧版本写在安装目录里的设备清单/设置/日志要搬一次；必须在读配置之前跑。
        HdcMirror.Core.AppPaths.MigrateFromLegacy();

        // 界面语言必须在任何窗口/页面 XAML 解析之前定下来：{x:Static res:Strings.Xxx}
        // 是静态求值，窗口一解析就定死，之后再改文化不会生效。
        // 只改 UICulture，日期与数字格式仍跟随系统区域。
        // 位置放在 MigrateFromLegacy 之后：旧版本写在安装目录里的 settings.json 要先搬过来。
        Localization.Apply(AppSettings.Current.UiLanguage);

        base.OnStartup(e);

        // 必须在任何界面代码之前装好：M0 第一次启动时因导航时机问题在构造函数里
        // 抛了 NullReferenceException，进程直接消失、托盘无图标、连日志目录都没建出来，
        // 用户看到的就是「双击了没反应」。这道守卫保证任何未处理异常都留下证据。
        InstallCrashGuard();

        // 关闭窗口只隐藏，进程存活 → 退出必须显式调用 Shutdown()
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!TryAcquireSingleInstance(_afterLanguageChange))
        {
            // 已有实例：把它唤到前台，本次进程直接退出（不弹任何提示框）
            SignalRunningInstance();
            Shutdown();
            return;
        }

        try
        {
            // 首启问一次语言：必须早于主窗口，主窗口 XAML 一解析文案就定死了
            EnsureLanguageChosen();

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

    /// <summary>
    /// 日志目录：与主日志同一个位置（<c>%APPDATA%\HdcMirror\logs</c>）。
    /// 早期版本写死在 <c>%LOCALAPPDATA%</c>，导致崩溃日志和运行日志分散在两个目录里。
    /// </summary>
    private static string LogDirectory => HdcMirror.Core.AppPaths.Logs;

    private void InstallCrashGuard()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;

            // WPF 渲染目标偶发申请失败（Win32 1816 / 1450 / 8）时，窗口本身没坏：
            // 抛出来的是 HwndTarget.UpdateWindowSettings，接着下一次布局就好。
            // 这类异常只记一行日志，不弹框、不退出，否则用户会看到「程序遇到未处理的错误」而其实没崩。
            if (IsTransientRenderFailure(args.Exception, out string note))
            {
                WriteTransientNote(note);
                return;
            }

            ReportCrash(args.Exception, "dispatcher");
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

    /// <summary>
    /// 判断是不是「可自愈」的 WPF 渲染异常。
    /// <para>
    /// 典型症状：<c>System.ComponentModel.Win32Exception (1816): 配额不足，无法处理此命令。</c>
    /// 堆栈落在 <c>HwndTarget.UpdateWindowSettings</c>。它由窗口尺寸/位置在极短时间里被反复改写引起，
    /// 布局稳定后自然恢复，不该让整个进程退出。
    /// </para>
    /// </summary>
    /// <param name="ex">要检查的异常。</param>
    /// <param name="note">命中的错误码说明，用于日志。</param>
    /// <returns>命中返回 true。</returns>
    private static bool IsTransientRenderFailure(Exception? ex, out string note)
    {
        note = string.Empty;
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is System.ComponentModel.Win32Exception w
                && (w.NativeErrorCode == 1816 || w.NativeErrorCode == 1450 || w.NativeErrorCode == 8))
            {
                note = $"Win32 {w.NativeErrorCode}：{w.Message}";
                return true;
            }
        }

        return false;
    }

    /// <summary>把可自愈异常追加到 <c>logs\transient-YYYYMMDD.log</c>，写失败就算了。</summary>
    /// <param name="note">一行说明。</param>
    private static void WriteTransientNote(string note)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory, $"transient-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {note}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 日志写不进去不该再引发一次崩溃。
        }
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
        ReleaseInstanceResources();
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

    /// <summary>
    /// 抢单实例闸门。<paramref name="allowRetry"/> 为真时（换语言重启拉起的新实例），
    /// 每 200 ms 重试一次、最多 25 次（约 5 秒），等上一个实例把 Mutex / 窗口 / 托盘图标
    /// 放干净 —— 否则用户会看到「已有实例在运行」而新窗口其实没起来。
    /// </summary>
    /// <param name="allowRetry">是否容忍上一个实例还没退干净。</param>
    /// <returns>抢到闸门返回 <see langword="true"/>。</returns>
    private bool TryAcquireSingleInstance(bool allowRetry)
    {
        const int MaxAttempts = 25;
        const int RetryDelayMs = 200;

        int attempts = allowRetry ? MaxAttempts : 1;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (createdNew)
            {
                break;
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;

            if (attempt == attempts - 1)
            {
                return false;
            }

            // 启动阶段还没有任何窗口，UI 线程上同步等一下比异步编排简单可靠
            Thread.Sleep(RetryDelayMs);
        }

        if (_instanceMutex is null)
        {
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

    /// <summary>命令行里是否带 <c>--after-language-change</c>（换语言重启时由旧实例传给新实例）。</summary>
    private static bool HasLanguageChangeArgument(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, Localization.AfterLanguageChangeArgument, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 首次启动问一次界面语言：<see cref="AppSettings.UiLanguage"/> 为空且从未选择过才弹。
    /// 用 <see cref="AppSettings.LanguageChosen"/> 显式记录「问过了」，
    /// 不依赖「配置文件是否存在」这种隐式条件。
    /// </summary>
    private static void EnsureLanguageChosen()
    {
        AppSettings settings = AppSettings.Current;
        if (settings.LanguageChosen || !string.IsNullOrEmpty(settings.UiLanguage))
        {
            return;
        }

        string fallback = Localization.SystemPrefersEnglish()
            ? Localization.English
            : Localization.ChineseSimplified;

        var dialog = new LanguageDialog(fallback);
        dialog.ShowDialog();

        // 直接关窗（Esc / 关闭按钮）时按系统语言记下来，不再反复弹
        string chosen = dialog.SelectedLanguage ?? fallback;
        settings.UiLanguage = chosen;
        settings.LanguageChosen = true;
        settings.Save();
        Localization.Apply(chosen);
    }

    /// <summary>
    /// 设置页改语言后调用：界面文案由 <c>{x:Static}</c> 静态求值，热切会留下半中半英，
    /// 所以只能拉起一个新实例再退出自己。新实例带 <see cref="HdcMirror.App.Services.Localization.AfterLanguageChangeArgument"/>，
    /// 抢单实例锁时会重试，等这个实例把句柄放干净。
    /// </summary>
    internal static void RequestLanguageRestart()
    {
        if (Application.Current is App app)
        {
            app.RestartForLanguageChange();
        }
    }

    private void RestartForLanguageChange()
    {
        if (_languageRestartRequested)
        {
            return;
        }

        _languageRestartRequested = true;
        IsExitRequested = true;

        // 从 ComboBox 的 SelectionChanged 里同步 Shutdown 会打断事件处理，
        // 推到消息循环下一轮再真正重启。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RestartForLanguageChangeCore));
    }

    private void RestartForLanguageChangeCore()
    {
        // 先放掉闸门：新实例的 TryAcquireSingleInstance 才拿得到 createdNew
        ReleaseInstanceResources();

        try
        {
            StartFollowUpInstance();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 起不来也不能把用户卡在旧语言上：本次照常退出，下次手动启动就是新语言
            WriteCrashLog(ex, "language-restart");
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        Shutdown();
    }

    /// <summary>拉起新实例。Debug 下用 <c>dotnet HdcMirror.dll</c> 跑时 ProcessPath 是
    /// dotnet.exe，直接把参数丢给它只会启动一个空 CLI，必须显式带上程序集路径。</summary>
    private static void StartFollowUpInstance()
    {
        string? exe = Environment.ProcessPath;
        string argument = Localization.AfterLanguageChangeArgument;
        bool hostedByDotnet = exe is not null
            && string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(exe) || hostedByDotnet)
        {
            string host = string.IsNullOrEmpty(exe) ? "dotnet" : exe;
            string assembly = typeof(App).Assembly.Location;
            string arguments = $"\"{assembly}\" {argument}";
            Process.Start(new ProcessStartInfo(host, arguments) { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo(exe, argument) { UseShellExecute = true });
    }

    /// <summary>放掉单实例闸门与激活信号；退出与「换语言重启」共用，重复调用安全。</summary>
    private void ReleaseInstanceResources()
    {
        _activateRegistration?.Unregister(null);
        _activateRegistration = null;

        _activateSignal?.Dispose();
        _activateSignal = null;

        if (_instanceMutex is null)
        {
            return;
        }

        try
        {
            _instanceMutex.ReleaseMutex();
        }
        catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
        {
            // 不是本线程持有（或已释放）：无所谓，Dispose 照样关掉句柄
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
    }

}
