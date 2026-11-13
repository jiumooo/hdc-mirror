using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdcMirror.Uninstaller;

/// <summary>卸载进度窗口：全程无控制台，步骤与结果都写在这一个窗口里。</summary>
internal sealed class ProgressForm : Form
{
    private readonly string _root;
    private readonly Label _statusLabel;
    private readonly ProgressBar _bar;
    private readonly TextBox _log;
    private readonly Button _closeButton;

    public bool Failed { get; private set; }

    public ProgressForm(string root)
    {
        _root = root;

        Text = "卸载 HdcMirror";
        ClientSize = new Size(560, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.White;

        _statusLabel = new Label
        {
            Text = "正在卸载…",
            AutoSize = false,
            Size = new Size(520, 24),
            Location = new Point(20, 18),
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
        };

        _bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 28,
            Location = new Point(20, 48),
            Size = new Size(520, 12),
        };

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(20, 72),
            Size = new Size(520, 250),
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Consolas", 9f),
        };

        _closeButton = new Button
        {
            Text = "关闭",
            Location = new Point(440, 334),
            Size = new Size(100, 32),
            Enabled = false,
        };
        _closeButton.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { _statusLabel, _bar, _log, _closeButton });

        Shown += (_, _) => RunAsync();
    }

    private void RunAsync()
    {
        var thread = new Thread(Work)
        {
            IsBackground = true,
        };
        thread.Start();
    }

    private void Work()
    {
        try
        {
            Step("① 结束正在运行的程序…");
            KillProcesses();

            Step("② 调起 Velopack 卸载器…");
            string updateExe = Path.Combine(_root, "Update.exe");
            if (File.Exists(updateExe))
            {
                try
                {
                    using Process process = Process.Start(new ProcessStartInfo
                    {
                        FileName = updateExe,
                        Arguments = "--uninstall --silent",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    })!;
                    if (!process.WaitForExit(60000))
                    {
                        Note("   Velopack 卸载器 60 秒未结束，继续按目录清理");
                    }
                }
                catch (Exception ex)
                {
                    Note("   调起卸载器失败：" + ex.Message);
                }
            }
            else
            {
                Note("   没有 Update.exe，直接按目录清理");
            }

            Step("③ 等待 Velopack 完成目录清理（最多 60 秒）…");
            for (int i = 1; i <= 60; i++)
            {
                if (!Directory.Exists(_root))
                {
                    break;
                }

                Thread.Sleep(1000);
            }

            if (Directory.Exists(_root))
            {
                Step("④ 清理安装目录…");
                DeleteInstallDirectory();
            }
            else
            {
                Note("   目录已由 Velopack 删除");
            }

            Step("⑤ 清理注册表…");
            DeleteRegistry();

            Step("⑥ 清理快捷方式…");
            DeleteShortcuts();

            if (Directory.Exists(_root))
            {
                string[] left = Directory.GetFileSystemEntries(_root);
                Note("   ! 目录仍在，剩余 " + left.Length + " 项：");
                foreach (string item in left)
                {
                    Note("     " + Path.GetFileName(item));
                }

                Failed = true;
                Finish("卸载未完全完成，请按上面列出的内容手工删除：" + _root, closeEnabled: true);
            }
            else
            {
                Finish("卸载完成，安装目录与数据已删除。", closeEnabled: true);
            }
        }
        catch (Exception ex)
        {
            Failed = true;
            Finish("卸载过程中出错：" + ex.Message, closeEnabled: true);
        }
    }

    /// <summary>只删安装目录内部，绝不向上递归；目录名不是 HdcMirror 就直接放弃。</summary>
    private void DeleteInstallDirectory()
    {
        if (!string.Equals(Path.GetFileName(_root), "HdcMirror", StringComparison.OrdinalIgnoreCase))
        {
            Note("   ! 目录名不是 HdcMirror，跳过删除");
            return;
        }

        string[] knownChildren = { "current", "packages", "data" };
        foreach (string child in knownChildren)
        {
            string path = Path.Combine(_root, child);
            if (Directory.Exists(path))
            {
                TryDeleteDirectory(path, child);
            }
        }

        foreach (string file in Directory.GetFiles(_root))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("HdcMirror.exe", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("HdcMirror-Uninstall", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("uninstall.ps1", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // 稍后重试
                }
            }
        }

        for (int attempt = 1; attempt <= 10; attempt++)
        {
            KillProcesses();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception)
            {
                // 继续重试
            }

            if (!Directory.Exists(_root))
            {
                Note("   已删除安装目录");
                return;
            }

            Note("   目录仍被占用，" + attempt + "/10 次重试…");
            Thread.Sleep(700);
        }
    }

    private void TryDeleteDirectory(string path, string label)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            Note("   已删 " + label);
        }
        catch (Exception ex)
        {
            Note("   删除 " + label + " 失败：" + ex.Message);
        }
    }

    private void KillProcesses()
    {
        foreach (string name in new[] { "HdcMirror", "HdcMirror.Audio" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill();
                    Note("   结束进程 " + name + " (" + process.Id + ")");
                    process.WaitForExit(3000);
                }
                catch (Exception)
                {
                    // 已经退出
                }
            }
        }
    }

    private void DeleteRegistry()
    {
        string[] keys =
        {
            @"Software\HdcMirror",
            @"Software\Classes\HdcMirror",
            UninstallKeyPath,
        };

        foreach (string key in keys)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                Note("   已清理 HKCU\\" + key);
            }
            catch (Exception ex)
            {
                Note("   清理 " + key + " 失败：" + ex.Message);
            }
        }
    }

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HdcMirror";

    private void DeleteShortcuts()
    {
        string[] shortcuts =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "HdcMirror.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "HdcMirror.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "HdcMirror.lnk"),
        };

        foreach (string shortcut in shortcuts)
        {
            try
            {
                if (File.Exists(shortcut))
                {
                    File.Delete(shortcut);
                    Note("   已删快捷方式 " + shortcut);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    private void Step(string text)
    {
        Report(() => _statusLabel.Text = text);
        Note(text);
    }

    private void Note(string text)
    {
        Report(() => _log.AppendText(text + Environment.NewLine));
    }

    private void Finish(string text, bool closeEnabled)
    {
        Report(() =>
        {
            _statusLabel.Text = text;
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = 100;
            _bar.MarqueeAnimationSpeed = 0;
            _closeButton.Enabled = closeEnabled;
            _closeButton.Focus();
        });
    }

    private void Report(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                Invoke(action);
            }
            catch (Exception)
            {
                // 窗口已关闭
            }
        }
        else
        {
            action();
        }
    }
}
