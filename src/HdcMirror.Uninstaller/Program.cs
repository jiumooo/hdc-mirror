using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdcMirror.Uninstaller;

internal static class Program
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HdcMirror";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? explicitRoot = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--root" or "-r" && i + 1 < args.Length)
            {
                explicitRoot = args[i + 1];
            }
        }

        string? root = ResolveInstallRoot(explicitRoot);
        if (root is null)
        {
            MessageBox.Show(
                "没有找到 HdcMirror 的安装目录。\n\n请把本程序放到 HdcMirror 安装目录里，再双击运行。",
                "卸载 HdcMirror",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 2;
        }

        // 安全闸：目录必须真的是一次 HdcMirror 安装，否则一律拒绝，绝不递归删父目录
        if (!IsSafeInstallRoot(root, out string reason))
        {
            MessageBox.Show(
                "安全检查未通过，已停止卸载：\n\n" + reason + "\n\n目标目录：" + root,
                "卸载 HdcMirror",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 3;
        }

        DialogResult confirm = MessageBox.Show(
            "确定要卸载 HdcMirror 吗？\n\n安装目录：" + root +
            "\n\n将删除：\n· 程序本体与桌面 / 开始菜单快捷方式\n· 安装目录下的全部内容（含 data 里的设备清单、设置、日志）\n· 注册表项 HKCU\\Software\\HdcMirror\n\n卸载后不可恢复。",
            "卸载 HdcMirror",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.Yes)
        {
            return 0;
        }

        using var progress = new ProgressForm(root);
        progress.ShowDialog();
        return progress.Failed ? 1 : 0;
    }

    internal static string? ResolveInstallRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Normalize(explicitRoot);
        }

        // ① 自己所在目录（安装时本程序被放在安装根）
        string self = Normalize(AppContext.BaseDirectory);
        if (LooksLikeInstallRoot(self))
        {
            return self;
        }

        // ② 注册表里记录的安装位置
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string location && location.Length > 0)
            {
                string candidate = Normalize(location);
                if (LooksLikeInstallRoot(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            // 读不到就继续往下
        }

        return null;
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }

    private static bool LooksLikeInstallRoot(string dir) =>
        File.Exists(Path.Combine(dir, "Update.exe")) &&
        (File.Exists(Path.Combine(dir, "HdcMirror.exe")) ||
         File.Exists(Path.Combine(dir, "current", "HdcMirror.exe")));

    /// <summary>硬闸：必须是 ...\HdcMirror 这样的一层目录，且里面确实是我们的安装，绝不允许删到驱动器根或用户目录。</summary>
    internal static bool IsSafeInstallRoot(string dir, out string reason)
    {
        reason = string.Empty;

        if (!Directory.Exists(dir))
        {
            reason = "目录不存在。";
            return false;
        }

        string name = Path.GetFileName(dir);
        if (!string.Equals(name, "HdcMirror", StringComparison.OrdinalIgnoreCase))
        {
            reason = "目录名不是 HdcMirror（当前为 \"" + name + "\"）。";
            return false;
        }

        // 只拒绝「本身就是驱动器根」，允许装在根目录下的 HdcMirror 文件夹（默认就是 C:\HdcMirror）
        string? dirRoot = Path.GetPathRoot(dir);
        if (!string.IsNullOrEmpty(dirRoot) &&
            string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), dirRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            reason = "目标是驱动器根，不是 HdcMirror 安装目录。";
            return false;
        }

        if (!File.Exists(Path.Combine(dir, "Update.exe")))
        {
            reason = "目录里没有 Update.exe，不像是一次 HdcMirror 安装。";
            return false;
        }

        if (!File.Exists(Path.Combine(dir, "HdcMirror.exe")) &&
            !File.Exists(Path.Combine(dir, "current", "HdcMirror.exe")))
        {
            reason = "目录里没有 HdcMirror.exe，不像是一次 HdcMirror 安装。";
            return false;
        }

        // 明确拒绝用户目录与系统目录
        string[] forbidden =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
        };

        foreach (string bad in forbidden)
        {
            if (bad.Length > 0 && string.Equals(dir, bad, StringComparison.OrdinalIgnoreCase))
            {
                reason = "目标目录是系统或用户目录（" + bad + "），拒绝卸载。";
                return false;
            }
        }

        return true;
    }
}
