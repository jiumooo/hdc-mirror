using System.Windows;

namespace HdcMirror.App;

/// <summary>
/// 「支持作者」对话框：捐赠二维码 + 支持者名单。
/// 名单数据将在 M5 接入远程清单，当前为空态。
/// </summary>
public partial class SupporterWindow
{
    public SupporterWindow() => InitializeComponent();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
