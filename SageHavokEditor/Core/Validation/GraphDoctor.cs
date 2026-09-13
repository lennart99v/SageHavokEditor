using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SageHavokEditor.Models;

namespace SageHavokEditor.Core.Validation
{
    /// <summary>What one pass of the doctor found.</summary>
    public sealed class GraphDoctorReport
    {
        /// <summary>Errors first, then warnings; within each, the order the checks ran.</summary>
        public List<ValidationIssue> Issues { get; init; } = new();

        /// <summary>How many objects an <c>.hkx</c> save would drop — see <see cref="ValidationIssue.CategoryPruned"/>.</summary>
        public int PrunedCount { get; init; }

        /// <summary>
        /// The behaviour graph's own name (<c>DragonBehavior.hkb</c>), for the
        /// refusal line. A file holds one graph, but the person reading the
        /// message may have several open across the project, and the name is what
        /// they recognise it by. Empty for a file with no graph in it.
        /// </summary>
        public string GraphName { get; init; } = "";

        /// <summary>
        /// One line per refused finding: which graph, what failed, and the likely
        /// cause. Nothing here is discoverable from the file afterwards — a graph
        /// this file describes is accepted by the converter and by Havok, so the
        /// only place the reason can be said is here, before the write.
        /// </summary>
        public string RefusalLine(ValidationIssue issue)
        {
            var where = string.IsNullOrEmpty(GraphName) ? "" : $"{GraphName} · ";
            var who = string.IsNullOrEmpty(issue.ObjectId)
                ? issue.ObjectName : $"{issue.ObjectName} ({issue.ObjectId})";
            var why = issue.HasCause ? $"  Likely cause: {issue.Cause}." : "";
            return $"{where}{who} — {issue.Description}{why}";
        }

        public int ErrorCount => Issues.Count(i => i.IsError);
        public int WarningCount => Issues.Count(i => i.IsWarning);

        /// <summary>
        /// The findings that say the graph contradicts itself — see
        /// <see cref="ValidationIssue.IsStructural"/>. An <c>.hkx</c> save is
        /// refused over any of these the file didn't already have.
        /// </summary>
        public List<ValidationIssue> StructuralErrors =>
            Issues.Where(i => i.IsStructural).ToList();

        /// <summary>Nothing to say — the save can go ahead without asking.</summary>
        public bool IsClean => Issues.Count == 0;

        /// <summary>
        /// Fingerprints of every structural error, for use as a baseline: the set
        /// a later pass is compared against to tell what this editing session
        /// broke from what was in the file when it was opened.
        /// </summary>
        public HashSet<string> StructuralFingerprints() =>
            Issues.Where(i => i.IsStructural).Select(i => i.Fingerprint).ToHashSet();

        /// <summary>
        /// One line naming the worst of it, for the top of the pre-save report.
        /// </summary>
        public string Headline
        {
            get
            {
                var parts = new List<string>();
                if (ErrorCount > 0) parts.Add($"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")}");
                if (WarningCount > 0) parts.Add($"{WarningCount} warning{(WarningCount == 1 ? "" : "s")}");
                if (parts.Count == 0) return "The graph looks structurally sound.";
                var lead = string.Join(" and ", parts);
                return PrunedCount > 0
                    ? $"{lead} — including {PrunedCount} object{(PrunedCount == 1 ? "" : "s")} an .hkx save would drop."
                    : $"{lead}.";
            }
        }
    }

    /// <summary>
    /// The pre-save pass. Havok's failure mode is silent — a wrong id or a state
    /// with nothing behind it T-poses in-game with no error, no log line and no
    /// failed conversion — so "it saved" and "it converted" both prove nothing.
    /// Everything checked here is already in the loaded data; the point is to say
    /// it out loud before the file leaves the editor.
    ///
    /// On top of <see cref="HavokValidator"/>'s file-integrity checks it adds the
    /// classics:
    ///
    /// <list type="bullet">
    /// <item><description>a generator slot left null — the node drives no animation;</description></item>
    /// <item><description>an event id or variable index past the end of the file's own
    /// table, which the runtime reads positionally and does not bounds-check;</description></item>
    /// <item><description>a clip naming an animation the character project never
    /// registered;</description></item>
    /// <item><description>a state no transition can enter;</description></item>
    /// <item><description>and the objects an <c>.hkx</c> save would silently drop,
    /// counted and named instead of just disappearing.</description></item>
    /// </list>
    /// </summary>
    public sealed class GraphDoctor
    {
        private readonly HavokManager _manager;
        private readonly List<string> _projectAnimations;
        private readonly Services.BehaviorReferenceIndex? _references;

        /// <param name="projectAnimations">
        /// The character's <c>animationNames</c>, when a character file is loaded.
        /// Empty skips the clip-registration check rather than reporting every clip.
        /// </param>
        /// <param name="references">
        /// Chases <c>behaviorName</c> paths to disk. Null skips both behaviour-
        /// reference checks — with no project on disk to search, "the file isn't
        /// there" would be a statement about the caller, not about the graph.
        /// </param>
        public GraphDoctor(HavokManager manager, IEnumerable<string>? projectAnimations = null,
            Services.BehaviorReferenceIndex? references = null)
        {
            _manager = manager;
            _projectAnimations = projectAnimations?.ToList() ?? new List<string>();
            _references = references;
        }

        public GraphDoctorReport Run()
        {
            var issues = new List<ValidationIssue>();
            if (_manager?.ObjectMap == null || _manager.ObjectMap.Count == 0)
                return new GraphDoctorReport();

            // What an .hkx save will actually write. Computed first because two
            // things need it: the prune report, and every check that has to be
            // judged on the final graph rather than on the editing session's
            // working set.
            var survives = Reachable();

            issues.AddRange(new HavokValidator(_manager).RunValidation());
            issues.AddRange(NullGenerators());
            issues.AddRange(IndicesOutOfRange());
            issues.AddRange(UnregisteredAnimations());
            issues.AddRange(UnreachableStates());
            issues.AddRange(SoftRefs(survives));
            issues.AddRange(BehaviorReferences());

            // A finding on an object the save drops is reported but never refused
            // over — see ValidationIssue.DroppedOnSave. Tagging happens here, once,
            // so no individual check has to remember to do it.
            if (survives != null)
                foreach (var issue in issues)
                    if (issue.ObjectId.Length > 0 && !survives.Contains(issue.ObjectId))
                        issue.DroppedOnSave = true;

            var pruned = PrunedOnSave(issues, survives);
            issues.AddRange(pruned);

            return new GraphDoctorReport
            {
                // Errors first — the list is long on a broken file and the thing
                // that stops the graph loading should not be below page two.
                Issues = issues.OrderByDescending(i => i.IsError).ToList(),
                PrunedCount = pruned.Count,
                GraphName = Name(_manager.ObjectMap.Values
                    .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraph")),
            };
        }

        // ── Checks ────────────────────────────────────────────────────────────

        /// <summary>
        /// A generator slot holding <c>null</c>. Every one of these is a node that
        /// produces no pose: a state that T-poses the moment it is entered, a
        /// blender child that contributes nothing, or — for
        /// <c>hkbBehaviorGraph.rootGenerator</c> — a graph with no animation at all.
        /// Havok neither rejects nor logs it.
        /// </summary>
        private IEnumerable<ValidationIssue> NullGenerators()
        {
            foreach (var obj in _manager.ObjectMap.Values)
            {
                foreach (var (path, param) in HkRefWalk.EnumerateParams(obj))
                {
                    if (param.Name != "generator" && param.Name != "rootGenerator") continue;
                    if (!IsNullRef(param.Value)) continue;

                    yield return new ValidationIssue
                    {
                        Severity = "Error",
                        Category = ValidationIssue.CategoryNullGenerator,
                        Cause = "the generator was removed without a replacement, or never wired in",
                        ObjectId = obj.Id,
                        ObjectClass = obj.ClassName,
                        ObjectName = Name(obj),
                        Description = param.Name == "rootGenerator"
                            ? "rootGenerator is null — the graph has nothing to play"
                            : $"{path} is null — this node produces no pose, which reads as a T-pose in-game",
                    };
                }
            }
        }

        /// <summary>
        /// Event ids and variable indices are bare positional indices into
        /// <c>hkbBehaviorGraphStringData</c>'s tables. The runtime does not
        /// bounds-check them, so one past the end is read as whatever follows.
        /// HavokTypeCatalog has already marked which ints are which (the same
        /// annotation that drives the property editor's name pickers), so this
        /// covers every site — transition <c>eventId</c>s, the intervals nested
        /// inside them, notify events, clip triggers and modifier bindings alike.
        /// </summary>
        private IEnumerable<ValidationIssue> IndicesOutOfRange()
        {
            var stringData = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            if (stringData == null) yield break;

            int events = Strings(stringData, "eventNames");
            int variables = Strings(stringData, "variableNames");

            foreach (var obj in _manager.ObjectMap.Values)
            {
                foreach (var (path, param) in HkRefWalk.EnumerateParams(obj))
                {
                    var semantic = param.TypeInfo?.Semantic ?? HkParamSemantic.None;
                    if (semantic == HkParamSemantic.None) continue;

                    var (count, table, kind) = semantic == HkParamSemantic.EventId
                        ? (events, "eventNames", "event")
                        : (variables, "variableNames", "variable");
                    if (count <= 0) continue;   // no table to be out of range of

                    if (!int.TryParse((param.Value ?? "").Trim(), NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out int n))
                        continue;               // not a number at all — the type check's job
                    if (n == -1 || (n >= 0 && n < count)) continue;

                    yield return new ValidationIssue
                    {
                        Severity = "Error",
                        Category = ValidationIssue.CategoryIndexRange,
                        Cause = $"the {kind} was deleted from {table} after this field was set, "
                              + "or the field was copied from a file with a longer table",
                        ObjectId = obj.Id,
                        ObjectClass = obj.ClassName,
                        ObjectName = Name(obj),
                        Description = $"{path} = {n} is not a {kind} in this file — " +
                                      $"{table} holds {count} (0…{count - 1}); the runtime reads the index positionally",
                    };
                }
            }
        }

        /// <summary>
        /// A clip whose <c>animationName</c> the character project never registered.
        /// The behaviour graph names animations by path but the runtime loads them
        /// through the character's <c>animationNames</c> list, so an unregistered
        /// path is a clip that plays nothing — the single most common outcome of
        /// adding an animation to a graph and forgetting the character file.
        /// </summary>
        private IEnumerable<ValidationIssue> UnregisteredAnimations()
        {
            if (_projectAnimations.Count == 0) yield break;

            var known = new HashSet<string>(
                _projectAnimations.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);

            foreach (var clip in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbClipGenerator"))
            {
                var anim = clip.Params.FirstOrDefault(p => p.Name == "animationName")?.Value;
                if (string.IsNullOrWhiteSpace(anim)) continue;   // the validator warns about blanks
                if (known.Contains(NormalizePath(anim))) continue;

                yield return new ValidationIssue
                {
                    Severity = "Warning",
                    Category = ValidationIssue.CategoryAnimation,
                    Cause = "the animation was added to the graph but not to the character file",
                    ObjectId = clip.Id,
                    ObjectClass = clip.ClassName,
                    ObjectName = Name(clip),
                    Description = $"animationName \"{anim}\" isn't in the character's animationNames — " +
                                  "the clip has nothing to load unless the animation is registered there too",
                };
            }
        }

        /// <summary>
        /// A state nothing can enter. Transitions route by <c>stateId</c>, so a
        /// state is entered only by being its machine's start state or by some
        /// transition's <c>toStateId</c> — a duplicated state that nobody wired up
        /// lands here, and so does one whose transition was deleted.
        ///
        /// Three deliberate blind spots keep this from crying wolf. A machine is
        /// skipped whole when it has a <c>startStateChooser</c> (it picks its start
        /// state in code) or a <c>randomTransitionEventId</c> /
        /// <c>transitionToNext{Higher,Lower}StateEventId</c> (those enter a state by
        /// position rather than by <c>toStateId</c>, so any state in the machine is
        /// fair game). And a <c>toNestedStateId</c> anywhere in the file counts as
        /// reaching that stateId in *any* machine rather than only the nested one
        /// it names — resolving it properly means walking the parent state's
        /// generator chain, and over-forgiving is the right way to be wrong in a
        /// warning.
        /// </summary>
        private IEnumerable<ValidationIssue> UnreachableStates()
        {
            var nestedTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arr in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachineTransitionInfoArray"))
                foreach (var tr in InlineElements(arr, "transitions"))
                {
                    if (!(Get(tr, "flags") ?? "").Contains("TO_NESTED")) continue;
                    var nested = Get(tr, "toNestedStateId");
                    if (!string.IsNullOrEmpty(nested)) nestedTargets.Add(nested);
                }

            foreach (var sm in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine"))
            {
                if (!IsNullRef(Get(sm, "startStateChooser"))) continue;
                if (PositionalEntry.Any(p => IsSet(Get(sm, p)))) continue;

                var states = HkRefList.Tokens(Get(sm, "states"))
                    .Select(r => _manager.TryResolve(r, out var so) ? so : null)
                    .Where(so => so != null)
                    .ToList();
                if (states.Count == 0) continue;   // the validator reports the empty machine

                var reached = new HashSet<string>(StringComparer.Ordinal)
                {
                    Get(sm, "startStateId") ?? "0"
                };
                foreach (var tr in InlineElements(Resolve(Get(sm, "wildcardTransitions")), "transitions"))
                    Add(reached, Get(tr, "toStateId"));
                foreach (var state in states)
                    foreach (var tr in InlineElements(Resolve(Get(state!, "transitions")), "transitions"))
                        Add(reached, Get(tr, "toStateId"));

                foreach (var state in states)
                {
                    var sid = Get(state!, "stateId");
                    if (string.IsNullOrEmpty(sid)) continue;
                    if (reached.Contains(sid) || nestedTargets.Contains(sid)) continue;

                    yield return new ValidationIssue
                    {
                        Severity = "Warning",
                        Category = ValidationIssue.CategoryUnreachableState,
                        Cause = "the state was added or duplicated and no transition into it was authored yet",
                        ObjectId = state!.Id,
                        ObjectClass = state.ClassName,
                        ObjectName = Name(state),
                        Description = $"Nothing enters this state — stateId {sid} is neither the start state " +
                                      $"of '{Name(sm)}' nor the target of any transition in it",
                    };
                }
            }

            static void Add(HashSet<string> set, string? id)
            {
                if (!string.IsNullOrEmpty(id) && id != "-1") set.Add(id);
            }
        }

        /// <summary>
        /// The objects an <c>.hkx</c> save would drop. Saving walks the graph from
        /// <c>toplevelobject</c>, so anything the walk doesn't reach is gone —
        /// silently, which is how a new object that was never wired into its parent
        /// disappears between saving and reloading. An XML save keeps them, so the
        /// wording says which format loses what.
        ///
        /// This supersedes the old "orphaned object" warning, which asked only
        /// whether anything referenced the object: two dead objects referencing
        /// each other passed that test and were pruned anyway.
        /// </summary>
        private List<ValidationIssue> PrunedOnSave(List<ValidationIssue> issues,
            HashSet<string>? survives)
        {
            var root = _manager.RootObject;
            if (root == null)
            {
                issues.Add(new ValidationIssue
                {
                    Severity = "Error",
                    Category = ValidationIssue.CategoryMissingRoot,
                    Cause = "the root object was deleted, or the file was assembled without one",
                    ObjectId = "",
                    ObjectClass = "hkpackfile",
                    ObjectName = "(file header)",
                    Description = $"toplevelobject \"{_manager.TopLevelObjectId}\" isn't an object in this file — " +
                                  "the runtime finds the graph through it, and a save has no root to walk from",
                });
                return new List<ValidationIssue>();
            }

            var reached = survives ?? new HashSet<string>(StringComparer.Ordinal);

            return _manager.ObjectMap.Values
                .Where(o => !reached.Contains(o.Id))
                .Select(o => new ValidationIssue
                {
                    Severity = "Warning",
                    Category = ValidationIssue.CategoryPruned,
                    Cause = "the object was created but never wired into its parent, "
                          + "or the only thing referencing it was deleted",
                    ObjectId = o.Id,
                    ObjectClass = o.ClassName,
                    ObjectName = Name(o),
                    Description = "Nothing reaches this object from the file root — an .hkx save drops it " +
                                  "(an XML save keeps it). Wire it into its parent to keep it.",
                })
                .ToList();
        }

        /// <summary>
        /// The references reachability cannot protect.
        ///
        /// An <c>.hkx</c> save keeps what the walk from the root reaches and drops
        /// the rest, which is a complete guarantee for a <c>#ref</c>: a pointer is
        /// either followed, and its target written, or it is not a pointer anybody
        /// holds. It is no guarantee at all for the references Havok spells as bare
        /// integers and resolves later, at runtime — a transition's
        /// <c>toStateId</c> and <c>toNestedStateId</c>, and a clip's
        /// <c>animationBindingIndex</c>. Delete the state one of those names and
        /// the collector does its job perfectly: the state is unreachable, so it is
        /// not written. The transition that still names it survives, pointing at a
        /// number that now means nothing. The file converts, Havok loads it, and
        /// the actor T-poses with nothing in any log.
        ///
        /// The sites read here are the ones <see cref="HavokValidator"/>'s
        /// <c>toStateId</c> check (its check 7) skips, and the skips are why this
        /// was worth writing. That check reads only the transition arrays hanging
        /// off a machine's own states, and inside those it steps over anything
        /// flagged <c>WILDCARD</c> or <c>TO_NESTED</c> — so a machine's
        /// <c>wildcardTransitions</c> array was never looked at at all, and neither
        /// was any nested destination. Wildcard transitions are how most Skyrim
        /// machines are actually entered, so the gap covered the common case rather
        /// than an exotic one.
        ///
        /// Judged on the graph as it will be saved rather than as it is being
        /// edited: only surviving objects are read, and only a surviving state
        /// counts as a destination, so a state on its way out cannot keep a
        /// transition looking valid.
        ///
        /// Measured before it was allowed to refuse anything. Over vanilla
        /// <c>0_master</c>, <c>mt_behavior</c>, the sixteen other character
        /// behaviours, <c>trollbehavior</c> and a modded dragon graph — 844
        /// wildcard transitions, 540 nested destinations, 3,552 clips — it reports
        /// nothing at all, and the doctor's output on those twenty files is
        /// identical to what it was before this check existed. Adding nothing to
        /// content that works is what earns it the right to refuse a save.
        ///
        /// It is <em>not</em> silent on vanilla <c>dragonbehavior</c>, which is the
        /// one thing to know before trusting the paragraph above: that file carries
        /// fifteen of these for real — nine dangling <c>toStateId</c> at the
        /// wildcard and nested sites, six dangling <c>toNestedStateId</c>, mostly
        /// states renumbered with the transitions into them left behind. They are
        /// inherited, so they land in the load-time baseline and refuse nothing,
        /// and all fifteen are re-derived from the raw XML in
        /// <c>tools/hkx-graph-doctor</c> rather than taken on this code's word.
        /// </summary>
        private IEnumerable<ValidationIssue> SoftRefs(HashSet<string>? survives)
        {
            if (survives == null) yield break;

            HkObject? Live(string? id) =>
                Resolve(id) is HkObject o && survives.Contains(o.Id) ? o : null;

            foreach (var sm in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine" && survives.Contains(o.Id)))
            {
                var states = HkRefList.Tokens(Get(sm, "states"))
                    .Select(Live).Where(o => o != null).ToList();
                if (states.Count == 0) continue;   // the validator reports the empty machine

                var stateIds = new HashSet<string>(
                    states.Select(st => Get(st!, "stateId") ?? ""), StringComparer.Ordinal);

                // The machine's own wildcard array first, then each state's — the
                // latter only for the transitions check 7 steps over, so no defect
                // is reported twice under two categories.
                var arrays = new List<(HkObject? Array, bool Wildcard)>
                {
                    (Live(Get(sm, "wildcardTransitions")), true)
                };
                foreach (var st in states)
                    arrays.Add((Live(Get(st!, "transitions")), false));

                foreach (var (array, wildcard) in arrays)
                {
                    if (array == null) continue;
                    foreach (var tr in InlineElements(array, "transitions"))
                    {
                        var nested = (Get(tr, "flags") ?? "").Contains("TO_NESTED");
                        if (!wildcard && !nested) continue;   // check 7 already has this one

                        var to = (Get(tr, "toStateId") ?? "").Trim();
                        if (to.Length == 0 || to == "-1") continue;

                        if (!stateIds.Contains(to))
                        {
                            yield return new ValidationIssue
                            {
                                Severity = "Error",
                                Category = ValidationIssue.CategoryToStateId,
                                // A transition is an inline element with no id of
                                // its own, so the array holding it is the anchor.
                                // The wildcard array and a state's own array are
                                // different objects, so the id tells them apart and
                                // Subject only has to separate this from a finding
                                // of another kind on the same array.
                                Subject = "transitions",
                                Cause = "the destination state was deleted, or its stateId was renumbered — "
                                      + "removing a state doesn't touch the transitions that name it",
                                ObjectId = array.Id,
                                ObjectClass = array.ClassName,
                                ObjectName = Name(sm),
                                Description = $"{(wildcard ? "Wildcard transition" : "Transition")} toStateId {to} " +
                                              $"is no state of '{Name(sm)}' " +
                                              $"(stateIds: {string.Join(", ", stateIds.OrderBy(x => x, StringComparer.Ordinal))})",
                            };
                            continue;   // no destination, so nothing to resolve a nested id against
                        }

                        if (!nested) continue;

                        var toNested = (Get(tr, "toNestedStateId") ?? "").Trim();
                        if (toNested.Length == 0 || toNested == "-1") continue;

                        var destination = states.First(st => Get(st!, "stateId") == to)!;
                        var inner = NestedMachine(destination, survives);
                        if (inner == null) continue;   // resolves in another file — see below

                        var innerIds = HkRefList.Tokens(Get(inner, "states"))
                            .Select(Live).Where(o => o != null)
                            .Select(o => Get(o!, "stateId") ?? "")
                            .ToHashSet(StringComparer.Ordinal);
                        if (innerIds.Count == 0 || innerIds.Contains(toNested)) continue;

                        yield return new ValidationIssue
                        {
                            Severity = "Error",
                            Category = ValidationIssue.CategoryToNestedStateId,
                            Subject = "toNestedStateId",
                            Cause = "the state was deleted from the nested machine, or its stateId was "
                                  + "renumbered, after this transition was pointed at it",
                            ObjectId = array.Id,
                            ObjectClass = array.ClassName,
                            ObjectName = Name(sm),
                            Description = $"Transition into '{Name(destination)}' asks for nested state {toNested}, " +
                                          $"which is no state of '{Name(inner)}' " +
                                          $"(stateIds: {string.Join(", ", innerIds.OrderBy(x => x, StringComparer.Ordinal))}) — " +
                                          "the nested machine starts in its own start state instead, silently",
                        };
                    }
                }
            }

            // A clip's animationBindingIndex is the third soft reference, and the
            // only one that resolves outside this file: it indexes the character's
            // registered animations. -1 means "bind by animationName instead" and
            // is what every one of the 3,552 clips in the measured corpus carries,
            // so this fires only on a value somebody set deliberately.
            if (_projectAnimations.Count == 0) yield break;

            foreach (var clip in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbClipGenerator" && survives.Contains(o.Id)))
            {
                var raw = (Get(clip, "animationBindingIndex") ?? "").Trim();
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bound))
                    continue;
                if (bound < 0 || bound < _projectAnimations.Count) continue;

                yield return new ValidationIssue
                {
                    // A warning, like the clip-registration check beside it and for
                    // the same reason: the character file is a second file this
                    // editor does not write, and it may be about to gain the
                    // animations that would make this index good.
                    Severity = "Warning",
                    Category = ValidationIssue.CategoryAnimation,
                    Subject = "animationBindingIndex",
                    Cause = "the index was set by hand or copied from a project with a longer "
                          + "animation list, or animations were removed from the character file",
                    ObjectId = clip.Id,
                    ObjectClass = clip.ClassName,
                    ObjectName = Name(clip),
                    Description = $"animationBindingIndex {bound} is past the end of the character's " +
                                  $"{_projectAnimations.Count} registered animations — " +
                                  "-1 is the normal value, and binds the clip by animationName instead",
                };
            }
        }

        /// <summary>
        /// The state machine a <c>TO_NESTED</c> transition actually starts: the one
        /// reached by following <c>generator</c> down from the destination state.
        /// Null when it cannot be known here, which is never a finding — a check
        /// that refuses saves has to be over-forgiving wherever it can't see.
        ///
        /// Over the 540 nested transitions in the measured corpus this resolves
        /// 515, every one of them through plain <c>generator</c> links: 412
        /// <c>hkbModifierGenerator</c> → <c>hkbStateMachine</c>, 101 straight to the
        /// machine, 2 through two modifier wrappers. All 25 it declines end at an
        /// <c>hkbBehaviorReferenceGenerator</c>, where the nested machine lives in a
        /// different file and this one genuinely has nothing to say about it.
        /// </summary>
        private HkObject? NestedMachine(HkObject state, HashSet<string> survives)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = Resolve(Get(state, "generator"));
            while (current != null && survives.Contains(current.Id) && seen.Add(current.Id))
            {
                if (current.ClassName == "hkbStateMachine") return current;
                if (current.ClassName == "hkbBehaviorReferenceGenerator") return null;
                // Only a wrapper that passes a single generator through is followed.
                // Anything holding several children picks between them at runtime,
                // and Get returns nothing for it, so the walk stops rather than
                // guessing which child the transition meant.
                current = Resolve(Get(current, "generator"));
            }
            return null;
        }

        /// <summary>
        /// The objects an <c>.hkx</c> save writes: everything the reference walk
        /// reaches from <c>toplevelobject</c>. Null when the file has no root, in
        /// which case there is no final graph to judge anything on and the
        /// missing-root error is the only thing worth saying.
        /// </summary>
        private HashSet<string>? Reachable()
        {
            var root = _manager.RootObject;
            if (root == null) return null;

            var reached = new HashSet<string>(StringComparer.Ordinal) { root.Id };
            var pending = new Stack<HkObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                foreach (var (_, refId) in HkRefWalk.EnumerateRefs(pending.Pop()))
                    if (_manager.ObjectMap.TryGetValue(refId, out var target)
                        && target != null && reached.Add(refId))
                        pending.Push(target);
            }
            return reached;
        }

        /// <summary>
        /// The two ways a behaviour reference goes wrong, both silent.
        ///
        /// It is the bridge node of every Nemesis/Pandora-style patch: a state's
        /// generator points at an <c>hkbBehaviorReferenceGenerator</c>, whose
        /// <c>behaviorName</c> is a path to a separate file pulled in at runtime.
        /// A path that resolves to nothing produces no error anywhere — the state
        /// is entered and nothing plays. And because the two graphs link by event
        /// *name*, each keeping its own table, an event the referenced graph uses
        /// that this file has never heard of cannot cross between them.
        ///
        /// Both are warnings, and the second is a lead rather than a verdict: a
        /// child graph's internal events legitimately appear only in its own
        /// table. What makes it worth saying is that the opposite mistake —
        /// expecting an event to cross when it can't — looks in-game exactly like
        /// everything working, right up until the animation doesn't play.
        /// </summary>
        private IEnumerable<ValidationIssue> BehaviorReferences()
        {
            if (_references == null) yield break;

            var stringData = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            var ownEvents = new HashSet<string>(
                stringData?.Params.FirstOrDefault(p => p.Name == "eventNames")?.Strings
                    ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var node in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbBehaviorReferenceGenerator"))
            {
                var behaviorName = (Get(node, "behaviorName") ?? "").Trim();
                if (behaviorName.Length == 0)
                {
                    yield return new ValidationIssue
                    {
                        Severity = "Warning",
                        Category = ValidationIssue.CategoryBehaviorReference,
                        Cause = "the reference was created before its target path was known",
                        ObjectId = node.Id,
                        ObjectClass = node.ClassName,
                        ObjectName = Name(node),
                        Description = "behaviorName is empty — this reference pulls in nothing, "
                                    + "so the state using it plays nothing",
                    };
                    continue;
                }

                var target = _references.Lookup(behaviorName);

                if (!target.Resolved)
                {
                    yield return new ValidationIssue
                    {
                        Severity = "Warning",
                        Category = ValidationIssue.CategoryBehaviorReference,
                        Cause = "the file hasn't been created yet, the path is relative to a different "
                              + "folder, or it exists only inside a mod manager's virtual file system",
                        ObjectId = node.Id,
                        ObjectClass = node.ClassName,
                        ObjectName = Name(node),
                        Description = $"behaviorName '{behaviorName}' wasn't found under the project — "
                                    + "the reference resolves by path at runtime, and one that resolves "
                                    + "to nothing is a state that plays nothing, with no error",
                    };
                    continue;
                }

                // Event alignment across the reference used to be reported here.
                // Measured against vanilla SSE 0_master it fires on 10 of its 13
                // references — 1, 1, 1, 2, 3, 3, 3, 32, 135 and 418 events — on a
                // file the game runs perfectly. "The referenced graph uses an event
                // this one hasn't got" is the normal condition, not a defect: a
                // child behaviour's internal events are its own business. It moved
                // to 🔗 Compare events with referenced file, where the same numbers
                // are an answer to a question rather than an accusation.
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// State-machine events that enter a state by position instead of by
        /// <c>toStateId</c> — a random pick, or a step through the stateId order.
        /// Any of them set means every state in the machine can be entered.
        /// </summary>
        private static readonly string[] PositionalEntry =
        {
            "randomTransitionEventId",
            "transitionToNextHigherStateEventId",
            "transitionToNextLowerStateEventId",
        };

        /// <summary>An event id slot actually holding an event — -1 is Havok's "none".</summary>
        private static bool IsSet(string? eventId)
        {
            var v = (eventId ?? "").Trim();
            return v.Length > 0 && v != "-1";
        }

        private static string Name(HkObject? o) =>
            o == null ? "" : o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id;

        private static string? Get(HkObject? o, string param) =>
            o?.Params.FirstOrDefault(p => p.Name == param)?.Value;

        private HkObject? Resolve(string? id) =>
            IsNullRef(id) ? null : (_manager.TryResolve(id, out var o) ? o : null);

        /// <summary>Havok writes an unset ref as <c>null</c>; <c>#0000</c> is the numeric spelling.</summary>
        private static bool IsNullRef(string? value)
        {
            var v = (value ?? "").Trim();
            return v.Length == 0 || v == "null" || v == "#0000";
        }

        private static int Strings(HkObject o, string param) =>
            o.Params.FirstOrDefault(p => p.Name == param)?.Strings.Count ?? 0;

        /// <summary>The inline (anonymous) elements of an array param — transitions and friends.</summary>
        private static IEnumerable<HkObject> InlineElements(HkObject? owner, string param)
        {
            var p = owner?.Params.FirstOrDefault(x => x.Name == param);
            if (p == null) yield break;
            foreach (var c in p.Children)
                if (string.IsNullOrEmpty(c.Id)) yield return c;
        }

        private static string NormalizePath(string p) =>
            (p ?? "").Trim().Replace('/', '\\');
    }
}
