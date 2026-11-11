using System.Globalization;

namespace HdcMirror.Core.Hdc;

/// <summary>设备连接方式，供界面显示与链路选择使用。</summary>
public enum ConnectionKind
{
    /// <summary>USB 直连（设备标识是纯序列号）。</summary>
    Usb,

    /// <summary>无线连接（设备标识形如 <c>ip:port</c>）。</summary>
    Wireless,
}

/// <summary>hdc 上报的一台目标设备。</summary>
/// <param name="Serial">设备标识：USB 为序列号，无线为 <c>ip:port</c>。</param>
/// <param name="Kind">连接方式。</param>
public sealed record HdcDevice(string Serial, ConnectionKind Kind)
{
    /// <summary>是否无线连接。</summary>
    public bool IsWireless => Kind == ConnectionKind.Wireless;

    /// <summary>是否 USB 连接。</summary>
    public bool IsUsb => Kind == ConnectionKind.Usb;

    /// <summary>按标识形态判断连接方式。</summary>
    /// <param name="serial">设备标识。</param>
    /// <returns>末段是 1~65535 的端口号即视为无线。</returns>
    public static ConnectionKind DetectKind(string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var separator = serial.LastIndexOf(':');
        if (separator <= 0 || separator == serial.Length - 1)
        {
            return ConnectionKind.Usb;
        }

        var portText = serial[(separator + 1)..];
        return int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
               && port > 0
               && port <= 65535
            ? ConnectionKind.Wireless
            : ConnectionKind.Usb;
    }

    /// <summary>由标识构造设备对象。</summary>
    /// <param name="serial">设备标识。</param>
    public static HdcDevice Parse(string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var trimmed = serial.Trim();
        return new HdcDevice(trimmed, DetectKind(trimmed));
    }

    /// <summary>解析 <c>hdc list targets</c> 的一行。</summary>
    /// <param name="line">原始行。</param>
    /// <param name="device">待填充的目标设备。</param>
    /// <returns>该行不是设备条目时返回 <see langword="false"/>（空行、<c>[Empty]</c> 等）。</returns>
    public static bool TryParse(string? line, out HdcDevice? device)
    {
        device = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();

        // 没有设备时 hdc 打印 [Empty] / Empty，不能当成设备。
        if (trimmed[0] == '[' || trimmed.Equals("Empty", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 多设备模式下可能带前缀空白或后随说明文字，只取首个空白分隔的 token。
        var token = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.Length == 0)
        {
            return false;
        }

        device = Parse(token);
        return true;
    }
}
