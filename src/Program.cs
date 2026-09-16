using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop
{
    internal static class Program
    {
        /// <summary>Keeps this app's taskbar identity separate from every browser on the machine.</summary>
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [STAThread]
        private static int Main(string[] args)
        {
            var options = CommandLineOptions.Parse(args);

            if (options.ShowHelp)
            {
                MessageBox.Show(HelpText(), "DeepSeek Harness 桌面版", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            try { SetCurrentProcessExplicitAppUserModelID("Gumiao.DshDesktop"); }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            var config = AppConfig.Load();
            config.PortableData = options.Portable || config.PortableData;
            if (options.Port.HasValue) config.PreferredPort = options.Port.Value;
            if (!string.IsNullOrWhiteSpace(options.Workspace)) config.Workspace = options.Workspace;

            try
            {
                Directory.CreateDirectory(config.WebViewDataFolder);
                Directory.CreateDirectory(config.LogDirectory);
            }
            catch { }

            var runtime = DescribeWebView2Runtime();
            if (runtime == null)
            {
                MessageBox.Show(
                    "缺少 Microsoft Edge WebView2 运行时，无法在窗口内显示界面。\n\n" +
                    "请安装后重试：https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" +
                    "（也可以先用 --browser 参数把界面交给系统默认浏览器打开。）",
                    "DeepSeek Harness 桌面版",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 2;
            }

            var selfTest = SelfTestOptions.From(options);

            try
            {
                using (var form = new MainForm(config, options, runtime))
                {
                    Application.Run(form);
                    if (selfTest != null)
                    {
                        selfTest.WriteResult(form.SelfTestReport);
                    }
                }
            }
            catch (Exception ex)
            {
                selfTest?.WriteResult("EXCEPTION: " + ex);
                try
                {
                    File.AppendAllText(
                        Path.Combine(config.LogDirectory, "launcher.log"),
                        DateTime.Now.ToString("s") + "  FATAL " + ex + Environment.NewLine);
                }
                catch { }

                if (selfTest == null)
                {
                    MessageBox.Show(
                        "启动器发生错误：\n\n" + ex.Message,
                        "DeepSeek Harness 桌面版",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return 1;
            }

            return 0;
        }

        /// <summary>The installed WebView2 runtime version, or null when the runtime is absent.</summary>
        private static string DescribeWebView2Runtime()
        {
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return string.IsNullOrWhiteSpace(version) ? null : version;
            }
            catch
            {
                return null;
            }
        }

        internal static string HelpText()
        {
            var text = new StringBuilder();
            text.AppendLine("DeepSeek Harness 桌面版 — 双击即用的本地 Harness 窗口");
            text.AppendLine();
            text.AppendLine("用法：DeepSeekHarness.exe [选项]");
            text.AppendLine();
            text.AppendLine("  --workspace <目录>   指定 dsh 的工作目录（默认：上次使用或用户主目录）");
            text.AppendLine("  --port <端口>        指定端口，0 表示由系统随机分配（默认：3080）");
            text.AppendLine("  --portable           把设置与缓存放在程序目录，而不是 %LOCALAPPDATA%");
            text.AppendLine("  --browser            不用内嵌窗口，改为在系统默认浏览器中打开");
            text.AppendLine("  --selftest[=<文件>]  自动启动、自检并把结果写入文件后退出");
            text.AppendLine("  -h, --help           显示本帮助");
            return text.ToString();
        }
    }

    /// <summary>Command line switches understood by the launcher.</summary>
    internal sealed class CommandLineOptions
    {
        public string Workspace;
        public int? Port;
        public bool Portable;
        public bool UseSystemBrowser;
        public bool ShowHelp;
        public string SelfTestPath;

        public static CommandLineOptions Parse(string[] args)
        {
            var options = new CommandLineOptions();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "-h":
                    case "--help":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                    case "--portable":
                        options.Portable = true;
                        break;
                    case "--browser":
                        options.UseSystemBrowser = true;
                        break;
                    case "--workspace":
                        if (i + 1 < args.Length) options.Workspace = args[++i];
                        break;
                    case "--port":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var port)) options.Port = port;
                        break;
                    default:
                        if (arg.StartsWith("--workspace=", StringComparison.OrdinalIgnoreCase))
                            options.Workspace = arg.Substring("--workspace=".Length);
                        else if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase) &&
                                 int.TryParse(arg.Substring("--port=".Length), out var inlinePort))
                            options.Port = inlinePort;
                        else if (arg.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase))
                        {
                            options.SelfTestPath = arg.Contains("=")
                                ? arg.Substring(arg.IndexOf('=') + 1)
                                : string.Empty;
                        }
                        break;
                }
            }
            return options;
        }
    }

    /// <summary>Headless verification mode: proves the window, WebView2 and dsh handshake all work.</summary>
    internal sealed class SelfTestOptions
    {
        public string ResultPath;
        public int MaxSeconds = 120;

        public static SelfTestOptions From(CommandLineOptions options)
        {
            if (options.SelfTestPath == null) return null;
            return new SelfTestOptions
            {
                ResultPath = string.IsNullOrWhiteSpace(options.SelfTestPath)
                    ? Path.Combine(AppContext.BaseDirectory, "selftest-result.txt")
                    : options.SelfTestPath,
            };
        }

        public void WriteResult(string report)
        {
            var payload = report ?? string.Empty;
            try
            {
                File.WriteAllText(ResultPath, payload, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(ResultPath + ".error.txt", ex.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }
}
