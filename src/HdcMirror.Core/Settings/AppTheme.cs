namespace HdcMirror.Core.Settings;

/// <summary>
/// 界面主题（需求说明书 §3.1：跟随系统 + 手动浅色/深色）。
/// 资源层只描述"想要什么"，具体应用由表现层的主题服务负责。
/// </summary>
public enum AppTheme
{
    /// <summary>跟随系统深浅色。</summary>
    System = 0,

    /// <summary>强制浅色。</summary>
    Light = 1,

    /// <summary>强制深色。</summary>
    Dark = 2,
}
