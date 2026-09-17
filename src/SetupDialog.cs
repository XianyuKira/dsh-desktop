using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace DshDesktop
{
    /// <summary>
    /// First-run questions for a packaged install: where runtime data should live, and the
    /// DeepSeek API key dsh needs. Both answers are stored, so this appears exactly once.
    ///
    /// The data folder matters because it defaults to %LOCALAPPDATA% and can run to hundreds of
    /// megabytes; a machine with a small system drive needs to be able to put it elsewhere.
    /// </summary>
    internal sealed class SetupDialog : Form
    {
        private readonly AppConfig _config;
        private readonly TextBox _dataRoot;
        private readonly TextBox _apiKey;
        private readonly CheckBox _autoStart;
        private readonly Label _status;

        public SetupDialog(AppConfig config)
        {
            _config = config;

            Text = "首次使用设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 388);
            BackColor = Color.FromArgb(250, 250, 252);
            Font = new Font("Microsoft YaHei UI", 9f);

            var title = new Label
            {
                Text = "还差两步就能用了",
                Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
                ForeColor = Color.FromArgb(32, 32, 40),
                AutoSize = true,
                Location = new Point(24, 20),
            };

            var intro = new Label
            {
                Text = "程序、Node 运行时、dsh 和常用插件都已经随包安装好了，不需要另外装任何东西。\n"
                     + "下面两项填一次即可。",
                ForeColor = Color.FromArgb(90, 90, 100),
                AutoSize = false,
                Size = new Size(570, 40),
                Location = new Point(26, 56),
            };

            // ---- data folder ----
            var dataLabel = new Label
            {
                Text = "数据目录（会话、缓存、日志）",
                ForeColor = Color.FromArgb(50, 50, 60),
                AutoSize = true,
                Location = new Point(26, 108),
            };

            _dataRoot = new TextBox
            {
                Text = config.DataDirectory,
                Location = new Point(28, 130),
                Size = new Size(470, 26),
            };

            var browse = new Button
            {
                Text = "浏览…",
                Location = new Point(506, 129),
                Size = new Size(92, 28),
            };
            browse.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择数据目录（建议放在空间较大的盘）";
                    dialog.SelectedPath = _dataRoot.Text;
                    dialog.ShowNewFolderButton = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK) _dataRoot.Text = dialog.SelectedPath;
                }
            };

            var dataHint = new Label
            {
                Text = "默认在 C 盘用户目录下。C 盘紧张就改到其它盘，之后所有数据都写在那里。",
                ForeColor = Color.FromArgb(140, 140, 150),
                AutoSize = false,
                Size = new Size(570, 20),
                Location = new Point(28, 160),
            };

            // ---- api key ----
            var keyLabel = new Label
            {
                Text = "DeepSeek API Key",
                ForeColor = Color.FromArgb(50, 50, 60),
                AutoSize = true,
                Location = new Point(26, 192),
            };

            _apiKey = new TextBox
            {
                Location = new Point(28, 214),
                Size = new Size(470, 26),
                UseSystemPasswordChar = true,
                PlaceholderText = "sk-…",
            };

            var showKey = new CheckBox
            {
                Text = "显示",
                Location = new Point(506, 215),
                Size = new Size(92, 24),
            };
            showKey.CheckedChanged += (s, e) => _apiKey.UseSystemPasswordChar = !showKey.Checked;

            var keyHint = new Label
            {
                Text = "在 platform.deepseek.com 创建。只保存在本机数据目录里，不会上传到别处。",
                ForeColor = Color.FromArgb(140, 140, 150),
                AutoSize = false,
                Size = new Size(570, 20),
                Location = new Point(28, 244),
            };

            _autoStart = new CheckBox
            {
                Text = "开机时自动启动",
                Location = new Point(28, 272),
                Size = new Size(200, 24),
                Checked = false,
            };

            _status = new Label
            {
                Text = string.Empty,
                ForeColor = Color.FromArgb(200, 110, 90),
                AutoSize = false,
                Size = new Size(570, 22),
                Location = new Point(28, 300),
            };

            var ok = new Button
            {
                Text = "完成设置并启动",
                Location = new Point(390, 334),
                Size = new Size(130, 34),
            };
            ok.Click += (s, e) => Finish();

            var cancel = new Button
            {
                Text = "稍后再说",
                Location = new Point(528, 334),
                Size = new Size(70, 34),
                DialogResult = DialogResult.Cancel,
            };

            Controls.AddRange(new Control[]
            {
                title, intro, dataLabel, _dataRoot, browse, dataHint,
                keyLabel, _apiKey, showKey, keyHint, _autoStart, _status, ok, cancel,
            });

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>True when the user completed setup and the launcher can start dsh.</summary>
        public bool Completed { get; private set; }

        private void Finish()
        {
            var dataRoot = (_dataRoot.Text ?? string.Empty).Trim();
            var key = (_apiKey.Text ?? string.Empty).Trim();

            if (dataRoot.Length == 0)
            {
                _status.Text = "请填写数据目录。";
                return;
            }
            if (!string.IsNullOrEmpty(key) && !key.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
            {
                // Warn but allow: keys may take other shapes, and blocking here would be worse.
                _status.Text = "提醒：DeepSeek 的 key 通常以 sk- 开头，请确认没贴错。";
            }
            if (string.IsNullOrEmpty(key))
            {
                _status.Text = "请填写 API Key（否则 dsh 无法连接模型）。";
                return;
            }

            try
            {
                Directory.CreateDirectory(dataRoot);
            }
            catch (Exception ex)
            {
                _status.Text = "这个目录不能用：" + ex.Message;
                return;
            }

            try
            {
                _config.DataRoot = dataRoot;
                Directory.CreateDirectory(_config.DataDirectory);
                Directory.CreateDirectory(_config.LogDirectory);

                // dsh keeps its credentials next to DSH_HOME.
                var dshHome = _config.DshHome ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                Directory.CreateDirectory(dshHome);
                WriteCredential(dshHome, key);

                _config.SetupComplete = true;
                _config.Save();

                ApplyAutoStart(_autoStart.Checked);
                Completed = true;
                Close();
            }
            catch (Exception ex)
            {
                _status.Text = "保存失败：" + ex.Message;
            }
        }

        /// <summary>
        /// Writes the credential record dsh reads. The file also holds its own bookkeeping
        /// version, so the shape is kept minimal and dsh fills in the rest on first use.
        /// </summary>
        private static void WriteCredential(string dshHome, string apiKey)
        {
            var path = Path.Combine(dshHome, ".credentials.yaml");
            var text = new StringBuilder();
            text.AppendLine("version: 1");
            text.AppendLine("records: {}");
            text.AppendLine("refs:");
            text.AppendLine("  DEEPSEEK_API_KEY: " + YamlString(apiKey));
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Minimal YAML scalar quoting: keys are plain ASCII but may contain ':' or '#'.
        /// </summary>
        private static string YamlString(string value)
        {
            if (value.Length == 0) return "\"\"";
            var needsQuotes = value.IndexOfAny(new[] { ':', '#', '\'', '"', '\n', '\r' }) >= 0
                              || value.StartsWith(" ") || value.EndsWith(" ");
            if (!needsQuotes) return value;
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>Registers or removes the launcher in the per-user Run key.</summary>
        private static void ApplyAutoStart(bool enabled)
        {
            try
            {
                const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true))
                {
                    if (key == null) return;
                    const string name = "DeepSeekHarness";
                    if (enabled)
                    {
                        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                        if (!string.IsNullOrEmpty(exe)) key.SetValue(name, "\"" + exe + "\"");
                    }
                    else
                    {
                        key.DeleteValue(name, throwOnMissingValue: false);
                    }
                }
            }
            catch
            {
                // Auto-start is a convenience; failing to set it must not block setup.
            }
        }
    }
}
