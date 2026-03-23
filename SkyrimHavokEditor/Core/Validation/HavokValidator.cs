using SkyrimHavokEditor.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkyrimHavokEditor.Models.ViewModels;

namespace SkyrimHavokEditor.Core.Validation
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

            // 1. Broken references — params with #xxxx values that don't exist in ObjectMap
            foreach (var obj in _manager.ObjectMap.Values)
            {
                foreach (var param in obj.Params)
                {
                    if (string.IsNullOrEmpty(param.Value)) continue;
                    if (!param.Value.StartsWith("#")) continue;
                    if (param.Value == "#0000") continue; // null ref convention

                    // Split space-separated references (e.g. "states" param)
                    var refs = param.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(r => r.StartsWith("#"));

                    foreach (var refId in refs)
                    {
                        if (!_manager.ObjectMap.ContainsKey(refId))
                        {
                            issues.Add(new ValidationIssue
                            {
                                Severity = "Error",
                                ObjectId = obj.Id,
                                ObjectClass = obj.ClassName,
                                ObjectName = GetName(obj),
                                Description = $"Broken reference: {param.Name} → {refId} (not found)"
                            });
                        }
                    }
                }
            }

            // 2. Orphaned objects — objects not referenced by anything
            var allRefs = new HashSet<string>();
            foreach (var obj in _manager.ObjectMap.Values)
                foreach (var param in obj.Params)
                    if (!string.IsNullOrEmpty(param.Value))
                        foreach (var r in param.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Where(r => r.StartsWith("#")))
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
                foreach (var stateRef in statesParam.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!_manager.TryResolve(stateRef, out var stateObj)) continue;
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
                foreach (var sr in smStatesParam.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (_manager.TryResolve(sr, out var so))
                    {
                        var sid = so.Params.FirstOrDefault(p => p.Name == "stateId")?.Value;
                        if (sid != null) validStateIds.Add(sid);
                    }
                }

                // Also check wildcardTransitions on the SM itself — skip those entirely
                var smWildcardRef = sm.Params.FirstOrDefault(p => p.Name == "wildcardTransitions")?.Value;
                var wildcardArrayIds = new HashSet<string>();
                if (!string.IsNullOrEmpty(smWildcardRef) && smWildcardRef != "null"
                    && _manager.TryResolve(smWildcardRef, out var wildcardArray))
                {
                    var wtp = wildcardArray.Params.FirstOrDefault(p => p.Name == "transitions");
                    if (wtp?.Children != null)
                        foreach (var wtr in wtp.Children)
                            wildcardArrayIds.Add(wtr.GetHashCode().ToString());
                }

                foreach (var sr in smStatesParam.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!_manager.TryResolve(sr, out var stateObj)) continue;
                    var transRef = stateObj.Params.FirstOrDefault(p => p.Name == "transitions")?.Value;
                    if (string.IsNullOrEmpty(transRef) || transRef == "null") continue;
                    if (!_manager.TryResolve(transRef, out var transArray)) continue;
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

                        if (!validStateIds.Contains(toSid))
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

            return issues;
        }
    }
}
