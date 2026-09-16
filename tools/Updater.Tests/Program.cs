using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace DshDesktopUpdater
{
    /// <summary>
    /// Proves the update engine against a real running process: a stub executable is started
    /// from a fixture install folder, then replaced while it runs. This is the risky part of
    /// self-updating, so it gets a real test rather than a hopeful one.
    /// </summary>
    internal static class Program
    {
        private const string OldMarker = "OLD-CONTENT-1.1.0";
        private const string NewMarker = "NEW-CONTENT-1.2.0";

        private static int _failures;

        private static int Main()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine("=== updater fixture ===");

            var root = Path.Combine(Path.GetTempPath(), "updtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var install = Path.Combine(root, "install");
            var payload = Path.Combine(root, "payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);

            try
            {
                // 1. A fixture install: the running program plus a file the user added.
                //    The stub must be a real executable that stays alive, otherwise the exe lock
                //    (the whole reason this helper exists) is never exercised. ping is a small
                //    real binary that idles without needing console input.
                var stubPath = Path.Combine(install, "DeepSeekHarness.exe");
                var stubSource = Path.Combine(Environment.SystemDirectory, "ping.exe");
                if (!File.Exists(stubSource))
                {
                    Console.WriteLine("  FAIL  ping.exe not found; cannot build a live stub");
                    return Summary();
                }
                File.Copy(stubSource, stubPath, true);
                File.WriteAllText(Path.Combine(install, "DeepSeekHarness.dll"), OldMarker);
                File.WriteAllText(Path.Combine(install, "runtimes-old.txt"), OldMarker);
                File.WriteAllText(Path.Combine(install, "my-notes.txt"), "keep me");

                // 2. The update payload: replaces both program files and adds a nested runtime dir.
                //    The exe payload is deliberately tiny text, so "replaced" is provable by content.
                File.WriteAllText(Path.Combine(payload, "DeepSeekHarness.exe"), NewMarker);
                File.WriteAllText(Path.Combine(payload, "DeepSeekHarness.dll"), NewMarker);
                Directory.CreateDirectory(Path.Combine(payload, "runtimes", "win-x64", "native"));
                File.WriteAllText(Path.Combine(payload, "runtimes", "win-x64", "native", "WebView2Loader.dll"), NewMarker);

                var zip = Path.Combine(root, "update.zip");
                ZipFile.CreateFromDirectory(payload, zip);
                Console.WriteLine($"  package: {new FileInfo(zip).Length} bytes");

                // 3. Start the stub so its exe is genuinely locked for about a minute.
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

                if (stub.HasExited)
                {
                    Fail("stub exited early, so the exe lock was never exercised");
                }
                else
                {
                    Console.WriteLine($"  stub running as pid {stub.Id}; its exe is locked");
                    try
                    {
                        File.Copy(Path.Combine(payload, "DeepSeekHarness.exe"), stubPath, true);
                        Fail("copying over a running exe unexpectedly succeeded");
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        Console.WriteLine("  confirmed: Windows refuses to overwrite a running exe");
                    }
                }

                // 4. Run the engine in a helper thread; it waits for the stub to exit, then swaps files.
                var plan = new UpdatePlan
                {
                    ZipPath = zip,
                    InstallDirectory = install,
                    ParentProcessId = stub.Id,
                    RestartExecutable = null,     // never actually spawn the fixture as a program
                    DeleteZipWhenDone = true,
                    ExitTimeout = TimeSpan.FromSeconds(30),
                    FileRetryWindow = TimeSpan.FromSeconds(20),
                };

                var log = new List<string>();
                UpdateOutcome outcome = null;
                var worker = new Thread(() => outcome = Updater.Apply(plan, message => { lock (log) log.Add(message); }));
                worker.Start();

                Thread.Sleep(4000);
                if (outcome == null)
                {
                    Console.WriteLine("  helper is waiting for the stub; terminating the stub in 3s");
                    Thread.Sleep(3000);
                    try { stub.Kill(true); } catch { }
                }

                if (!worker.Join(TimeSpan.FromSeconds(90)))
                {
                    Fail("helper thread never finished");
                    return Summary();
                }

                var outcomeLog = outcome.Log;
                Console.WriteLine("  --- helper log ---");
                foreach (var line in outcomeLog.TrimEnd().Split('\n'))
                {
                    Console.WriteLine("    " + line.TrimEnd());
                }

                // 5. Assertions.
                if (!outcome.Succeeded) Fail("update reported failure: " + outcome.Error);

                // The stub was a real binary, so its bytes cannot be compared to text; the
                // marker file in its place proves the payload landed over a locked exe.
                Check("running program file was replaced by the package",
                    Read(stubPath) == NewMarker);
                Check("dll was replaced", Read(Path.Combine(install, "DeepSeekHarness.dll")) == NewMarker);
                Check("nested directory was created",
                    Read(Path.Combine(install, "runtimes", "win-x64", "native", "WebView2Loader.dll")) == NewMarker);
                Check("unrelated user file was preserved", Read(Path.Combine(install, "my-notes.txt")) == "keep me");
                Check("file only in the old version is left in place",
                    Read(Path.Combine(install, "runtimes-old.txt")) == OldMarker);
                Check("downloaded package was cleaned up", !File.Exists(zip));
                Check("staging directory was cleaned up",
                    outcome.StagingDirectory == null || !Directory.Exists(outcome.StagingDirectory));
                Check("nothing was executed as a restart", !outcome.Restarted);

                // 6. Idempotency: applying the same package again must still succeed.
                var second = Updater.Apply(new UpdatePlan
                {
                    ZipPath = MakeSecondZip(root, payload),
                    InstallDirectory = install,
                    ParentProcessId = -1,
                    RestartExecutable = null,
                });
                Check("second update over an already-updated folder succeeds", second.Succeeded);
                Check("files still correct after the second update", Read(stubPath) == NewMarker);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            return Summary();
        }

        private static string MakeSecondZip(string root, string payload)
        {
            var zip = Path.Combine(root, "update-again.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(payload, zip);
            return zip;
        }

        private static string Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : "(missing)"; }
            catch (Exception ex) { return "(error: " + ex.Message + ")"; }
        }

        private static void Check(string what, bool ok)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
            if (!ok) _failures++;
        }

        private static void Fail(string what)
        {
            Console.WriteLine("  FAIL  " + what);
            _failures++;
        }

        private static int Summary()
        {
            Console.WriteLine(_failures == 0
                ? "=== all checks passed ==="
                : $"=== {_failures} check(s) failed ===");
            return _failures == 0 ? 0 : 1;
        }
    }
}
