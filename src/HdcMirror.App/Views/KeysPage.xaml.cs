using System.Windows.Controls;

namespace HdcMirror.App.Views;

/// <summary>
/// 按键映射说明页：列出手机窗口里已经生效的快捷键。
/// 映射目前是固定的一套（见 <c>MirrorWindow.OnScreenKeyDown</c>），后续要做自定义再升级成可编辑表格。
/// </summary>
public partial class KeysPage : UserControl
{
    /// <summary>初始化按键映射页。</summary>
    public KeysPage()
    {
        InitializeComponent();
    }
}
