using Wpf.Ui.Abstractions;

namespace HdcMirror.App;

/// <summary>
/// NavigationView 的页面提供者：按 <see cref="Type"/> 反射创建页面实例。
/// M0 页面无构造参数依赖，M1 起如果页面需要服务，再换成 DI 容器解析。
/// </summary>
internal sealed class AppPageProvider : INavigationViewPageProvider
{
    private AppPageProvider()
    {
    }

    internal static AppPageProvider Instance { get; } = new();

    /// <inheritdoc />
    public object? GetPage(Type pageType) => Activator.CreateInstance(pageType);
}
