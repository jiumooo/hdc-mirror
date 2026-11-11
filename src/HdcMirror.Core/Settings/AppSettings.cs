namespace HdcMirror.Core.Settings;

/// <summary>
/// 本地持久化的应用设置。
/// M0 只保留三项：关闭到托盘、主题、音画同步策略；其余设置项随对应里程碑追加，
/// 追加时只允许新增带默认值的成员，保证旧 settings.json 反序列化后仍然可用。
/// </summary>
public sealed record AppSettings
{
    /// <summary>关闭主窗口时最小化到托盘（而不是退出）。</summary>
    public bool MinimizeToTray { get; init; } = true;

    /// <summary>界面主题。</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>音画同步策略，默认自动。</summary>
    public AvSyncMode AvSyncMode { get; init; } = AvSyncMode.Auto;

    /// <summary>默认设置（读档失败时的回退值）。</summary>
    public static AppSettings Default { get; } = new();
}
