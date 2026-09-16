using System;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace DshDesktop
{
    /// <summary>
    /// Asks GitHub for this project's newest release and reports whether it is newer than
    /// the running build. Releases are the only update channel; there is no installer and
    /// nothing is downloaded automatically.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string Owner = "XianyuKira";
        public const string Repo = "dsh-desktop";
        public const string ReleasesPage = "https://github.com/" + Owner + "/" + Repo + "/releases";
        public const string LatestReleaseApi = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

        /// <summary>The version compiled into this build, from the assembly metadata.</summary>
        public static Version Current =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

        public static string CurrentDisplay => ToDisplay(Current);

        public sealed class Result
        {
            public bool Succeeded;
            public string Message;

            /// <summary>Version carried by the newest release tag, when one could be read.</summary>
            public Version Latest;

            /// <summary>Tag exactly as published, e.g. <c>v1.1.0</c>.</summary>
            public string LatestTag;

            public string ReleaseUrl;
            public string PublishedAt;

            /// <summary>True only when a parseable, strictly newer release was found.</summary>
            public bool UpdateAvailable;
        }

        public static async Task<Result> RunAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(20);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop/" + CurrentDisplay);
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    // Anonymous requests get 60/hour per IP; a stored credential gets 5000/hour.
                    GitHubCredential.Apply(client);

                    using (var response = await client.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return new Result { Succeeded = false, Message = DescribeStatus(response) };
                        }

                        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using (var document = JsonDocument.Parse(json))
                        {
                            var root = document.RootElement;
                            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
                            var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : ReleasesPage;
                            var published = root.TryGetProperty("published_at", out var publishedElement) ? publishedElement.GetString() : null;

                            if (string.IsNullOrWhiteSpace(tag))
                            {
                                return new Result { Succeeded = false, Message = "未能从 GitHub 读取版本号。" };
                            }

                            var latest = ParseVersion(tag);
                            if (latest == null)
                            {
                                return new Result
                                {
                                    Succeeded = false,
                                    Message = "最新发布标签无法解析为版本号：" + tag,
                                    LatestTag = tag,
                                    ReleaseUrl = url,
                                };
                            }

                            return new Result
                            {
                                Succeeded = true,
                                Latest = latest,
                                LatestTag = tag,
                                ReleaseUrl = url,
                                PublishedAt = published,
                                UpdateAvailable = latest > Current,
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new Result
                {
                    Succeeded = false,
                    Message = Describe(ex),
                };
            }
        }

        /// <summary>
        /// Explains a non-success HTTP status. Rate limiting gets its own wording, because
        /// "cannot reach GitHub" would send the user chasing a network problem that is not there.
        /// </summary>
        private static string DescribeStatus(System.Net.Http.HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;

            // 403 is what GitHub returns when the anonymous per-IP quota is exhausted;
            // 429 is the explicit "too many requests" form.
            if (status == 403 || status == 429)
            {
                var remaining = Header(response, "X-RateLimit-Remaining");
                var reset = Header(response, "X-RateLimit-Reset");
                var when = string.Empty;
                if (long.TryParse(reset, out var epoch))
                {
                    try { when = "，约 " + DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("HH:mm") + " 恢复"; }
                    catch { }
                }

                return remaining == "0"
                    ? "GitHub 请求配额已用尽" + when + "。匿名请求按 IP 限每小时 60 次；稍后重试即可。"
                    : "GitHub 拒绝了本次请求（HTTP " + status + "）。稍后重试，或改用「打开发布页」手动更新。";
            }

            return "GitHub 返回 HTTP " + status + "。可稍后重试，或用「打开发布页」手动更新。";
        }

        private static string Header(System.Net.Http.HttpResponseMessage response, string name)
        {
            try
            {
                return response.Headers.TryGetValues(name, out var values)
                    ? string.Join(",", values).Trim()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Turns a release tag such as <c>v1.2.3</c> or <c>1.2.3-beta.1</c> into a comparable version.</summary>
        internal static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var match = Regex.Match(tag, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
            if (!match.Success) return null;

            int Part(int index) =>
                match.Groups[index].Success && int.TryParse(match.Groups[index].Value, out var value) ? value : 0;

            return new Version(Part(1), Part(2), Part(3), Part(4));
        }

        /// <summary>Three-part display form; the assembly revision is noise to a human.</summary>
        internal static string ToDisplay(Version version)
        {
            if (version == null) return "未知";
            return version.Revision > 0
                ? version.ToString(4)
                : version.Build >= 0 ? version.ToString(3) : version.ToString(2);
        }

        private static string Describe(Exception ex)
        {
            var cause = ex;
            while (cause.InnerException != null) cause = cause.InnerException;

            if (cause is HttpRequestException)
                return "连不上 GitHub（网络或代理问题）。可手动打开 Release 页面查看。";
            if (cause is TaskCanceledException || cause is OperationCanceledException)
                return "请求 GitHub 超时。可手动打开 Release 页面查看。";

            return cause.Message;
        }
    }
}
