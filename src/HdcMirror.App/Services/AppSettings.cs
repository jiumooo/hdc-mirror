using System;
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

    /// <summary>当前生效的设置（首次访问时从磁盘加载）。</summary>
    public static AppSettings Current => _current ??= Load();

    /// <summary>配置文件路径。</summary>
    public static string FilePath => HdcMirror.Core.AppPaths.SettingsFile;

    /// <summary>采集帧率上限，取值 30 或 60。</summary>
    public int FrameRate { get; set; } = 60;

    /// <summary>码率，单位 Mbps。</summary>
    public int BitRateMbps { get; set; } = 8;


    /// <summary>连接设备后自动开启音频。</summary>
    public bool AutoStartAudio { get; set; }

    /// <summary>窗口关闭时最小化到托盘，而不是退出程序。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>采集帧率（已校验到合法区间）。</summary>
    [JsonIgnore]
    public int EffectiveFrameRate => FrameRate >= 60 ? 60 : 30;

    /// <summary>码率（字节/秒，已校验到合法区间）。</summary>
    [JsonIgnore]
    public int EffectiveBitRate
    {
        get
        {
            int mbps = Math.Clamp(BitRateMbps, 1, 64);
            return mbps * 1024 * 1024;
        }
    }



    /// <summary>从磁盘加载；文件不存在或损坏时返回默认值。</summary>
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

    /// <summary>把当前值写回磁盘。</summary>
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
