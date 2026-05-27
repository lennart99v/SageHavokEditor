using SkyrimHavokEditor.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimHavokEditor.Core.Patching
{
    public class ApplyResult
    {
        public bool Success { get; set; } = true;
        public List<string> Applied { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();

        public int AppliedCount => Applied.Count;
        public int WarningCount => Warnings.Count;
        public int ErrorCount => Errors.Count;
    }

    public class PatchApplier
    {
        private readonly HavokManager _manager;

        /// Maps localId (from patch) → actual object ID in the target file
        private readonly Dictionary<string, string> _localIdMap = new();

        public PatchApplier(HavokManager manager)
        {
            _manager = manager;
        }

        public ApplyResult Apply(BehaviorPatch patch)
        {
            var result = new ApplyResult();
            _localIdMap.Clear();

            foreach (var op in patch.Operations)
            {
                try
                {
                    switch (op)
                    {
                        case AddObjectOp add:
                            ApplyAddObject(add, result);
                            break;

                        case DeleteObjectOp del:
                            ApplyDeleteObject(del, result);
                            break;

                        case ModifyParamOp mod:
                            ApplyModifyParam(mod, result);
                            break;

                        case AppendParamOp apd:
                            ApplyAppendParam(apd, result);
                            break;

                        case AddChildOp ach:
                            ApplyAddChild(ach, result);
                            break;

                        case ModifyChildOp chd:
                            ApplyModifyChild(chd, result);
                            break;

                        case RenameEventOp rev:
                            ApplyRenameEvent(rev, result);
                            break;

                        case AddEventOp aev:
                            ApplyAddEvent(aev, result);
                            break;

                        case RenameVariableOp rvr:
                            ApplyRenameVariable(rvr, result);
                            break;

                        case AddVariableOp avr:
                            ApplyAddVariable(avr, result);
                            break;

                        default:
                            result.Warnings.Add($"Unknown operation type: {op.GetType().Name}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Exception in {op.GetType().Name} [{op.Note}]: {ex.Message}");
                    result.Success = false;
                }
            }

            if (result.Errors.Count > 0)
                result.Success = false;

            return result;
        }

        // ── ADD OBJECT ────────────────────────────────────────────────────────

        private void ApplyAddObject(AddObjectOp op, ApplyResult result)
        {
            var newId = GenerateNewObjectId();
            var obj = new HkObject
            {
                Id = newId,
                ClassName = op.ClassName,
                Signature = op.Signature,
                Params = new List<HkParam>()
            };

            foreach (var pp in op.Params)
            {
                // Resolve localRef → actual ID if needed
                var val = ResolveValue(pp.Value, pp.LocalRef);
                obj.Params.Add(new HkParam { Name = pp.Name, Value = val });
            }

            // Inline children
            foreach (var childOp in op.Children)
            {
                var childObj = new HkObject
                {
                    ClassName = childOp.ClassName,
                    Params = childOp.Params.Select(cp =>
                        new HkParam { Name = cp.Name, Value = ResolveValue(cp.Value, cp.LocalRef) })
                        .ToList()
                };
                // Find the parent param to attach to (last param with Children)
                var parentParam = obj.Params.LastOrDefault();
                if (parentParam != null)
                {
                    parentParam.Children ??= new List<HkObject>();
                    parentParam.Children.Add(childObj);
                }
            }

            _manager.ObjectMap[newId] = obj;

            // Register localId so subsequent AppendParamOps can reference it
            if (!string.IsNullOrEmpty(op.LocalId))
                _localIdMap[op.LocalId] = newId;

            result.Applied.Add($"Added {op.ClassName} as {newId} [{op.Note}]");
        }

        // ── DELETE OBJECT ─────────────────────────────────────────────────────

        private void ApplyDeleteObject(DeleteObjectOp op, ApplyResult result)
        {
            var obj = ResolveAnchor(op.Anchor);
            if (obj == null)
            {
                result.Warnings.Add($"DeleteObject: could not resolve anchor '{op.Anchor}' — skipping");
                return;
            }

            _manager.ObjectMap.Remove(obj.Id);
            result.Applied.Add($"Deleted {obj.ClassName} {obj.Id} [{op.Note}]");
        }

        // ── MODIFY PARAM ──────────────────────────────────────────────────────

        private void ApplyModifyParam(ModifyParamOp op, ApplyResult result)
        {
            var obj = ResolveAnchor(op.Anchor);
            if (obj == null)
            {
                result.Warnings.Add($"ModifyParam: could not resolve anchor '{op.Anchor}' — skipping [{op.Note}]");
                return;
            }

            var param = obj.Params.FirstOrDefault(p => p.Name == op.ParamName);
            if (param == null)
            {
                result.Warnings.Add($"ModifyParam: param '{op.ParamName}' not found on {obj.Id} — skipping");
                return;
            }

            // Warn if current value doesn't match expected old value
            if (!string.IsNullOrEmpty(op.OldValue) && param.Value != op.OldValue)
                result.Warnings.Add($"ModifyParam: {obj.Id}.{op.ParamName} expected " +
                    $"'{Truncate(op.OldValue)}' but found '{Truncate(param.Value)}' — applying anyway");

            param.Value = op.NewValue;
            result.Applied.Add($"Modified {obj.Id}.{op.ParamName} [{op.Note}]");
        }

        // ── APPEND PARAM ──────────────────────────────────────────────────────

        private void ApplyAppendParam(AppendParamOp op, ApplyResult result)
        {
            var obj = ResolveAnchor(op.Anchor);
            if (obj == null)
            {
                result.Warnings.Add($"AppendParam: could not resolve anchor '{op.Anchor}' — skipping");
                return;
            }

            var param = obj.Params.FirstOrDefault(p => p.Name == op.ParamName);
            if (param == null)
            {
                result.Warnings.Add($"AppendParam: param '{op.ParamName}' not found on {obj.Id} — skipping");
                return;
            }

            var valueToAppend = ResolveValue(op.Value, op.LocalRef);
            if (string.IsNullOrEmpty(valueToAppend))
            {
                result.Warnings.Add($"AppendParam: could not resolve value/localRef for {op.ParamName} — skipping");
                return;
            }

            // Check not already present
            var existing = (param.Value ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (existing.Contains(valueToAppend))
            {
                result.Warnings.Add($"AppendParam: {valueToAppend} already in {obj.Id}.{op.ParamName} — skipping");
                return;
            }

            param.Value = string.IsNullOrEmpty(param.Value)
                ? valueToAppend
                : param.Value + " " + valueToAppend;

            // Update NumElements if present
            if (!string.IsNullOrEmpty(param.NumElements))
            {
                if (int.TryParse(param.NumElements, out int n))
                    param.NumElements = (n + 1).ToString();
            }

            result.Applied.Add($"Appended {valueToAppend} to {obj.Id}.{op.ParamName}");
        }

        // ── ADD CHILD ──────────────────────────────────────────────────────
        private void ApplyAddChild(AddChildOp op, ApplyResult result)
        {
            var obj = ResolveAnchor(op.Anchor);
            if (obj == null)
            {
                result.Warnings.Add($"AddChild: could not resolve anchor '{op.Anchor}' — skipping");
                return;
            }

            var param = obj.Params.FirstOrDefault(p => p.Name == op.ParamName);
            if (param == null)
            {
                result.Warnings.Add($"AddChild: param '{op.ParamName}' not found on {obj.Id} — skipping");
                return;
            }

            param.Children ??= new List<HkObject>();

            var newChild = new HkObject
            {
                ClassName = op.ClassName,
                Params = op.Params.Select(pp =>
                    new HkParam { Name = pp.Name, Value = ResolveValue(pp.Value, pp.LocalRef) })
                    .ToList()
            };

            param.Children.Add(newChild);

            if (!string.IsNullOrEmpty(param.NumElements) &&
                int.TryParse(param.NumElements, out int n))
                param.NumElements = (n + 1).ToString();

            result.Applied.Add($"Added child to {obj.Id}.{op.ParamName} [{op.Note}]");
        }

        // ── MODIFY CHILD ──────────────────────────────────────────────────────

        private void ApplyModifyChild(ModifyChildOp op, ApplyResult result)
        {
            // Skip string data noise operations
            if (op.ParamName == "eventNames" ||
    op.ParamName == "stringData" ||
    op.Anchor.Contains("stringData"))
            {
                result.Warnings.Add($"Skipped string data child op (use RenameEventOp instead)");
                return;
            }
            var obj = ResolveAnchor(op.Anchor);
            if (obj == null)
            {
                result.Warnings.Add($"ModifyChild: could not resolve anchor '{op.Anchor}' — skipping");
                return;
            }

            var param = obj.Params.FirstOrDefault(p => p.Name == op.ParamName);
            if (param?.Children == null || op.ChildIndex >= param.Children.Count)
            {
                result.Warnings.Add($"ModifyChild: {obj.Id}.{op.ParamName}[{op.ChildIndex}] not found — skipping");
                return;
            }

            var child = param.Children[op.ChildIndex];
            var childParam = child.Params.FirstOrDefault(p => p.Name == op.ChildParam);
            if (childParam == null)
            {
                result.Warnings.Add($"ModifyChild: child param '{op.ChildParam}' not found — skipping");
                return;
            }

            if (!string.IsNullOrEmpty(op.OldValue) && childParam.Value != op.OldValue)
                result.Warnings.Add($"ModifyChild: {op.ChildParam} expected '{op.OldValue}' " +
                    $"but found '{childParam.Value}' — applying anyway");

            childParam.Value = op.NewValue;
            result.Applied.Add($"Modified child {obj.Id}.{op.ParamName}[{op.ChildIndex}].{op.ChildParam}");
        }

        // ── RENAME EVENT ──────────────────────────────────────────────────────

        private void ApplyRenameEvent(RenameEventOp op, ApplyResult result)
        {
            // Replace the single FirstOrDefault line in both methods with:
            var strData = _manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData"
                                  || o.ClassName == "hkbProjectStringData");
            if (strData == null)
            {
                result.Warnings.Add($"RenameEvent: hkbBehaviorGraphStringData not found — skipping");
                return;
            }

            var ep = strData.Params.FirstOrDefault(p => p.Name == "eventNames");
            if (ep == null)
            {
                result.Warnings.Add($"RenameEvent: eventNames param not found — skipping");
                return;
            }

            // Work with the Strings list if populated, otherwise split Value
            if (ep.Strings == null || ep.Strings.Count == 0)
            {
                ep.Strings = (ep.Value ?? "")
                    .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
            }

            if (op.Index >= ep.Strings.Count)
            {
                result.Warnings.Add($"RenameEvent: index {op.Index} out of range ({ep.Strings.Count} events)");
                return;
            }

            if (ep.Strings[op.Index] != op.OldName)
                result.Warnings.Add($"RenameEvent[{op.Index}]: expected '{op.OldName}' " +
                    $"but found '{ep.Strings[op.Index]}' — applying anyway");

            ep.Strings[op.Index] = op.NewName;
            ep.Value = string.Join("\n", ep.Strings);
            result.Applied.Add($"Renamed event[{op.Index}] '{op.OldName}' → '{op.NewName}'");
        }

        // ── ADD EVENT ─────────────────────────────────────────────────────────

        private void ApplyAddEvent(AddEventOp op, ApplyResult result)
        {
            var strData = _manager.ObjectMap.Values
    .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData"
                      || o.ClassName == "hkbProjectStringData");
            if (strData == null)
            {
                result.Warnings.Add("AddEvent: hkbBehaviorGraphStringData not found — skipping");
                return;
            }

            var ep = strData.Params.FirstOrDefault(p => p.Name == "eventNames");
            if (ep == null)
            {
                result.Warnings.Add("AddEvent: eventNames param not found — skipping");
                return;
            }

            if (ep.Strings == null || ep.Strings.Count == 0)
                ep.Strings = (ep.Value ?? "")
                    .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

            // Avoid duplicates
            if (ep.Strings.Contains(op.Name))
            {
                result.Warnings.Add($"AddEvent: '{op.Name}' already exists — skipping");
                return;
            }

            ep.Strings.Add(op.Name);
            ep.Value = string.Join("\n", ep.Strings);
            ep.NumElements = ep.Strings.Count.ToString();
            result.Applied.Add($"Added event '{op.Name}' at index {ep.Strings.Count - 1}");
        }

        // ── RENAME VARIABLE ───────────────────────────────────────────────────

        private void ApplyRenameVariable(RenameVariableOp op, ApplyResult result)
        {
            var strData = GetStringData();
            if (strData == null)
            {
                result.Warnings.Add("RenameVariable: string data not found — skipping");
                return;
            }

            var np = strData.Params.FirstOrDefault(p => p.Name == "variableNames");
            if (np == null)
            {
                result.Warnings.Add("RenameVariable: variableNames param not found — skipping");
                return;
            }

            if (np.Strings == null || np.Strings.Count == 0)
                np.Strings = (np.Value ?? "")
                    .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

            if (op.Index >= np.Strings.Count)
            {
                result.Warnings.Add($"RenameVariable: index {op.Index} out of range — skipping");
                return;
            }

            if (np.Strings[op.Index] != op.OldName)
                result.Warnings.Add($"RenameVariable[{op.Index}]: expected '{op.OldName}' " +
                    $"but found '{np.Strings[op.Index]}' — applying anyway");

            np.Strings[op.Index] = op.NewName;
            np.Value = string.Join("\n", np.Strings);
            result.Applied.Add($"Renamed variable[{op.Index}] '{op.OldName}' → '{op.NewName}'");
        }

        // ── ADD VARIABLE ──────────────────────────────────────────────────────

        private void ApplyAddVariable(AddVariableOp op, ApplyResult result)
        {
            var strData = GetStringData();
            if (strData == null)
            {
                result.Warnings.Add("AddVariable: string data not found — skipping");
                return;
            }

            var np = strData.Params.FirstOrDefault(p => p.Name == "variableNames");
            if (np == null)
            {
                result.Warnings.Add("AddVariable: variableNames param not found — skipping");
                return;
            }

            if (np.Strings == null || np.Strings.Count == 0)
                np.Strings = (np.Value ?? "")
                    .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

            if (np.Strings.Contains(op.Name))
            {
                result.Warnings.Add($"AddVariable: '{op.Name}' already exists — skipping");
                return;
            }

            np.Strings.Add(op.Name);
            np.Value = string.Join("\n", np.Strings);
            np.NumElements = np.Strings.Count.ToString();
            result.Applied.Add($"Added variable '{op.Name}' at index {np.Strings.Count - 1}");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// Resolve "name:BHR_Master" or "id:#0052" to an HkObject
        private HkObject ResolveAnchor(string anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return null;

            // name:BHR_Master
            if (anchor.StartsWith("name:"))
            {
                var name = anchor.Substring(5);
                return _manager.ObjectMap.Values.FirstOrDefault(o =>
                    o.Params.Any(p => p.Name == "name" && p.Value == name));
            }

            // id:#0052 — direct ID lookup, least portable
            if (anchor.StartsWith("id:"))
            {
                _manager.ObjectMap.TryGetValue(anchor.Substring(3), out var byId);
                return byId;
            }

            // stateId:3 — find hkbStateMachineStateInfo with matching stateId
            if (anchor.StartsWith("stateId:"))
            {
                var sid = anchor.Substring(8);
                return _manager.ObjectMap.Values.FirstOrDefault(o =>
                    o.ClassName == "hkbStateMachineStateInfo" &&
                    o.Params.Any(p => p.Name == "stateId" && p.Value == sid));
            }

            // animName:Animations\Ground_Bite.HKX
            if (anchor.StartsWith("animName:"))
            {
                var anim = anchor.Substring(9);
                return _manager.ObjectMap.Values.FirstOrDefault(o =>
                    o.ClassName == "hkbClipGenerator" &&
                    o.Params.Any(p => p.Name == "animationName" && p.Value == anim));
            }

            // class:hkbBehaviorGraphStringData — for singleton objects
            if (anchor.StartsWith("class:"))
            {
                var cls = anchor.Substring(6);
                // Try exact match first
                var exact = _manager.ObjectMap.Values
                    .FirstOrDefault(o => o.ClassName == cls);
                if (exact != null) return exact;

                // Fallback: hkbBehaviorGraphStringData ↔ hkbProjectStringData are interchangeable
                if (cls == "hkbBehaviorGraphStringData")
                    return _manager.ObjectMap.Values
                        .FirstOrDefault(o => o.ClassName == "hkbProjectStringData");
                if (cls == "hkbProjectStringData")
                    return _manager.ObjectMap.Values
                        .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");

                return null;
            }

            return null;
        }

        /// Resolve a value — if localRef is set, look up the mapped actual ID
        private string ResolveValue(string value, string localRef)
        {
            if (!string.IsNullOrEmpty(localRef))
            {
                if (_localIdMap.TryGetValue(localRef, out var mapped))
                    return mapped;
                // localRef not yet mapped — return raw (may be resolved later)
                return localRef;
            }
            return value ?? "";
        }

        private string GenerateNewObjectId()
        {
            var existing = _manager.ObjectMap.Keys
                .Where(k => k.StartsWith("#"))
                .Select(k => int.TryParse(k.Substring(1), out int n) ? n : 0)
                .ToHashSet();
            int next = 1;
            while (existing.Contains(next)) next++;
            return $"#{next:D4}";
        }

        // Add to PatchApplier:
        private HkObject GetStringData() =>
            _manager.ObjectMap.Values.FirstOrDefault(o =>
                o.ClassName == "hkbBehaviorGraphStringData" ||
                o.ClassName == "hkbProjectStringData");

        private static string Truncate(string s, int max = 40)
            => s?.Length > max ? s.Substring(0, max) + "…" : s ?? "";
    }
}