using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshDesktopUpdater
{
    /// <summary>
    /// Thin command-line front end for <see cref="Updater"/>. The app starts this process
    /// before exiting, which is the only way to replace the running executable on Windows.
    ///
    /// Usage: DshDesktopUpdater.exe &lt;zip&gt; &lt;installDir&gt; &lt;parentPid&gt; &lt;exeToRestart&gt;
    ///        DshDesktopUpdater.exe --selftest
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--selftest")
            {
                return SelfTest.Run();
            }

            if (args.Length < 4)
            {
                MessageBox.Show(
                    "更新助手由 DeepSeek Harness 桌面版自动调用，不需要手动运行。\n\n" +
                    "参数：<更新包> <安装目录> <父进程号> <要重启的程序>\n" +
                    "自检：DshDesktopUpdater.exe --selftest",
                    "DeepSeek Harness 更新助手",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 2;
            }

            var plan = new UpdatePlan
            {
                ZipPath = args[0],
                InstallDirectory = args[1],
                ParentProcessId = int.TryParse(args[2], out var pid) ? pid : -1,
                RestartExecutable = args[3],
            };

            var outcome = Updater.Apply(plan);

            if (!outcome.Succeeded)
            {
                try
                {
                    MessageBox.Show(
                        "自动更新失败：\n\n" + outcome.Error +
                        "\n\n程序文件可能处于半更新状态，建议重新下载最新包手动解压覆盖。\n" +
                        "详细日志：" + Updater.DefaultLogPath,
                        "DeepSeek Harness 更新助手",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch { }
                return 1;
            }

            return 0;
        }
    }

    /// <summary>
    /// End-to-end check for this executable, run with <c>--selftest</c>.
    ///
    /// The library-level fixture tests <see cref="Updater.Apply"/> in-process, which cannot catch
    /// a helper that dies at startup (a missing DshDesktopUpdater.Core.dll once did exactly that,
    /// silently, with exit code 0x8000809A). This test therefore launches a real helper process,
    /// exactly the way the app does, and asserts the file swap actually happened.
    /// </summary>
    internal static class SelfTest
    {
        private const string OldMarker = "OLD-1.2.0";
        private const string NewMarker = "NEW-1.3.1";

        private static int _failures;

        public static int Run()
        {
            // This exe is a WinExe: it may have no console at all, and touching Console there
            // throws. Results therefore go to a file, and the exit code carries the verdict.
            var lines = new List<string>();
            void Say(string message) => lines.Add(message);

            Say("=== updater selftest (real process) ===");
            Say("  helper: " + Environment.ProcessPath);

            var root = Path.Combine(Path.GetTempPath(), "updself-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var install = Path.Combine(root, "install");
            var payload = Path.Combine(root, "payload");
            var helperDir = Path.Combine(root, "helper");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(helperDir);

            try
            {
                // Fixture install: a real binary plus the files an update must replace.
                var stubPath = Path.Combine(install, "DeepSeekHarness.exe");
                var stubSource = Path.Combine(Environment.SystemDirectory, "ping.exe");
                if (!File.Exists(stubSource)) { Fail(Say, "ping.exe not found"); return Summary(Say, lines); }
                File.Copy(stubSource, stubPath, true);
                File.WriteAllText(Path.Combine(install, "DeepSeekHarness.dll"), OldMarker);
                File.WriteAllText(Path.Combine(install, "my-notes.txt"), "keep me");

                // Payload: a small text file is enough to prove the swap by content.
                File.WriteAllText(Path.Combine(payload, "DeepSeekHarness.exe"), NewMarker);
                File.WriteAllText(Path.Combine(payload, "DeepSeekHarness.dll"), NewMarker);
                Directory.CreateDirectory(Path.Combine(payload, "runtimes", "win-x64", "native"));
                File.WriteAllText(Path.Combine(payload, "runtimes", "win-x64", "native", "WebView2Loader.dll"), NewMarker);

                var zip = Path.Combine(root, "update.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                Say($"  package: {new FileInfo(zip).Length} bytes");

                // Copy the helper the same way the app does: whole folder, never a lone exe.
                foreach (var file in new DirectoryInfo(AppContext.BaseDirectory).GetFiles())
                {
                    File.Copy(file.FullName, Path.Combine(helperDir, file.Name), true);
                }
                var helperExe = Path.Combine(helperDir, "DshDesktopUpdater.exe");
                Check(Say, "helper exe copied", File.Exists(helperExe));
                Check(Say, "helper core library copied", File.Exists(Path.Combine(helperDir, "DshDesktopUpdater.Core.dll")));

                // A live, locked stub.
                var stub = Process.Start(new ProcessStartInfo
                {
                    FileName = stubPath,
                    Arguments = "-n 60 127.0.0.1",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                Thread.Sleep(2000);
                if (stub.HasExited) Fail(Say, "stub exited early; the exe lock is not being exercised");
                else Say($"  stub running as pid {stub.Id}; its exe is locked");

                // Launch the helper exactly as the app does.
                var logBefore = LogLength();
                var startInfo = new ProcessStartInfo
                {
                    FileName = helperExe,
                    Arguments = Quote(zip) + " " + Quote(install) + " " + stub.Id + " " + Quote(""),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = helperDir,
                };

                Process helper = null;
                try
                {
                    helper = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    Fail(Say, "helper did not start: " + ex.Message);
                    return Summary(Say, lines);
                }

                Check(Say, "helper process started", helper != null);
                Thread.Sleep(3000);

                // The helper must be alive and waiting, not dead on arrival.
                if (helper != null)
                {
                    helper.Refresh();
                    Check(Say, "helper still running while the stub holds the lock", !helper.HasExited);
                    if (helper.HasExited)
                    {
                        Fail(Say, $"helper died immediately with exit code {helper.ExitCode} (0x{helper.ExitCode:X8})");
                    }
                }

                Say("  terminating the stub so the helper can proceed");
                try { stub.Kill(true); } catch { }

                var finished = helper != null && helper.WaitForExit(120000);
                Check(Say, "helper finished", finished);
                if (helper != null && helper.HasExited)
                {
                    Check(Say, "helper exit code is 0", helper.ExitCode == 0);
                }

                Check(Say, "a log entry was written", LogLength() > logBefore);
                Check(Say, "the locked exe was replaced by the package", Read(stubPath) == NewMarker);
                Check(Say, "the dll was replaced", Read(Path.Combine(install, "DeepSeekHarness.dll")) == NewMarker);
                Check(Say, "a nested directory was created",
                    Read(Path.Combine(install, "runtimes", "win-x64", "native", "WebView2Loader.dll")) == NewMarker);
                Check(Say, "the user's own file survived", Read(Path.Combine(install, "my-notes.txt")) == "keep me");
                Check(Say, "the downloaded package was cleaned up", !File.Exists(zip));
            }
            catch (Exception ex)
            {
                Fail(Say, "unexpected: " + ex);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            return Summary(Say, lines);
        }

        private static long LogLength()
        {
            try { return File.Exists(Updater.DefaultLogPath) ? new FileInfo(Updater.DefaultLogPath).Length : 0; }
            catch { return 0; }
        }

        private static string Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : "(missing)"; }
            catch (Exception ex) { return "(error: " + ex.Message + ")"; }
        }

        private static void Check(Action<string> say, string what, bool ok)
        {
            say((ok ? "  PASS  " : "  FAIL  ") + what);
            if (!ok) _failures++;
        }

        private static void Fail(Action<string> say, string what)
        {
            say("  FAIL  " + what);
            _failures++;
        }

        private static int Summary(Action<string> say, List<string> lines)
        {
            say(_failures == 0
                ? "=== all checks passed ==="
                : $"=== {_failures} check(s) failed ===");

            // Write the transcript next to the update log so a failed run can be read afterwards
            // even though this process usually has no console.
            var reportPath = Path.Combine(Path.GetTempPath(), "dsh-desktop-updater-selftest.log");
            try { File.WriteAllText(reportPath, string.Join(Environment.NewLine, lines), new UTF8Encoding(false)); }
            catch { }

            try { Console.Out.Write(string.Join(Environment.NewLine, lines) + Environment.NewLine); }
            catch { /* no console attached; the file above is the record */ }

            return _failures == 0 ? 0 : 1;
        }

        private static string Quote(string value) => "\"" + value + "\"";
    }
}
