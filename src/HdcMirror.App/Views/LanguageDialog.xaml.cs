using System;
using System.Windows;
using HdcMirror.App.Services;

// System.Windows 里也有一个 Localization 类型，别名把它固定到我们自己的实现
using Localization = HdcMirror.App.Services.Localization;

// 只引用这三个类型：整体 using Wpf.Ui.Controls 会与 System.Windows.Controls
// 与 Localization 等名字上产生 CS0104 歧义
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using UiButton = Wpf.Ui.Controls.Button;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace HdcMirror.App.Views;

/// <summary>
/// 首次启动的语言选择窗。此刻还不知道用户想要哪种语言，所以标题与底部说明故意双语，
/// 两个按钮各显示该语言自己的写法；默认高亮项按系统界面语言决定。
/// <para>
/// 选完只写配置 + 应用 UI 文化，**不重启** —— 主窗口还没建出来，
/// <c>{x:Static res:Strings.Xxx}</c> 还没求值，改了文化立刻生效。
/// </para>
/// </summary>
public partial class LanguageDialog : FluentWindow
{
    private readonly string _defaultLanguage;

    /// <summary>以系统语言推出来的默认高亮项创建对话框。</summary>
    /// <param name="defaultLanguage">
    /// <see cref="HdcMirror.App.Services.Localization.ChineseSimplified"/> 或 <see cref="HdcMirror.App.Services.Localization.English"/>；
    /// 其它值按简体中文处理。
    /// </param>
    public LanguageDialog(string defaultLanguage)
    {
        _defaultLanguage = string.Equals(defaultLanguage, Localization.English, StringComparison.OrdinalIgnoreCase)
            ? Localization.English
            : Localization.ChineseSimplified;

        InitializeComponent();
        HighlightDefaultChoice();
    }

    /// <summary>
    /// 用户选定的语言；直接关窗（Esc / 关闭按钮）时为 <see langword="null"/>，
    /// 由调用方回落到默认值。
    /// </summary>
    public string? SelectedLanguage { get; private set; }

    private void HighlightDefaultChoice()
    {
        bool english = _defaultLanguage == Localization.English;
        UiButton primary = english ? EnglishButton : ChineseButton;
        primary.Appearance = ControlAppearance.Primary;

        Loaded += (_, _) => primary.Focus();
    }

    private void OnChineseClick(object sender, RoutedEventArgs e) => Close(Localization.ChineseSimplified);

    private void OnEnglishClick(object sender, RoutedEventArgs e) => Close(Localization.English);

    private void Close(string language)
    {
        SelectedLanguage = language;
        Close();
    }
}
