using System;
using System.Globalization;

namespace HdcMirror.App.Services;

/// <summary>
/// 界面语言的唯一入口。
/// <para>
/// 只改 <see cref="CultureInfo.CurrentUICulture"/> 与
/// <see cref="CultureInfo.DefaultThreadCurrentUICulture"/>，绝不动 CurrentCulture：
/// 日期、数字与 <c>{0:N1}</c> 这类格式串继续跟随系统区域，日志和 FFmpeg 参数不受影响。
/// </para>
/// <para>
/// 必须在 <c>App.OnStartup</c> 最前面调用。XAML 里的 <c>{x:Static res:Strings.Xxx}</c>
/// 是静态求值，窗口一解析就定死，之后再改文化不会生效 —— 所以切换语言只能重启应用。
/// </para>
/// </summary>
public static class Localization
{
    /// <summary>跟随系统（沿用进程当前的 UI 文化）。</summary>
    public const string FollowSystem = "";

    /// <summary>简体中文。</summary>
    public const string ChineseSimplified = "zh-Hans";

    /// <summary>英文。</summary>
    public const string English = "en";

    /// <summary>
    /// 语言切换后重启时带的命令行参数。新实例看到它，就会在抢单实例锁失败时重试，
    /// 容忍旧实例还在收尾（托盘图标、窗口句柄、Mutex 都要几百毫秒才放干净）。
    /// </summary>
    public const string AfterLanguageChangeArgument = "--after-language-change";

    /// <summary>
    /// 应用界面语言。<c>""</c> = 跟随系统；<c>"zh-Hans"</c> / <c>"en"</c> = 指定语言。
    /// 无法识别的值按「跟随系统」处理，不会抛异常。
    /// </summary>
    public static void Apply(string? language)
    {
        CultureInfo ui = Resolve(language) ?? CultureInfo.CurrentUICulture;

        // 新线程默认值 + 当前线程。启动最前面调用时还没有别的工作线程在用文化，
        // 设默认值等价于「给所有已存在/将来的线程都设上」。
        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.CurrentUICulture = ui;
    }

    /// <summary>
    /// 把保存值解析成文化对象；<c>null</c> / 空串 / 未知文化一律返回 <c>null</c>，
    /// 由调用方决定回落到什么（<see cref="Apply"/> 回落到当前 UI 文化）。
    /// </summary>
    public static CultureInfo? Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        try
        {
            return new CultureInfo(language.Trim(), useUserOverride: false);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 首启语言选择窗的默认高亮项：系统界面是英文就给 English，其余（含判断不出来的）
    /// 一律给简体中文。
    /// </summary>
    public static bool SystemPrefersEnglish()
    {
        string tag = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;

        // 不变文化（"iv"）拿不到系统语言，退回进程当前 UI 文化
        if (string.IsNullOrEmpty(tag) || string.Equals(tag, "iv", StringComparison.OrdinalIgnoreCase))
        {
            tag = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        }

        return string.Equals(tag, "en", StringComparison.OrdinalIgnoreCase);
    }
}
