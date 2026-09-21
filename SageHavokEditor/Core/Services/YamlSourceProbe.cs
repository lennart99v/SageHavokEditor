using System;
using System.Collections.Generic;
using System.IO;

namespace SageHavokEditor.Core.Services
{
    /// <summary>What a Community Behaviors YAML source describes.</summary>
    public enum YamlSourceKind
    {
        /// <summary>Not YAML source at all — a packfile, Havok XML, or something else.</summary>
        None = 0,
        Behavior,
        Character,
        Project,
        Animation,
        /// <summary>YAML, but none of the four documents we know how to name.</summary>
        Unknown,
    }

    /// <summary>
    /// Tells a Community Behaviors YAML source from a Havok packfile by reading it,
    /// because the name no longer tells you.
    ///
    /// In her compiler a <c>.hkx</c> path is a *compile target*, not necessarily a
    /// binary: a <c>&lt;stem&gt;.hkx</c> <b>folder</b> is a multi-file unit
    /// (<c>behavior.yaml</c> plus <c>clips/</c>, <c>states/</c>, …), and a
    /// <c>&lt;stem&gt;.hkx</c> <b>file</b> is a single-file unit whose contents are
    /// YAML — which is how her animations are authored (<c>Resolver.cpp</c>: "the
    /// <c>.hkx</c> path IS the output name"). Assets are discovered by that suffix
    /// and the relative path is the roster entry, so there is no separate
    /// registration step and no second extension left to dispatch on.
    ///
    /// Everything here reads the head of the file only. The classification comes
    /// from the top-level keys, which is where all four documents declare
    /// themselves: <c>behavior:</c>, <c>character:</c>, <c>project:</c>, and for an
    /// animation either an <c>animation:</c> wrapper or its bare <c>tracks:</c> /
    /// <c>floatTracks:</c> — the wrapper is optional, per her
    /// <c>AnimationYamlLoader.h</c>.
    /// </summary>
    public static class YamlSourceProbe
    {
        /// <summary>
        /// Enough head to reach the top-level keys. An animation writes
        /// name/duration/skeleton/compression — a few hundred bytes — before its
        /// first <c>tracks:</c>, and a unit document is smaller still, so this is
        /// slack rather than a budget.
        /// </summary>
        private const int HeadBytes = 32 * 1024;

        /// <summary>The unit documents, by the file that declares one.</summary>
        private static readonly (string File, YamlSourceKind Kind)[] UnitDocuments =
        {
            ("behavior.yaml",  YamlSourceKind.Behavior),
            ("character.yaml", YamlSourceKind.Character),
            ("project.yaml",   YamlSourceKind.Project),
            ("animation.yaml", YamlSourceKind.Animation),
        };

        /// <summary>
        /// A behaviour unit whose root document is missing: the node folders are the
        /// shape. Kept from the original folder test so nothing that opened before
        /// stops opening.
        /// </summary>
        private static readonly string[] BehaviorSubdirectories =
            { "clips", "generators", "states", "modifiers", "transitions" };

        // ── Files ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// What <paramref name="path"/> is, read from its contents.
        /// <see cref="YamlSourceKind.None"/> for a packfile, for Havok XML, for
        /// anything binary, and for a file that isn't there.
        /// </summary>
        public static YamlSourceKind ProbeFile(string path)
        {
            byte[] head;
            try
            {
                using var fs = File.OpenRead(path);
                head = new byte[(int)Math.Min(fs.Length, HeadBytes)];
                int read = 0;
                while (read < head.Length)
                {
                    int n = fs.Read(head, read, head.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read < head.Length) Array.Resize(ref head, read);
            }
            catch { return YamlSourceKind.None; }

            return ProbeBytes(head);
        }

        /// <summary>
        /// <see cref="ProbeFile"/> over a head already in memory — the form the
        /// harness drives, and the one that makes the rule testable without a file.
        /// </summary>
        public static YamlSourceKind ProbeBytes(ReadOnlySpan<byte> head)
        {
            if (IsPackfile(head)) return YamlSourceKind.None;

            // A NUL anywhere in the head means binary, whatever the first bytes
            // said. The text this decides over is UTF-8 or ASCII; neither writes one.
            foreach (var b in head)
                if (b == 0) return YamlSourceKind.None;

            var text = Decode(head);
            var keys = TopLevelKeys(text, out bool startsWithAngleBracket);
            if (startsWithAngleBracket) return YamlSourceKind.None;   // Havok XML
            if (keys.Count == 0) return YamlSourceKind.None;          // no mapping — not a YAML document

            foreach (var (file, kind) in UnitDocuments)
            {
                var declared = file[..^5];                            // "behavior.yaml" → "behavior"
                if (keys.Contains(declared)) return kind;
            }

            // An animation may omit the wrapper, in which case its tracks are the
            // only thing that names it.
            if (keys.Contains("tracks") || keys.Contains("floattracks"))
                return YamlSourceKind.Animation;

            return YamlSourceKind.Unknown;
        }

        /// <summary>Havok packfile magic, <c>57 E0 E0 57</c>.</summary>
        public static bool IsPackfile(ReadOnlySpan<byte> head) =>
            head.Length >= 4 &&
            head[0] == 0x57 && head[1] == 0xE0 && head[2] == 0xE0 && head[3] == 0x57;

        // ── Units (folders) ───────────────────────────────────────────────────────

        /// <summary>
        /// What the directory <paramref name="path"/> is a unit of, or
        /// <see cref="YamlSourceKind.None"/> if it isn't one.
        /// </summary>
        public static YamlSourceKind ProbeUnit(string path)
        {
            if (!Directory.Exists(path)) return YamlSourceKind.None;

            foreach (var (file, kind) in UnitDocuments)
                if (File.Exists(Path.Combine(path, file))) return kind;

            foreach (var sub in BehaviorSubdirectories)
                if (Directory.Exists(Path.Combine(path, sub))) return YamlSourceKind.Behavior;

            return YamlSourceKind.None;
        }

        /// <summary>
        /// When a unit was last written — the newest of the YAML documents under it.
        ///
        /// The folder's own timestamp is no use for this: Windows moves it when a
        /// file is created, renamed or deleted, and not when one is edited, so a
        /// unit whose clip changed under you looks untouched. Every caller that
        /// caches a read of a unit is caching it against somebody editing that unit
        /// in the other window, which is exactly the case the folder stamp misses.
        /// The timestamps come off the directory walk itself, so this is one scan
        /// rather than a stat per file.
        /// </summary>
        public static DateTime UnitWriteTimeUtc(string unitPath)
        {
            try
            {
                var newest = Directory.GetLastWriteTimeUtc(unitPath);
                foreach (var f in new DirectoryInfo(unitPath)
                             .EnumerateFiles("*.yaml", SearchOption.AllDirectories))
                    if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
                return newest;
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// The unit a loose YAML document belongs to — its own folder, when that
        /// folder is a unit. Dropping <c>…\sprintbehavior.hkx\behavior.yaml</c> on
        /// the window means the behaviour, not the one file, because a unit is only
        /// meaningful whole.
        /// </summary>
        public static string? OwningUnit(string filePath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return dir != null && ProbeUnit(dir) != YamlSourceKind.None ? dir : null;
        }

        // ── Wording ───────────────────────────────────────────────────────────────

        /// <summary>How to name this kind in a message to the user.</summary>
        public static string Describe(YamlSourceKind kind) => kind switch
        {
            YamlSourceKind.Behavior  => "a behaviour graph",
            YamlSourceKind.Character => "a character",
            YamlSourceKind.Project   => "a project",
            YamlSourceKind.Animation => "an animation",
            YamlSourceKind.Unknown   => "something this editor doesn't recognise",
            _                        => "not YAML source",
        };

        // ── YAML head reading ─────────────────────────────────────────────────────

        private static string Decode(ReadOnlySpan<byte> head)
        {
            // Skip a UTF-8 BOM — her behaviour documents carry one.
            if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                head = head[3..];
            return System.Text.Encoding.UTF8.GetString(head);
        }

        /// <summary>
        /// Every key written at column 0, lowercased. Column 0 is what makes a key
        /// top-level, which is the whole reason this can be read without a YAML
        /// parser: a nested <c>name:</c> is indented, and a document's own
        /// declaration never is.
        /// </summary>
        private static HashSet<string> TopLevelKeys(string text, out bool startsWithAngleBracket)
        {
            startsWithAngleBracket = false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            bool seenContent = false;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                var trimmed = line.TrimStart();
                if (trimmed.Length == 0) continue;
                if (trimmed[0] == '#') continue;
                if (trimmed == "---" || trimmed == "...") continue;

                if (!seenContent)
                {
                    seenContent = true;
                    if (trimmed[0] == '<') { startsWithAngleBracket = true; return keys; }
                }

                if (line.Length != trimmed.Length) continue;          // indented — not top-level

                var key = KeyOf(line);
                if (key != null) keys.Add(key);
            }

            return keys;
        }

        /// <summary>
        /// The key of <c>key: value</c>, lowercased, or null if this line isn't a
        /// mapping entry. Deliberately strict about what a key may contain, so a
        /// stray line of prose with a colon in it doesn't read as YAML.
        /// </summary>
        private static string? KeyOf(string line)
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            if (colon + 1 < line.Length && line[colon + 1] != ' ' && line[colon + 1] != '\t')
                return null;

            var key = line.Substring(0, colon).Trim().Trim('"', '\'');
            if (key.Length == 0) return null;

            foreach (var c in key)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.') return null;

            return key.ToLowerInvariant();
        }
    }
}
