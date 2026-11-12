using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HdcMirror.App.Services;

/// <summary>
/// 用户可调的运行参数：画质档位、连接行为、音频开关。
/// 只存本机 <c>%LOCALAPPDATA%\HdcMirror\settings.json</c>，不联网、不含任何设备身份信息。
/// 读写失败一律退回默认值，绝不因为配置文件坏了就让软件起不来。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static AppSettings? _current;

    /// <summary>启动后是否自动扫描局域网设备（关掉则只能手动点「扫描」）。</summary>
    public bool AutoScanOnStart { get; set; } = true;

    /// <summary>当前生效的设置（首次访问时从磁盘加载）。</summary>
    public static AppSettings Current => _current ??= Load();

    /// <summary>设置文件路径：<c>%LOCALAPPDATA%\HdcMirror\settings.json</c>。</summary>
    public static string FilePath => HdcMirror.Core.AppPaths.SettingsFile;

    /// <summary>上次关闭时的主窗口位置与大小（DIP）。宽高都 &gt; 100 才算记录过。</summary>
    public double WindowLeft { get; set; }

    /// <summary>上次关闭时的主窗口顶部坐标（DIP）。</summary>
    public double WindowTop { get; set; }

    /// <summary>上次关闭时的主窗口宽度（DIP）。</summary>
    public double WindowWidth { get; set; }

    /// <summary>上次关闭时的主窗口高度（DIP）。</summary>
    public double WindowHeight { get; set; }

    /// <summary>虚拟屏窗口上次关闭时的宽度（DIP，0 = 还没记录过）。竖屏与横屏各自记一份。</summary>
    public double MirrorWidth { get; set; }

    /// <summary>竖屏虚拟屏窗口记忆（DIP，0 = 还没记录过）。</summary>
    public double MirrorPortraitLeft { get; set; }

    /// <summary>竖屏虚拟屏窗口的上边坐标（DIP）。</summary>
    public double MirrorPortraitTop { get; set; }

    /// <summary>竖屏虚拟屏窗口的宽度（DIP）。</summary>
    public double MirrorPortraitWidth { get; set; }

    /// <summary>竖屏虚拟屏窗口的高度（DIP）。</summary>
    public double MirrorPortraitHeight { get; set; }

    /// <summary>横屏虚拟屏窗口记忆（DIP，0 = 还没记录过）。</summary>
    public double MirrorLandscapeLeft { get; set; }

    /// <summary>横屏虚拟屏窗口的上边坐标（DIP）。</summary>
    public double MirrorLandscapeTop { get; set; }

    /// <summary>横屏虚拟屏窗口的宽度（DIP）。</summary>
    public double MirrorLandscapeWidth { get; set; }

    /// <summary>横屏虚拟屏窗口的高度（DIP）。</summary>
    public double MirrorLandscapeHeight { get; set; }

    /// <summary>是否已经记录过主窗口位置。</summary>
    [JsonIgnore]
    public bool HasWindowPlacement => WindowWidth > 200 && WindowHeight > 200;

    /// <summary>是否已经记录过竖屏虚拟屏窗口的位置与大小。</summary>
    [JsonIgnore]
    public bool HasMirrorPortrait => MirrorPortraitWidth > 200 && MirrorPortraitHeight > 200;

    /// <summary>是否已经记录过横屏虚拟屏窗口的位置与大小。</summary>
    [JsonIgnore]
    public bool HasMirrorLandscape => MirrorLandscapeWidth > 200 && MirrorLandscapeHeight > 200;
    /// <summary>目标帧率（30 / 60）。</summary>
    public int FrameRate { get; set; } = 60;

    /// <summary>目标码率（Mbps）。</summary>
    public int BitRateMbps { get; set; } = 8;

    /// <summary>
    /// 画质百分比：100 = 手机原分辨率（默认），75 / 50 / 30 依次降采样。
    /// 降画质能明显减轻解码压力、提高实际帧率，是「帧率上不去」最有效的一档。
    /// </summary>
    public int QualityPercent { get; set; } = 100;

    /// <summary>连接后自动开启音频接收。</summary>
    public bool AutoStartAudio { get; set; }

    /// <summary>关闭主窗口时最小化到托盘，而不是退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// 音量以手机为主：开启时本应用的电脑侧音量固定 100%，音量完全由手机侧决定（推荐）。
    /// 关掉才使用 <see cref="AudioAppVolume"/>。
    /// </summary>
    public bool AudioFollowPhone { get; set; } = true;

    /// <summary>电脑侧本应用音量（0~1），仅在 <see cref="AudioFollowPhone"/> 关闭时生效。</summary>
    public double AudioAppVolume { get; set; } = 1.0;

    /// <summary>
    /// 音频接收子进程的输出设备端点 ID；空串 = 跟随系统默认输出。
    /// 只影响 <c>HdcMirror.Audio.exe</c> 这一个进程，不修改系统默认设备。
    /// </summary>
    public string AudioOutputDeviceId { get; set; } = "";

    /// <summary>用户自己录的按键映射；空清单 = 不拦截任何按键（不做预设）。</summary>
    public List<KeyBinding> KeyBindings { get; set; } = new();

    /// <summary>是否定时抓锁屏画面当手机屏背景。</summary>
    public bool LockScreenCapture { get; set; } = true;

    /// <summary>锁屏画面抓取间隔（秒），下限 15。</summary>
    public int LockScreenIntervalSeconds { get; set; } = 60;
    [JsonIgnore]
    public int EffectiveFrameRate => FrameRate <= 0 ? 0 : Math.Clamp(FrameRate, 24, 240);
    [JsonIgnore]
    public int EffectiveBitRate
    {
        get
        {
            int mbps = Math.Clamp(BitRateMbps, 1, 64);
            return mbps * 1024 * 1024;
        }
    }


    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string text = File.ReadAllText(FilePath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions);
                if (loaded is not null)
                {
                    _current = loaded;
                    return loaded;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        _current = new AppSettings();
        return _current;
    }
    public void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
