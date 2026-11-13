using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace HdcMirror.Installer;

/// <summary>安装界面：语言选择 → 用户说明与免责声明 → 安装位置 → 安装。</summary>
internal sealed class SetupForm : Form
{
    private readonly ComboBox _languageBox;
    private readonly Label _titleLabel;
    private readonly Label _descLabel;
    private readonly Label _agreeTitle;
    private readonly RichTextBox _agreeText;
    private readonly CheckBox _agreeBox;
    private readonly Label _pathLabel;
    private string _hintDefault = string.Empty;
    private readonly TextBox _pathBox;
    private readonly Button _browseButton;
    private readonly Label _hintLabel;
    private readonly Button _installButton;
    private readonly Button _cancelButton;

    private bool _english;
    private bool _busy;

    public SetupForm()
    {
        Text = "HdcMirror 安装";
        ClientSize = new Size(620, 604);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.White;

        _titleLabel = new Label
        {
            Text = "鸿蒙手机无线控制 · HdcMirror",
            Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 18),
        };

        _languageBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(470, 24),
            Size = new Size(126, 25),
        };
        _languageBox.Items.AddRange(new object[] { "简体中文", "English" });
        _languageBox.SelectedIndex = 0;
        _languageBox.SelectedIndexChanged += (_, _) =>
        {
            _english = _languageBox.SelectedIndex == 1;
            ApplyLanguage();
        };

        _descLabel = new Label
        {
            AutoSize = false,
            Size = new Size(572, 34),
            Location = new Point(24, 56),
            ForeColor = Color.FromArgb(70, 70, 70),
        };

        _agreeTitle = new Label
        {
            AutoSize = true,
            Location = new Point(24, 96),
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
        };

        _agreeText = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(24, 120),
            Size = new Size(572, 250),
            BackColor = Color.FromArgb(250, 250, 250),
            DetectUrls = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };

        _agreeBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(24, 380),
            Checked = false,
        };
        _agreeBox.CheckedChanged += (_, _) => UpdateInstallEnabled();

        _pathLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 416),
        };

        _pathBox = new TextBox
        {
            Text = Program.DefaultInstallDirectory,
            Location = new Point(92, 412),
            Size = new Size(400, 27),
        };

        _browseButton = new Button
        {
            Text = "浏览…",
            Location = new Point(500, 411),
            Size = new Size(96, 29),
        };
        _browseButton.Click += OnBrowseClick;

        _hintLabel = new Label
        {
            AutoSize = false,
            Size = new Size(572, 36),
            Location = new Point(24, 448),
            ForeColor = Color.FromArgb(120, 120, 120),
        };

        _installButton = new Button
        {
            Text = "安装",
            Location = new Point(400, 544),
            Size = new Size(90, 34),
            Enabled = false,
        };
        _installButton.Click += OnInstallClick;

        _cancelButton = new Button
        {
            Text = "取消",
            Location = new Point(500, 544),
            Size = new Size(96, 34),
            DialogResult = DialogResult.Cancel,
        };

        CancelButton = _cancelButton;

        Controls.AddRange(new Control[]
        {
            _titleLabel, _languageBox, _descLabel, _agreeTitle, _agreeText, _agreeBox,
            _pathLabel, _pathBox, _browseButton, _hintLabel, _installButton, _cancelButton,
        });

        ApplyLanguage();
    }

    private static string ZhText => string.Join(
        Environment.NewLine,
        new[]
        {
            "一、软件性质",
            "HdcMirror 为个人自研的开源 Windows 桌面客户端，通过系统自带的 hdc 无线调试在电脑上镜像并操控鸿蒙手机。本项目与华为技术有限公司及其关联公司无任何隶属、合作或授权关系，不是官方软件。",
            "",
            "二、使用范围",
            "请在你自己拥有、或已获得明确授权的设备上使用。不得用于监控他人、窃取隐私或任何违法用途。",
            "",
            "三、数据处理",
            "本软件不收集、不上传任何个人信息。手机地址、设备名、截图、日志只保存在本机安装目录下的 data 文件夹，卸载时可一并删除。",
            "",
            "四、技术限制与风险",
            "· 依赖手机系统的 hdc 无线调试、uitest 与设备侧投屏服务，不同机型与系统版本可能不兼容，或被系统策略限制；",
            "· 画面是屏幕内容的实时编码流，存在延迟；蓝牙 A2DP 音频延迟约 100~300 ms，不适合对音画同步要求极高的场景；",
            "· 输入注入在某些界面可能表现异常（例如长按、连续拖拽），操作重要内容前请先保存。",
            "",
            "因使用本软件造成的设备异常、数据丢失或其他损失，由使用者自行承担；作者不提供任何明示或暗示的担保。",
            "",
            "五、开源与反馈",
            "源码与问题反馈：https://github.com/jiumooo/hdc-mirror",
            "",
            "点击「安装」即表示你已阅读并接受上述全部内容。",
        });

    private static string EnText => string.Join(
        Environment.NewLine,
        new[]
        {
            "1. About this software",
            "HdcMirror is a self-developed open-source Windows desktop client that mirrors and controls a HarmonyOS phone over the system's built-in hdc wireless debugging. It is not affiliated with, endorsed by, or authorized by Huawei Technologies or its affiliates.",
            string.Empty,
            "2. Intended use",
            "Use it only on devices you own or are explicitly authorized to control. Do not use it to monitor other people, harvest private data, or for any unlawful purpose.",
            string.Empty,
            "3. Data handling",
            "No personal data is collected or uploaded. Phone address, device name, screenshots and logs stay in the data folder inside the install directory and are removed when you uninstall.",
            string.Empty,
            "4. Limitations and risk",
            "- Depends on hdc wireless debugging, uitest and the on-device casting service; compatibility varies by model and OS version and may be restricted by system policy.",
            "- Video is a live encoded stream and has latency; Bluetooth A2DP audio adds roughly 100-300 ms, so it is not suitable where strict A/V sync is required.",
            "- Input injection may behave unexpectedly in some screens (long press, continuous drag). Save your work before operating on anything important.",
            string.Empty,
            "Any device malfunction, data loss or other damage caused by using this software is borne by the user; the author provides no warranty, express or implied.",
            string.Empty,
            "5. Source and feedback",
            "https://github.com/jiumooo/hdc-mirror",
            string.Empty,
            "Clicking Install means you have read and accepted all of the above.",
        });

    private void ApplyLanguage()
    {
        Text = _english ? "HdcMirror Setup" : "HdcMirror 安装";
        _descLabel.Text = _english
            ? "Choose where to install. The program goes into the folder you type, with a HdcMirror subfolder appended automatically."
            : "选择安装位置。程序会装在你填的目录下，并自动补一个 HdcMirror 子文件夹。";
        _agreeTitle.Text = _english ? "User guide, disclaimer and terms" : "用户说明与免责声明";
        _agreeText.Text = _english ? EnText : ZhText;
        _agreeBox.Text = _english
            ? "I have read and accept the notes and disclaimer above"
            : "我已阅读并同意上述说明与免责声明";
        _pathLabel.Text = _english ? "Install to:" : "安装到：";
        _browseButton.Text = _english ? "Browse..." : "浏览…";
        _installButton.Text = _english ? "Install" : "安装";
        _cancelButton.Text = _english ? "Cancel" : "取消";
        _hintDefault = _english
            ? "Settings, device list and logs are kept in the data folder inside the install directory. A standalone uninstaller (卸载 HdcMirror.exe) is placed in the install folder."
            : "设置、设备清单与日志保存在安装目录下的 data 文件夹；安装目录里会放一个独立的「卸载 HdcMirror.exe」。";
        _hintLabel.Text = _hintDefault;
        _hintLabel.ForeColor = SystemColors.GrayText;
        ValidatePathAndHint();
    }

    private void UpdateInstallEnabled()
    {
        bool pathOk = InstallPathGuard.TryValidate(_pathBox.Text, _english, out _, out _);
        _installButton.Enabled = !_busy && _agreeBox.Checked && pathOk;
    }

    /// <summary>路径即时校验：不合格就把原因写在提示行并把「安装」置灰。</summary>
    private void ValidatePathAndHint()
    {
        bool ok = InstallPathGuard.TryValidate(_pathBox.Text, _english, out string normalized, out string error);
        if (ok)
        {
            _hintLabel.ForeColor = SystemColors.GrayText;
            _hintLabel.Text = _hintDefault;
            if (!string.Equals(_pathBox.Text, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _pathPreview = normalized;
            }
        }
        else
        {
            _hintLabel.ForeColor = Color.FromArgb(0xB3, 0x26, 0x1E);
            _hintLabel.Text = error;
            _pathPreview = string.Empty;
        }

        UpdateInstallEnabled();
    }

    private string _pathPreview = string.Empty;

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = _english
                ? "Choose where to install (a HdcMirror subfolder is appended)"
                : "选择安装位置（会自动补 HdcMirror 子文件夹）",
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
        if (_busy)
        {
            return;
        }

        if (!InstallPathGuard.TryValidate(_pathBox.Text, _english, out string target, out string pathError))
        {
            _hintLabel.ForeColor = Color.FromArgb(0xB3, 0x26, 0x1E);
            _hintLabel.Text = pathError;
            UpdateInstallEnabled();
            return;
        }

        _pathBox.Text = target;
        _busy = true;
        _pathBox.Enabled = false;
        _browseButton.Enabled = false;
        _agreeBox.Enabled = false;
        _languageBox.Enabled = false;
        _hintLabel.Text = (_english ? "Installing to " : "正在安装到 ") + target +
                          (_english ? " ..." : " …（首次会检查 .NET 8 桌面运行时）");
        Cursor = Cursors.WaitCursor;
        UpdateInstallEnabled();
        Refresh();

        int exitCode = Program.RunInstaller(target);
        bool extras = exitCode == 0 && Program.WriteExtras(target);
        Cursor = Cursors.Default;
        _busy = false;

        if (exitCode != 0)
        {
            _pathBox.Enabled = true;
            _browseButton.Enabled = true;
            _agreeBox.Enabled = true;
            _languageBox.Enabled = true;
            _hintLabel.Text = _english
                ? "Install did not finish (exit code " + exitCode + "). Try another folder."
                : "安装未完成（返回码 " + exitCode + "），可换一个目录再试。";
            UpdateInstallEnabled();
            return;
        }

        string exe = Path.Combine(target, "current", "HdcMirror.exe");
        if (!File.Exists(exe))
        {
            _hintLabel.Text = _english
                ? "Setup finished but HdcMirror.exe is missing in the target folder."
                : "安装程序已结束，但目标目录里没有 HdcMirror.exe，请检查目录。";
            _pathBox.Enabled = true;
            _browseButton.Enabled = true;
            _agreeBox.Enabled = true;
            _languageBox.Enabled = true;
            UpdateInstallEnabled();
            return;
        }

        string body = _english
            ? "Installation complete." + Environment.NewLine + Environment.NewLine +
              "Program folder: " + target + Environment.NewLine +
              "Data folder: " + Path.Combine(target, "data") + Environment.NewLine +
              "Uninstaller: " + Path.Combine(target, Program.UninstallExeName) +
              (extras ? string.Empty : " (could not be copied)") + Environment.NewLine + Environment.NewLine +
              "Start HdcMirror now?"
            : "安装完成。" + Environment.NewLine + Environment.NewLine +
              "程序目录：" + target + Environment.NewLine +
              "数据目录：" + Path.Combine(target, "data") + Environment.NewLine +
              "卸载入口：" + Path.Combine(target, Program.UninstallExeName) +
              (extras ? string.Empty : "（复制失败）") + Environment.NewLine + Environment.NewLine +
              "现在启动 HdcMirror？";

        DialogResult launch = MessageBox.Show(
            this,
            body,
            _english ? "HdcMirror Setup" : "HdcMirror 安装",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (launch == DialogResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = target });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, _english ? "Launch failed" : "启动失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        Close();
    }
}
