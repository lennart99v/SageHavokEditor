using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SageHavokEditor.Models;
using SageHavokEditor.Models.ViewModels;

namespace SageHavokEditor.Core.Validation
{
    public class HavokValidator
    {
        private readonly HavokManager _manager;
        public HavokValidator(HavokManager manager) => _manager = manager;

        public List<ValidationIssue> RunValidation()
        {
            var issues = new List<ValidationIssue>();

            string GetName(HkObject o) =>
                o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id;

            // 1. Broken references — #xxxx tokens that don't exist in ObjectMap.
            // EnumerateRefs walks inline (anonymous) children too — transition
            // arrays and hkRootLevelContainer.namedVariants carry their refs in
            // nested params, not the param value.
            foreach (var obj in _manager.ObjectMap.Values)
            {
                foreach (var (path, refId) in EnumerateRefs(obj))
                {
                    if (refId == "#0000") continue; // null ref convention
                    if (!_manager.ObjectMap.ContainsKey(refId))
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = "Error",
                            ObjectId = obj.Id,
                            ObjectClass = obj.ClassName,
                            ObjectName = GetName(obj),
                            Description = $"Broken reference: {path} → {refId} (not found)"
                        });
                    }
                }
            }

            // 2. Orphaned objects — objects not referenced by anything
            var allRefs = new HashSet<string>();
            foreach (var obj in _manager.ObjectMap.Values)
                foreach (var (_, r) in EnumerateRefs(obj))
                    allRefs.Add(r);

            // Top level container is never referenced by anything — exclude it
            var topLevel = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkRootLevelContainer");

            foreach (var obj in _manager.ObjectMap.Values)
            {
                if (obj == topLevel) continue;
                if (!allRefs.Contains(obj.Id))
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = "Warning",
                        ObjectId = obj.Id,
                        ObjectClass = obj.ClassName,
                        ObjectName = GetName(obj),
                        Description = "Orphaned object — not referenced by any other object"
                    });
                }
            }

            // 3. State machines with no states
            foreach (var sm in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine"))
            {
                var statesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
                if (statesParam == null || string.IsNullOrWhiteSpace(statesParam.Value))
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = "Warning",
                        ObjectId = sm.Id,
                        ObjectClass = sm.ClassName,
                        ObjectName = GetName(sm),
                        Description = "State machine has no states"
                    });
                }
            }

            // 3b. startStateId that doesn't match any state — the machine has no
            // valid start state and T-poses silently in-game. Only checked when
            // the start state actually comes from startStateId: a chooser or a
            // non-default startStateMode picks the start state some other way.
            foreach (var sm in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine"))
            {
                var statesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
                if (statesParam == null || string.IsNullOrWhiteSpace(statesParam.Value)) continue;

                var chooser = sm.Params.FirstOrDefault(p => p.Name == "startStateChooser")?.Value;
                if (!string.IsNullOrEmpty(chooser) && chooser != "null") continue;
                var mode = sm.Params.FirstOrDefault(p => p.Name == "startStateMode")?.Value;
                if (!string.IsNullOrEmpty(mode) && !mode.Contains("DEFAULT")) continue;

                var startId = sm.Params.FirstOrDefault(p => p.Name == "startStateId")?.Value ?? "0";
                var knownIds = HkRefList.Tokens(statesParam.Value)
                    .Select(r => _manager.TryResolve(r, out var so) && so != null
                        ? so.Params.FirstOrDefault(p => p.Name == "stateId")?.Value : null)
                    .Where(id => id != null)
                    .ToHashSet();

                if (knownIds.Count > 0 && !knownIds.Contains(startId))
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = "Error",
                        ObjectId = sm.Id,
                        ObjectClass = sm.ClassName,
                        ObjectName = GetName(sm),
                        Description = $"startStateId {startId} doesn't match any state " +
                                      $"(stateIds: {string.Join(", ", knownIds.OrderBy(x => x, StringComparer.Ordinal))})"
                    });
                }
            }

            // 4. Clips with empty animation paths
            foreach (var clip in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbClipGenerator"))
            {
                var animParam = clip.Params.FirstOrDefault(p => p.Name == "animationName");
                if (animParam == null || string.IsNullOrWhiteSpace(animParam.Value))
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = "Warning",
                        ObjectId = clip.Id,
                        ObjectClass = clip.ClassName,
                        ObjectName = GetName(clip),
                        Description = "Clip has no animation path set"
                    });
                }
            }

            // 5. Variable count mismatch between names and values
            var nameData = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            var valueSet = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbVariableValueSet");

            if (nameData != null && valueSet != null)
            {
                var namesParam = nameData.Params.FirstOrDefault(p => p.Name == "variableNames");
                var valuesParam = valueSet.Params.FirstOrDefault(p => p.Name == "wordVariableValues");

                int nameCount = namesParam?.Strings.Count ?? 0;
                int valueCount = valuesParam?.Children.Count ?? 0;

                if (nameCount != valueCount)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = "Error",
                        ObjectId = valueSet.Id,
                        ObjectClass = "hkbVariableValueSet",
                        ObjectName = valueSet.Id,
                        Description = $"Variable count mismatch: {nameCount} names but {valueCount} values"
                    });
                }
            }

            // 6. Duplicate state IDs within a state machine
            foreach (var sm in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine"))
            {
                var statesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
                if (statesParam == null) continue;

                var stateIds = new Dictionary<string, string>();
                foreach (var stateRef in HkRefList.Tokens(statesParam.Value))
                {
                    if (!_manager.TryResolve(stateRef, out var stateObj) || stateObj == null) continue;
                    var stateId = stateObj.Params.FirstOrDefault(p => p.Name == "stateId")?.Value ?? "";
                    if (stateIds.TryGetValue(stateId, out var existing))
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = "Error",
                            ObjectId = sm.Id,
                            ObjectClass = sm.ClassName,
                            ObjectName = GetName(sm),
                            Description = $"Duplicate stateId {stateId} in states {existing} and {stateRef}"
                        });
                    }
                    else stateIds[stateId] = stateRef;
                }
            }

            // 7. toStateId cross-validation
            foreach (var sm in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine"))
            {
                var smStatesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
                if (smStatesParam == null) continue;

                var validStateIds = new HashSet<string>();
                foreach (var sr in HkRefList.Tokens(smStatesParam.Value))
                {
                    if (_manager.TryResolve(sr, out var so) && so != null)
                    {
                        var sid = so.Params.FirstOrDefault(p => p.Name == "stateId")?.Value;
                        if (sid != null) validStateIds.Add(sid);
                    }
                }

                // Also check wildcardTransitions on the SM itself — skip those entirely
                var smWildcardRef = sm.Params.FirstOrDefault(p => p.Name == "wildcardTransitions")?.Value;
                var wildcardArrayIds = new HashSet<string>();
                if (!string.IsNullOrEmpty(smWildcardRef) && smWildcardRef != "null"
                    && _manager.TryResolve(smWildcardRef, out var wildcardArray) && wildcardArray != null)
                {
                    var wtp = wildcardArray.Params.FirstOrDefault(p => p.Name == "transitions");
                    if (wtp?.Children != null)
                        foreach (var wtr in wtp.Children)
                            wildcardArrayIds.Add(wtr.GetHashCode().ToString());
                }

                foreach (var sr in HkRefList.Tokens(smStatesParam.Value))
                {
                    if (!_manager.TryResolve(sr, out var stateObj) || stateObj == null) continue;
                    var transRef = stateObj.Params.FirstOrDefault(p => p.Name == "transitions")?.Value;
                    if (string.IsNullOrEmpty(transRef) || transRef == "null") continue;
                    if (!_manager.TryResolve(transRef, out var transArray) || transArray == null) continue;
                    var tp = transArray.Params.FirstOrDefault(p => p.Name == "transitions");
                    if (tp?.Children == null) continue;

                    foreach (var tr in tp.Children)
                    {
                        var toSid = tr.Params.FirstOrDefault(p => p.Name == "toStateId")?.Value;
                        var flags = tr.Params.FirstOrDefault(p => p.Name == "flags")?.Value ?? "";

                        // Skip any transition with wildcard or nested-state flags
                        if (flags.Contains("WILDCARD") ||
                            flags.Contains("FLAG_TO_NESTED_STATE_ID_IS_VALID")) continue;

                        // Skip toStateId -1 (used as "no target" in some setups)
                        if (toSid == "-1" || toSid == null) continue;

                        if (flags.Contains("NESTED") || flags.Contains("WILDCARD")) continue;

                        // Skip if toNestedStateId is set (non-zero means it targets a nested state)
                        var toNestedSid = tr.Params.FirstOrDefault(p => p.Name == "toNestedStateId")?.Value;
                        if (toNestedSid != null && toNestedSid != "0") continue;

                        // Also skip FLAG_TO_NESTED_STATE_ID_IS_VALID explicitly  
                        if (flags.Contains("TO_NESTED")) continue;

                        if (toSid != null && !validStateIds.Contains(toSid))
                        {
                            issues.Add(new ValidationIssue
                            {
                                Severity = "Error",
                                ObjectId = stateObj.Id,
                                ObjectClass = stateObj.ClassName,
                                ObjectName = GetName(stateObj),
                                Description = $"Transition toStateId {toSid} not found in SM '{GetName(sm)}' " +
                                              $"(valid: {string.Join(", ", validStateIds.OrderBy(x => x))})"
                            });
                        }
                    }
                }
            }

            // 8. Values that don't parse as their declared Havok type
            issues.AddRange(CheckParamTypes());

            // 9. Name/info array pairing — the runtime aligns eventNames↔eventInfos
            // and variableNames↔variableInfos by index, so a count mismatch breaks
            // every entry past the shorter array.
            var stringData = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            var graphData = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphData");
            if (stringData != null && graphData != null)
            {
                void CheckPairing(string namesParam, string infosParam)
                {
                    var names = stringData.Params.FirstOrDefault(p => p.Name == namesParam)?.Strings;
                    var infos = graphData.Params.FirstOrDefault(p => p.Name == infosParam)?.Children;
                    if (names == null || infos == null || names.Count == infos.Count) return;
                    issues.Add(new ValidationIssue
                    {
                        Severity = "Error",
                        ObjectId = graphData.Id,
                        ObjectClass = graphData.ClassName,
                        ObjectName = GetName(graphData),
                        Description = $"{namesParam} has {names.Count} entries but {infosParam} has {infos.Count} — " +
                                      "the runtime pairs them by index; entries past the shorter array are broken in-game"
                    });
                }
                CheckPairing("eventNames", "eventInfos");
                CheckPairing("variableNames", "variableInfos");
            }

            return issues;
        }

        /// <summary>
        /// Type-check every param value against its declared Havok type (annotated
        /// by HavokTypeCatalog at load), nested inline params included. These are
        /// the values HKX2's XML→HKX conversion would reject with a bare
        /// FormatException — run before save to fail with a pointable error instead.
        /// </summary>
        public List<ValidationIssue> CheckParamTypes()
        {
            var issues = new List<ValidationIssue>();

            string GetName(HkObject o) =>
                o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id;

            foreach (var obj in _manager.ObjectMap.Values)
                foreach (var param in obj.Params)
                    CheckParam(obj, param, param.Name, issues, GetName);

            return issues;
        }

        private static void CheckParam(HkObject owner, HkParam param, string path,
            List<ValidationIssue> issues, Func<HkObject, string> getName)
        {
            if (param.TypeInfo != null && !param.IsValueTypeValid)
            {
                issues.Add(new ValidationIssue
                {
                    Severity = "Error",
                    ObjectId = owner.Id,
                    ObjectClass = owner.ClassName,
                    ObjectName = getName(owner),
                    Description = $"{path} = \"{Truncate(param.Value)}\" doesn't match its declared type. {param.TypeInfo.Hint}"
                });
            }

            // Inline (anonymous) structs — cached resolved refs are top-level
            // objects and get checked in their own right.
            for (int i = 0; i < param.Children.Count; i++)
            {
                var child = param.Children[i];
                if (!string.IsNullOrEmpty(child.Id)) continue;
                foreach (var cp in child.Params)
                    CheckParam(owner, cp, $"{path}[{i}].{cp.Name}", issues, getName);
            }
        }

        private static string Truncate(string? v) =>
            (v ?? "").Length <= 40 ? v ?? "" : v!.Substring(0, 37) + "…";

        /// <summary>
        /// Every #ref token in an object, with its param path — including refs
        /// inside inline (anonymous) child structs, which top-level-only scans
        /// miss (transition arrays, hkRootLevelContainer.namedVariants).
        /// </summary>
        private static IEnumerable<(string Path, string RefId)> EnumerateRefs(HkObject obj)
        {
            foreach (var (path, param) in EnumerateParams(obj))
                foreach (var tok in HkRefList.Tokens(param.Value))
                    if (tok.StartsWith("#"))
                        yield return (path, tok);
        }

        private static IEnumerable<(string Path, HkParam Param)> EnumerateParams(HkObject obj)
        {
            foreach (var p in obj.Params)
            {
                yield return (p.Name, p);
                for (int i = 0; i < p.Children.Count; i++)
                {
                    var c = p.Children[i];
                    if (!string.IsNullOrEmpty(c.Id)) continue;  // cached resolved ref, not inline
                    foreach (var (subPath, sp) in EnumerateParams(c))
                        yield return ($"{p.Name}[{i}].{subPath}", sp);
                }
            }
        }
    }
}
