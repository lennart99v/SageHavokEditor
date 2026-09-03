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

        public int ErrorCount => Issues.Count(i => i.IsError);
        public int WarningCount => Issues.Count(i => i.IsWarning);

        /// <summary>Nothing to say — the save can go ahead without asking.</summary>
        public bool IsClean => Issues.Count == 0;

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

        /// <param name="projectAnimations">
        /// The character's <c>animationNames</c>, when a character file is loaded.
        /// Empty skips the clip-registration check rather than reporting every clip.
        /// </param>
        public GraphDoctor(HavokManager manager, IEnumerable<string>? projectAnimations = null)
        {
            _manager = manager;
            _projectAnimations = projectAnimations?.ToList() ?? new List<string>();
        }

        public GraphDoctorReport Run()
        {
            var issues = new List<ValidationIssue>();
            if (_manager?.ObjectMap == null || _manager.ObjectMap.Count == 0)
                return new GraphDoctorReport();

            issues.AddRange(new HavokValidator(_manager).RunValidation());
            issues.AddRange(NullGenerators());
            issues.AddRange(IndicesOutOfRange());
            issues.AddRange(UnregisteredAnimations());
            issues.AddRange(UnreachableStates());

            var pruned = PrunedOnSave(issues);
            issues.AddRange(pruned);

            return new GraphDoctorReport
            {
                // Errors first — the list is long on a broken file and the thing
                // that stops the graph loading should not be below page two.
                Issues = issues.OrderByDescending(i => i.IsError).ToList(),
                PrunedCount = pruned.Count,
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
        private List<ValidationIssue> PrunedOnSave(List<ValidationIssue> issues)
        {
            var root = _manager.RootObject;
            if (root == null)
            {
                issues.Add(new ValidationIssue
                {
                    Severity = "Error",
                    Category = ValidationIssue.CategoryPruned,
                    ObjectId = "",
                    ObjectClass = "hkpackfile",
                    ObjectName = "(file header)",
                    Description = $"toplevelobject \"{_manager.TopLevelObjectId}\" isn't an object in this file — " +
                                  "the runtime finds the graph through it, and a save has no root to walk from",
                });
                return new List<ValidationIssue>();
            }

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

            return _manager.ObjectMap.Values
                .Where(o => !reached.Contains(o.Id))
                .Select(o => new ValidationIssue
                {
                    Severity = "Warning",
                    Category = ValidationIssue.CategoryPruned,
                    ObjectId = o.Id,
                    ObjectClass = o.ClassName,
                    ObjectName = Name(o),
                    Description = "Nothing reaches this object from the file root — an .hkx save drops it " +
                                  "(an XML save keeps it). Wire it into its parent to keep it.",
                })
                .ToList();
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

        private static string Name(HkObject o) =>
            o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id;

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
