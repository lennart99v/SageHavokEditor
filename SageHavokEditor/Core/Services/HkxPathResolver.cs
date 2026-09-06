using System;
using System.IO;
using System.Linq;

namespace SageHavokEditor.Core.Services
{
    /// <summary>
    /// Finding the file a Havok path names. Every path inside a behaviour project
    /// — a character's <c>behaviorFilename</c>, a reference generator's
    /// <c>behaviorName</c> — is written relative to a folder the file itself never
    /// states, and in whatever casing the author typed. Windows forgives the
    /// casing; a mod archive extracted on a case-sensitive filesystem, or read
    /// through a VFS, does not always.
    /// </summary>
    public static class HkxPathResolver
    {
        /// <summary>
        /// Walk a path segment by segment, matching each case-insensitively, and
        /// return the real path on disk — or null if any segment is missing.
        /// </summary>
        public static string? FindFileCaseInsensitive(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (File.Exists(path)) return path; // fast path on Windows

            try
            {
                var parts = path.Replace('/', '\\').Split('\\');
                var current = parts[0] + "\\"; // "C:\"

                for (int i = 1; i < parts.Length; i++)
                {
                    if (!Directory.Exists(current)) return null;
                    var target = parts[i];
                    var isLast = i == parts.Length - 1;

                    var entries = isLast
                        ? Directory.GetFiles(current).Select(Path.GetFileName).ToArray()
                        : Directory.GetDirectories(current).Select(Path.GetFileName).ToArray();

                    var match = entries.FirstOrDefault(e =>
                        string.Equals(e, target, StringComparison.OrdinalIgnoreCase));

                    if (match == null) return null;
                    current = Path.Combine(current, match);
                }
                return File.Exists(current) ? current : null;
            }
            catch { return null; }
        }

        /// <summary>Combine, swallowing the malformed-path exceptions. Null on failure.</summary>
        public static string? TryCombine(string? dir, string? rel)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(rel)) return null;
            try { return Path.GetFullPath(Path.Combine(dir, rel)); }
            catch { return null; }
        }
    }
}
