using System;
using System.IO;
using System.Threading;

namespace HdcMirror.Core;

/// <summary>
/// 应用数据目录解析。
/// <para>
/// 数据目录与安装目录**刻意分开**：Velopack 把程序装在 <c>%LOCALAPPDATA%\HdcMirror</c> 下，
/// 卸载/更新会整目录删除或替换；如果数据也放那里，用户的设备清单、设置、日志会被一起清掉。
/// 因此数据固定在 <c>%APPDATA%\HdcMirror</c>（漫游目录，卸载不动）。
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>数据目录名。</summary>
    public const string FolderName = "HdcMirror";

    /// <summary>数据根目录（<c>%APPDATA%\HdcMirror</c>）。</summary>
    public static string DataRoot { get; } = ResolveDataRoot();

    /// <summary>日志目录。</summary>
    public static string Logs => Path.Combine(DataRoot, "logs");

    /// <summary>设置文件。</summary>
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>记住的设备清单。</summary>
    public static string DevicesFile => Path.Combine(DataRoot, "devices.json");

    /// <summary>端口线索清单。</summary>
    public static string EndpointsFile => Path.Combine(DataRoot, "endpoints.json");

    /// <summary>
    /// 旧版数据目录（<c>%LOCALAPPDATA%\HdcMirror</c>）。
    /// 0.0.1 及更早版本把数据写在安装目录里，升级后需要搬一次。
    /// </summary>
    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName);

    /// <summary>确保数据目录与日志目录存在；失败静默（数据不可用不应阻断启动）。</summary>
    public static void EnsureExists()
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(Logs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 把旧版数据搬到新位置。只在目标文件不存在时才搬，绝不覆盖用户现有数据；
    /// 旧文件保持原样（不删），因为旧安装目录由 Velopack 管理，卸载时自然会清掉。
    /// </summary>
    public static void MigrateFromLegacy()
    {
        EnsureExists();

        if (string.Equals(
                Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(LegacyRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (string name in new[] { "settings.json", "devices.json", "endpoints.json" })
        {
            try
            {
                string source = Path.Combine(LegacyRoot, name);
                string target = Path.Combine(DataRoot, name);
                if (File.Exists(source) && !File.Exists(target))
                {
                    File.Copy(source, target, overwrite: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            string sourceLogs = Path.Combine(LegacyRoot, "logs");
            if (Directory.Exists(sourceLogs))
            {
                Directory.CreateDirectory(Logs);
                foreach (string source in Directory.EnumerateFiles(sourceLogs, "*.log"))
                {
                    string target = Path.Combine(Logs, Path.GetFileName(source));
                    if (!File.Exists(target))
                    {
                        File.Copy(source, target, overwrite: false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string ResolveDataRoot()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(roaming))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roaming = Path.Combine(profile, "AppData", "Roaming");
        }

        return Path.Combine(roaming, FolderName);
    }
}
