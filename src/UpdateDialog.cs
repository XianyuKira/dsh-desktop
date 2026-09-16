using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshDesktop
{
    /// <summary>
    /// Reports what the update probe found and offers the two things a person can do about it:
    /// update in place (download, replace, restart) or open the release page. The in-place path
    /// needs the app to exit, because Windows will not let a running executable overwrite itself.
    /// </summary>
    internal sealed class UpdateDialog : Form
    {
        private readonly UpdateCheck.Result _result;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly Button _downloadButton;
        private readonly Button _pageButton;
        private readonly Button _closeButton;

        /// <summary>Set when the helper has been started and this process should exit.</summary>
        private bool _restartPending;

        public UpdateDialog(UpdateCheck.Result result, bool alreadyLatest)
        {
            _result = result;

            Text = result.UpdateAvailable ? "发现新版本" : "检查更新";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 330);
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
                Size = new Size(512, 160),
                Location = new Point(26, 62),
            };

            _progress = new ProgressBar
            {
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100,
                Size = new Size(512, 8),
                Location = new Point(26, 224),
                Visible = false,
            };

            _status = new Label
            {
                Text = string.Empty,
                ForeColor = Color.FromArgb(120, 120, 130),
                AutoSize = false,
                Size = new Size(512, 22),
                Location = new Point(26, 238),
            };

            _downloadButton = new Button
            {
                Text = "一键更新",
                Size = new Size(120, 32),
                Location = new Point(382, 272),
                Visible = result.UpdateAvailable,
            };
            _downloadButton.Click += async (s, e) => await InstallAsync();

            _pageButton = new Button
            {
                Text = result.UpdateAvailable ? "打开发布页" : "打开仓库",
                Size = new Size(120, 32),
                Location = new Point(232, 272),
            };
            _pageButton.Click += (s, e) => Finish(OpenReleasePage);

            _closeButton = new Button
            {
                Text = "关闭",
                Size = new Size(90, 32),
                Location = new Point(444, 272),
                DialogResult = DialogResult.Cancel,
            };

            if (result.UpdateAvailable)
            {
                _status.Text = "点「一键更新」自动下载并替换，完成后程序自动重启。";
            }

            Controls.AddRange(new Control[] { title, detail, _progress, _status, _downloadButton, _pageButton, _closeButton });
            CancelButton = _closeButton;
            AcceptButton = result.UpdateAvailable ? _downloadButton : _pageButton;

            if (result.UpdateAvailable) _downloadButton.Select();
        }

        /// <summary>True once the helper owns the update, meaning this process must exit.</summary>
        public bool RestartPending => _restartPending;

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
            if (published != null) text += "发布时间：" + published + Environment.NewLine;
            text += Environment.NewLine +
                    "更新过程：关闭本程序 → 替换安装目录里的文件 → 自动重新打开。" +
                    Environment.NewLine +
                    "同一个文件夹会被就地替换，不会再多出一个新目录；会话与设置也不受影响。";
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

        private async Task InstallAsync()
        {
            if (_restartPending) return;

            SetBusy(true, "正在查找更新包…");
            var (check, asset) = await AppUpdate.FindPackageAsync();
            if (!check.Succeeded)
            {
                SetBusy(false, "检查更新失败：" + check.Message);
                return;
            }
            if (asset == null)
            {
                SetBusy(false, "该发布没有可用的安装包，请用「打开发布页」手动下载。");
                return;
            }

            _progress.Visible = true;
            var progress = new Progress<double>(fraction =>
            {
                var percent = (int)Math.Round(fraction * 100);
                _progress.Value = Math.Max(0, Math.Min(100, percent));
                _status.Text = $"正在下载 {asset.Name} … {percent}%";
            });

            var download = await AppUpdate.DownloadAsync(asset, check.LatestTag, progress);
            if (!download.Succeeded)
            {
                SetBusy(false, "下载失败：" + download.Message);
                return;
            }

            _status.Text = $"已下载 {download.Size / 1024.0 / 1024.0:N1} MB，正在准备替换…";
            _progress.Value = 100;

            var installDirectory = AppUpdate.InstallDirectory;
            var exePath = System.IO.Path.Combine(installDirectory, "DeepSeekHarness.exe");
            if (!System.IO.File.Exists(exePath))
            {
                SetBusy(false, "找不到 " + exePath + "，无法自动替换，请用「打开发布页」手动更新。");
                return;
            }

            if (!AppUpdate.StartReplaceAndRestart(download.ZipPath, installDirectory, Environment.ProcessId, out var error))
            {
                SetBusy(false, "启动更新助手失败：" + error);
                return;
            }

            _restartPending = true;
            _status.Text = "更新助手已就绪，正在关闭本程序…";
            await Task.Delay(400);
            Close();
        }

        private void SetBusy(bool busy, string status)
        {
            _downloadButton.Enabled = !busy;
            _pageButton.Enabled = !busy;
            _closeButton.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (!string.IsNullOrEmpty(status)) _status.Text = status;
        }

        private void Finish(Action action)
        {
            try { action(); } catch { }
            Close();
        }

        private void OpenReleasePage() => OpenUrl(_result.ReleaseUrl ?? UpdateCheck.ReleasesPage);

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
                    if (dialog.RestartPending)
                    {
                        // The helper is waiting for this process to exit before it swaps files.
                        var main = owner as MainForm;
                        main?.RequestExitForUpdate();
                    }
                }
            }
        }
    }
}
