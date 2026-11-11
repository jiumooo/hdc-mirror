using System.Text.RegularExpressions;

namespace HdcMirror.AudioPoc;

/// <summary>
/// 隐私脱敏（依据 RULES.md「隐私红线」）。
/// 规则：蓝牙 MAC / 设备 ID 里的地址片段**永远**脱敏；
/// 设备显示名默认掩码，仅当显式传 --show-names 时才原样打印（用于操作者本机辨认）。
/// </summary>
internal static partial class Privacy
{
    /// <summary>是否原样打印设备显示名（由命令行 --show-names 控制）。</summary>
    public static bool ShowNames { get; set; }

    /// <summary>把任意字符串里的 MAC 地址（XX:XX:XX:XX:XX:XX 或 XX:XX:XX:XX:XX:XX）替换为占位符。</summary>
    [GeneratedRegex("[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}|[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5}")]
    private static partial Regex MacPattern();

    /// <summary>
    /// BTHENUM 设备实例 ID 里的蓝牙地址是**无分隔符 12 位十六进制**（如 ……&amp;0&amp;AB12CD34EF56_C00000000#……）。
    /// 用「前面必须是 &amp;」的后顾断言避让 GUID 末段（GUID 末段前面是 -），避免误伤固定的服务 GUID。
    /// </summary>
    [GeneratedRegex(@"(?<=&)[0-9A-Fa-f]{12}")]
    private static partial Regex BareBluetoothAddress();

    /// <summary>适配器型号（VID/PID）属于硬件标识，一并隐去。</summary>
    [GeneratedRegex(@"VID&[0-9A-Za-z]+_PID&[0-9A-Za-z]+")]
    private static partial Regex VidPidPattern();

    /// <summary>设备 ID 脱敏：保留结构与固定协议 GUID，抹掉其中的蓝牙地址与适配器型号。</summary>
    public static string Id(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "<空>";
        }

        var text = MacPattern().Replace(raw, "XX:XX:XX:XX:XX:XX");
        text = BareBluetoothAddress().Replace(text, "XXXXXXXXXXXX");
        text = VidPidPattern().Replace(text, "VID&****_PID&****");
        return text;
    }

    /// <summary>设备显示名脱敏：只留首尾各 2 个字符。</summary>
    public static string Name(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "<无名>";
        }

        var text = raw.Trim();
        if (ShowNames)
        {
            return text;
        }

        if (text.Length <= 4)
        {
            return text[..1] + "***";
        }

        return string.Concat(text.AsSpan(0, 2), "***", text.AsSpan(text.Length - 2));
    }
}
