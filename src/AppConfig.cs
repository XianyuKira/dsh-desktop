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

        [JsonIgnore]
        public string ConfigPath => Path.Combine(
            PortableData ? ExeDirectory : AppDataDirectory, "settings.json");

        [JsonIgnore]
        public string WebViewDataFolder => Path.Combine(
            PortableData ? ExeDirectory : AppDataDirectory, "webview");

        [JsonIgnore]
        public string LogDirectory => Path.Combine(
            PortableData ? ExeDirectory : AppDataDirectory, "logs");

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
