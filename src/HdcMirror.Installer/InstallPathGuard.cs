using System.Text;

namespace HdcMirror.Installer;

/// <summary>
/// 安装路径校验：安装和卸载必须用同一套规矩 —— 卸载拒绝乱删，安装也必须拒绝往系统目录、
/// 用户数据目录、临时目录、以及非空的陌生目录里塞文件。
/// </summary>
internal static class InstallPathGuard
{
    internal const int MaxPathLength = 170;

    internal static bool TryValidate(string? input, bool english, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        string raw = (input ?? string.Empty).Trim().Trim('"');
        if (raw.Length == 0)
        {
            error = english ? "Please enter an install location." : "请填写安装位置。";
            return false;
        }

        if (raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = english ? "The path contains characters that are not allowed." : "路径里含有不允许的字符。";
            return false;
        }

        if (!Path.IsPathRooted(raw))
        {
            error = english
                ? @"Enter a full path, for example C:\HdcMirror"
                : @"请填写完整路径，例如 C:\HdcMirror";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            error = english ? "The path is not valid." : "路径无效。";
            return false;
        }

        string leaf = Path.GetFileName(full);
        if (!string.Equals(leaf, "HdcMirror", StringComparison.OrdinalIgnoreCase))
        {
            full = Path.Combine(full, "HdcMirror");
            leaf = "HdcMirror";
        }

        normalized = full;

        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            error = english
                ? "Pick a folder, not a drive root."
                : "不能直接装到磁盘根目录，请选一个文件夹。";
            return false;
        }

        if (full.Length > MaxPathLength)
        {
            error = (english ? "The path is too long (limit " : "路径太长（上限 ") + MaxPathLength + ").";
            return false;
        }

        foreach ((string bad, string label) in ForbiddenRoots())
        {
            if (bad.Length > 0 && IsSameOrInside(full, bad))
            {
                error = english
                    ? "That location is reserved for system or application data. Choose another folder."
                    : "这个位置属于系统目录或应用数据目录，请换一个位置。";
                return false;
            }
        }

        if (Directory.Exists(full))
        {
            bool empty;
            try
            {
                empty = !Directory.EnumerateFileSystemEntries(full).Any();
            }
            catch (Exception)
            {
                error = english ? "Cannot read that folder." : "无法读取该目录。";
                return false;
            }

            if (!empty && !LooksLikeOurInstall(full))
            {
                error = english
                    ? "That folder already contains other files and is not an existing HdcMirror install."
                    : "目标目录里已经有别的文件，而且它不是已有的 HdcMirror 安装目录。";
                return false;
            }
        }

        if (!CanWrite(full, out string writeError))
        {
            error = english
                ? "No write permission there (" + writeError + "). Pick another folder."
                : "该位置没有写入权限（" + writeError + "），请换一个目录。";
            return false;
        }

        return true;
    }

    internal static bool LooksLikeOurInstall(string dir) =>
        File.Exists(Path.Combine(dir, "Update.exe")) ||
        File.Exists(Path.Combine(dir, "current", "HdcMirror.exe")) ||
        Directory.Exists(Path.Combine(dir, "current"));

    private static IEnumerable<(string Path, string Label)> ForbiddenRoots()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Windows");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.System), "System32");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAppData");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UserProfile");
        yield return (Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "Temp");
    }

    private static bool IsSameOrInside(string path, string parent)
    {
        string p = path.TrimEnd(Path.DirectorySeparatorChar);
        string q = parent.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(p, q, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return p.StartsWith(q + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanWrite(string dir, out string error)
    {
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".hdcm-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "probe", Encoding.ASCII);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            return false;
        }
    }
}
