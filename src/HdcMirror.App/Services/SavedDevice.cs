using System;
using System.Globalization;

namespace HdcMirror.App.Services;

/// <summary>
/// 一条被记住的设备记录：只存「显示名 + 端点」，不存序列号 / UDID 这类设备身份标识。
/// </summary>
public sealed class SavedDevice
{
    /// <summary>设备显示名，来自 hdc 的友好名；拿不到时用端点兜底。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>端点，形如 <c>192.0.2.10:5555</c> 或 USB 序列号。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>最近一次连接时间，用于列表排序（越近越靠前）。</summary>
    public DateTimeOffset LastConnected { get; set; } = DateTimeOffset.Now;

    /// <summary>列表主标题：优先显示设备名。</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Endpoint : Name;

    /// <summary>列表副标题：端点。</summary>
    public string Subtitle => Endpoint;

    /// <summary>下拉/列表的纯文本形式，屏幕阅读器读到的也是它。</summary>
    /// <returns>形如「HarmonyOS NEXT 真机（型号略） · 192.0.2.10:5555」。</returns>
    public override string ToString() => string.Format(
        CultureInfo.CurrentCulture,
        "{0} · {1}",
        Title,
        Endpoint);
}
