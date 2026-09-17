using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshDesktop
{
    /// <summary>
    /// Launcher settings, persisted next to the executable in <c>settings.json</c>.
    /// Everything is optional: a missing file means "use the defaults".
    /// </summary>
    internal sealed class AppConfig
    {
        /// <summary>Folder the bundled <c>dsh</c> runs in; that folder is the default workspace.</summary>
        public string Workspace { get; set; }

        /// <summary>Port to try first. 0 lets the OS pick one.</summary>
        public int PreferredPort { get; set; } = 3080;

        /// <summary>Remember a port only after it was actually used, so a fallback port is never persisted.</summary>
        public bool RememberPort { get; set; } = true;

        /// <summary>Keep <c>settings.json</c> / <c>webview/</c> next to the exe instead of under %LOCALAPPDATA%.</summary>
        public bool PortableData { get; set; }

        /// <summary>
        /// Where session data, caches and logs live. Set at install time or from the setup dialog,
        /// so a machine with a small system drive can keep all of it elsewhere. When empty, the
        /// portable rule applies (next to the exe if <see cref="PortableData"/>, else %LOCALAPPDATA%).
        /// </summary>
        public string DataRoot { get; set; }

        /// <summary>True once the first-run question (API key + data folder) has been answered.</summary>
        public bool SetupComplete { get; set; }

        /// <summary>Force a fixed port: never fall back to an OS-assigned one when it is busy.</summary>
        public bool StrictPort { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string ExeDirectory => AppContext.BaseDirectory;

        public static string AppDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshDesktop");

        /// <summary>
        /// Environment overrides, used by automated tests so a test run never reads or writes the
        /// settings a real user is relying on. Setting <c>DSH_DESKTOP_CONFIG</c> to a file path
        /// isolates configuration entirely; <c>DSH_DESKTOP_DATA_ROOT</c> redirects runtime data.
        /// </summary>
        public const string ConfigPathVariable = "DSH_DESKTOP_CONFIG";
        public const string DataRootVariable = "DSH_DESKTOP_DATA_ROOT";

        private static string EnvironmentPath(string name)
        {
            try
            {
                var value = Environment.GetEnvironmentVariable(name);
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
            }
            catch { return null; }
        }

        /// <summary>
        /// Where settings.json lives. This must stay findable even when <see cref="DataRoot"/>
        /// points elsewhere — otherwise changing the data folder would lose the setting itself.
        /// Precedence: environment override, a "portable.marker" next to the exe, then %LOCALAPPDATA%.
        /// </summary>
        [JsonIgnore]
        public string ConfigPath
        {
            get
            {
                var overridden = EnvironmentPath(ConfigPathVariable);
                if (overridden != null) return overridden;

                try
                {
                    if (File.Exists(Path.Combine(ExeDirectory, "portable.marker")))
                        return Path.Combine(ExeDirectory, "settings.json");
                }
                catch { }
                return Path.Combine(AppDataDirectory, "settings.json");
            }
        }

        /// <summary>
        /// The folder holding runtime data, following <see cref="DataRoot"/> when set.
        ///
        /// A packaged install defaults to <c>data\</c> next to the program: that leaves the system
        /// drive untouched unless the user deliberately picks somewhere else, and it keeps the
        /// install self-contained — delete the folder, delete everything.
        /// </summary>
        [JsonIgnore]
        public string DataDirectory
        {
            get
            {
                // Tests redirect here so they never write into a real user's data folder.
                var overridden = EnvironmentPath(DataRootVariable);
                if (overridden != null) return Path.GetFullPath(overridden);

                if (!string.IsNullOrWhiteSpace(DataRoot))
                {
                    try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataRoot.Trim().Trim('"'))); }
                    catch { }
                }
                if (IsBundled) return Path.Combine(ExeDirectory, "data");
                return PortableData ? ExeDirectory : AppDataDirectory;
            }
        }

        [JsonIgnore]
        public string WebViewDataFolder => Path.Combine(DataDirectory, "webview");

        [JsonIgnore]
        public string LogDirectory => Path.Combine(DataDirectory, "logs");

        /// <summary>
        /// The bundled runtime, when this is a packaged install: <c>runtime\node\node.exe</c> and
        /// <c>runtime\vendor</c> next to the executable. Null for a plain source/portable build.
        /// </summary>
        [JsonIgnore]
        public string BundledNode => ExistingFile(Path.Combine(ExeDirectory, "runtime", "node", "node.exe"));

        [JsonIgnore]
        public string BundledVendor => ExistingDirectory(Path.Combine(ExeDirectory, "runtime", "vendor"));

        [JsonIgnore]
        public string BundledProfile => ExistingDirectory(Path.Combine(ExeDirectory, "runtime", "profile-web"));

        [JsonIgnore]
        public bool IsBundled => BundledNode != null && BundledVendor != null;

        /// <summary>The DSH_HOME this install uses, or null to let dsh keep its own default.</summary>
        [JsonIgnore]
        public string DshHome
        {
            get
            {
                if (!IsBundled) return null;
                return Path.Combine(DataDirectory, "dsh-home");
            }
        }

        private static string ExistingFile(string path)
        {
            try { return File.Exists(path) ? path : null; }
            catch { return null; }
        }

        private static string ExistingDirectory(string path)
        {
            try { return Directory.Exists(path) ? path : null; }
            catch { return null; }
        }

        public static AppConfig Load()
        {
            var probe = new AppConfig();
            try
            {
                if (File.Exists(probe.ConfigPath))
                {
                    var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(probe.ConfigPath), JsonOptions);
                    if (loaded != null)
                    {
                        loaded.PortableData = probe.PortableData;
                        if (string.IsNullOrWhiteSpace(loaded.Workspace)) loaded.Workspace = probe.Workspace;
                        return loaded;
                    }
                }
            }
            catch
            {
                // A broken settings file must never stop the app from starting.
            }
            return probe;
        }

        public void Save()
        {
            try
            {
                var path = ConfigPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
                // Persisting preferences is best-effort.
            }
        }

        public string ResolveWorkspace()
        {
            var candidate = Workspace;
            if (string.IsNullOrWhiteSpace(candidate))
                candidate = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"')));
            }
            catch
            {
                candidate = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            return candidate;
        }

        /// <summary>Ports to try, in order: the remembered one first, then the OS-assigned wildcard.</summary>
        public IEnumerable<int> PortCandidates()
        {
            if (PreferredPort > 0) yield return PreferredPort;
            if (!StrictPort) yield return 0;
        }
    }
}
