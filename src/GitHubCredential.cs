using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DshDesktop
{
    /// <summary>
    /// Supplies a GitHub credential for API calls when one is already available on this machine.
    ///
    /// Anonymous GitHub API requests are limited to 60 per hour **per IP**, shared by everyone
    /// behind that address, which makes "check for updates" fail for reasons the user cannot
    /// influence. An authenticated request gets 5000 per hour against the user's own account.
    /// The credential Git Credential Manager already holds (from a `git push`) is reused here;
    /// when there is none, callers simply stay anonymous.
    /// </summary>
    internal static class GitHubCredential
    {
        private static readonly object Gate = new object();
        private static bool _resolved;
        private static string _token;
        private static bool _verified;
        private static string _verifyResult;

        /// <summary>The credential to send, or null to stay anonymous.</summary>
        public static string Token
        {
            get
            {
                lock (Gate)
                {
                    if (!_resolved)
                    {
                        _token = Resolve();
                        _resolved = true;
                    }
                    return _token;
                }
            }
        }

        /// <summary>True when requests will carry a credential (and therefore a bigger quota).</summary>
        public static bool Available => !string.IsNullOrEmpty(Token);

        /// <summary>
        /// Confirms the credential is actually accepted by GitHub before it is advertised.
        /// "I found a string" and "GitHub accepts it" are different claims, and only the second
        /// one justifies telling the user they are on the 5000/hour quota.
        /// </summary>
        public static bool IsVerified
        {
            get
            {
                lock (Gate)
                {
                    if (_verified) return true;
                    if (string.IsNullOrEmpty(Token)) return false;
                    if (_verifyResult != null) return _verifyResult == "ok";

                    try
                    {
                        using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                        {
                            client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop/" + UpdateCheck.CurrentDisplay);
                            client.DefaultRequestHeaders.Authorization =
                                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                            using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
                                       "https://api.github.com/user"))
                            using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                            {
                                _verifyResult = response.IsSuccessStatusCode ? "ok" : ("HTTP " + (int)response.StatusCode);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _verifyResult = ex.GetType().Name;
                    }

                    if (_verifyResult == "ok") _verified = true;
                    return _verified;
                }
            }
        }

        /// <summary>Human-readable note about which credential state was resolved (for --check-update).</summary>
        public static string Describe()
        {
            if (!Available) return "匿名（60 次/小时，按 IP，容易被挤掉）";
            return IsVerified
                ? "带凭据且已验证（5000 次/小时，按账号）"
                : "带凭据但未通过校验（" + (_verifyResult ?? "未检测") + "），实际仍按匿名配额";
        }

        private static string Resolve()
        {
            // An explicit token in the environment wins: it is how CI or a user can override.
            foreach (var name in new[] { "DSH_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
            {
                try
                {
                    var value = Environment.GetEnvironmentVariable(name);
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                catch { }
            }

            return FromGitCredentialManager();
        }

        /// <summary>
        /// Asks Git Credential Manager for the stored github.com credential. Runs `git` with a
        /// short timeout: a machine without git, or without a stored credential, must simply end
        /// up anonymous rather than hang the update check.
        ///
        /// The query reaches git through a file handle rather than StandardInput: writing to the
        /// child's stdin was observed to fail here with "refusing to work with credential missing
        /// protocol field", while file redirection works. The temp file is deleted while still
        /// open, so the credential query never sits in the directory as a readable path.
        /// </summary>
        private static string FromGitCredentialManager()
        {
            string queryPath = null;
            try
            {
                queryPath = Path.Combine(Path.GetTempPath(),
                    "dsh-gcm-" + Guid.NewGuid().ToString("N").Substring(0, 8));

                using (var queryFile = new FileStream(queryPath, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, 4096))
                using (var writer = new StreamWriter(queryFile, new UTF8Encoding(false)))
                {
                    writer.Write("protocol=https\nhost=github.com\n\n");
                    writer.Flush();
                    queryFile.Flush(true);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    // cmd owns the redirection; git itself does not parse "< path".
                    Arguments = "/d /s /c \"git credential fill < \"" + queryPath + "\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };
                startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return null;

                    var output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(true); } catch { }
                        return null;
                    }

                    foreach (var line in output.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("password=", StringComparison.Ordinal))
                        {
                            var value = trimmed.Substring("password=".Length).Trim();
                            return string.IsNullOrWhiteSpace(value) ? null : value;
                        }
                    }
                    return null;
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (queryPath != null && File.Exists(queryPath)) File.Delete(queryPath); }
                catch { }
            }
        }

        /// <summary>
        /// Adds the Authorization header when a credential exists. Called for both the update
        /// check and the package download so they share one quota.
        /// </summary>
        public static void Apply(System.Net.Http.HttpClient client)
        {
            var token = Token;
            if (string.IsNullOrEmpty(token)) return;
            try
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            catch { }
        }
    }
}
