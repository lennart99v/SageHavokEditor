using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimHavokEditor.Models;

namespace SkyrimHavokEditor.Core.Patching
{
    public class PatchGenerator
    {
        private readonly HavokManager _manager;
        private readonly Dictionary<string, ObjectSnapshot> _snapshot;
        private readonly List<string> _eventsOriginal;
        private readonly List<string> _varsOriginal;

        public PatchGenerator(
            HavokManager manager,
            Dictionary<string, ObjectSnapshot> snapshot,
            List<string> originalEvents,
            List<string> originalVars)
        {
            _manager = manager;
            _snapshot = snapshot;
            _eventsOriginal = originalEvents;
            _varsOriginal = originalVars;
        }

        public BehaviorPatch Generate(string baseFileName,
            string author = "", string description = "")
        {
            var patch = new BehaviorPatch
            {
                BaseFile = baseFileName,
                Author = author,
                Description = description,
                Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };

            // localId map: objectId in current file → localId string used in patch
            var newObjectLocalIds = new Dictionary<string, string>();
            int localCounter = 1;

            // ── 1. Added objects ──────────────────────────────────────────────
            foreach (var kvp in _manager.ObjectMap)
            {
                if (_snapshot.ContainsKey(kvp.Key)) continue;

                var obj = kvp.Value;
                var objName = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "";
                var localId = $"new_{SafeName(obj.ClassName)}_{localCounter++}";
                newObjectLocalIds[kvp.Key] = localId;

                var addOp = new AddObjectOp
                {
                    LocalId = localId,
                    ClassName = obj.ClassName ?? "",
                    Signature = obj.Signature ?? "",
                    Note = $"New {obj.ClassName}: {objName.IfEmpty(kvp.Key)}"
                };

                foreach (var param in obj.Params)
                {
                    addOp.Params.Add(new PatchParam
                    {
                        Name = param.Name,
                        Value = param.Value ?? ""
                    });

                    if (param.Children?.Count > 0)
                    {
                        foreach (var child in param.Children)
                        {
                            var childOp = new AddObjectOp { ClassName = child.ClassName ?? "" };
                            foreach (var cp in child.Params)
                                childOp.Params.Add(new PatchParam
                                { Name = cp.Name, Value = cp.Value ?? "" });
                            addOp.Children.Add(childOp);
                        }
                    }
                }

                patch.Operations.Add(addOp);
            }

            // ── 2. Deleted objects ────────────────────────────────────────────
            foreach (var id in _snapshot.Keys)
            {
                if (_manager.ObjectMap.ContainsKey(id)) continue;
                var snap = _snapshot[id];
                var anchor = MakeAnchorFromSnapshot(id, snap);
                patch.Operations.Add(new DeleteObjectOp
                {
                    Anchor = anchor,
                    Note = $"Removed {snap.ClassName}: {snap.Name.IfEmpty(id)}"
                });
            }

            // ── 3. Modified existing objects ──────────────────────────────────
            foreach (var kvp in _manager.ObjectMap)
            {
                if (!_snapshot.TryGetValue(kvp.Key, out var snap)) continue;

                var obj = kvp.Value;
                var objName = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "";
                var anchor = MakeAnchor(kvp.Key, obj);

                foreach (var param in obj.Params)
                {
                    snap.Params.TryGetValue(param.Name, out var oldSnap);
                    var oldVal = oldSnap?.Value ?? "";
                    var newVal = param.Value ?? "";

                    // ── 3a. Value changed ─────────────────────────────────────
                    if (oldVal != newVal &&
                        param.Name != "eventNames" &&
                        param.Name != "variableNames" &&
                        param.Name != "wordVariableNames")
                    {
                        var oldTokens = oldVal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var newTokens = newVal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var added = newTokens.Except(oldTokens).ToList();
                        var removed = oldTokens.Except(newTokens).ToList();

                        if (removed.Count == 0 && added.Count > 0 && oldTokens.Length > 0)
                        {
                            foreach (var token in added)
                            {
                                newObjectLocalIds.TryGetValue(token, out var localRef);
                                patch.Operations.Add(new AppendParamOp
                                {
                                    Anchor = anchor,
                                    ParamName = param.Name,
                                    LocalRef = localRef ?? "",
                                    Value = string.IsNullOrEmpty(localRef) ? token : "",
                                    Note = $"{objName.IfEmpty(kvp.Key)}.{param.Name} += {token}"
                                });
                            }
                        }
                        else
                        {
                            patch.Operations.Add(new ModifyParamOp
                            {
                                Anchor = anchor,
                                ParamName = param.Name,
                                OldValue = oldVal,
                                NewValue = newVal,
                                Note = $"{objName.IfEmpty(kvp.Key)}.{param.Name}: " +
                                            $"\"{Truncate(oldVal)}\" → \"{Truncate(newVal)}\""
                            });
                        }
                    }

                    // ── 3b. Inline children diff ──────────────────────────────
                    if (param.Children?.Count > 0 &&
    oldSnap?.Children != null &&
    param.Name != "stringData" &&        // ← add this
    param.Name != "eventNames" &&        // ← and this
    param.Name != "variableNames" &&     // ← and this
    (param.Strings == null || param.Strings.Count == 0))
                    {
                        int count = Math.Min(param.Children.Count, oldSnap.Children.Count);
                        for (int ci = 0; ci < count; ci++)
                        {
                            var currentChild = param.Children[ci];
                            var snapChild = oldSnap.Children[ci];

                            foreach (var cp in currentChild.Params)
                            {
                                snapChild.Params.TryGetValue(cp.Name, out var oldChildVal);
                                var newChildVal = cp.Value ?? "";
                                if (oldChildVal == newChildVal) continue;

                                patch.Operations.Add(new ModifyChildOp
                                {
                                    Anchor = anchor,
                                    ParamName = param.Name,
                                    ChildIndex = ci,
                                    ChildParam = cp.Name,
                                    OldValue = oldChildVal ?? "",
                                    NewValue = newChildVal,
                                    Note = $"{objName.IfEmpty(kvp.Key)}" +
                                                 $".{param.Name}[{ci}].{cp.Name}: " +
                                                 $"\"{Truncate(oldChildVal)}\" → \"{Truncate(newChildVal)}\""
                                });
                            }
                        }
                        // ── Added children (new transitions etc.) ────────────────
                        for (int ci = count; ci < param.Children.Count; ci++)
                        {
                            var newChild = param.Children[ci];
                            var childOp = new AddChildOp
                            {
                                Anchor = anchor,
                                ParamName = param.Name,
                                ClassName = newChild.ClassName ?? "",
                                Note = $"{objName.IfEmpty(kvp.Key)}.{param.Name} += new child [{ci}]"
                            };
                            foreach (var cp in newChild.Params)
                                childOp.Params.Add(new PatchParam
                                {
                                    Name = cp.Name,
                                    Value = cp.Value ?? ""
                                });
                            patch.Operations.Add(childOp);
                        }
                    }

                    // ── 3c. Strings list diff ─────────────────────────────────
                    if (param.Strings?.Count > 0 && oldSnap?.Strings?.Count > 0)
                    {
                        int minLen = Math.Min(param.Strings.Count, oldSnap.Strings.Count);
                        for (int si = 0; si < minLen; si++)
                        {
                            if (param.Strings[si] == oldSnap.Strings[si]) continue;

                            bool isEvent = param.Name == "eventNames";
                            bool isVar = param.Name == "variableNames"
                                        || param.Name == "wordVariableNames";

                            if (isEvent)
                                patch.Operations.Add(new RenameEventOp
                                {
                                    Index = si,
                                    OldName = oldSnap.Strings[si],
                                    NewName = param.Strings[si],
                                    Note = $"Rename event[{si}]: \"{oldSnap.Strings[si]}\" → \"{param.Strings[si]}\""
                                });
                            else if (isVar)
                                patch.Operations.Add(new RenameVariableOp
                                {
                                    Index = si,
                                    OldName = oldSnap.Strings[si],
                                    NewName = param.Strings[si],
                                    Note = $"Rename variable[{si}]: \"{oldSnap.Strings[si]}\" → \"{param.Strings[si]}\""
                                });
                            else
                                patch.Operations.Add(new ModifyParamOp
                                {
                                    Anchor = anchor,
                                    ParamName = $"{param.Name}[{si}]",
                                    OldValue = oldSnap.Strings[si],
                                    NewValue = param.Strings[si],
                                    Note = $"{param.Name}[{si}] renamed"
                                });
                        }
                    }

                }
            }

            // ── 4. New events (appended beyond original count) ────────────────
            var currentEvents = GetCurrentEventNames();
            for (int i = _eventsOriginal.Count; i < currentEvents.Count; i++)
                patch.Operations.Add(new AddEventOp
                {
                    Name = currentEvents[i],
                    Note = $"New event at index {i}: {currentEvents[i]}"
                });

            // Also catch renames that happened via EventList directly
            // (when the Strings list wasn't updated but EventList was)
            int sharedEventCount = Math.Min(_eventsOriginal.Count, currentEvents.Count);
            for (int i = 0; i < sharedEventCount; i++)
            {
                if (_eventsOriginal[i] == currentEvents[i]) continue;
                // Only add RenameEventOp if not already added from Strings diff above
                bool alreadyCovered = patch.Operations.OfType<RenameEventOp>()
                    .Any(r => r.Index == i);
                if (!alreadyCovered)
                    patch.Operations.Add(new RenameEventOp
                    {
                        Index = i,
                        OldName = _eventsOriginal[i],
                        NewName = currentEvents[i],
                        Note = $"Rename event[{i}]: \"{_eventsOriginal[i]}\" → \"{currentEvents[i]}\""
                    });
            }

            // ── 5. New variables ──────────────────────────────────────────────
            var currentVars = GetCurrentVarNames();
            for (int i = _varsOriginal.Count; i < currentVars.Count; i++)
                patch.Operations.Add(new AddVariableOp
                {
                    Name = currentVars[i],
                    Note = $"New variable at index {i}: {currentVars[i]}"
                });

            // Renames within existing variables
            int sharedVarCount = Math.Min(_varsOriginal.Count, currentVars.Count);
            for (int i = 0; i < sharedVarCount; i++)
            {
                if (_varsOriginal[i] == currentVars[i]) continue;
                bool alreadyCovered = patch.Operations.OfType<RenameVariableOp>()
                    .Any(r => r.Index == i);
                if (!alreadyCovered)
                    patch.Operations.Add(new RenameVariableOp
                    {
                        Index = i,
                        OldName = _varsOriginal[i],
                        NewName = currentVars[i],
                        Note = $"Rename variable[{i}]: \"{_varsOriginal[i]}\" → \"{currentVars[i]}\""
                    });
            }

            return patch;
        }

        private static string MakeAnchorFromSnapshot(string id, ObjectSnapshot snap)
        {
            // Named objects
            if (!string.IsNullOrEmpty(snap.Name))
                return $"name:{snap.Name}";

            // Singleton classes
            var singletonClasses = new HashSet<string>
    {
        "hkbBehaviorGraphStringData", "hkbBehaviorGraphData",
        "hkbVariableValueSet", "hkbBehaviorGraph", "hkRootLevelContainer"
    };
            if (singletonClasses.Contains(snap.ClassName))
                return $"class:{snap.ClassName}";

            // stateId for state info objects
            if (snap.ClassName == "hkbStateMachineStateInfo" &&
                snap.Params.TryGetValue("stateId", out var stateSnap) &&
                !string.IsNullOrEmpty(stateSnap.Value))
                return $"stateId:{stateSnap.Value}";

            // animName for clips
            if (snap.ClassName == "hkbClipGenerator" &&
                snap.Params.TryGetValue("animationName", out var animSnap) &&
                !string.IsNullOrEmpty(animSnap.Value))
                return $"animName:{animSnap.Value}";

            return $"id:{id}";
        }

        // ── Static helper: take a deep snapshot ──────────────────────────────
        public static Dictionary<string, ObjectSnapshot> TakeSnapshot(HavokManager manager)
        {
            var snap = new Dictionary<string, ObjectSnapshot>();
            foreach (var kvp in manager.ObjectMap)
            {
                var obj = kvp.Value;
                var name = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "";
                var s = new ObjectSnapshot
                {
                    ClassName = obj.ClassName ?? "",
                    Name = name
                };

                foreach (var p in obj.Params)
                {
                    var ps = new ParamSnapshot
                    {
                        Value = p.Value ?? "",
                        NumElements = p.NumElements ?? "",
                        Strings = p.Strings != null ? new List<string>(p.Strings) : new List<string>()
                    };

                    // Deep-capture inline children
                    if (p.Children != null)
                    {
                        foreach (var child in p.Children)
                        {
                            var cs = new ChildSnapshot { ClassName = child.ClassName ?? "" };
                            foreach (var cp in child.Params)
                                cs.Params[cp.Name] = cp.Value ?? "";
                            ps.Children.Add(cs);
                        }
                    }

                    s.Params[p.Name] = ps;
                }

                snap[kvp.Key] = s;
            }
            return snap;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private List<string> GetCurrentEventNames()
        {
            var strData = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            if (strData == null) return new List<string>();
            var ep = strData.Params.FirstOrDefault(p => p.Name == "eventNames");
            if (ep == null) return new List<string>();
            return ep.Strings?.Count > 0
                ? ep.Strings
                : (ep.Value ?? "")
                    .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
        }

        private List<string> GetCurrentVarNames()
        {
            var strData = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            if (strData == null) return new List<string>();
            var np = strData.Params.FirstOrDefault(p => p.Name == "variableNames");
            if (np == null) return new List<string>();
            return np.Strings?.Count > 0
                ? np.Strings
                : (np.Value ?? "")
                    .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
        }

        private static string MakeAnchor(string id, HkObject obj)
        {
            // 1. Named objects — most portable
            var name = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value;
            if (!string.IsNullOrEmpty(name))
                return $"name:{name}";

            // 2. Objects with a unique identifying param combo
            // e.g. hkbStateMachineStateInfo has stateId + parent SM
            if (obj.ClassName == "hkbStateMachineStateInfo")
            {
                var stateId = obj.Params.FirstOrDefault(p => p.Name == "stateId")?.Value;
                if (!string.IsNullOrEmpty(stateId))
                    return $"stateId:{stateId}";
            }

            // 3. hkbClipGenerator — animationName is unique enough
            if (obj.ClassName == "hkbClipGenerator")
            {
                var anim = obj.Params.FirstOrDefault(p => p.Name == "animationName")?.Value;
                if (!string.IsNullOrEmpty(anim))
                    return $"animName:{anim}";
            }

            // 4. Transition arrays — identified by their owning state
            if (obj.ClassName == "hkbStateMachineTransitionInfoArray")
            {
                // Find the state that owns this transition array
                // (stored as a reference in that state's "transitions" param)
                return $"id:{id}"; // fallback — improved below
            }

            // Singleton classes — only one instance per file
            var singletonClasses = new HashSet<string>
            {
                "hkbBehaviorGraphStringData",
                "hkbBehaviorGraphData",
                "hkbVariableValueSet",
                "hkbBehaviorGraph",
                "hkRootLevelContainer"
            };

            if (singletonClasses.Contains(obj.ClassName))
                return $"class:{obj.ClassName}";

            // 5. Raw ID fallback — least portable, flagged in patch preview as a warning
            return $"id:{id}";
        }

        private static readonly HashSet<string> _skipChildDiffParams = new()
{
    "eventNames", "variableNames", "wordVariableNames",
    "animationNames", "stringData"
};

        private static string SafeName(string? cls)
            => (cls ?? "obj").Replace("hkb", "").Replace("hk", "").ToLower();

        private static string Truncate(string? s, int max = 40)
            => s?.Length > max ? s.Substring(0, max) + "…" : s ?? "";
    }

    internal static class StringExtensions
    {
        public static string IfEmpty(this string s, string fallback)
            => string.IsNullOrEmpty(s) ? fallback : s;
    }
}
