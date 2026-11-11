using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HdcMirror.Core.Settings;

/// <summary>
/// <see cref="AppSettings"/> 的本地 JSON 存取。
/// 落盘位置固定为 <c>%LOCALAPPDATA%\HdcMirror\settings.json</c>（由 <see cref="Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)"/>
/// 解析，绝不写死用户目录）；读取失败一律回退到 <see cref="AppSettings.Default"/>，不让配置问题演变成启动崩溃。
/// </summary>
public sealed class AppSettingsStore
{
    /// <summary>配置目录名（位于 LOCALAPPDATA 下）。</summary>
    public const string DirectoryName = "HdcMirror";

    /// <summary>配置文件名。</summary>
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>创建设置存取器。</summary>
    /// <param name="directoryPath">配置目录；为 <see langword="null"/> 时使用 LOCALAPPDATA 下的默认目录（测试可注入临时目录）。</param>
    public AppSettingsStore(string? directoryPath = null)
    {
        DirectoryPath = string.IsNullOrWhiteSpace(directoryPath)
            ? Path.Combine(GetLocalAppDataRoot(), DirectoryName)
            : directoryPath;

        SettingsFilePath = Path.Combine(DirectoryPath, FileName);
    }

    /// <summary>配置目录的绝对路径。</summary>
    public string DirectoryPath { get; }

    /// <summary>配置文件的绝对路径。</summary>
    public string SettingsFilePath { get; }

    /// <summary>读取设置；文件缺失、内容损坏或无权访问时返回默认值。</summary>
    /// <returns>反序列化得到的设置，或 <see cref="AppSettings.Default"/>。</returns>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return AppSettings.Default;
            }

            string json = File.ReadAllText(SettingsFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return AppSettings.Default;
            }

            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? AppSettings.Default;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return AppSettings.Default;
        }
    }

    /// <summary>写入设置（先写临时文件再替换，避免写到一半断电留下半截 JSON）。</summary>
    /// <param name="settings">要保存的设置。</param>
    /// <returns>成功为 <see langword="true"/>；磁盘只读、无权限等失败情形返回 <see langword="false"/> 而不抛出。</returns>
    public bool TrySave(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            Directory.CreateDirectory(DirectoryPath);

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            string tempPath = SettingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return false;
        }
    }

    private static string GetLocalAppDataRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(localAppData) ? AppContext.BaseDirectory : localAppData;
    }

    /// <summary>可恢复的配置读写异常：磁盘/权限/JSON 语法，均不应让进程失败。</summary>
    private static bool IsRecoverable(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or JsonException
            or NotSupportedException
            or ArgumentException;
}
