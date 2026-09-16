using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DshDesktop
{
    /// <summary>
    /// Reads the plugin market's local HTTP API so the desktop shell can show market and
    /// plugin-update state in its status bar.
    ///
    /// Everything here talks to the dsh web server this launcher started (loopback only) and
    /// uses the market's documented public endpoints:
    ///   GET /dsh-market/api/v1/capabilities
    ///   GET /dsh-market/status
    ///   GET /dsh-market/api/v1/updates?name=&lt;package&gt;
    /// None of them need the session token, and all are read-only.
    /// </summary>
    internal static class MarketStatus
    {
        /// <summary>One installed plugin as the market sees it.</summary>
        internal sealed class PluginRow
        {
            public string Name;
            public string InstalledVersion;
            public string LatestVersion;
            public bool UpdateAvailable;
        }

        internal sealed class Snapshot
        {
            public bool Reached;

            /// <summary>Market plugin version, e.g. 1.47.0.</summary>
            public string MarketVersion;

            /// <summary>Release channel and region the market is configured for.</summary>
            public string Channel;
            public string Region;

            public int InstalledCount;
            public int UpdateCount;
            public List<PluginRow> Plugins = new List<PluginRow>();

            /// <summary>True while the market is installing or updating something.</summary>
            public bool Busy;
            public string BusyTarget;
            public int Done;
            public int Total;

            public string Error;

            /// <summary>Short status-bar text, or null when there is nothing worth saying.</summary>
            public string Summary()
            {
                if (Error != null) return "插件市场：不可用";
                if (!Reached) return null;

                if (Busy)
                {
                    var progress = Total > 0 ? Done + "/" + Total : "处理中";
                    return "插件市场：正在安装 " + (BusyTarget ?? "") + " " + progress;
                }

                if (UpdateCount > 0) return "插件市场：" + UpdateCount + " 个插件可更新";

                return "插件市场 " + (MarketVersion ?? "?") + " · " + InstalledCount + " 个插件";
            }
        }

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop/" + UpdateCheck.CurrentDisplay);
            return client;
        }

        /// <summary>
        /// Reads market state. The update sweep is the slow part (it may hit the registry), so
        /// callers should run this off the UI thread.
        /// </summary>
        public static async Task<Snapshot> ReadAsync(string baseUrl, bool checkUpdates, CancellationToken cancellationToken = default)
        {
            var snapshot = new Snapshot();
            if (string.IsNullOrWhiteSpace(baseUrl)) return snapshot;

            try
            {
                var capabilities = await GetJsonAsync(baseUrl + "/dsh-market/api/v1/capabilities", cancellationToken).ConfigureAwait(false);
                if (capabilities == null) return snapshot;

                snapshot.Reached = true;
                snapshot.MarketVersion = Text(capabilities.Value, "marketVersion");

                var status = await GetJsonAsync(baseUrl + "/dsh-market/status", cancellationToken).ConfigureAwait(false);
                if (status.HasValue)
                {
                    var root = status.Value;
                    snapshot.Channel = Text(root, "channel");
                    snapshot.Region = Text(root, "region");
                    snapshot.Busy = Bool(root, "busy") || Bool(root, "active");
                    snapshot.BusyTarget = Text(root, "currentPackage") ?? Text(root, "target");
                    snapshot.Done = Int(root, "done");
                    snapshot.Total = Int(root, "total");

                    if (root.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var entry in installed.EnumerateObject())
                        {
                            snapshot.Plugins.Add(new PluginRow
                            {
                                Name = entry.Name,
                                InstalledVersion = entry.Value.GetString(),
                            });
                        }
                    }
                }

                snapshot.InstalledCount = snapshot.Plugins.Count;

                if (checkUpdates && snapshot.Plugins.Count > 0)
                {
                    await CheckUpdatesAsync(baseUrl, snapshot, cancellationToken).ConfigureAwait(false);
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                snapshot.Error = ex.Message;
                return snapshot;
            }
        }

        /// <summary>
        /// Asks the market about each installed plugin. Sequential on purpose: the market may
        /// reach out to the registry per package, and a burst would hammer it.
        /// </summary>
        private static async Task CheckUpdatesAsync(string baseUrl, Snapshot snapshot, CancellationToken cancellationToken)
        {
            foreach (var plugin in snapshot.Plugins)
            {
                if (cancellationToken.IsCancellationRequested) return;

                try
                {
                    var url = baseUrl + "/dsh-market/api/v1/updates?name=" + Uri.EscapeDataString(plugin.Name);
                    var response = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
                    if (!response.HasValue) continue;
                    if (!response.Value.TryGetProperty("package", out var package)) continue;

                    plugin.InstalledVersion = Text(package, "installedVersion") ?? plugin.InstalledVersion;
                    plugin.LatestVersion = Text(package, "latestVersion");
                    plugin.UpdateAvailable = Bool(package, "updateAvailable");
                    if (plugin.UpdateAvailable) snapshot.UpdateCount++;
                }
                catch
                {
                    // One plugin failing to report must not blank the whole status bar.
                }
            }
        }

        private static async Task<JsonElement?> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            using (var response = await Client.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body)) return null;
                using (var document = JsonDocument.Parse(body))
                {
                    // Clone: the document is disposed with this scope.
                    return document.RootElement.Clone();
                }
            }
        }

        private static string Text(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (!element.TryGetProperty(name, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static bool Bool(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return false;
            if (!element.TryGetProperty(name, out var value)) return false;
            return value.ValueKind == JsonValueKind.True;
        }

        private static int Int(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return 0;
            if (!element.TryGetProperty(name, out var value)) return 0;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
        }
    }
}
