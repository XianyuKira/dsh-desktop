using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace DshDesktopUpdater
{
    /// <summary>
    /// The file-swap engine, kept in its own library so it can be exercised by tests without
    /// spawning a real helper process.
    ///
    /// The problem it solves: on Windows a running executable cannot overwrite itself. So the
    /// app downloads the new package and starts a helper; the helper waits for the app to exit,
    /// copies the package over the install folder, and starts the app again.
    /// </summary>
    public sealed class UpdatePlan
    {
        public string ZipPath { get; set; }
        public string InstallDirectory { get; set; }
        public int ParentProcessId { get; set; } = -1;
        public string RestartExecutable { get; set; }

        /// <summary>How long to wait for the app to exit before forcing it.</summary>
        public TimeSpan ExitTimeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>Per-file retry window; a just-exited process can hold a handle briefly.</summary>
        public TimeSpan FileRetryWindow { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>Delete the downloaded zip once its contents are installed.</summary>
        public bool DeleteZipWhenDone { get; set; } = true;
    }

    public sealed class UpdateOutcome
    {
        public bool Succeeded;
        public string Error;
        public string StagingDirectory;
        public int FilesCopied;
        public int DirectoriesCreated;
        public bool Restarted;
        public string Log;
    }

    public static class Updater
    {
        public static string DefaultLogPath =>
            Path.Combine(Path.GetTempPath(), "dsh-desktop-update.log");

        /// <summary>Runs one whole update: wait, extract, copy, restart.</summary>
        public static UpdateOutcome Apply(UpdatePlan plan, Action<string> log = null)
        {
            var outcome = new UpdateOutcome { Log = string.Empty };
            var transcript = new StringBuilder();

            void Note(string message)
            {
                var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
                transcript.AppendLine(line);
                try { log?.Invoke(message); } catch { }
            }

            try
            {
                Note("=== update started ===");
                Note("zip        " + plan.ZipPath);
                Note("install    " + plan.InstallDirectory);

                if (string.IsNullOrWhiteSpace(plan.ZipPath) || !File.Exists(plan.ZipPath))
                    throw new FileNotFoundException("找不到更新包", plan.ZipPath);
                if (string.IsNullOrWhiteSpace(plan.InstallDirectory) || !Directory.Exists(plan.InstallDirectory))
                    throw new DirectoryNotFoundException("找不到安装目录：" + plan.InstallDirectory);

                WaitForExit(plan, Note);

                outcome.StagingDirectory = Extract(plan.ZipPath, Note);
                var copy = CopyOver(outcome.StagingDirectory, plan.InstallDirectory, plan, Note);
                outcome.FilesCopied = copy.Files;
                outcome.DirectoriesCreated = copy.Directories;
                Note("replaced " + copy.Files + " files");

                TryDeleteDirectory(outcome.StagingDirectory, Note);
                outcome.StagingDirectory = null;
                if (plan.DeleteZipWhenDone) TryDeleteFile(plan.ZipPath, Note);

                outcome.Restarted = Restart(plan.RestartExecutable, Note);
                outcome.Succeeded = true;
                Note("=== update finished ===");
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
                Note("FAILED: " + ex);
            }

            outcome.Log = transcript.ToString();
            AppendToFile(DefaultLogPath, outcome.Log);
            return outcome;
        }

        /// <summary>Waits for the parent process to exit, forcing it if it overstays.</summary>
        public static void WaitForExit(UpdatePlan plan, Action<string> note = null)
        {
            if (plan.ParentProcessId > 0)
            {
                try
                {
                    using (var process = Process.GetProcessById(plan.ParentProcessId))
                    {
                        note?.Invoke("waiting for pid " + plan.ParentProcessId + " to exit");
                        if (!process.WaitForExit((int)plan.ExitTimeout.TotalMilliseconds))
                        {
                            note?.Invoke("parent still alive after " + plan.ExitTimeout.TotalSeconds + "s; terminating it");
                            try { process.Kill(true); } catch { }
                            process.WaitForExit(15000);
                        }
                    }
                }
                catch (ArgumentException)
                {
                    note?.Invoke("parent already gone");
                }
                catch (Exception ex)
                {
                    note?.Invoke("wait failed: " + ex.Message);
                }
            }

            // Windows needs a moment to release the old process's file handles.
            Thread.Sleep(1200);
        }

        /// <summary>Extracts the package into a fresh temp folder and returns its path.</summary>
        public static string Extract(string zipPath, Action<string> note = null)
        {
            var staging = Path.Combine(Path.GetTempPath(),
                "dsh-desktop-staging-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging);
            note?.Invoke("extracted to " + staging);
            return staging;
        }

        /// <summary>
        /// Copies every extracted file over the install folder. Existing files are overwritten
        /// (with retries); unrelated files the user may have put there are left alone.
        /// </summary>
        public static (int Files, int Directories) CopyOver(
            string source, string destination, UpdatePlan plan, Action<string> note = null)
        {
            var sourceRoot = new DirectoryInfo(source);
            int files = 0, directories = 0;

            foreach (var file in sourceRoot.GetFiles("*", SearchOption.AllDirectories))
            {
                var relative = file.FullName.Substring(sourceRoot.FullName.Length).TrimStart(Path.DirectorySeparatorChar);
                var targetPath = Path.Combine(destination, relative);
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    directories++;
                }

                CopyWithRetry(file.FullName, targetPath, plan, note);
                files++;

                try { File.SetLastWriteTimeUtc(targetPath, file.LastWriteTimeUtc); }
                catch { }
            }

            return (files, directories);
        }

        private static void CopyWithRetry(string source, string target, UpdatePlan plan, Action<string> note)
        {
            var deadline = DateTime.UtcNow + plan.FileRetryWindow;
            var firstAttempt = true;

            while (true)
            {
                try
                {
                    File.Copy(source, target, true);
                    note?.Invoke("  replaced " + Path.GetFileName(target));
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && DateTime.UtcNow < deadline)
                {
                    if (firstAttempt)
                    {
                        note?.Invoke("  locked, retrying: " + Path.GetFileName(target));
                        firstAttempt = false;
                    }
                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>Starts the freshly installed program again.</summary>
        public static bool Restart(string exePath, Action<string> note = null)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                note?.Invoke("restart skipped: " + exePath + " not found");
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = true,
                };
                using (Process.Start(startInfo)) { }
                note?.Invoke("restarted " + exePath);
                return true;
            }
            catch (Exception ex)
            {
                note?.Invoke("restart failed: " + ex.Message);
                return false;
            }
        }

        private static void TryDeleteFile(string path, Action<string> note)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { note?.Invoke("could not delete " + path + ": " + ex.Message); }
        }

        private static void TryDeleteDirectory(string path, Action<string> note)
        {
            try { if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception ex) { note?.Invoke("could not remove staging: " + ex.Message); }
        }

        private static void AppendToFile(string path, string text)
        {
            try { File.AppendAllText(path, text, new UTF8Encoding(false)); }
            catch { }
        }
    }
}
