using System.Windows;

namespace HdcMirror.App;

/// <summary>
/// 通用确认对话框：危险操作前问一句，避免误删。
/// 只做「是 / 否」二选一，不接受输入，尺寸固定，居中对齐父窗口。
/// </summary>
public partial class ConfirmWindow : Window
{
    /// <summary>初始化确认框。</summary>
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    /// <summary>用户点了「删除 / 确定」。</summary>
    public bool Confirmed { get; private set; }

    /// <summary>
    /// 弹出确认框并等待用户选择。
    /// </summary>
    /// <param name="owner">父窗口，用于居中与模态；可为 <see langword="null"/>。</param>
    /// <param name="title">标题，例如「删除设备」。</param>
    /// <param name="body">正文，说明影响范围。</param>
    /// <param name="okText">确认按钮文字，例如「删除」。</param>
    /// <returns>用户点了确认按钮时为 <see langword="true"/>。</returns>
    public static bool Ask(Window? owner, string title, string body, string okText)
    {
        var dialog = new ConfirmWindow();
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
        }

        dialog.TitleText.Text = title;
        dialog.BodyText.Text = body;
        dialog.Title = title;
        dialog.OkButton.Content = okText;
        dialog.ShowDialog();
        return dialog.Confirmed;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
