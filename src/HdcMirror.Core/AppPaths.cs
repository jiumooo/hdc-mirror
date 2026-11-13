using System;
using System.IO;

namespace HdcMirror.Core;

/// <summary>
/// 应用数据目录解析。
/// <para>
/// 按用户要求，数据与程序放在一起：<c>&lt;安装根&gt;\data</c>。
/// Velopack 的安装根下是 <c>Update.exe</c> + <c>current\</c> + <c>packages\</c>，
/// 升级只替换 <c>current\</c>，因此把数据放在安装根的 <c>data\</c> 里既能「跟安装目录走」，
/// 又不会被升级覆盖；卸载安装根时会连数据一起清掉（这也是用户要的效果）。
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>数据目录名。</summary>
    public const string FolderName = "HdcMirror";

    /// <summary>安装根目录（<c>current\</c> 的上一级）。</summary>
    public static string InstallRoot { get; } = ResolveInstallRoot();

    /// <summary>数据根目录（<c>&lt;安装根&gt;\data</c>）。</summary>
    public static string DataRoot { get; } = Path.Combine(InstallRoot, "data");

    /// <summary>日志目录。</summary>
    public static string Logs => Path.Combine(DataRoot, "logs");

    /// <summary>设置文件。</summary>
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>记住的设备清单。</summary>
    public static string DevicesFile => Path.Combine(DataRoot, "devices.json");

    /// <summary>端口线索清单。</summary>
    public static string EndpointsFile => Path.Combine(DataRoot, "endpoints.json");

    /// <summary>0.1.x 及更早版本的数据目录（<c>%APPDATA%\HdcMirror</c>），升级后搬一次。</summary>
    public static string LegacyRoamingRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } roaming
            ? roaming
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming"),
        FolderName);

    /// <summary>0.0.1 及更早版本写在安装目录里的数据（<c>%LOCALAPPDATA%\HdcMirror</c>）。</summary>
    public static string LegacyInstallRoot => Path.Combine(
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
    /// 把旧位置的数据搬到 <c>&lt;安装根&gt;\data</c>。只在目标文件不存在时才搬，绝不覆盖用户现有数据；
    /// 旧文件保持原样（由安装/卸载流程自行清理）。
    /// </summary>
    public static void MigrateFromLegacy()
    {
        EnsureExists();

        foreach (string legacy in new[] { LegacyRoamingRoot, LegacyInstallRoot })
        {
            CopyMissing(legacy);
        }
    }

    private static void CopyMissing(string legacyRoot)
    {
        try
        {
            if (string.IsNullOrEmpty(legacyRoot)
                || string.Equals(
                    Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(legacyRoot).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (string name in new[] { "settings.json", "devices.json", "endpoints.json", "lockscreen.png" })
            {
                string source = Path.Combine(legacyRoot, name);
                string target = Path.Combine(DataRoot, name);
                if (File.Exists(source) && !File.Exists(target))
                {
                    File.Copy(source, target, overwrite: false);
                }
            }

            string sourceLogs = Path.Combine(legacyRoot, "logs");
            if (!Directory.Exists(sourceLogs))
            {
                return;
            }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 旧目录不可读就算了，不影响启动。
        }
    }

    /// <summary>
    /// 安装根 = 程序所在目录的上一级。开发机（bin\Debug\...）与正式安装（&lt;root&gt;\current\）都成立。
    /// </summary>
    private static string ResolveInstallRoot()
    {
        string baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
        }

        DirectoryInfo? parent = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return parent?.FullName ?? baseDir.TrimEnd(Path.DirectorySeparatorChar);
    }
}