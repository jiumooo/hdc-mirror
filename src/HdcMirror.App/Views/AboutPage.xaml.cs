using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using HdcMirror.App.Resources;

namespace HdcMirror.App.Views;

/// <summary>关于页：显示程序集里携带的版本号（不写死在 XAML 里）。</summary>
public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Nav_About_VersionFormat,
            ReadAssemblyVersion());
    }

    /// <summary>
    /// 读 <see cref="AssemblyInformationalVersionAttribute"/>；SDK 会在其后附加 <c>+&lt;commit&gt;</c>，
    /// 这里截掉源链接后缀，界面只显示纯版本号。
    /// </summary>
    private static string ReadAssemblyVersion()
    {
        Assembly assembly = typeof(AboutPage).Assembly;
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? string.Empty;

        int metadataSeparator = informational.IndexOf('+', StringComparison.Ordinal);
        if (metadataSeparator >= 0)
        {
            informational = informational[..metadataSeparator];
        }

        return informational.Length > 0
            ? informational
            : assembly.GetName().Version?.ToString(3) ?? string.Empty;
    }
}
