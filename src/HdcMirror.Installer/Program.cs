using System.Diagnostics;
using System.Reflection;

namespace HdcMirror.Installer;

internal static class Program
{
    private const string DefaultPath = @"C:\HdcMirror";

    [STAThread]
    private static int Main(string[] args)
    {
        // 静默：HdcMirror-Setup.exe --installto "D:\X\HdcMirror" --silent
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--installto" or "-t" && i + 1 < args.Length)
            {
                if (!InstallPathGuard.TryValidate(args[i + 1], english: false, out string dir, out string error))
                {
                    ApplicationConfiguration.Initialize();
                    MessageBox.Show(error, "HdcMirror 安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 4;
                }

                int code = RunInstaller(dir);
                if (code == 0)
                {
                    WriteExtras(dir);
                }

                return code;
            }
        }

        ApplicationConfiguration.Initialize();
        using var form = new SetupForm();
        Application.Run(form);
        return 0;
    }

    /// <summary>解出内嵌的 Velopack 载荷并按 --installto 安装。</summary>
    internal static int RunInstaller(string targetDirectory)
    {
        string directory = NormalizeDirectory(targetDirectory);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            return 1;
        }

        string payload = Extract("payload.setup.exe", "HDCM-payload-");
        var startInfo = new ProcessStartInfo
        {
            FileName = payload,
            Arguments = "--installto \"" + directory + "\" --silent",
            UseShellExecute = false,
        };

        try
        {
            using Process process = Process.Start(startInfo)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>把卸载入口放到安装根目录（Velopack 只放在 current\，用户找不到，更新时也会被替换）。</summary>
    internal static bool WriteExtras(string installRoot)
    {
        try
        {
            string exe = Extract("extras.uninstaller.exe", "HDCM-uninst-exe-");
            File.Copy(exe, Path.Combine(installRoot, UninstallExeName), overwrite: true);
            TryDelete(exe);

            // 清掉旧版本遗留的脚本式卸载入口（脚本曾因安装根判断错误误删用户目录）
            foreach (string legacy in new[] { "卸载 HdcMirror.cmd", "uninstall.ps1" })
            {
                string path = Path.Combine(installRoot, legacy);
                if (File.Exists(path))
                {
                    TryDelete(path);
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal const string UninstallExeName = "卸载 HdcMirror.exe";

    internal static string NormalizeDirectory(string input)
    {
        string path = (input ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
        {
            path = DefaultPath;
        }

        path = Environment.ExpandEnvironmentVariables(path);
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            full = path;
        }

        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(full), "HdcMirror", StringComparison.OrdinalIgnoreCase))
        {
            full = Path.Combine(full, "HdcMirror");
        }

        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件留着无害
        }
    }

    private static string Extract(string resourceName, string prefix)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("安装包缺少内嵌资源：" + resourceName);
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        string path = Path.Combine(Path.GetTempPath(), prefix + Environment.ProcessId + "-" + suffix);
        using FileStream file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    internal static string DefaultInstallDirectory => DefaultPath;
}
