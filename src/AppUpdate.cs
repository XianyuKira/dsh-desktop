using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DshDesktop
{
    /// <summary>
    /// Downloads the newest release package and hands the actual file swap to a tiny helper
    /// process, because Windows will not let a running executable overwrite itself.
    /// </summary>
    internal static class AppUpdate
    {
        public const string Owner = "XianyuKira";
        public const string Repo = "dsh-desktop";
        public const string LatestReleaseApi = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

        /// <summary>
        /// The package newer installs receive. The framework-dependent build needs the .NET
        /// desktop runtime, which this program already requires, so it is the safe small choice.
        /// </summary>
        public static string PreferredAssetSuffix => "-win-x64.zip";

        /// <summary>Folder this build was launched from; that folder is what an update replaces.</summary>
        public static string InstallDirectory
        {
            get
            {
                var path = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(path)) path = Environment.ProcessPath;
                return Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
            }
        }

        /// <summary>Where the updater helper writes its running log.</summary>
        public static string UpdateLogPath =>
            Path.Combine(Path.GetTempPath(), "dsh-desktop-update.log");

        internal sealed class ReleaseAsset
        {
            public string Name;
            public string DownloadUrl;
            public long Size;
        }

        internal sealed class DownloadResult
        {
            public bool Succeeded;
            public string Message;
            public string ZipPath;
            public string Version;
            public long Size;
        }

        /// <summary>Finds the newest release and the package to install for this architecture.</summary>
        public static async Task<(UpdateCheck.Result Check, ReleaseAsset Asset)> FindPackageAsync(CancellationToken cancellationToken = default)
        {
            var check = await UpdateCheck.RunAsync(cancellationToken).ConfigureAwait(false);
            if (!check.Succeeded || !check.UpdateAvailable) return (check, null);

            try
            {
                using (var client = BuildClient())
                {
                    var json = await client.GetStringAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
                    using (var document = JsonDocument.Parse(json))
                    {
                        if (!document.RootElement.TryGetProperty("assets", out var assets)) return (check, null);

                        ReleaseAsset fallback = null;
                        foreach (var entry in assets.EnumerateArray())
                        {
                            var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var url = entry.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                            var size = entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var parsed) ? parsed : 0;
                            var asset = new ReleaseAsset { Name = name, DownloadUrl = url, Size = size };

                            if (name.EndsWith(PreferredAssetSuffix, StringComparison.OrdinalIgnoreCase)) return (check, asset);
                            fallback ??= asset;
                        }
                        return (check, fallback);
                    }
                }
            }
            catch
            {
                return (check, null);
            }
        }

        /// <summary>Streams the package to a temp file and reports progress as a 0..1 fraction.</summary>
        public static async Task<DownloadResult> DownloadAsync(
            ReleaseAsset asset,
            string version,
            IProgress<double> progress,
            CancellationToken cancellationToken = default)
        {
            var result = new DownloadResult { Version = version };
            if (asset == null)
            {
                result.Message = "该发布没有可用的 zip 附件。";
                return result;
            }

            var target = Path.Combine(Path.GetTempPath(),
                "dsh-desktop-" + (version ?? "new").TrimStart('v', 'V') + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip");

            try
            {
                using (var client = BuildClient())
                using (var response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? asset.Size;

                    using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                    using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long copied = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            copied += read;
                            if (total > 0) progress?.Report(Math.Min(1.0, (double)copied / total));
                        }
                    }
                }

                // A truncated or HTML-error download must never be handed to the updater.
                if (!IsZip(target))
                {
                    TryDelete(target);
                    result.Message = "下载到的文件不是有效的 zip。";
                    return result;
                }

                result.Succeeded = true;
                result.ZipPath = target;
                result.Size = new FileInfo(target).Length;
                return result;
            }
            catch (Exception ex)
            {
                TryDelete(target);
                result.Message = Describe(ex);
                return result;
            }
        }

        /// <summary>
        /// Launches the helper that waits for this process to exit, copies the package over the
        /// install folder, and starts the program again. The helper must outlive this process, so
        /// it is started detached rather than as a child.
        /// </summary>
        public static bool StartReplaceAndRestart(string zipPath, string installDirectory, int currentProcessId, out string error)
        {
            error = null;
            try
            {
                var helper = Path.Combine(Path.GetTempPath(),
                    "DshDesktopUpdater-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".exe");

                var source = Path.Combine(InstallDirectory, "DshDesktopUpdater.exe");
                if (!File.Exists(source))
                {
                    error = "找不到更新助手 DshDesktopUpdater.exe，包可能不完整。";
                    return false;
                }
                File.Copy(source, helper, true);

                var arguments = string.Join(" ", new[]
                {
                    Quote(zipPath),
                    Quote(installDirectory),
                    currentProcessId.ToString(),
                    Quote(Path.Combine(InstallDirectory, "DeepSeekHarness.exe")),
                });

                var startInfo = new ProcessStartInfo
                {
                    FileName = helper,
                    Arguments = arguments,
                    UseShellExecute = true,     // detach: this helper must survive our exit
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),
                };

                var process = Process.Start(startInfo);
                if (process == null)
                {
                    error = "无法启动更新助手。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static HttpClient BuildClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop/" + UpdateCheck.CurrentDisplay);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            // Share the quota with the update check: anonymous access is 60/hour per IP.
            GitHubCredential.Apply(client);
            return client;
        }

        private static bool IsZip(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    return stream.Length > 4 && stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
                }
            }
            catch
            {
                return false;
            }
        }

        public static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static string Describe(Exception ex)
        {
            var cause = ex;
            while (cause.InnerException != null) cause = cause.InnerException;

            if (cause is HttpRequestException http)
            {
                // 403 on the API is GitHub's anonymous quota, not a connectivity problem.
                var status = (int?)http.StatusCode;
                if (status == 403 || status == 429)
                    return "GitHub 请求配额已用尽或被拒绝，请稍后重试。";
                return "下载失败：连不上 GitHub。";
            }
            if (cause is TaskCanceledException || cause is OperationCanceledException) return "下载超时。";
            return cause.Message;
        }

        private static string Quote(string value) => "\"" + value + "\"";
    }
}
