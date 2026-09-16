using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshDesktop
{
    /// <summary>
    /// Reports what the update probe found and offers the two things a person can actually
    /// do about it: open the release page, or jump straight to the download.
    /// Nothing is downloaded or replaced automatically.
    /// </summary>
    internal sealed class UpdateDialog : Form
    {
        private readonly UpdateCheck.Result _result;
        private readonly Label _status;
        private readonly Button _downloadButton;
        private readonly Button _pageButton;

        public UpdateDialog(UpdateCheck.Result result, bool alreadyLatest)
        {
            _result = result;

            Text = result.UpdateAvailable ? "发现新版本" : "检查更新";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 300);
            BackColor = Color.FromArgb(250, 250, 252);
            Font = new Font("Microsoft YaHei UI", 9f);

            var title = new Label
            {
                Text = result.UpdateAvailable ? "有新版本可用" : "已是最新版本",
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
                ForeColor = Color.FromArgb(32, 32, 40),
                AutoSize = true,
                Location = new Point(24, 22),
            };

            var detail = new Label
            {
                Text = BuildDetail(result),
                ForeColor = Color.FromArgb(70, 70, 80),
                AutoSize = false,
                Size = new Size(512, 150),
                Location = new Point(26, 62),
            };

            _status = new Label
            {
                Text = string.Empty,
                ForeColor = Color.FromArgb(120, 120, 130),
                AutoSize = false,
                Size = new Size(512, 22),
                Location = new Point(26, 190),
            };

            _downloadButton = new Button
            {
                Text = "下载新版本",
                Size = new Size(120, 32),
                Location = new Point(232, 250),
                Visible = result.UpdateAvailable,
            };
            _downloadButton.Click += (s, e) => Finish(OpenDownload);

            _pageButton = new Button
            {
                Text = result.UpdateAvailable ? "打开发布页" : "打开仓库",
                Size = new Size(120, 32),
                Location = new Point(232, 250),
            };
            _pageButton.Click += (s, e) => Finish(OpenReleasePage);

            var closeButton = new Button
            {
                Text = "关闭",
                Size = new Size(90, 32),
                Location = new Point(444, 250),
                DialogResult = DialogResult.Cancel,
            };

            if (result.UpdateAvailable)
            {
                _status.Text = "点「下载新版本」直接拿压缩包；覆盖旧文件即可完成更新。";
            }
            else if (alreadyLatest)
            {
                _status.Text = "";
            }

            Controls.AddRange(new Control[] { title, detail, _status, _downloadButton, _pageButton, closeButton });
            CancelButton = closeButton;

            if (result.UpdateAvailable)
            {
                // The download is the primary action.
                _downloadButton.Location = new Point(232, 250);
                _pageButton.Location = new Point(102, 250);
                AcceptButton = _downloadButton;
                _downloadButton.Select();
            }
            else
            {
                _pageButton.Location = new Point(232, 250);
                AcceptButton = _pageButton;
            }
        }

        private static string BuildDetail(UpdateCheck.Result result)
        {
            if (!result.Succeeded)
            {
                return "当前版本：" + UpdateCheck.CurrentDisplay + Environment.NewLine + Environment.NewLine +
                       "检查更新失败：" + result.Message;
            }

            if (!result.UpdateAvailable)
            {
                return "当前版本：" + UpdateCheck.CurrentDisplay + Environment.NewLine + Environment.NewLine +
                       "GitHub 上最新的发布就是这一版，无需更新。";
            }

            var text = "当前版本：" + UpdateCheck.CurrentDisplay + Environment.NewLine +
                       "最新版本：" + (result.LatestTag ?? UpdateCheck.ToDisplay(result.Latest)) + Environment.NewLine;
            var published = FormatPublished(result.PublishedAt);
            if (published != null)
            {
                text += "发布时间：" + published + Environment.NewLine;
            }
            text += Environment.NewLine +
                    "这是一个绿色程序，没有安装器：下载新包、解压覆盖旧文件，就完成更新了。" +
                    Environment.NewLine +
                    "（覆盖前请先关闭正在运行的程序，否则 exe 会被占用。）";
            return text;
        }

        /// <summary>Turns the API's ISO timestamp into something a person reads at a glance.</summary>
        private static string FormatPublished(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTimeOffset.TryParse(raw, out var parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : raw;
        }

        private void Finish(Action action)
        {
            try { action(); } catch { }
            Close();
        }

        private void OpenReleasePage() => OpenUrl(_result.ReleaseUrl ?? UpdateCheck.ReleasesPage);

        /// <summary>Straight to the newest asset on the release page when one was reported.</summary>
        private void OpenDownload()
        {
            var version = _result.LatestTag != null && _result.LatestTag.StartsWith("v")
                ? _result.LatestTag
                : "v" + UpdateCheck.ToDisplay(_result.Latest);
            if (_result.ReleaseUrl != null && _result.ReleaseUrl.Contains("/releases/tag/"))
            {
                OpenUrl(_result.ReleaseUrl);
                return;
            }
            OpenUrl(UpdateCheck.ReleasesPage);
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }

        /// <summary>Runs the probe with a small dialog in front of it, then shows the outcome.</summary>
        public static async Task ShowCheckAsync(IWin32Window owner)
        {
            using (var waiting = new Form())
            {
                waiting.Text = "检查更新";
                waiting.FormBorderStyle = FormBorderStyle.FixedDialog;
                waiting.ControlBox = false;
                waiting.StartPosition = FormStartPosition.CenterParent;
                waiting.ClientSize = new Size(320, 90);
                waiting.Controls.Add(new Label
                {
                    Text = "正在查询 GitHub 上的最新版本…",
                    AutoSize = false,
                    Size = new Size(300, 40),
                    Location = new Point(14, 26),
                    Font = new Font("Microsoft YaHei UI", 9f),
                });
                waiting.Show(owner);
                waiting.Refresh();

                var result = await UpdateCheck.RunAsync();
                waiting.Close();

                using (var dialog = new UpdateDialog(result, alreadyLatest: result.Succeeded && !result.UpdateAvailable))
                {
                    dialog.ShowDialog(owner);
                }
            }
        }
    }
}
