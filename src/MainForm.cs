using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshDesktop
{
    /// <summary>
    /// The desktop shell: it boots the bundled <c>dsh web</c> server as a child process,
    /// reads the authenticated URL that server prints, and renders that URL in an embedded
    /// WebView2 window — no external browser is involved.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly AppConfig _config;
        private readonly CommandLineOptions _options;
        private readonly string _runtimeVersion;
        private readonly SelfTestOptions _selfTest;
        private readonly string _bootId = Guid.NewGuid().ToString("N").Substring(0, 8);

        private readonly StringBuilder _report = new StringBuilder();
        private readonly List<string> _pendingLog = new List<string>();

        private ToolStrip _toolStrip;
        private ToolStripButton _refreshButton;
        private ToolStripButton _externalButton;
        private ToolStripButton _logButton;
        private ToolStripButton _zoomOutButton;
        private ToolStripButton _zoomInButton;
        private ToolStripDropDownButton _menuButton;
        private Panel _contentPanel;
        private WebView2 _webView;
        private Panel _logPanel;
        private TextBox _logBox;
        private Panel _overlay;
        private Label _overlayTitle;
        private Label _overlayDetail;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _stateLabel;
        private ToolStripStatusLabel _portLabel;
        private ToolStripStatusLabel _workspaceLabel;
        private ToolStripStatusLabel _zoomLabel;
        private System.Windows.Forms.Timer _overlayTimer;

        private DshServer _server;
        private CoreWebView2Environment _environment;
        private string _webViewDataFolder;

        private int _overlayTick;
        private int _zoomPercent = 20;
        private bool _webViewReady;
        private bool _firstNavigationSettled;
        private bool _logVisible;
        private bool _selfTestFinished;
        private bool _browserMode;

        private string _serverUrl;
        private string _diagnostics = "(未采集)";
        private DateTime _selfTestDeadline;

        public MainForm(AppConfig config, CommandLineOptions options, string runtimeVersion)
        {
            _config = config;
            _options = options;
            _runtimeVersion = runtimeVersion;
            _selfTest = SelfTestOptions.From(options);

            BuildInterface();
        }

        /// <summary>Aggregated evidence produced for <c>--selftest</c>; written by Program.Main.</summary>
        public string SelfTestReport { get; private set; }

        /// <summary>
        /// Closes the window so the update helper can replace files. Going through the normal
        /// close path stops the dsh server first, which is what releases the executable.
        /// </summary>
        public void RequestExitForUpdate()
        {
            try
            {
                if (IsHandleCreated) BeginInvoke(new Action(Close));
                else Close();
            }
            catch
            {
                try { Close(); } catch { }
            }
        }

        // ---------------------------------------------------------------- interface

        private void BuildInterface()
        {
            Text = "DeepSeek Harness 桌面版";
            Icon = AppIcon.Create();
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 600);
            Size = new Size(1360, 900);
            BackColor = Color.FromArgb(24, 24, 27);
            KeyPreview = true;

            // The in-window UI owns the page, so the native frame gets no extras.
            _contentPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 27) };

            BuildOverlay();
            BuildWebView();
            BuildLogPanel();
            BuildToolStrip();
            BuildStatusStrip();

            Controls.Add(_contentPanel);
            Controls.Add(_logPanel);
            Controls.Add(_toolStrip);
            Controls.Add(_statusStrip);
            _contentPanel.Controls.Add(_overlay);
            _contentPanel.Controls.Add(_webView);

            if (_selfTest != null)
            {
                // Verification runs off-screen so it never steals focus from the human.
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
                Size = new Size(1440, 960);
                _selfTestDeadline = DateTime.UtcNow.AddSeconds(_selfTest.MaxSeconds);
            }

            SetZoom(_zoomPercent);
        }

        private void BuildToolStrip()
        {
            _toolStrip = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                RenderMode = ToolStripRenderMode.System,
                ImageScalingSize = new Size(16, 16),
                Padding = new Padding(6, 2, 6, 2),
            };

            _refreshButton = new ToolStripButton("刷新(R)") { ToolTipText = "重新加载界面 (Ctrl+R / F5)" };
            _refreshButton.Click += (s, e) => ReloadUi();

            _externalButton = new ToolStripButton("浏览器打开") { ToolTipText = "把当前界面交给系统默认浏览器" };
            _externalButton.Click += (s, e) => OpenInSystemBrowser();

            _logButton = new ToolStripButton("运行日志") { ToolTipText = "显示或隐藏 dsh 服务日志" };
            _logButton.Click += (s, e) => ToggleLogPanel();

            _zoomOutButton = new ToolStripButton("－") { ToolTipText = "缩小 (Ctrl+-)" };
            _zoomOutButton.Click += (s, e) => StepZoom(-5);

            _zoomInButton = new ToolStripButton("＋") { ToolTipText = "放大 (Ctrl+=)" };
            _zoomInButton.Click += (s, e) => StepZoom(+5);

            _menuButton = new ToolStripDropDownButton("菜单");

            var itemOpen = new ToolStripMenuItem("在浏览器中打开(&B)");
            itemOpen.Click += (s, e) => OpenInSystemBrowser();

            var itemCopy = new ToolStripMenuItem("复制访问地址(&C)");
            itemCopy.Click += (s, e) => CopyUrl();

            var itemWorkspace = new ToolStripMenuItem("更改工作目录(&W)…");
            itemWorkspace.Click += (s, e) => ChangeWorkspace();

            var itemDevTools = new ToolStripMenuItem("开发者工具(&D)") { Enabled = false };
            itemDevTools.Click += (s, e) => { try { _webView.CoreWebView2?.OpenDevToolsWindow(); } catch { } };

            var itemDataFolder = new ToolStripMenuItem("打开数据目录(&F)");
            itemDataFolder.Click += (s, e) => OpenFolder(_config.PortableData ? AppConfig.ExeDirectory : AppConfig.AppDataDirectory);

            var itemLogFolder = new ToolStripMenuItem("打开日志目录(&L)");
            itemLogFolder.Click += (s, e) => OpenFolder(_config.LogDirectory);

            var itemRestart = new ToolStripMenuItem("重启 dsh 服务(&R)");
            itemRestart.Click += (s, e) => FireAndForget(RestartServerAsync());

            var itemStop = new ToolStripMenuItem("停止 dsh 服务(&S)");
            itemStop.Click += (s, e) =>
            {
                var server = _server;
                if (server == null) return;
                server.Stop();
                SetState("已停止", Color.FromArgb(200, 120, 60));
                ShowOverlay("服务已停止", "点击“刷新”可重新启动 dsh 服务。");
            };

            var itemAbout = new ToolStripMenuItem("关于(&A)");
            itemAbout.Click += (s, e) => ShowAbout();

            var itemUpdate = new ToolStripMenuItem("检查更新(&U)…");
            itemUpdate.Click += (s, e) => FireAndForget(UpdateDialog.ShowCheckAsync(this));

            _menuButton.DropDownItems.AddRange(new ToolStripItem[]
            {
                itemOpen, itemCopy, new ToolStripSeparator(), itemWorkspace, itemDevTools,
                new ToolStripSeparator(), itemDataFolder, itemLogFolder, new ToolStripSeparator(),
                itemRestart, itemStop, new ToolStripSeparator(), itemUpdate, itemAbout,
            });

            _toolStrip.Items.AddRange(new ToolStripItem[]
            {
                _refreshButton, _externalButton, new ToolStripSeparator(),
                _zoomOutButton, _zoomInButton, new ToolStripSeparator(),
                _logButton, _menuButton,
            });

            _menuButton.DropDownOpening += (s, e) => itemDevTools.Enabled = _webViewReady;
        }

        private void BuildWebView()
        {
            _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        }

        private void BuildLogPanel()
        {
            _logBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 18, 20),
                ForeColor = Color.FromArgb(200, 205, 210),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
            };

            _logPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 200,
                Visible = false,
                BackColor = Color.FromArgb(18, 18, 20),
                Padding = new Padding(8, 6, 8, 6),
            };
            _logPanel.Controls.Add(_logBox);
        }

        private void BuildStatusStrip()
        {
            _stateLabel = new ToolStripStatusLabel("待启动") { ForeColor = Color.FromArgb(180, 180, 185) };
            _portLabel = new ToolStripStatusLabel("");
            _workspaceLabel = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _zoomLabel = new ToolStripStatusLabel("");

            _statusStrip = new StatusStrip { SizingGrip = true, BackColor = Color.FromArgb(32, 32, 36) };
            _statusStrip.Items.AddRange(new ToolStripItem[]
            {
                _stateLabel, new ToolStripStatusLabel("│"), _portLabel, new ToolStripStatusLabel("│"),
                _workspaceLabel, _zoomLabel,
            });
        }

        private void BuildOverlay()
        {
            _overlay = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 22, 26), Visible = false };

            var card = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.None,
                Padding = new Padding(0),
            };

            _overlayTitle = new Label
            {
                Text = "正在启动 DeepSeek Harness…",
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Regular),
                ForeColor = Color.FromArgb(235, 235, 240),
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0, 0, 0, 12),
            };

            _overlayDetail = new Label
            {
                Text = "准备中",
                Font = new Font("Microsoft YaHei UI", 9.5f),
                ForeColor = Color.FromArgb(150, 150, 158),
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Anchor = AnchorStyles.None,
            };

            card.Controls.Add(_overlayTitle, 0, 0);
            card.Controls.Add(new Label { Text = "", Height = 4, AutoSize = false }, 0, 1);
            card.Controls.Add(_overlayDetail, 0, 2);

            _overlay.Controls.Add(card);
            _overlay.Resize += (s, e) =>
            {
                card.Left = Math.Max(0, (_overlay.ClientSize.Width - card.Width) / 2);
                card.Top = Math.Max(0, (_overlay.ClientSize.Height - card.Height) / 2);
            };

            _overlayTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _overlayTimer.Tick += (s, e) =>
            {
                _overlayTick++;
                // A tiny bit of motion while dsh boots, so a slow first start never looks hung.
                _overlayTitle.Text = _overlayBase + new string('·', (_overlayTick % 3) + 1);
            };
        }

        private string _overlayBase = "正在启动 DeepSeek Harness…";

        private void ShowOverlay(string title, string detail)
        {
            _overlayBase = title;
            _overlayTitle.Text = title;
            _overlayDetail.Text = detail ?? string.Empty;
            _overlay.Visible = true;
            _overlay.BringToFront();
            _webView.Visible = false;
            _overlayTimer.Start();
            LayoutOverlay();
        }

        private void HideOverlay()
        {
            _overlayTimer.Stop();
            _overlay.Visible = false;
            _webView.Visible = true;
        }

        private void LayoutOverlay()
        {
            if (_overlay?.Controls.Count > 0)
            {
                var card = _overlay.Controls[0];
                card.Left = Math.Max(0, (_overlay.ClientSize.Width - card.Width) / 2);
                card.Top = Math.Max(0, (_overlay.ClientSize.Height - card.Height) / 2);
            }
        }

        // ---------------------------------------------------------------- lifecycle

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await StartupAsync();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window must take this app's private dsh server down with it;
            // job objects elsewhere cover the crash paths.
            try { _server?.Stop(); } catch { }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _overlayTimer?.Stop(); } catch { }
            try { _webView?.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.R:
                case Keys.F5:
                    ReloadUi();
                    return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Add:
                    StepZoom(+5);
                    return true;
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract:
                    StepZoom(-5);
                    return true;
                case Keys.Control | Keys.D0:
                    SetZoom(20);
                    return true;
                case Keys.F12:
                    try { _webView.CoreWebView2?.OpenDevToolsWindow(); } catch { }
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------------------------------------------------------- startup

        private async Task StartupAsync()
        {
            Note("boot " + _bootId);
            Note("exe   " + AppContext.BaseDirectory);
            Note("webview2 runtime " + _runtimeVersion);

            var workspace = _config.ResolveWorkspace();
            _workspaceLabel.Text = "工作目录 " + workspace;
            Note("workspace " + workspace);

            ShowOverlay("正在检查 dsh 安装…", "查找 node 与 @deepseek-ai/dsh");
            Application.DoEvents();

            var command = DshLocator.BuildCommandLine(0);
            if (command == null)
            {
                Note("dsh NOT FOUND");
                ShowOverlay("找不到 dsh", "未能在 PATH 或全局 npm 目录中找到 dsh。请先安装 DeepSeek Harness，或检查 node 是否可用。");
                return;
            }
            Note("command " + command);

            _browserMode = _options.UseSystemBrowser;
            if (_browserMode)
            {
                SetState("浏览器模式", Color.FromArgb(150, 190, 220));
                _externalButton.Enabled = false;
                _refreshButton.Enabled = false;
                Note("browser mode: the page will be handed to the system browser");
            }
            else if (!await InitializeWebViewAsync())
            {
                return;
            }

            ShowOverlay("正在启动 dsh 服务…", "首次启动需要几秒钟");
            await LaunchServerAsync(workspace);

            if (_selfTest != null)
            {
                await RunSelfTestAsync();
            }
        }

        private async Task<bool> InitializeWebViewAsync()
        {
            // The data folder is created fresh per attempt because the port — and therefore the
            // page origin — is only known once dsh is up.
            _webViewDataFolder = Path.Combine(
                _config.WebViewDataFolder,
                _runtimeVersion.Replace('.', '_') + "-" + _bootId);
            Directory.CreateDirectory(_webViewDataFolder);
            PruneWebViewFolders();

            try
            {
                _environment = await CoreWebView2Environment.CreateAsync(null, _webViewDataFolder, null);
                Note("webview data " + _webViewDataFolder);
                return true;
            }
            catch (Exception ex)
            {
                Note("webview init FAILED: " + ex.Message);
                ShowOverlay("无法初始化内嵌浏览器", ex.Message + "\n\n可去掉 --browser 之外的参数重试，或重新安装 WebView2 运行时。");
                return false;
            }
        }

        /// <summary>
        /// Every launch uses its own WebView2 profile folder, so old ones are removed to keep
        /// the data directory from growing without bound. Folders still in use are skipped.
        /// </summary>
        private void PruneWebViewFolders()
        {
            try
            {
                var root = new DirectoryInfo(_config.WebViewDataFolder);
                if (!root.Exists) return;

                var stale = new List<DirectoryInfo>();
                foreach (var dir in root.GetDirectories())
                {
                    if (string.Equals(dir.FullName, _webViewDataFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    stale.Add(dir);
                }

                // Newest first; keep a couple in case a second window is still open.
                stale.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 2; i < stale.Count; i++)
                {
                    try { stale[i].Delete(true); }
                    catch { /* locked by a running instance — leave it alone */ }
                }
            }
            catch { }
        }

        private async Task LaunchServerAsync(string workspace)
        {
            var candidates = new List<int>(_config.PortCandidates());
            for (int index = 0; index < candidates.Count; index++)
            {
                var port = candidates[index];
                // Only the first candidate is the remembered port; anything after it is a fallback.
                var isPreferredAttempt = index == 0;
                var server = new DshServer();
                server.Output += OnServerOutput;
                _server = server;

                var label = port == 0 ? "由系统分配端口" : ("端口 " + port);
                ShowOverlay("正在启动 dsh 服务…", label + " · 工作目录 " + workspace);
                Note("--- start attempt port=" + port + " ---");

                var result = await Task.Run(() => server.Start(port, workspace, TimeSpan.FromSeconds(port == 0 ? 150 : 45)));
                Note("attempt outcome " + result.Outcome + (result.Detail == null ? "" : " :: " + result.Detail));

                if (result.Outcome == ServerStartOutcome.Ready)
                {
                    _serverUrl = result.Url;
                    var actualPort = ExtractPort(result.Url);
                    _portLabel.Text = "端口 " + (actualPort?.ToString() ?? "?");
                    SetState("运行中", Color.FromArgb(120, 200, 140));
                    Note("ready " + Redact(result.Url));

                    // Only a port that actually worked is worth remembering; a busy-port
                    // fallback would otherwise become sticky and be retried forever.
                    if (isPreferredAttempt && actualPort.HasValue && _config.RememberPort)
                    {
                        _config.PreferredPort = actualPort.Value;
                        _config.Save();
                    }

                    await NavigateAsync(result.Url);
                    return;
                }

                var busy = result.Outcome == ServerStartOutcome.PortBusy;
                Note(busy ? "port busy, retrying" : "start failed");
                server.Stop();

                if (busy && !_config.StrictPort) continue;

                ShowOverlay(
                    busy ? "端口被占用" : "dsh 启动失败",
                    (result.Detail ?? "未知原因") + "\n\n可展开“运行日志”查看 dsh 的原始输出。");
                return;
            }

            ShowOverlay("dsh 启动失败", "所有端口候选都失败了。请查看“运行日志”。");
        }

        private async Task NavigateAsync(string url)
        {
            if (_browserMode)
            {
                ShowOverlay("已在默认浏览器中打开", "本窗口仅作为服务看守；关闭它就会结束 dsh 服务。");
                OpenExternal(url);
                return;
            }

            try
            {
                if (_webView.CoreWebView2 == null)
                {
                    await _webView.EnsureCoreWebView2Async(_environment);
                    HookWebViewEvents();
                }

                Note("navigate " + Redact(url));
                _webView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                Note("navigate FAILED " + ex.Message);
                ShowOverlay("无法加载界面", ex.Message);
            }
        }

        private void HookWebViewEvents()
        {
            var core = _webView.CoreWebView2;
            _webViewReady = true;

            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            core.NavigationStarting += (s, e) => Note("navigating " + Redact(e.Uri));

            core.NavigationCompleted += async (s, e) =>
            {
                Note("navigation completed ok=" + e.IsSuccess + " status=" + e.WebErrorStatus);
                if (!e.IsSuccess)
                {
                    SetState("加载失败", Color.FromArgb(220, 110, 110));
                    ShowOverlay("界面加载失败", e.WebErrorStatus.ToString());
                    return;
                }

                HideOverlay();
                _firstNavigationSettled = true;
                try { _webView.ZoomFactor = _zoomPercent / 100.0; }
                catch { }

                _diagnostics = await CollectDiagnosticsAsync();

                if (_selfTest == null)
                {
                    BringToFront();
                    Activate();
                }
            };

            core.ProcessFailed += (s, e) =>
            {
                Note("process failed " + e.ProcessFailedKind);
                SetState("界面异常", Color.FromArgb(220, 110, 110));
                ShowOverlay("界面进程异常", e.ProcessFailedKind.ToString());
            };

            core.NewWindowRequested += (s, e) =>
            {
                // Anything the page wants to open in a new window belongs to the real browser.
                e.Handled = true;
                OpenExternal(e.Uri);
            };

            core.DownloadStarting += (s, e) =>
            {
                Note("download " + e.ResultFilePath);
            };

            core.DOMContentLoaded += (s, e) => Note("dom content loaded");
        }

        private async Task<string> CollectDiagnosticsAsync()
        {
            try
            {
                var script =
                    "(function(){try{var b=document.body;" +
                    "return JSON.stringify({" +
                    "title:document.title," +
                    "url:location.href," +
                    "boot:typeof window.__DSH_BOOT__," +
                    "textLength:b?b.innerText.length:0," +
                    "nodes:b?b.querySelectorAll('*').length:0," +
                    "hasComposer:!!document.querySelector('textarea,[contenteditable=true]')," +
                    "sidebarItems:b?b.querySelectorAll('li,button').length:0," +
                    "snippet:(b?b.innerText:'').replace(/\\s+/g,' ').slice(0,240)" +
                    "});}catch(e){return 'DIAG_ERROR: '+e.message}})()";
                return await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                return "DIAG_EXCEPTION: " + ex.Message;
            }
        }

        private async Task RunSelfTestAsync()
        {
            Note("selftest start");
            while (DateTime.UtcNow < _selfTestDeadline && !_selfTestFinished)
            {
                if (_firstNavigationSettled && _diagnostics != null && !_diagnostics.StartsWith("DIAG_"))
                {
                    // Give the client plugins a moment to finish mounting before judging the DOM.
                    await Task.Delay(2500);
                    _diagnostics = await CollectDiagnosticsAsync();
                    FinishSelfTest();
                    return;
                }
                await Task.Delay(500);
            }

            if (!_selfTestFinished)
            {
                Note("selftest TIMEOUT");
                FinishSelfTest();
            }
        }

        private void FinishSelfTest()
        {
            if (_selfTestFinished) return;
            _selfTestFinished = true;

            Note("FinishSelfTest entered");

            // Snapshot the launcher log BEFORE building the report, so the report never
            // describes its own construction.
            string launcherLog;
            lock (_report) launcherLog = _report.ToString();

            var report = new StringBuilder();
            report.AppendLine("=== DeepSeek Harness 桌面版 自检 ===");
            report.AppendLine("time             " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("exe              " + AppContext.BaseDirectory);
            report.AppendLine("webview2         " + _runtimeVersion);
            report.AppendLine("workspace        " + _config.ResolveWorkspace());
            report.AppendLine("server url       " + Redact(_serverUrl));
            report.AppendLine("server running   " + (_server?.IsRunning == true));
            report.AppendLine("first navigation " + _firstNavigationSettled);
            report.AppendLine("dom diagnostics  " + _diagnostics);
            report.AppendLine();
            report.AppendLine("--- launcher log ---");
            report.Append(launcherLog);

            // Program.Main writes this exactly once, after the message loop has ended.
            SelfTestReport = report.ToString();

            // Leave no orphaned server behind when the check ends.
            try { _server?.Stop(); } catch { }
            BeginInvoke(new Action(Close));
        }

        // ---------------------------------------------------------------- actions

        private void ReloadUi()
        {
            if (_webViewReady && _serverUrl != null && _server != null && _server.IsRunning)
            {
                HideOverlay();
                _webView.CoreWebView2.Navigate(_serverUrl);
                return;
            }

            if (_server != null && !_server.IsRunning)
            {
                FireAndForget(RestartServerAsync());
                return;
            }

            if (_webView?.CoreWebView2 != null) _webView.CoreWebView2.Reload();
        }

        private async Task RestartServerAsync()
        {
            Note("restart requested");
            try { _server?.Stop(); } catch { }
            _serverUrl = null;
            _firstNavigationSettled = false;
            SetState("重启中", Color.FromArgb(200, 190, 120));
            ShowOverlay("正在重启 dsh 服务…", "请稍候");
            await LaunchServerAsync(_config.ResolveWorkspace());
        }

        private void StepZoom(int delta)
        {
            SetZoom(_zoomPercent + delta);
        }

        private void SetZoom(int percent)
        {
            _zoomPercent = Math.Max(5, Math.Min(200, percent));
            try
            {
                if (_webView != null) _webView.ZoomFactor = _zoomPercent / 100.0;
            }
            catch { }
            if (_zoomLabel != null) _zoomLabel.Text = "缩放 " + _zoomPercent + "%";
        }

        private void ToggleLogPanel()
        {
            _logVisible = !_logVisible;
            _logPanel.Visible = _logVisible;
            _logButton.Checked = _logVisible;
            if (_logVisible)
            {
                foreach (var line in _pendingLog) _logBox.AppendText(line + Environment.NewLine);
                _pendingLog.Clear();
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }

        private void CopyUrl()
        {
            if (string.IsNullOrEmpty(_serverUrl))
            {
                MessageBox.Show("服务还没有启动完成。", "复制地址", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Clipboard.SetText(_serverUrl);
                SetState("地址已复制", Color.FromArgb(150, 190, 220));
            }
            catch { }
        }

        private void OpenInSystemBrowser() => OpenExternal(_serverUrl);

        private static void OpenExternal(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch { }
        }

        private void ChangeWorkspace()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 dsh 的工作目录（也是会话默认工作区）";
                dialog.SelectedPath = _config.ResolveWorkspace();
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                _config.Workspace = dialog.SelectedPath;
                _config.Save();
                _workspaceLabel.Text = "工作目录 " + dialog.SelectedPath;
                Note("workspace changed to " + dialog.SelectedPath);
                FireAndForget(RestartServerAsync());
            }
        }

        private void ShowAbout()
        {
            var text = new StringBuilder();
            text.AppendLine("DeepSeek Harness 桌面版 " + UpdateCheck.CurrentDisplay);
            text.AppendLine();
            text.AppendLine("把 dsh 的 Web 界面装进一个独立窗口：双击即用，无需浏览器。");
            text.AppendLine();
            text.AppendLine("WebView2 运行时：" + _runtimeVersion);
            text.AppendLine("当前服务地址：" + (string.IsNullOrEmpty(_serverUrl) ? "（未启动）" : Redact(_serverUrl)));
            text.AppendLine("工作目录：" + _config.ResolveWorkspace());
            text.AppendLine("配置目录：" + (_config.PortableData ? AppConfig.ExeDirectory : AppConfig.AppDataDirectory));
            text.AppendLine();
            text.AppendLine("启动器：贤余sama");
            MessageBox.Show(text.ToString(), "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------------------------------------------------------- helpers

        private void SetState(string text, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetState(text, color))); return; }
            _stateLabel.Text = text;
            _stateLabel.ForeColor = color;
        }

        private void OnServerOutput(ServerLine line)
        {
            var prefix = line.IsError ? "! " : "  ";
            var rendered = prefix + line.Text;

            if (IsHandleCreated && InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendLog(rendered)));
            }
            else
            {
                AppendLog(rendered);
            }

            Note(line.Text);
        }

        private void AppendLog(string rendered)
        {
            if (_logVisible && _logBox != null)
            {
                _logBox.AppendText(rendered + Environment.NewLine);
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
            else
            {
                _pendingLog.Add(rendered);
                if (_pendingLog.Count > 4000) _pendingLog.RemoveRange(0, 1000);
            }
        }

        private void Note(string text)
        {
            lock (_report)
            {
                _report.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text);
            }
        }

        /// <summary>Never let the process token reach logs, reports or screenshots of them.</summary>
        private static string Redact(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var index = url.IndexOf("token=", StringComparison.OrdinalIgnoreCase);
            return index < 0 ? url : url.Substring(0, index) + "token=***";
        }

        private static int? ExtractPort(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.Port;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Runs a task without ever letting its failure surface as an unhandled exception.</summary>
        private void FireAndForget(Task task)
        {
            task.ContinueWith(
                t => Note("async failure: " + t.Exception?.GetBaseException().Message),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    /// <summary>Draws the window/taskbar icon at runtime so the app needs no binary asset.</summary>
    internal static class AppIcon
    {
        public static Icon Create()
        {
            try
            {
                using (var bitmap = new Bitmap(64, 64))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);

                    using (var brush = new SolidBrush(Color.FromArgb(77, 107, 254)))
                    {
                        graphics.FillEllipse(brush, 2, 2, 60, 60);
                    }
                    using (var brush = new SolidBrush(Color.White))
                    using (var font = new Font("Segoe UI", 30f, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        var format = new StringFormat
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center,
                        };
                        graphics.DrawString("D", font, brush, new RectangleF(0, 0, 64, 64), format);
                    }

                    var handle = bitmap.GetHicon();
                    try
                    {
                        using (var temp = Icon.FromHandle(handle))
                        {
                            return (Icon)temp.Clone();
                        }
                    }
                    finally
                    {
                        DestroyIcon(handle);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
