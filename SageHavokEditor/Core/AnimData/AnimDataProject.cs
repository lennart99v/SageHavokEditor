using System;
using System.Collections.Generic;
using System.Linq;

namespace SageHavokEditor.Core.AnimData
{
    /// <summary>
    /// One clip generator's cache record. Every numeric field is the file's own
    /// text, never a reparsed number — see <see cref="AnimationDataFile"/>.
    /// </summary>
    public sealed class AnimDataClip
    {
        /// <summary>The clip generator's name, matching <c>hkbClipGenerator.name</c>
        /// in the behaviour graph. <b>Case is significant</b> — see
        /// <see cref="AnimDataProject.FindClip"/>.</summary>
        public string Name { get; }

        /// <summary>Position in the character's <c>animationNames</c> roster —
        /// <b>not</b> <c>hkbClipGenerator.animationBindingIndex</c>, which is -1
        /// throughout vanilla. Verbatim, so a Nemesis patch's <c>code$N</c>
        /// symbol survives being read.</summary>
        public string AnimIndex { get; }

        public string PlaybackSpeed { get; }
        public string CropStart { get; }
        public string CropEnd { get; }

        /// <summary>Raw <c>"Event:time"</c> lines, in the cache's own order.</summary>
        public IReadOnlyList<string> Triggers { get; }

        public AnimDataClip(string name, string animIndex, string playbackSpeed,
            string cropStart, string cropEnd, IReadOnlyList<string> triggers)
        {
            Name = name; AnimIndex = animIndex; PlaybackSpeed = playbackSpeed;
            CropStart = cropStart; CropEnd = cropEnd; Triggers = triggers;
        }

        /// <summary>
        /// The <see cref="AnimIndex"/> as a number, or null when it is a Nemesis
        /// patch symbol rather than a resolved index.
        /// </summary>
        public int? Index => int.TryParse(AnimIndex, out int v) ? v : (int?)null;

        public override string ToString() => $"{Name} → #{AnimIndex}";
    }

    /// <summary>
    /// One root-motion record: the translation and rotation keys the runtime
    /// applies as root motion. Keyed to a clip by <see cref="AnimIndex"/>.
    /// </summary>
    public sealed class AnimDataMotion
    {
        public string AnimIndex { get; }
        public string Duration { get; }
        public IReadOnlyList<string> Translations { get; }
        public IReadOnlyList<string> Rotations { get; }

        public AnimDataMotion(string animIndex, string duration,
            IReadOnlyList<string> translations, IReadOnlyList<string> rotations)
        {
            AnimIndex = animIndex; Duration = duration;
            Translations = translations; Rotations = rotations;
        }

        public int? Index => int.TryParse(AnimIndex, out int v) ? v : (int?)null;

        public override string ToString()
            => $"#{AnimIndex} {Duration}s ({Translations.Count}t/{Rotations.Count}r)";
    }

    /// <summary>
    /// One project's cache block. 429 of these in vanilla, of which only 49
    /// carry clip data — the other 380 are header-only prop and trigger
    /// projects that declare their assets and nothing else.
    /// </summary>
    public sealed class AnimDataProject
    {
        /// <summary>As written in the header list, e.g. <c>"DefaultMale.txt"</c>.</summary>
        public string Name { get; }

        /// <summary>The constant leading <c>"1"</c>, kept verbatim.</summary>
        public string FieldX { get; }

        /// <summary>Behaviour, character and skeleton paths, e.g.
        /// <c>"Behaviors\0_Master.hkx"</c>.</summary>
        public IReadOnlyList<string> AssetPaths { get; }

        /// <summary>The <c>hasAnimData</c> flag — false means no clips and no
        /// section B at all.</summary>
        public bool HasAnimData { get; }

        public IReadOnlyList<AnimDataClip> Clips { get; }
        public IReadOnlyList<AnimDataMotion> Motions { get; }

        public AnimDataProject(string name, string fieldX, IReadOnlyList<string> assetPaths,
            bool hasAnimData, IReadOnlyList<AnimDataClip> clips, IReadOnlyList<AnimDataMotion> motions)
        {
            Name = name; FieldX = fieldX; AssetPaths = assetPaths;
            HasAnimData = hasAnimData; Clips = clips; Motions = motions;
        }

        /// <summary>
        /// The project stem the engine keys on: the name minus its <i>last</i>
        /// extension, so <c>"DefaultMale.txt"</c> → <c>"DefaultMale"</c>.
        /// </summary>
        public string Stem
        {
            get
            {
                int dot = Name.LastIndexOf('.');
                return dot > 0 ? Name.Substring(0, dot) : Name;
            }
        }

        /// <summary>
        /// Find a clip by name, <b>case-sensitively first</b>.
        ///
        /// <para>The case sensitivity is not pedantry, it is vanilla data.
        /// <c>DefaultMale</c> carries both <c>CrossBow_IdleHeld</c> (animIndex
        /// 557) and <c>Crossbow_IdleHeld</c> (animIndex 941) — two records
        /// differing only in one letter's case, pointing at <b>different
        /// animations</b> — and both spellings are really used by vanilla
        /// graphs (<c>CrossBow_</c> in six behaviour files, <c>Crossbow_</c> once,
        /// in <c>horsebehavior</c>). A case-insensitive lookup answers one of
        /// them with the other's animation, which is the silent-wrong-answer
        /// shape this editor tries hard not to have.</para>
        ///
        /// <para>An exact match wins outright. Failing that we accept a
        /// case-insensitive match only when it is <b>unique</b> — that covers a
        /// modded graph whose casing drifted, and the harmless vanilla pair
        /// <c>Tor_Idle</c>/<c>TOR_Idle</c> (same animIndex) — and return null
        /// when it is ambiguous, rather than guessing.</para>
        /// </summary>
        /// <param name="ambiguous">
        /// Set when the name matched several records case-insensitively and none
        /// exactly, so the caller can say so instead of reporting "not registered".
        /// </param>
        public AnimDataClip? FindClip(string? name, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrEmpty(name)) return null;

            foreach (var c in Clips)
                if (string.Equals(c.Name, name, StringComparison.Ordinal))
                    return c;

            AnimDataClip? loose = null;
            foreach (var c in Clips)
            {
                if (!string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (loose != null) { ambiguous = true; return null; }
                loose = c;
            }
            return loose;
        }

        /// <summary>Convenience overload for callers that don't need the ambiguity flag.</summary>
        public AnimDataClip? FindClip(string? name) => FindClip(name, out _);

        /// <summary>
        /// The root-motion record for an <c>animIndex</c>, or null when the clip
        /// has none — most clips don't (vanilla has 10,597 clips to 6,725
        /// motions), so a miss here is normal rather than a fault.
        /// </summary>
        public AnimDataMotion? FindMotion(string? animIndex)
            => string.IsNullOrEmpty(animIndex)
                ? null
                : Motions.FirstOrDefault(m => string.Equals(m.AnimIndex, animIndex, StringComparison.Ordinal));

        /// <summary>True when this project lists the given behaviour/character/skeleton
        /// asset, compared as a Havok path (case- and separator-insensitive).</summary>
        public bool ReferencesAsset(string? assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return false;
            var want = AnimDataPaths.Normalize(assetPath);
            foreach (var a in AssetPaths)
                if (AnimDataPaths.Normalize(a) == want) return true;
            return false;
        }

        public override string ToString()
            => HasAnimData ? $"{Name} ({Clips.Count} clips, {Motions.Count} motions)"
                           : $"{Name} (header only)";
    }

    /// <summary>Havok paths compare case-insensitively and either slash.</summary>
    internal static class AnimDataPaths
    {
        public static string Normalize(string p)
            => (p ?? "").Trim().Replace('/', '\\').ToLowerInvariant();
    }
}
