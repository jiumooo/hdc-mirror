using System.Windows.Controls;

namespace HdcMirror.App.Views;

/// <summary>
/// 支持作者页：捐赠二维码占位 + 支持者名单。
/// 和其它设置页一样塞进主窗口的内容宿主，不弹新窗口。
/// 二维码正式图片后续放到 Assets\donate-qr.png，未提供前显示占位文案。
/// </summary>
public partial class SupportPage : UserControl
{
    /// <summary>初始化支持作者页。</summary>
    public SupportPage()
    {
        InitializeComponent();
    }
}
