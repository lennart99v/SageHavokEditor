using System;
using System.Collections.Generic;
using System.Linq;
using SageHavokEditor.Models;

namespace SageHavokEditor.Core.Services
{
    /// <summary>
    /// Copies an <c>hkbStateMachineStateInfo</c> together with the objects hanging
    /// off it — the generator chain, the binding set, the notify-event arrays, the
    /// transition array — giving every copy a fresh <c>#id</c> and rewiring the
    /// copies to point at each other rather than at the originals.
    ///
    /// Building a family of near-identical states (Aim / Throw / Recall / Catch off
    /// one clip pattern) is otherwise an object-at-a-time job in the property
    /// editor, and the failure mode is the usual silent one: miss a single ref and
    /// the copy quietly drives the original's generator.
    ///
    /// Two rules decide where the copy stops:
    ///
    /// <list type="bullet">
    /// <item><description><b>Transition effects are shared, never copied.</b> A file
    /// typically has one <c>hkbBlendingTransitionEffect</c> that hundreds of
    /// transitions point at; copying it per duplicated transition would be pure
    /// bloat, and the effect carries no per-state data.</description></item>
    /// <item><description><b>File-level singletons are never copied.</b> The walk
    /// should never reach the behaviour graph, its data or its string data, but a
    /// hand-edited file can point anywhere, and a second copy of
    /// <c>hkbBehaviorGraphStringData</c> would be catastrophic rather than
    /// merely wrong.</description></item>
    /// </list>
    ///
    /// Nothing transitions to the copy: transitions route by <c>stateId</c>, and the
    /// copy is given a fresh one (unique within its machine — stateIds restart per
    /// state machine). The caller is expected to say so.
    /// </summary>
    public static class StateDuplicator
    {
        /// <summary>
        /// Shared by design across a whole file — a copy would be redundant, and in
        /// the singleton cases actively harmful.
        /// </summary>
        private static readonly HashSet<string> NeverCopy = new(StringComparer.Ordinal)
        {
            "hkRootLevelContainer",
            "hkbBehaviorGraph",
            "hkbBehaviorGraphData",
            "hkbBehaviorGraphStringData",
            "hkbVariableValueSet",
            "hkbCharacterData",
            "hkbCharacterStringData",
            "hkbProjectData",
            "hkbProjectStringData",
            "hkbSymbolIdMap",
        };

        private static bool IsShared(string? className) =>
            className != null
            && (NeverCopy.Contains(className)
                // hkbBlendingTransitionEffect and friends — one instance serves the file.
                || className.EndsWith("TransitionEffect", StringComparison.Ordinal));

        /// <summary>
        /// The objects a duplicate would copy, the state first, in walk order.
        /// Runs the same traversal the copy does, so the count shown to the user
        /// before the fact is the count they get.
        /// </summary>
        public static List<HkObject> CollectSubtree(
            HavokManager manager, HkObject state, bool copyGenerator, bool copyTransitions)
        {
            var order = new List<HkObject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Visit(HkObject obj)
            {
                if (string.IsNullOrEmpty(obj.Id) || !seen.Add(obj.Id)) return;
                order.Add(obj);

                foreach (var p in obj.Params)
                {
                    // The two params the user gets a say over — skipping the walk here
                    // is what leaves the copy sharing (or dropping) them.
                    if (ReferenceEquals(obj, state))
                    {
                        if (p.Name == "generator" && !copyGenerator) continue;
                        if (p.Name == "transitions" && !copyTransitions) continue;
                    }

                    foreach (var id in RefsOf(p, manager))
                        if (manager.ObjectMap.TryGetValue(id, out var target)
                            && target != null && !IsShared(target.ClassName))
                            Visit(target);
                }
            }

            Visit(state);
            return order;
        }

        /// <summary>
        /// Every top-level ref a param carries, including the ones nested inside
        /// inline (anonymous) elements — a transition array keeps its effect and
        /// its intervals a level below the param, so a scan of Value alone misses
        /// them. Mirrors the graph's own WalkParamRefs.
        /// </summary>
        private static IEnumerable<string> RefsOf(HkParam p, HavokManager manager)
        {
            foreach (var tok in HkRefList.Tokens(p.Value))
                if (tok.StartsWith("#", StringComparison.Ordinal))
                    yield return tok;

            foreach (var child in p.Children)
                if (string.IsNullOrEmpty(child.Id) || !manager.ObjectMap.ContainsKey(child.Id))
                    foreach (var cp in child.Params)
                        foreach (var r in RefsOf(cp, manager))
                            yield return r;
        }

        /// <summary>The result of a duplication — nothing is in the ObjectMap yet.</summary>
        public sealed class Result
        {
            /// <summary>The copy of the state, first entry of <see cref="Created"/>.</summary>
            public HkObject NewState { get; init; } = null!;
            /// <summary>Every new object, the state included, in walk order.</summary>
            public List<HkObject> Created { get; init; } = new();
            /// <summary>The copy's stateId — fresh within the owning machine.</summary>
            public int NewStateId { get; init; }
        }

        /// <summary>
        /// Build the copies. The caller owns the commit: adding them to the
        /// ObjectMap and appending the new state to the machine's states list, as
        /// one undoable action. A state that isn't wired into its machine is
        /// dropped by the orphan-pruning .hkx save.
        /// </summary>
        public static Result Duplicate(
            HavokManager manager, HkObject state, HkObject machine, string newName,
            bool copyGenerator, bool copyTransitions)
        {
            var originals = CollectSubtree(manager, state, copyGenerator, copyTransitions);

            var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var allocator = new IdAllocator(manager.ObjectMap.Keys);
            foreach (var o in originals) idMap[o.Id] = allocator.Next();

            // Shells first, params second: a subtree can reference itself (a nested
            // machine's states point back at generators above them), so every copy
            // has to exist before any param is rewritten.
            var shells = originals.ToDictionary(
                o => o.Id,
                o => new HkObject { Id = idMap[o.Id], ClassName = o.ClassName, Signature = o.Signature },
                StringComparer.Ordinal);

            HkObject MapRef(HkObject c) =>
                !string.IsNullOrEmpty(c.Id) && shells.TryGetValue(c.Id, out var s) ? s : c;
            string MapId(string id) => idMap.TryGetValue(id, out var n) ? n : id;

            foreach (var o in originals)
                shells[o.Id].Params = o.Params.Select(p => p.CloneRemapped(MapRef, MapId)).ToList();

            var newState = shells[state.Id];

            // Names are cosmetic to Havok but load-bearing for every list, picker and
            // graph label in the editor, so two objects called "AimClip" is a trap.
            var oldName = state.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "";
            var taken = new HashSet<string>(
                manager.ObjectMap.Values.Select(o => o.DisplayName), StringComparer.Ordinal);
            taken.Add(newName);

            foreach (var o in originals)
            {
                var clone = shells[o.Id];
                var nameParam = clone.Params.FirstOrDefault(p => p.Name == "name");
                if (nameParam == null || string.IsNullOrEmpty(nameParam.Value)) continue;

                if (ReferenceEquals(o, state)) { nameParam.Value = newName; continue; }

                // Carry the rename through the subtree where the original name is
                // visible in it: duplicating "Aim" as "Throw" turns "AimClip" into
                // "ThrowClip" rather than "AimClip_2".
                var derived = !string.IsNullOrEmpty(oldName) && nameParam.Value.Contains(oldName, StringComparison.Ordinal)
                    ? nameParam.Value.Replace(oldName, newName, StringComparison.Ordinal)
                    : nameParam.Value;

                nameParam.Value = Uniquify(derived, taken);
                taken.Add(nameParam.Value);
            }

            // stateId is positional within THIS machine — the numbers restart per
            // state machine, so scoping the max to the file would hand a 3-state
            // machine a stateId in the hundreds.
            int newStateId = HkRefList.Tokens(machine.Params.FirstOrDefault(p => p.Name == "states")?.Value)
                .Select(r => manager.TryResolve(r, out var so) && so != null
                    ? (int.TryParse(so.Params.FirstOrDefault(p => p.Name == "stateId")?.Value, out int n) ? n : -1)
                    : -1)
                .DefaultIfEmpty(-1).Max() + 1;
            SetParam(newState, "stateId", newStateId.ToString());

            // Not copying the transitions means starting with none. Leaving the
            // original array referenced would look like a copy while actually
            // sharing it — editing one state's transitions would edit the other's.
            if (!copyTransitions)
            {
                var t = newState.Params.FirstOrDefault(p => p.Name == "transitions");
                if (t != null) { t.Children.Clear(); t.Value = "null"; t.NumElements = ""; }
            }

            return new Result
            {
                NewState = newState,
                Created = originals.Select(o => shells[o.Id]).ToList(),
                NewStateId = newStateId,
            };
        }

        private static void SetParam(HkObject o, string name, string value)
        {
            var p = o.Params.FirstOrDefault(x => x.Name == name);
            if (p != null) p.Value = value;
            else o.Params.Add(new HkParam { Name = name, Value = value });
        }

        private static string Uniquify(string baseName, HashSet<string> taken)
        {
            if (!taken.Contains(baseName)) return baseName;
            for (int i = 2; ; i++)
            {
                var candidate = $"{baseName}_{i}";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Hands out free <c>#NNNN</c> ids. A duplication needs a whole batch of
        /// them at once, and the scan-the-ObjectMap-each-time helpers only work
        /// when the previous id has already been inserted.
        /// </summary>
        private sealed class IdAllocator
        {
            private readonly HashSet<int> _used;
            private int _next = 1;

            public IdAllocator(IEnumerable<string> existingIds)
            {
                _used = existingIds
                    .Where(k => k.StartsWith("#", StringComparison.Ordinal))
                    .Select(k => int.TryParse(k.Substring(1), out int n) ? n : 0)
                    .ToHashSet();
            }

            public string Next()
            {
                while (_used.Contains(_next)) _next++;
                _used.Add(_next);
                return $"#{_next:D4}";
            }
        }
    }
}
