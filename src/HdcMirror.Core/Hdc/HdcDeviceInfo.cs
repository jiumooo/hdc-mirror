namespace HdcMirror.Core.Hdc;

/// <summary>
/// 一台鸿蒙设备的静态信息。
/// <para>
/// 九个字段与界面「设备信息」九项一一对应；任一字段读取失败时保持空字符串，
/// 失败原因写日志，并把字段名收进 <see cref="MissingFields"/>，由界面决定怎么呈现。
/// </para>
/// <para>
/// 隐私约定：<see cref="Serial"/> 在无线连接下通常是 <c>ip:port</c>（设备不向普通 shell 暴露真实 SN），
/// 界面显示时应打码，日志里也只记长度不记明文。
/// </para>
/// </summary>
public sealed record HdcDeviceInfo
{
    /// <summary>九个字段全空的实例，用于读取整体失败时兜底。</summary>
    public static HdcDeviceInfo Empty { get; } = new();

    /// <summary>设备名（市场名），取自 <c>const.product.name</c>。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>品牌，取自 <c>const.product.brand</c>。</summary>
    public string Brand { get; init; } = string.Empty;

    /// <summary>型号，取自 <c>const.product.model</c>。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>序列号；无线连接且设备未开放 SN 时为 <c>ip:port</c> 连接标识。</summary>
    public string Serial { get; init; } = string.Empty;

    /// <summary>鸿蒙版本，取自 <c>const.product.software.version</c>。</summary>
    public string HarmonyVersion { get; init; } = string.Empty;

    /// <summary>内核版本，取自 <c>uname -r</c>。</summary>
    public string KernelVersion { get; init; } = string.Empty;

    /// <summary>处理器，架构 + ABI（SoC 型号需 root，普通 shell 取不到）。</summary>
    public string Cpu { get; init; } = string.Empty;

    /// <summary>物理分辨率，形如 <c>1260x2720</c>。</summary>
    public string Resolution { get; init; } = string.Empty;

    /// <summary>物理内存，形如 <c>11.32 GiB (11869524 kB)</c>。</summary>
    public string Memory { get; init; } = string.Empty;

    /// <summary>读取失败而留空的字段名（<see cref="HdcDeviceInfo"/> 的属性名）。</summary>
    public IReadOnlyList<string> MissingFields { get; init; } = Array.Empty<string>();

    /// <summary>九个字段是否全部读到。</summary>
    public bool IsComplete => MissingFields.Count == 0;

    /// <inheritdoc />
    public override string ToString()
        => $"Name={Name}; Brand={Brand}; Model={Model}; Harmony={HarmonyVersion}; Kernel={KernelVersion}; " +
           $"Cpu={Cpu}; Resolution={Resolution}; Memory={Memory}; Missing={MissingFields.Count}";
}
