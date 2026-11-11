using HdcMirror.Core.Diagnostics;

namespace HdcMirror.Core.Hdc;

/// <summary>
/// 设备枚举：解析 <c>hdc list targets</c>，并按 D-03 的约定决定优先使用哪一台。
/// </summary>
public sealed class HdcDeviceEnumerator
{
    private const string Category = "hdc";

    private readonly HdcRunner _runner;
    private readonly ILogSink _log;

    /// <summary>创建设备枚举器。</summary>
    /// <param name="runner">hdc 执行器（应为不带 <c>-t</c> 的实例，否则 <c>list targets</c> 会带设备前缀）。</param>
    /// <param name="log">日志出口，可为空。</param>
    public HdcDeviceEnumerator(HdcRunner runner, ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>列出当前所有设备。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<HdcDevice>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(new[] { "list", "targets" }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new HdcCommandException(result);
        }

        var devices = ParseTargets(result.StandardOutput);
        _log.Debug(Category, $"list targets 解析出 {devices.Count} 台设备。");
        return devices;
    }

    /// <summary>按 D-03 选择设备：存在 USB 则优先 USB，否则取第一台无线设备。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>没有设备时返回 <see langword="null"/>。</returns>
    public async Task<HdcDevice?> SelectPreferredAsync(CancellationToken cancellationToken = default)
    {
        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        if (devices.Count == 0)
        {
            _log.Warn(Category, "没有可用设备。");
            return null;
        }

        foreach (var device in devices)
        {
            if (device.Kind == ConnectionKind.Usb)
            {
                _log.Info(Category, $"选中 USB 设备 {device.Serial}（共 {devices.Count} 台）。");
                return device;
            }
        }

        var wireless = devices[0];
        _log.Info(Category, $"无 USB 设备，选中无线设备 {wireless.Serial}（共 {devices.Count} 台）。");
        return wireless;
    }

    /// <summary>解析 <c>hdc list targets</c> 的输出。</summary>
    /// <param name="output">命令的标准输出。</param>
    public static IReadOnlyList<HdcDevice> ParseTargets(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<HdcDevice>();
        }

        var devices = new List<HdcDevice>(4);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n'))
        {
            if (!HdcDevice.TryParse(line.TrimEnd('\r'), out var device) || device is null)
            {
                continue;
            }

            // hdc 偶尔会重复打印同一台设备。
            if (seen.Add(device.Serial))
            {
                devices.Add(device);
            }
        }

        return devices;
    }
}
