using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HdcMirror.App.Resources;

namespace HdcMirror.App.Views;

/// <summary>
/// 支持作者页：两个按钮切换支付宝 / 微信收款码，图片随程序集内嵌资源加载。
/// </summary>
public partial class SupportPage : UserControl
{
    private static readonly Uri AlipayUri = new("pack://application:,,,/Assets/donate/alipay-final.png", UriKind.Absolute);
    private static readonly Uri WeChatUri = new("pack://application:,,,/Assets/donate/wechat-final.png", UriKind.Absolute);

    public SupportPage()
    {
        InitializeComponent();
        Show(AlipayUri, AlipayButton, WeChatButton);
    }

    private void OnAlipayClick(object sender, RoutedEventArgs e) => Show(AlipayUri, AlipayButton, WeChatButton);

    private void OnWeChatClick(object sender, RoutedEventArgs e) => Show(WeChatUri, WeChatButton, AlipayButton);

    private void Show(Uri uri, Wpf.Ui.Controls.Button active, Wpf.Ui.Controls.Button other)
    {
        if (QrImage is null)
        {
            return;
        }

        BitmapImage image = new();
        image.BeginInit();
        image.UriSource = uri;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        QrImage.Source = image;

        active.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
        other.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
    }
}