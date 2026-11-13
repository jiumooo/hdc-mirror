using System.Diagnostics;
using System.Reflection;

namespace HdcMirror.Installer;

internal static class Program
{
    private const string DefaultPath = @"C:\HdcMirror";

    [STAThread]
    private static int Main(string[] args)
    {
        // 支持静默：HdcMirror-Setup.exe --installto D:\Programs\HdcMirror --silent
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--installto" or "-t" && i + 1 < args.Length)
            {
                return RunInstaller(args[i + 1], silent: true);
            }
        }

        ApplicationConfiguration.Initialize();
        using var form = new SetupForm();
        Application.Run(form);
        return 0;
    }

    /// <summary>解出内嵌的安装包并调用它（Velopack 的 --installto 指定目标目录）。</summary>
    internal static int RunInstaller(string targetDirectory, bool silent)
    {
        string directory = NormalizeDirectory(targetDirectory);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show(
                    "无法创建安装目录：" + directory + Environment.NewLine + ex.Message,
                    "HdcMirror 安装",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 1;
        }

        string payload = ExtractPayload();
        var startInfo = new ProcessStartInfo
        {
            FileName = payload,
            Arguments = "--installto \"" + directory + "\" --silent",
            UseShellExecute = false,
            RedirectStandardOutput = false,
            CreateNoWindow = false,
        };

        try
        {
            using Process process = Process.Start(startInfo)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show(
                    "调用安装程序失败：" + ex.Message,
                    "HdcMirror 安装",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 1;
        }
    }

    /// <summary>用户选 C:\Program Files 时补 HdcMirror；已经选了 HdcMirror 目录就原样用。</summary>
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

    private static string ExtractPayload()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream("payload.setup.exe")
            ?? throw new InvalidOperationException("安装包内没有找到安装载荷（payload.setup.exe）。");
        string path = Path.Combine(Path.GetTempPath(), "HdcMirror-payload-" + Environment.ProcessId + ".exe");
        using FileStream file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    internal static string DefaultInstallDirectory => DefaultPath;
}

/// <summary>安装位置选择窗口：默认 C:\HdcMirror，可直接改盘或浏览选择。</summary>
internal sealed class SetupForm : Form
{
    private readonly TextBox _pathBox;
    private readonly Label _hint;
    private readonly Button _installButton;
    private readonly Button _browseButton;

    public SetupForm()
    {
        Text = "HdcMirror 安装";
        ClientSize = new Size(540, 236);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.White;

        var title = new Label
        {
            Text = "鸿蒙手机无线控制 · HdcMirror",
            Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20),
        };

        var desc = new Label
        {
            Text = "选择安装位置。程序会装在你填的目录下，并自动补一个 HdcMirror 子文件夹。",
            AutoSize = false,
            Size = new Size(492, 36),
            Location = new Point(24, 56),
            ForeColor = Color.FromArgb(70, 70, 70),
        };

        var label = new Label
        {
            Text = "安装到：",
            AutoSize = true,
            Location = new Point(24, 104),
        };

        _pathBox = new TextBox
        {
            Text = Program.DefaultInstallDirectory,
            Location = new Point(92, 100),
            Size = new Size(330, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _browseButton = new Button
        {
            Text = "浏览…",
            Location = new Point(430, 99),
            Size = new Size(86, 29),
        };
        _browseButton.Click += OnBrowseClick;

        _hint = new Label
        {
            Text = "数据（设置、设备清单、日志）会保存在安装目录下的 data 文件夹里。",
            AutoSize = false,
            Size = new Size(492, 20),
            Location = new Point(24, 140),
            ForeColor = Color.FromArgb(120, 120, 120),
        };

        _installButton = new Button
        {
            Text = "安装",
            Location = new Point(352, 176),
            Size = new Size(78, 32),
        };
        _installButton.Click += OnInstallClick;

        var cancelButton = new Button
        {
            Text = "取消",
            Location = new Point(438, 176),
            Size = new Size(78, 32),
            DialogResult = DialogResult.Cancel,
        };

        AcceptButton = _installButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[] { title, desc, label, _pathBox, _browseButton, _hint, _installButton, cancelButton });
    }

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择安装位置（会自动补 HdcMirror 子文件夹）",
            ShowNewFolderButton = true,
        };
        if (Directory.Exists(_pathBox.Text))
        {
            dialog.SelectedPath = _pathBox.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dialog.SelectedPath;
        }
    }

    private void OnInstallClick(object? sender, EventArgs e)
    {
        string target = Program.NormalizeDirectory(_pathBox.Text);
        _pathBox.Text = target;
        _installButton.Enabled = false;
        _browseButton.Enabled = false;
        _hint.Text = "正在安装到 " + target + " …（首次会检查 .NET 8 桌面运行时）";
        Cursor = Cursors.WaitCursor;
        Refresh();

        int exitCode = Program.RunInstaller(target, silent: false);
        Cursor = Cursors.Default;

        if (exitCode != 0)
        {
            _installButton.Enabled = true;
            _browseButton.Enabled = true;
            _hint.Text = "安装未完成（返回码 " + exitCode + "），可换一个目录再试。";
            return;
        }

        string exe = Path.Combine(target, "current", "HdcMirror.exe");
        if (!File.Exists(exe))
        {
            _hint.Text = "安装程序已结束，但目标目录里没有 HdcMirror.exe，请检查目录。";
            _installButton.Enabled = true;
            _browseButton.Enabled = true;
            return;
        }

        DialogResult launch = MessageBox.Show(
            this,
            "安装完成。" + Environment.NewLine + Environment.NewLine +
            "程序目录：" + target + Environment.NewLine +
            "数据目录：" + Path.Combine(target, "data") + Environment.NewLine + Environment.NewLine +
            "现在启动 HdcMirror？",
            "HdcMirror 安装",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (launch == DialogResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "启动失败：" + ex.Message, "HdcMirror 安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        Close();
    }
}