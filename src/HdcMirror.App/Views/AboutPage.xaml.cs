using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HdcMirror.App.Resources;

namespace HdcMirror.App.Views;

/// <summary>关于页：版本号取自程序集，仓库 / 反馈链接点击后交给默认浏览器。</summary>
public partial class AboutPage : UserControl
{
    private const string RepositoryUrl = "https://github.com/jiumooo/hdc-mirror";
    private const string IssuesUrl = RepositoryUrl + "/issues";

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Nav_About_VersionFormat,
            ReadAssemblyVersion());
    }

    private void OnRepoClick(object sender, RoutedEventArgs e) => Open(RepositoryUrl);

    private void OnIssuesClick(object sender, RoutedEventArgs e) => Open(IssuesUrl);

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打不开浏览器不是致命错误（比如被组策略禁掉），界面保持原样即可。
        }
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