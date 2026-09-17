using System;
using System.Collections.Generic;

namespace SageHavokEditor.Core.AnimData
{
    /// <summary>What the cache says about one clip generator.</summary>
    public enum ClipCacheStatus
    {
        /// <summary>No cache loaded, or no project in it to ask — we have no
        /// opinion, which is different from an answer of "fine".</summary>
        Unknown,

        /// <summary>The cache has this clip, and its <c>animIndex</c> resolves to
        /// the animation the graph names. When no roster was available the index
        /// half is untested — see <see cref="ClipCacheVerdict.RosterChecked"/>.</summary>
        Registered,

        /// <summary>No record with this clip's name. Nemesis/Pandora regenerate
        /// the cache to add one, so this is usually "the patcher hasn't been run
        /// since this clip was added", and in-game the clip has no root motion
        /// and none of its cache triggers.</summary>
        NotInCache,

        /// <summary>Several records match the name case-insensitively and none
        /// exactly, so which one the runtime means is not ours to guess.</summary>
        NameAmbiguous,

        /// <summary><c>animIndex</c> is a Nemesis patch symbol rather than a
        /// number — the cache is a patch fragment, not a compiled one.</summary>
        IndexUnresolved,

        /// <summary><c>animIndex</c> points past the end of the character's
        /// roster. The runtime reads it positionally and does not bounds-check.</summary>
        IndexOutOfRange,

        /// <summary>The cache resolves this clip to a <b>different animation</b>
        /// than the graph names. The graph and the character file can both look
        /// right and the actor still plays the wrong thing.</summary>
        AnimationMismatch,
    }

    /// <summary>The cache's answer about one clip, with what it was based on.</summary>
    public sealed class ClipCacheVerdict
    {
        public ClipCacheStatus Status { get; init; }

        /// <summary>The project the answer came from, so a report can say which.</summary>
        public AnimDataProject? Project { get; init; }

        /// <summary>The matched record, when there was one.</summary>
        public AnimDataClip? Clip { get; init; }

        /// <summary>The animation <c>animIndex</c> resolved to, when it resolved.</summary>
        public string? CachedAnimation { get; init; }

        /// <summary>
        /// False when no character roster was available, so only the weaker
        /// question — does a record exist — was actually answered.
        /// </summary>
        public bool RosterChecked { get; init; }

        /// <summary>One sentence for a dialog or a validation row.</summary>
        public string Explanation { get; init; } = "";

        /// <summary>True for the states worth interrupting someone over.</summary>
        public bool IsProblem => Status is ClipCacheStatus.NotInCache
                                        or ClipCacheStatus.NameAmbiguous
                                        or ClipCacheStatus.IndexOutOfRange
                                        or ClipCacheStatus.AnimationMismatch;
    }

    /// <summary>
    /// Answers "is this clip's <c>animationName</c> actually registered for this
    /// project?" against <c>animationdatasinglefile.txt</c>.
    ///
    /// <para>This is the third side of a trap whose other two sides the editor
    /// already watches. The graph names an animation by path; the character
    /// file's <c>animationNames</c> roster has to contain that path; and the
    /// cache stores the <b>position in that roster</b> which the runtime
    /// actually dereferences. Check only the first two and a clip can pass both
    /// while the cache still sends it to a different animation — which is why
    /// the interesting verdict here is not "missing" but
    /// <see cref="ClipCacheStatus.AnimationMismatch"/>.</para>
    ///
    /// <para>Pure and roster-injected so it can be driven without a workspace.</para>
    /// </summary>
    public sealed class ClipCacheCheck
    {
        private readonly IReadOnlyList<AnimDataProject> _projects;
        private readonly IReadOnlyList<string> _roster;

        /// <param name="projects">
        /// Every project the open behaviour could belong to. Usually one, but a
        /// shared graph belongs to several — <c>0_Master.hkx</c> is listed by
        /// <c>DefaultMale</c>, <c>DefaultFemale</c> and <c>FirstPerson</c> alike —
        /// and picking one of those to answer from would be a guess with a
        /// different roster behind it. A clip that any candidate is happy with is
        /// reported as fine; a problem is reported only when <b>none</b> of them
        /// is, which is the claim we can actually stand behind.
        /// </param>
        /// <param name="roster">
        /// The character's <c>animationNames</c> in order. Empty answers only the
        /// weaker question rather than reporting every clip as mismatched — with
        /// no roster, an <c>animIndex</c> resolves to nothing at all, and that is
        /// a fact about us rather than about the graph.
        /// </param>
        public ClipCacheCheck(IEnumerable<AnimDataProject>? projects, IEnumerable<string>? roster = null)
        {
            _projects = projects as IReadOnlyList<AnimDataProject>
                        ?? new List<AnimDataProject>(projects ?? Array.Empty<AnimDataProject>());
            _roster = roster as IReadOnlyList<string> ?? new List<string>(roster ?? Array.Empty<string>());
        }

        /// <summary>Convenience for the single-project case.</summary>
        public ClipCacheCheck(AnimDataProject? project, IEnumerable<string>? roster = null)
            : this(project == null ? Array.Empty<AnimDataProject>() : new[] { project }, roster) { }

        /// <summary>
        /// Ask about one clip generator. With several candidate projects, the
        /// answer is the happiest one — and if none is happy, the most specific
        /// complaint among them, since that is the one worth acting on.
        /// </summary>
        /// <param name="clipName">The generator's <c>name</c>.</param>
        /// <param name="animationName">Its <c>animationName</c> path.</param>
        public ClipCacheVerdict Check(string? clipName, string? animationName)
        {
            if (_projects.Count == 0 || string.IsNullOrWhiteSpace(clipName))
                return new ClipCacheVerdict
                {
                    Status = ClipCacheStatus.Unknown,
                    Explanation = "no animationdatasinglefile.txt project to check against",
                };

            ClipCacheVerdict? worst = null;
            foreach (var p in _projects)
            {
                var v = CheckOne(p, clipName, animationName);
                if (!v.IsProblem) return v;                 // registered somewhere: good enough
                if (worst == null || Severity(v.Status) > Severity(worst.Status)) worst = v;
            }

            // The complaint above names one project, but all of them were asked
            // and all of them complained. Saying only the first would read as
            // though the others had not been looked at.
            if (_projects.Count > 1)
                worst = new ClipCacheVerdict
                {
                    Status = worst!.Status,
                    Project = worst.Project,
                    Clip = worst.Clip,
                    CachedAnimation = worst.CachedAnimation,
                    RosterChecked = worst.RosterChecked,
                    Explanation = worst.Explanation
                        + $". This behaviour belongs to {_projects.Count} cache projects "
                        + $"({string.Join(", ", _projects.Select(p => p.Stem))}) and none of them "
                        + "is satisfied",
                };

            return worst!;
        }

        /// <summary>
        /// How specific a complaint is. A mismatch names a concrete wrong
        /// animation and outranks "there is no record", which on a multi-project
        /// graph is the vaguer of the two.
        /// </summary>
        private static int Severity(ClipCacheStatus s) => s switch
        {
            ClipCacheStatus.AnimationMismatch => 4,
            ClipCacheStatus.IndexOutOfRange => 3,
            ClipCacheStatus.NameAmbiguous => 2,
            ClipCacheStatus.NotInCache => 1,
            _ => 0,
        };

        private ClipCacheVerdict CheckOne(AnimDataProject _project, string clipName, string? animationName)
        {
            var where = $"the animation cache's {_project.Name}";
            var clip = _project.FindClip(clipName, out bool ambiguous);

            if (ambiguous)
                return new ClipCacheVerdict
                {
                    Status = ClipCacheStatus.NameAmbiguous,
                    Project = _project,
                    Explanation = $"{where} has more than one clip named \"{clipName}\" differing only "
                                + "in case, so which one the runtime means can't be determined here",
                };

            if (clip == null)
                return new ClipCacheVerdict
                {
                    Status = ClipCacheStatus.NotInCache,
                    Project = _project,
                    Explanation = $"{where} has no clip named \"{clipName}\" — the animation cache "
                                + "hasn't been regenerated since this clip was added, so in-game it "
                                + "has no root motion and none of its cache triggers",
                };

            var index = clip.Index;
            if (index == null)
                return new ClipCacheVerdict
                {
                    Status = ClipCacheStatus.IndexUnresolved,
                    Project = _project, Clip = clip,
                    Explanation = $"\"{clipName}\" carries the patch symbol \"{clip.AnimIndex}\" rather "
                                + "than a resolved index — this is a Nemesis patch fragment, not the "
                                + "compiled cache the game reads",
                };

            // With no roster there is no index to resolve against, so stop at the
            // weaker answer instead of inventing a stronger one.
            if (_roster.Count == 0)
                return new ClipCacheVerdict
                {
                    Status = ClipCacheStatus.Registered,
                    Project = _project, Clip = clip, RosterChecked = false,
                    Explanation = $"{where} has \"{clipName}\" at animIndex {clip.AnimIndex}; open the "
                                + "character file to check that index points at the right animation",
                };

            if (index < 0 || index >= _roster.Count)
                return new ClipCacheVerdict
                {
                    Status = ClipCacheStatus.IndexOutOfRange,
                    Project = _project, Clip = clip, RosterChecked = true,
                    Explanation = $"{where} points \"{clipName}\" at animIndex {clip.AnimIndex}, but the "
                                + $"character's roster holds {_roster.Count} (0…{_roster.Count - 1}) — "
                                + "the runtime reads that index positionally and does not bounds-check it",
                };

            var cached = _roster[index.Value];
            if (!string.IsNullOrWhiteSpace(animationName)
                && !PathsEqual(cached, animationName))
                return new ClipCacheVerdict
                {
                    Status = ClipCacheStatus.AnimationMismatch,
                    Project = _project, Clip = clip, CachedAnimation = cached, RosterChecked = true,
                    Explanation = $"{where} points \"{clipName}\" at animIndex {clip.AnimIndex}, which is "
                                + $"\"{cached}\" — but the graph says it plays \"{animationName}\". The "
                                + "runtime follows the cache, so this clip plays the wrong animation "
                                + "with nothing in any log",
                };

            return new ClipCacheVerdict
            {
                Status = ClipCacheStatus.Registered,
                Project = _project, Clip = clip, CachedAnimation = cached, RosterChecked = true,
                Explanation = $"{where} resolves \"{clipName}\" to \"{cached}\"",
            };
        }

        private static bool PathsEqual(string? a, string? b)
            => string.Equals(
                (a ?? "").Trim().Replace('/', '\\'),
                (b ?? "").Trim().Replace('/', '\\'),
                StringComparison.OrdinalIgnoreCase);
    }
}
