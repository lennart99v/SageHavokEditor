using System;
using System.Collections.Generic;
using System.IO;

namespace SageHavokEditor.Core.AnimData
{
    /// <summary>
    /// A read-only model of Skyrim's <c>animationdatasinglefile.txt</c> — the
    /// clip-generator and root-motion cache that Nemesis/Pandora regenerate to
    /// register a mod's new clips.
    ///
    /// <para>We read it to answer one question: <b>is this clip's
    /// <c>animationName</c> actually registered for this project?</b> The
    /// behaviour graph names an animation by path, the character file holds the
    /// roster that path must appear in, and this cache holds the
    /// <c>animIndex</c> the runtime actually dereferences — so the graph and the
    /// character file can both look right while the cache still points a clip at
    /// a different animation. That is a T-pose with nothing in any log.</para>
    ///
    /// <para><b>We never write it.</b> Emitting this file is Nemesis/Pandora's
    /// job and there is a maintained implementation next door; the round-trip
    /// emitter that proves this parser faithful lives in
    /// <c>tools/hkx-animdata</c>, deliberately outside the shipped assembly.</para>
    ///
    /// <para>The format is <b>positional and count-prefixed</b>, so a single
    /// miscount shifts the rest of the file rather than failing locally — which
    /// is why every count is checked against the structure it introduces instead
    /// of trusted. All numeric fields are carried <b>verbatim as strings</b> and
    /// never reparsed: vanilla holds both <c>0.014</c> and <c>5.96046e-008</c>,
    /// and reformatting either would change bytes the cache is compared by.</para>
    ///
    /// <para>Grammar (from <c>havok/anim/AnimationData.h</c> in Skyrim Content
    /// Tools, and re-verified here against vanilla — 429 projects, 10,597 clip
    /// generators, 6,725 motion records):</para>
    /// <code>
    ///   FILE        := N ; name x N ; projectData x N
    ///   projectData := sectionA [ sectionB ]            // B iff hasAnimData == 1
    ///   sectionA    := aLineCount ; fieldX ; K ; assetPath x K ; hasAnimData ; clip x *
    ///   sectionB    := bLineCount ; motion x *
    ///   clip        := name ; animIndex ; playbackSpeed ; cropStart ; cropEnd ;
    ///                  T ; trigger x T ("Event:time") ; BLANK
    ///   motion      := animIndex ; duration ; TC ; tKey x TC ; RC ; rKey x RC ; BLANK
    /// </code>
    /// <para>The line counts include the blank separator after every record,
    /// including the last one.</para>
    /// </summary>
    public sealed class AnimationDataFile
    {
        /// <summary>Projects in file order. The order is significant — the
        /// header name list and the data blocks are matched positionally.</summary>
        public IReadOnlyList<AnimDataProject> Projects { get; }

        /// <summary>
        /// True when every line was CRLF-terminated including the last, with no
        /// BOM — the canonical layout the game and the offline tools write.
        /// A false here is not a parse failure (we read LF-only files fine), but
        /// it does mean the file has been through something that rewrote it.
        /// </summary>
        public bool IsCanonicalCrLf { get; }

        private AnimationDataFile(IReadOnlyList<AnimDataProject> projects, bool canonical)
        {
            Projects = projects;
            IsCanonicalCrLf = canonical;
        }

        /// <summary>
        /// Read and parse a file from disk. Latin-1 because the cache is bytes,
        /// not text: decoding as UTF-8 would replace any high byte with U+FFFD
        /// and lose the round trip.
        /// </summary>
        public static AnimationDataFile Load(string path)
            => Parse(File.ReadAllText(path, System.Text.Encoding.Latin1));

        /// <summary>
        /// Parse the whole file. Throws <see cref="AnimDataParseException"/>
        /// naming the 1-based line, because a positional format that fails
        /// silently is exactly the trap this class exists to close.
        /// </summary>
        public static AnimationDataFile Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            bool canonical = text.EndsWith("\r\n", StringComparison.Ordinal);
            var lines = SplitLines(text, ref canonical);
            var r = new LineReader(lines);

            int n = r.Count("project count");
            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = r.Next();

            var projects = new List<AnimDataProject>(n);
            for (int i = 0; i < n; i++) projects.Add(ReadProject(r, names[i]));

            if (!r.AtEnd)
                throw new AnimDataParseException(
                    $"{r.Line}: {r.Remaining} line(s) left over after the last project " +
                    "— a line count earlier in the file is short, and everything after it has shifted");

            return new AnimationDataFile(projects, canonical);
        }

        // ── Lookup ───────────────────────────────────────────────────────────

        /// <summary>
        /// The project a stem names, or null. The stem is the project name minus
        /// its last extension, matched case-insensitively, which is how the
        /// engine keys it.
        ///
        /// <para>Vanilla lists ten names <b>twice</b> — <c>SmallBird01.txt</c>,
        /// <c>Moth.txt</c>, <c>CommentTrigger.txt</c> and seven more, all
        /// header-only prop projects. Every one of those pairs is a byte-identical
        /// record, so taking the first is not a guess there; use
        /// <see cref="ProjectsWithStem"/> where a modded file might disagree.</para>
        /// </summary>
        public AnimDataProject? FindProject(string? stem)
        {
            if (string.IsNullOrWhiteSpace(stem)) return null;
            var want = StripExtension(stem.Trim());
            foreach (var p in Projects)
                if (string.Equals(p.Stem, want, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        /// <summary>Every project with this stem — see <see cref="FindProject"/>
        /// for why there can be more than one.</summary>
        public IReadOnlyList<AnimDataProject> ProjectsWithStem(string? stem)
        {
            var hits = new List<AnimDataProject>();
            if (string.IsNullOrWhiteSpace(stem)) return hits;
            var want = StripExtension(stem.Trim());
            foreach (var p in Projects)
                if (string.Equals(p.Stem, want, StringComparison.OrdinalIgnoreCase))
                    hits.Add(p);
            return hits;
        }

        /// <summary>
        /// Every project that lists the given asset — the way to get from the
        /// behaviour file someone has open back to the projects it belongs to,
        /// since a graph never names its project. Expect more than one:
        /// <c>Behaviors\0_Master.hkx</c> belongs to <c>DefaultMale</c>,
        /// <c>DefaultFemale</c> and <c>FirstPerson</c> alike.
        /// </summary>
        public IReadOnlyList<AnimDataProject> ProjectsForAsset(string? assetPath)
        {
            var hits = new List<AnimDataProject>();
            if (string.IsNullOrWhiteSpace(assetPath)) return hits;
            foreach (var p in Projects)
                if (p.ReferencesAsset(assetPath)) hits.Add(p);
            return hits;
        }

        private static string StripExtension(string s)
        {
            int dot = s.LastIndexOf('.');
            return dot > 0 ? s.Substring(0, dot) : s;
        }

        // ── Grammar ──────────────────────────────────────────────────────────

        private static AnimDataProject ReadProject(LineReader r, string name)
        {
            // Section A is a line count then exactly that many body lines. We
            // slice the body out first and walk it with its own reader, so a body
            // that under- or over-runs its declared count is caught here rather
            // than by silently eating the next project's header.
            int aCount = r.Count($"line count for project '{name}'");
            var a = r.Slice(aCount, $"section A of project '{name}'");

            string fieldX = a.Next();
            int assetCount = a.Count($"asset count for project '{name}'");
            var assets = new string[assetCount];
            for (int i = 0; i < assetCount; i++) assets[i] = a.Next();

            string hasAnimData = a.Next();
            bool hasData = hasAnimData == "1";

            var clips = new List<AnimDataClip>();
            if (hasData)
            {
                while (!a.AtEnd) clips.Add(ReadClip(a, name));
            }
            else if (!a.AtEnd)
            {
                throw new AnimDataParseException(
                    $"{a.Line}: project '{name}' says hasAnimData={hasAnimData} but its section A has " +
                    $"{a.Remaining} line(s) of body after the flag");
            }

            var motions = new List<AnimDataMotion>();
            if (hasData)
            {
                int bCount = r.Count($"motion line count for project '{name}'");
                var b = r.Slice(bCount, $"section B of project '{name}'");
                while (!b.AtEnd) motions.Add(ReadMotion(b, name));
            }

            return new AnimDataProject(name, fieldX, assets, hasData, clips, motions);
        }

        private static AnimDataClip ReadClip(LineReader a, string project)
        {
            string name = a.Next();
            string animIndex = a.Next();
            string speed = a.Next();
            string cropStart = a.Next();
            string cropEnd = a.Next();
            int triggerCount = a.Count($"trigger count for clip '{name}' in '{project}'");

            var triggers = new string[triggerCount];
            for (int i = 0; i < triggerCount; i++) triggers[i] = a.Next();

            a.ExpectBlank($"after clip '{name}' in '{project}'");
            return new AnimDataClip(name, animIndex, speed, cropStart, cropEnd, triggers);
        }

        private static AnimDataMotion ReadMotion(LineReader b, string project)
        {
            string animIndex = b.Next();
            string duration = b.Next();

            int tCount = b.Count($"translation count for motion {animIndex} in '{project}'");
            var translations = new string[tCount];
            for (int i = 0; i < tCount; i++) translations[i] = b.Next();

            int rCount = b.Count($"rotation count for motion {animIndex} in '{project}'");
            var rotations = new string[rCount];
            for (int i = 0; i < rCount; i++) rotations[i] = b.Next();

            b.ExpectBlank($"after motion {animIndex} in '{project}'");
            return new AnimDataMotion(animIndex, duration, translations, rotations);
        }

        // ── Lines ────────────────────────────────────────────────────────────

        /// <summary>
        /// Split on newlines, tolerating LF where the canonical file uses CRLF.
        /// A blank line is content here — it separates records — so the only line
        /// dropped is the empty one produced by the final terminator.
        /// </summary>
        private static List<string> SplitLines(string text, ref bool canonical)
        {
            if (text.StartsWith("﻿", StringComparison.Ordinal))
            {
                text = text.Substring(1);
                canonical = false;
            }

            var raw = text.Split('\n');
            var lines = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                var s = raw[i];
                if (s.EndsWith("\r", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
                else if (i < raw.Length - 1) canonical = false;   // an LF-only line
                lines.Add(s);
            }

            // Drop the empty element the final terminator produces. A file whose
            // last line is unterminated keeps it, because there it is real content.
            if (lines.Count > 0 && text.EndsWith("\n", StringComparison.Ordinal))
                lines.RemoveAt(lines.Count - 1);
            else
                canonical = false;

            return lines;
        }

        /// <summary>
        /// A cursor over a span of lines that reports the real 1-based file line
        /// in every message. A slice keeps the offset, so an error deep inside a
        /// project body still names the line you can open the file to.
        /// </summary>
        private sealed class LineReader
        {
            private readonly IReadOnlyList<string> _lines;
            private readonly int _end;
            private int _index;

            public LineReader(IReadOnlyList<string> lines) : this(lines, 0, lines.Count) { }

            private LineReader(IReadOnlyList<string> lines, int start, int end)
            {
                _lines = lines; _end = end; _index = start;
            }

            /// <summary>1-based file line of the cursor, for messages.</summary>
            public int Line => _index + 1;
            public bool AtEnd => _index >= _end;
            public int Remaining => _end - _index;

            public string Next()
            {
                if (AtEnd)
                    throw new AnimDataParseException($"{Line}: the file ends in the middle of a record");
                return _lines[_index++];
            }

            public int Count(string what)
            {
                var raw = Next();
                if (!int.TryParse(raw.Trim(), out int v) || v < 0)
                    throw new AnimDataParseException(
                        $"{Line - 1}: expected the {what}, got \"{Truncate(raw)}\"");
                return v;
            }

            public LineReader Slice(int count, string what)
            {
                if (Remaining < count)
                    throw new AnimDataParseException(
                        $"{Line}: {what} declares {count} line(s) but only {Remaining} remain in the file");
                var slice = new LineReader(_lines, _index, _index + count);
                _index += count;
                return slice;
            }

            public void ExpectBlank(string where)
            {
                var s = Next();
                if (s.Length != 0)
                    throw new AnimDataParseException(
                        $"{Line - 1}: expected the blank separator {where}, got \"{Truncate(s)}\" " +
                        "— a count above it is wrong and the rest of this project has shifted");
            }

            private static string Truncate(string s)
                => s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }
    }

    /// <summary>A structural failure, naming the 1-based file line it was found at.</summary>
    public sealed class AnimDataParseException : Exception
    {
        public AnimDataParseException(string message)
            : base("animationdatasinglefile.txt line " + message) { }
    }
}
