using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SkyrimHavokEditor
{
    public static class AppSettings
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SkyrimHavokEditor", "settings.txt");

        private static Dictionary<string, string> _cache;

        private static Dictionary<string, string> Load()
        {
            if (_cache != null) return _cache;
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(_path)) return _cache;
                foreach (var line in File.ReadAllLines(_path))
                {
                    var idx = line.IndexOf('=');
                    if (idx > 0)
                        _cache[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }
            }
            catch { }
            return _cache;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllLines(_path,
                    Load().Select(kv => $"{kv.Key}={kv.Value}"));
            }
            catch { }
        }

        private static string Get(string key, string defaultValue = "")
        {
            Load().TryGetValue(key, out var val);
            return val ?? defaultValue;
        }

        private static void Set(string key, string value)
        {
            Load()[key] = value;
            Save();
        }

        // ── Public properties ────────────────────────────────────────────

        /// <summary>Root Skyrim install folder (contains Data/).</summary>
        public static string GamePath
        {
            get => Get("GamePath");
            set => Set("GamePath", value);
        }

        /// <summary>
        /// Path to Data\Meshes — derived from GamePath automatically,
        /// but can be overridden if the user has a custom setup.
        /// </summary>
        public static string MeshesPath
        {
            get
            {
                var custom = Get("MeshesPath");
                if (!string.IsNullOrEmpty(custom)) return custom;
                var gp = GamePath;
                return string.IsNullOrEmpty(gp)
                    ? ""
                    : Path.Combine(gp, "Data", "Meshes");
            }
            set => Set("MeshesPath", value);
        }

        public static bool IsDarkMode
        {
            get => Get("DarkMode", "true") == "true";
            set => Set("DarkMode", value ? "true" : "false");
        }

        /// <summary>Default view axis for the clip preview: "Side", "Front", or "Top".</summary>
        public static string PreviewDefaultAxis
        {
            get => Get("PreviewDefaultAxis", "Side");
            set => Set("PreviewDefaultAxis", value);
        }

        /// <summary>Whether the clip preview starts playing automatically when opened.</summary>
        public static bool PreviewAutoplay
        {
            get => Get("PreviewAutoplay", "true") == "true";
            set => Set("PreviewAutoplay", value ? "true" : "false");
        }
    }
}