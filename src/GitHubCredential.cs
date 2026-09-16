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
        /// </summary>
        private static string FromGitCredentialManager()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };
                startInfo.ArgumentList.Add("credential");
                startInfo.ArgumentList.Add("fill");
                startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return null;

                    // The helper answers this query on stdin.
                    process.StandardInput.Write("protocol=https\nhost=github.com\n\n");
                    process.StandardInput.Flush();
                    process.StandardInput.Close();

                    var output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(8000))
                    {
                        try { process.Kill(true); } catch { }
                        return null;
                    }
                    if (process.ExitCode != 0) return null;

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
