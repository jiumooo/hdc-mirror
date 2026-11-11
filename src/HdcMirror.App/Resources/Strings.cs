using System.Globalization;
using System.Resources;

namespace HdcMirror.App.Resources;

/// <summary>
/// <c>Resources.resx</c> 的强类型访问器。
/// 界面文案的唯一来源：XAML 用 <c>{x:Static res:Strings.Xxx}</c>，代码用属性访问，
/// 任何 .xaml / .cs 里都不允许出现中文字面量（需求 §6.4 L1~L3）。
/// </summary>
public static class Strings
{
    private static readonly ResourceManager ResourceManagerInstance =
        new(baseName: "HdcMirror.App.Resources.Resources", assembly: typeof(Strings).Assembly);

    /// <summary>应用正式名称。</summary>
    public static string App_Title => Get(nameof(App_Title));

    /// <summary>导航项 / 设备页标题。</summary>
    public static string Nav_Devices => Get(nameof(Nav_Devices));

    /// <summary>导航项 / 设置页标题。</summary>
    public static string Nav_Settings => Get(nameof(Nav_Settings));

    /// <summary>导航项 / 关于页标题。</summary>
    public static string Nav_About => Get(nameof(Nav_About));

    /// <summary>设备页占位说明。</summary>
    public static string Nav_Devices_Description => Get(nameof(Nav_Devices_Description));

    /// <summary>设置页占位说明。</summary>
    public static string Nav_Settings_Description => Get(nameof(Nav_Settings_Description));

    /// <summary>关于页占位说明。</summary>
    public static string Nav_About_Description => Get(nameof(Nav_About_Description));

    /// <summary>关于页的产品全称。</summary>
    public static string Nav_About_FullName => Get(nameof(Nav_About_FullName));

    /// <summary>关于页版本号格式（<c>{0}</c> 为版本字符串）。</summary>
    public static string Nav_About_VersionFormat => Get(nameof(Nav_About_VersionFormat));

    /// <summary>托盘图标悬浮提示。</summary>
    public static string Tray_ToolTip => Get(nameof(Tray_ToolTip));

    /// <summary>托盘菜单：显示主窗口。</summary>
    public static string Tray_ShowWindow => Get(nameof(Tray_ShowWindow));

    /// <summary>托盘菜单：基本设置。</summary>
    public static string Tray_BasicSettings => Get(nameof(Tray_BasicSettings));

    /// <summary>托盘菜单：关于。</summary>
    public static string Tray_About => Get(nameof(Tray_About));

    /// <summary>托盘菜单：完全退出。</summary>
    public static string Tray_Exit => Get(nameof(Tray_Exit));

    /// <summary>状态栏初始文案。</summary>
    public static string Status_Ready => Get(nameof(Status_Ready));

    /// <summary>未处理异常对话框标题。</summary>
    public static string Err_UnhandledTitle => Get(nameof(Err_UnhandledTitle));

    /// <summary>未处理异常对话框正文（<c>{0}</c> 为异常摘要，<c>{1}</c> 为日志目录）。</summary>
    public static string Err_UnhandledBody => Get(nameof(Err_UnhandledBody));

    /// <summary>日志目录提示（<c>{0}</c> 为目录路径）。</summary>
    public static string Err_LogFolderOpen => Get(nameof(Err_LogFolderOpen));

    /// <summary>顶部菜单：应用菜单标题（短名）。</summary>
    public static string Menu_App => Get(nameof(Menu_App));

    /// <summary>应用菜单项：关于（<c>{0}</c> 为短名）。</summary>
    public static string Menu_App_AboutFormat => Get(nameof(Menu_App_AboutFormat));

    /// <summary>应用菜单项：检查更新。</summary>
    public static string Menu_App_CheckUpdate => Get(nameof(Menu_App_CheckUpdate));

    /// <summary>应用菜单项：退出（<c>{0}</c> 为短名）。</summary>
    public static string Menu_App_ExitFormat => Get(nameof(Menu_App_ExitFormat));

    /// <summary>顶部菜单：工具。</summary>
    public static string Menu_Tools => Get(nameof(Menu_Tools));

    /// <summary>工具菜单项：基本设置。</summary>
    public static string Menu_Tools_Settings => Get(nameof(Menu_Tools_Settings));

    /// <summary>工具菜单项：断开连接。</summary>
    public static string Menu_Tools_Disconnect => Get(nameof(Menu_Tools_Disconnect));

    /// <summary>顶部菜单：帮助。</summary>
    public static string Menu_Help => Get(nameof(Menu_Help));

    /// <summary>帮助菜单项：打开日志目录。</summary>
    public static string Menu_Help_LogFolder => Get(nameof(Menu_Help_LogFolder));

    public static string Status_Mirror_Reconnecting => Get(nameof(Status_Mirror_Reconnecting));

    public static string Err_Mirror_Daemon => Get(nameof(Err_Mirror_Daemon));

    public static string Err_Mirror_Forward => Get(nameof(Err_Mirror_Forward));

    public static string Err_Mirror_StreamClosed => Get(nameof(Err_Mirror_StreamClosed));

    private static string Get(string key) =>
        ResourceManagerInstance.GetString(key, CultureInfo.CurrentUICulture) ?? string.Empty;

    /// <summary>界面文案 <c>Menu_Settings</c>。</summary>
    public static string Menu_Settings => Get(nameof(Menu_Settings));

    /// <summary>界面文案 <c>Menu_Settings_General</c>。</summary>
    public static string Menu_Settings_General => Get(nameof(Menu_Settings_General));

    /// <summary>界面文案 <c>Menu_Settings_Keys</c>。</summary>
    public static string Menu_Settings_Keys => Get(nameof(Menu_Settings_Keys));

    /// <summary>界面文案 <c>Menu_Settings_Av</c>。</summary>
    public static string Menu_Settings_Av => Get(nameof(Menu_Settings_Av));

    /// <summary>界面文案 <c>Menu_Help_Support</c>。</summary>
    public static string Menu_Help_Support => Get(nameof(Menu_Help_Support));

    /// <summary>界面文案 <c>Menu_Help_Report</c>。</summary>
    public static string Menu_Help_Report => Get(nameof(Menu_Help_Report));

    /// <summary>界面文案 <c>Menu_Help_Docs</c>。</summary>
    public static string Menu_Help_Docs => Get(nameof(Menu_Help_Docs));

    /// <summary>界面文案 <c>Ui_Scan</c>。</summary>
    public static string Ui_Scan => Get(nameof(Ui_Scan));

    /// <summary>界面文案 <c>Ui_Refresh</c>。</summary>
    public static string Ui_Refresh => Get(nameof(Ui_Refresh));

    /// <summary>界面文案 <c>Ui_Connect</c>。</summary>
    public static string Ui_Connect => Get(nameof(Ui_Connect));

    /// <summary>界面文案 <c>Ui_Disconnect</c>。</summary>
    public static string Ui_Disconnect => Get(nameof(Ui_Disconnect));

    /// <summary>界面文案 <c>Dev_NoDevice</c>。</summary>
    public static string Dev_NoDevice => Get(nameof(Dev_NoDevice));

    /// <summary>界面文案 <c>Dev_NoDeviceHint</c>。</summary>
    public static string Dev_NoDeviceHint => Get(nameof(Dev_NoDeviceHint));

    /// <summary>界面文案 <c>Dev_Info_Name</c>。</summary>
    public static string Dev_Info_Name => Get(nameof(Dev_Info_Name));

    /// <summary>界面文案 <c>Dev_Info_Brand</c>。</summary>
    public static string Dev_Info_Brand => Get(nameof(Dev_Info_Brand));

    /// <summary>界面文案 <c>Dev_Info_Model</c>。</summary>
    public static string Dev_Info_Model => Get(nameof(Dev_Info_Model));

    /// <summary>界面文案 <c>Dev_Info_Os</c>。</summary>
    public static string Dev_Info_Os => Get(nameof(Dev_Info_Os));

    /// <summary>界面文案 <c>Dev_Info_Kernel</c>。</summary>
    public static string Dev_Info_Kernel => Get(nameof(Dev_Info_Kernel));

    /// <summary>界面文案 <c>Dev_Info_Cpu</c>。</summary>
    public static string Dev_Info_Cpu => Get(nameof(Dev_Info_Cpu));

    /// <summary>界面文案 <c>Dev_Info_Resolution</c>。</summary>
    public static string Dev_Info_Resolution => Get(nameof(Dev_Info_Resolution));

    /// <summary>界面文案 <c>Dev_Info_Memory</c>。</summary>
    public static string Dev_Info_Memory => Get(nameof(Dev_Info_Memory));

    /// <summary>界面文案 <c>Dev_Info_Address</c>。</summary>
    public static string Dev_Info_Address => Get(nameof(Dev_Info_Address));

    /// <summary>界面文案 <c>Tip_Minimize</c>。</summary>
    public static string Tip_Minimize => Get(nameof(Tip_Minimize));

    /// <summary>界面文案 <c>Tip_Maximize</c>。</summary>
    public static string Tip_Maximize => Get(nameof(Tip_Maximize));

    /// <summary>界面文案 <c>Tip_Close</c>。</summary>
    public static string Tip_Close => Get(nameof(Tip_Close));

    /// <summary>界面文案 <c>Supporter_Title</c>。</summary>
    public static string Supporter_Title => Get(nameof(Supporter_Title));

    /// <summary>界面文案 <c>Supporter_ListTitle</c>。</summary>
    public static string Supporter_ListTitle => Get(nameof(Supporter_ListTitle));

    /// <summary>界面文案 <c>Supporter_Empty</c>。</summary>
    public static string Supporter_Empty => Get(nameof(Supporter_Empty));

    /// <summary>界面文案 <c>Supporter_Note</c>。</summary>
    public static string Supporter_Note => Get(nameof(Supporter_Note));

    /// <summary>界面文案 <c>Upd_Checking</c>。</summary>
    public static string Upd_Checking => Get(nameof(Upd_Checking));

    /// <summary>界面文案 <c>Link_Docs</c>。</summary>
    public static string Link_Docs => Get(nameof(Link_Docs));

    /// <summary>界面文案 <c>Link_Issues</c>。</summary>
    public static string Link_Issues => Get(nameof(Link_Issues));

    /// <summary>界面文案 <c>Link_Releases</c>。</summary>
    public static string Link_Releases => Get(nameof(Link_Releases));

    /// <summary>界面文案 <c>Supporter_QrPlaceholder</c>。</summary>
    public static string Supporter_QrPlaceholder => Get(nameof(Supporter_QrPlaceholder));

    /// <summary>界面文案 <c>Ui_Close</c>。</summary>
    public static string Ui_Close => Get(nameof(Ui_Close));

    /// <summary>界面文案 <c>Status_Mirror_Starting</c>。</summary>
    public static string Status_Mirror_Starting => Get(nameof(Status_Mirror_Starting));

    /// <summary>界面文案 <c>Status_Mirror_Streaming</c>（{0}=宽，{1}=高，{2}=fps）。</summary>
    public static string Status_Mirror_Streaming => Get(nameof(Status_Mirror_Streaming));

    /// <summary>界面文案 <c>Status_Mirror_Idle</c>。</summary>
    public static string Status_Mirror_Idle => Get(nameof(Status_Mirror_Idle));

    /// <summary>界面文案 <c>Status_Mirror_Stopped</c>。</summary>
    public static string Status_Mirror_Stopped => Get(nameof(Status_Mirror_Stopped));

    /// <summary>界面文案 <c>Err_Mirror_Start</c>（{0}=原因）。</summary>
    public static string Err_Mirror_Start => Get(nameof(Err_Mirror_Start));

    /// <summary>界面文案 <c>Err_Mirror_NoDevice</c>。</summary>
    public static string Err_Mirror_NoDevice => Get(nameof(Err_Mirror_NoDevice));

    /// <summary>界面文案 <c>Err_Mirror_Decode</c>（{0}=原因）。</summary>
    public static string Err_Mirror_Decode => Get(nameof(Err_Mirror_Decode));

    /// <summary>正在扫描局域网设备。</summary>
    public static string Status_Scanning => Get(nameof(Status_Scanning));

    /// <summary>发现 N 台设备。</summary>
    public static string Status_DeviceFoundFormat => Get(nameof(Status_DeviceFoundFormat));

    /// <summary>未发现设备提示。</summary>
    public static string Status_NoDeviceFound => Get(nameof(Status_NoDeviceFound));

    /// <summary>hdc 不可用提示。</summary>
    public static string Status_HdcUnavailable => Get(nameof(Status_HdcUnavailable));

    /// <summary>正在连接设备。</summary>
    public static string Status_Connecting => Get(nameof(Status_Connecting));

    /// <summary>连接失败提示。</summary>
    public static string Status_ConnectFailed => Get(nameof(Status_ConnectFailed));

    /// <summary>界面文案 <c>Menu_Settings_Devices</c>：设置菜单下的设备管理页。</summary>
    public static string Menu_Settings_Devices => Get(nameof(Menu_Settings_Devices));

    /// <summary>界面文案 <c>DevMgr_Title</c>：设备管理页标题。</summary>
    public static string DevMgr_Title => Get(nameof(DevMgr_Title));

    /// <summary>界面文案 <c>DevMgr_Saved</c>：设备管理页：已保存清单分组标题。</summary>
    public static string DevMgr_Saved => Get(nameof(DevMgr_Saved));

    /// <summary>界面文案 <c>DevMgr_Empty</c>：设备管理页：清单为空时的提示。</summary>
    public static string DevMgr_Empty => Get(nameof(DevMgr_Empty));

    /// <summary>界面文案 <c>DevMgr_Manual</c>：设备管理页：手动输入地址分组标题。</summary>
    public static string DevMgr_Manual => Get(nameof(DevMgr_Manual));

    /// <summary>界面文案 <c>DevMgr_Ip</c>：手动连接 IP 输入框占位。</summary>
    public static string DevMgr_Ip => Get(nameof(DevMgr_Ip));

    /// <summary>界面文案 <c>DevMgr_Port</c>：手动连接端口输入框占位。</summary>
    public static string DevMgr_Port => Get(nameof(DevMgr_Port));

    /// <summary>界面文案 <c>DevMgr_Connect</c>：手动连接按钮。</summary>
    public static string DevMgr_Connect => Get(nameof(DevMgr_Connect));

    /// <summary>界面文案 <c>DevMgr_Delete</c>：删除已记住的设备。</summary>
    public static string DevMgr_Delete => Get(nameof(DevMgr_Delete));

    /// <summary>界面文案 <c>DevMgr_Hint</c>：设备管理页：隐私说明。</summary>
    public static string DevMgr_Hint => Get(nameof(DevMgr_Hint));

    /// <summary>界面文案 <c>Ui_ZoomFormat</c>：手机窗口控制条上的缩放比例。</summary>
    public static string Ui_ZoomFormat => Get(nameof(Ui_ZoomFormat));
    /// <summary>界面文案 <c>Ui_Delete</c>：删除设备按钮提示。</summary>
    public static string Ui_Delete => Get(nameof(Ui_Delete));

    /// <summary>界面文案 <c>Ui_Cancel</c>：取消按钮。</summary>
    public static string Ui_Cancel => Get(nameof(Ui_Cancel));

    /// <summary>界面文案 <c>Confirm_DeleteTitle</c>：删除确认框标题。</summary>
    public static string Confirm_DeleteTitle => Get(nameof(Confirm_DeleteTitle));

    /// <summary>界面文案 <c>Confirm_DeleteBodyFormat</c>：删除确认框正文，0 为设备名。</summary>
    public static string Confirm_DeleteBodyFormat => Get(nameof(Confirm_DeleteBodyFormat));

    /// <summary>界面文案 <c>Confirm_DeleteOk</c>：删除确认框的删除按钮。</summary>
    public static string Confirm_DeleteOk => Get(nameof(Confirm_DeleteOk));

    /// <summary>界面文案 <c>Status_DeviceRemovedFormat</c>：删除历史设备之后的状态提示。</summary>
    public static string Status_DeviceRemovedFormat => Get(nameof(Status_DeviceRemovedFormat));

    /// <summary>界面文案 <c>Status_DeviceSelectedFormat</c>：选中尚未连接的设备后的状态提示。</summary>
    public static string Status_DeviceSelectedFormat => Get(nameof(Status_DeviceSelectedFormat));

    /// <summary>界面文案 <c>Ui_Forget</c>：下拉项右侧的忘记按钮。</summary>
    public static string Ui_Forget => Get(nameof(Ui_Forget));

    /// <summary>界面文案 <c>Ui_Audio</c>：手机窗口控制条上的音频开关按钮</summary>
    public static string Ui_Audio => Get(nameof(Ui_Audio));
    /// <summary>界面文案 <c>Status_Audio_Waiting</c>：音频开关打开后等待手机端切换输出</summary>
    public static string Status_Audio_Waiting => Get(nameof(Status_Audio_Waiting));
    /// <summary>界面文案 <c>Status_Audio_On</c>：音频连接成功的状态提示</summary>
    public static string Status_Audio_On => Get(nameof(Status_Audio_On));
    /// <summary>界面文案 <c>Status_Audio_Off</c>：音频开关关掉时的状态提示</summary>
    public static string Status_Audio_Off => Get(nameof(Status_Audio_Off));
    /// <summary>界面文案 <c>Status_Audio_NoDevice</c>：系统里没有可作音频源的已配对蓝牙设备</summary>
    public static string Status_Audio_NoDevice => Get(nameof(Status_Audio_NoDevice));
    /// <summary>界面文案 <c>Status_Audio_FailedFormat</c>：音频连接失败提示，0 为错误码</summary>
    public static string Status_Audio_FailedFormat => Get(nameof(Status_Audio_FailedFormat));

    /// <summary>界面文案 <c>Status_ScanDeepFormat</c>：常规扫描没结果、改为对线索主机做端口细扫时的提示</summary>
    public static string Status_ScanDeepFormat => Get(nameof(Status_ScanDeepFormat));
    /// <summary>界面文案 <c>Settings_Quality</c>：设置页画质分组的标题</summary>
    public static string Settings_Quality => Get(nameof(Settings_Quality));
    /// <summary>界面文案 <c>Settings_FrameRate</c>：设置项：抓取帧率上限</summary>
    public static string Settings_FrameRate => Get(nameof(Settings_FrameRate));
    /// <summary>界面文案 <c>Settings_FrameRateHint</c>：帧率选项说明</summary>
    public static string Settings_FrameRateHint => Get(nameof(Settings_FrameRateHint));
    /// <summary>界面文案 <c>Settings_BitRate</c>：设置项：码率</summary>
    public static string Settings_BitRate => Get(nameof(Settings_BitRate));
    /// <summary>界面文案 <c>Settings_BitRateHint</c>：码率选项说明</summary>
    public static string Settings_BitRateHint => Get(nameof(Settings_BitRateHint));
    /// <summary>界面文案 <c>Settings_Behavior</c>：设置页行为分组标题</summary>
    public static string Settings_Behavior => Get(nameof(Settings_Behavior));
    /// <summary>界面文案 <c>Settings_CloseToTray</c>：是否最小化到托盘</summary>
    public static string Settings_CloseToTray => Get(nameof(Settings_CloseToTray));
    /// <summary>界面文案 <c>Settings_Audio</c>：设置页音频分组标题</summary>
    public static string Settings_Audio => Get(nameof(Settings_Audio));
    /// <summary>界面文案 <c>Settings_AutoAudio</c>：是否自动开音频</summary>
    public static string Settings_AutoAudio => Get(nameof(Settings_AutoAudio));
    /// <summary>界面文案 <c>Settings_EffectiveHint</c>：设置页底部说明</summary>
    public static string Settings_EffectiveHint => Get(nameof(Settings_EffectiveHint));
    /// <summary>界面文案 <c>Keys_Title</c>：按键映射页标题</summary>
    public static string Keys_Title => Get(nameof(Keys_Title));
    /// <summary>界面文案 <c>Keys_Desc</c>：按键映射页说明</summary>
    public static string Keys_Desc => Get(nameof(Keys_Desc));
    /// <summary>界面文案 <c>Keys_Col_Key</c>：列头</summary>
    public static string Keys_Col_Key => Get(nameof(Keys_Col_Key));
    /// <summary>界面文案 <c>Keys_Col_Action</c>：列头</summary>
    public static string Keys_Col_Action => Get(nameof(Keys_Col_Action));
    /// <summary>界面文案 <c>Keys_Esc</c>：按键映射行</summary>
    public static string Keys_Esc => Get(nameof(Keys_Esc));
    /// <summary>界面文案 <c>Keys_EscAction</c>：按键映射行</summary>
    public static string Keys_EscAction => Get(nameof(Keys_EscAction));
    /// <summary>界面文案 <c>Keys_Home</c>：按键映射行</summary>
    public static string Keys_Home => Get(nameof(Keys_Home));
    /// <summary>界面文案 <c>Keys_HomeAction</c>：按键映射行</summary>
    public static string Keys_HomeAction => Get(nameof(Keys_HomeAction));
    /// <summary>界面文案 <c>Keys_Recents</c>：按键映射行</summary>
    public static string Keys_Recents => Get(nameof(Keys_Recents));
    /// <summary>界面文案 <c>Keys_RecentsAction</c>：按键映射行</summary>
    public static string Keys_RecentsAction => Get(nameof(Keys_RecentsAction));
    /// <summary>界面文案 <c>Keys_Power</c>：按键映射行</summary>
    public static string Keys_Power => Get(nameof(Keys_Power));
    /// <summary>界面文案 <c>Keys_PowerAction</c>：按键映射行</summary>
    public static string Keys_PowerAction => Get(nameof(Keys_PowerAction));
    /// <summary>界面文案 <c>Keys_VolumeUp</c>：按键映射行</summary>
    public static string Keys_VolumeUp => Get(nameof(Keys_VolumeUp));
    /// <summary>界面文案 <c>Keys_VolumeUpAction</c>：按键映射行</summary>
    public static string Keys_VolumeUpAction => Get(nameof(Keys_VolumeUpAction));
    /// <summary>界面文案 <c>Keys_VolumeDown</c>：按键映射行</summary>
    public static string Keys_VolumeDown => Get(nameof(Keys_VolumeDown));
    /// <summary>界面文案 <c>Keys_VolumeDownAction</c>：按键映射行</summary>
    public static string Keys_VolumeDownAction => Get(nameof(Keys_VolumeDownAction));
    /// <summary>界面文案 <c>Keys_Editing</c>：按键映射行</summary>
    public static string Keys_Editing => Get(nameof(Keys_Editing));
    /// <summary>界面文案 <c>Keys_EditingAction</c>：按键映射行</summary>
    public static string Keys_EditingAction => Get(nameof(Keys_EditingAction));
    /// <summary>界面文案 <c>Upd_Title</c>：更新提示框标题</summary>
    public static string Upd_Title => Get(nameof(Upd_Title));
    /// <summary>界面文案 <c>Upd_AvailableFormat</c>：发现更新，0 为版本号</summary>
    public static string Upd_AvailableFormat => Get(nameof(Upd_AvailableFormat));
    /// <summary>界面文案 <c>Upd_UpToDate</c>：无更新</summary>
    public static string Upd_UpToDate => Get(nameof(Upd_UpToDate));
    /// <summary>界面文案 <c>Upd_NotConfigured</c>：未配置仓库地址</summary>
    public static string Upd_NotConfigured => Get(nameof(Upd_NotConfigured));
    /// <summary>界面文案 <c>Upd_NotFound</c>：404</summary>
    public static string Upd_NotFound => Get(nameof(Upd_NotFound));
    /// <summary>界面文案 <c>Upd_FailedFormat</c>：网络或解析失败，0 为原因</summary>
    public static string Upd_FailedFormat => Get(nameof(Upd_FailedFormat));
    /// <summary>界面文案 <c>Upd_DownloadFailedFormat</c>：下载失败，0 为原因</summary>
    public static string Upd_DownloadFailedFormat => Get(nameof(Upd_DownloadFailedFormat));
    /// <summary>界面文案 <c>Upd_Downloading</c>：下载中</summary>
    public static string Upd_Downloading => Get(nameof(Upd_Downloading));
    /// <summary>界面文案 <c>Upd_InstallNow</c>：确认按钮文字</summary>
    public static string Upd_InstallNow => Get(nameof(Upd_InstallNow));
    /// <summary>界面文案 <c>Tray_Devices</c>：托盘菜单项</summary>
    public static string Tray_Devices => Get(nameof(Tray_Devices));
    /// <summary>界面文案 <c>Tray_CheckUpdate</c>：托盘菜单项</summary>
    public static string Tray_CheckUpdate => Get(nameof(Tray_CheckUpdate));
    /// <summary>界面文案 <c>Status_Mirror_PreparingDaemon</c>：连接阶段提示</summary>
    public static string Status_Mirror_PreparingDaemon => Get(nameof(Status_Mirror_PreparingDaemon));
    /// <summary>界面文案 <c>Status_Mirror_Forwarding</c>：连接阶段提示</summary>
    public static string Status_Mirror_Forwarding => Get(nameof(Status_Mirror_Forwarding));
    /// <summary>界面文案 <c>Status_Mirror_WaitingFrame</c>：连接阶段提示</summary>
    public static string Status_Mirror_WaitingFrame => Get(nameof(Status_Mirror_WaitingFrame));
    /// <summary>界面文案 <c>Ui_KeyHome</c>：桌面键</summary>
    public static string Ui_KeyHome => Get(nameof(Ui_KeyHome));
    /// <summary>界面文案 <c>Ui_KeyBack</c>：返回键</summary>
    public static string Ui_KeyBack => Get(nameof(Ui_KeyBack));
    /// <summary>界面文案 <c>Ui_KeyRecent</c>：最近任务</summary>
    public static string Ui_KeyRecent => Get(nameof(Ui_KeyRecent));
    /// <summary>界面文案 <c>Ui_KeyPower</c>：电源键</summary>
    public static string Ui_KeyPower => Get(nameof(Ui_KeyPower));
}
