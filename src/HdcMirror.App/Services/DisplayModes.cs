using HdcMirror.Core.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HdcMirror.App.Resources;
using HdcMirror.Core.Hdc;

namespace HdcMirror.App.Services;

/// <summary>
/// 屏幕刷新率能力缓存：设备信息读取完成后由主窗口写入，供设置页与镜像会话共用。
/// <para>
/// 数据来源是 <c>hidumper -s RenderService -a screen</c> 的 <c>supportedMode</c> 与
/// <c>activeMode</c>（真机实测：1260x2720 支持 120 / 90 / 60 / 30，当前 60）。
/// 没读到设备时退化为空清单，界面只给 30 / 60，不做猜测。
/// </para>
/// </summary>
public static class DisplayModes
{
    /// <summary>设备支持的刷新率（去重升序）；未读取到时为空。</summary>
    public static IReadOnlyList<int> Supported { get; private set; } = Array.Empty<int>();

    /// <summary>设备当前生效的刷新率；未读取到时为 0。</summary>
    public static int Active { get; private set; }

    /// <summary>「自动」档实际取用的帧率：手机现在多少就多少，读不到按 60 兜底。</summary>
    public static int AutoFrameRate => Active > 0 ? Active : 60;

    /// <summary>把一次设备信息读取的结果写进缓存。</summary>
    public static void Update(HdcDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Supported = info.SupportedRefreshRates;
        Active = info.ActiveRefreshRate;
    }

    /// <summary>
    /// 可选档位（不含「自动」）：以 30 为一档，取到设备最高刷新率为止。
    /// 设备支持 120 → 30 / 60 / 90 / 120；只支持 60 → 30 / 60。
    /// </summary>
    public static IReadOnlyList<int> Choices()
    {
        int ceiling = Supported.Count > 0 ? Supported.Max() : 60;
        if (Active > ceiling)
        {
            ceiling = Active;
        }

        var list = new List<int>();
        for (int rate = 30; rate <= ceiling; rate += 30)
        {
            list.Add(rate);
        }

        return list;
    }

    /// <summary>
    /// 缓存还是空的就去找一台记住过的设备读一次刷新率。
    /// 目的是：用户没连接手机、只是打开设置页时，档位也能显示真机的 30/60/90/120。
    /// </summary>
    /// <returns>读到并写入缓存返回 true。</returns>
    public static async Task<bool> EnsureLoadedAsync(ILogSink log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (Supported.Count > 0 || Active > 0)
        {
            return true;
        }

        foreach (SavedDevice saved in new DeviceStore().Load())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reader = new HdcDeviceInfoReader(
                    new HdcRunner(deviceSerial: saved.Endpoint, log: log),
                    log,
                    TimeSpan.FromSeconds(4));
                HdcDeviceInfo info = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (info.SupportedRefreshRates.Count > 0 || info.ActiveRefreshRate > 0)
                {
                    Update(info);
                    return true;
                }
            }
            catch (HdcException ex)
            {
                log.Debug("display", "读刷新率失败：" + ex.Message);
            }
        }

        return false;
    }
    /// <summary>档位说明，显示在设置页下方（例如「你的手机：当前 60，支持 30/60/90/120」）。</summary>
    public static string Describe()
    {
        if (Supported.Count == 0 && Active == 0)
        {
            return Strings.Settings_FrameRateHintUnknown;
        }

        string rates = Supported.Count > 0
            ? string.Join('/', Supported)
            : Strings.Settings_FrameRateUnknown;
        return string.Format(
            CultureInfo.CurrentCulture,
            Strings.Settings_FrameRateHintFormat,
            Active,
            rates);
    }
}
