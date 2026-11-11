namespace HdcMirror.Core.Settings;

/// <summary>
/// 音画同步策略（需求说明书 §5.0 的三档定案）。
/// 默认 <see cref="Auto"/>，由自适应状态机在运行期决定同步档位。
/// </summary>
public enum AvSyncMode
{
    /// <summary>自动：按抖动与延迟实测在同步/低延迟之间切换。</summary>
    Auto = 0,

    /// <summary>强制音画同步：优先对齐，容忍更高端到端延迟。</summary>
    ForceSync = 1,

    /// <summary>强制低延迟：牺牲对齐换取最低延迟。</summary>
    ForceLowLatency = 2,
}
