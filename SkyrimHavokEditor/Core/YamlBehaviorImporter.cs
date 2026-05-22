using SkyrimHavokEditor.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SkyrimHavokEditor.Core
{
    /// <summary>
    /// Imports a Pandora-style YAML behavior folder into a HavokManager.
    ///
    /// Handles both flat key:value and list-based YAML fields.
    /// String lists (eventNames, variableNames) are stored in HkParam.Strings
    /// so RefreshLookups() finds them exactly as it would from XML.
    ///
    /// Variable definitions with name/type/value (Pandora data/ format) are
    /// expanded into the correct hkbBehaviorGraphStringData + hkbBehaviorGraphData
    /// + hkbVariableValueSet structure the rest of the app expects.
    /// </summary>
    public class YamlBehaviorImporter
    {
        // ── Folder → default class mapping ───────────────────────────────────────
        private static readonly Dictionary<string, string> FolderToClass =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["clips"] = "hkbClipGenerator",
                ["generators"] = "hkbBlenderGenerator",
                ["modifiers"] = "hkbModifierGenerator",
                ["states"] = "hkbStateMachineStateInfo",
                ["transitions"] = "hkbStateMachineTransitionInfoArray",
                ["references"] = "hkbBehaviorReferenceGenerator",
                ["selectors"] = "BSiStateTaggingGenerator",
                ["tagging"] = "BSiStateTaggingGenerator",
                ["data"] = "hkbBehaviorGraphData",
            };

        // ── Fields holding single object-name references ─────────────────────────
        private static readonly HashSet<string> SingleRefFields =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "rootGenerator", "generator", "modifier", "data",
                "variableBindingSet", "transitions", "wildcardTransitions",
                "startStateChooser", "pDefaultGenerator", "pBlenderGenerator",
                "stringData", "variableInitialValues", "condition",
                "transition",
            };

        // ── Fields holding space-separated lists of object-name references ────────
        private static readonly HashSet<string> MultiRefFields =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "states", "generators", "modifiers", "ChildrenA",
            };

        // ── Fields that are string lists (stored in HkParam.Strings) ─────────────
        private static readonly HashSet<string> StringListFields =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "eventNames", "variableNames", "characterPropertyNames",
                "animationNames", "attributeNames",
            };

        private int _nextId = 1;
        private readonly Dictionary<string, HkObject> _nameToObject =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<HkObject> _allObjects = new();

        // ── Public entry point ────────────────────────────────────────────────────

        public string Import(string folderPath, HavokManager manager)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

            _nextId = 1;
            _nameToObject.Clear();
            _allObjects.Clear();

               string behaviorName = LoadAllYaml(folderPath);
            ResolveAllReferences();       // object name refs: transition: Name → #ID
            ResolveTransitionFields();    // event: Name → eventId: N, toState: Name → toStateId: N
            WireStateTransitions();       // wrap inline transition lists → TransitionInfoArray objects
            ResolveVariableBindings();    // variable: Name → variableIndex: N
            WireInlineBindings();         // inline bindings: → hkbVariableBindingSet objects

            manager.ObjectMap.Clear();
            foreach (var obj in _allObjects)
                manager.ObjectMap[obj.Id] = obj;

            return behaviorName;
        }

        // ── Pass 1: Load all YAML files ───────────────────────────────────────────

        private string LoadAllYaml(string folderPath)
        {
            string behaviorName = Path.GetFileName(folderPath);

            var behaviorYaml = Path.Combine(folderPath, "behavior.yaml");
            if (File.Exists(behaviorYaml))
                behaviorName = LoadBehaviorRoot(behaviorYaml);

            foreach (var subDir in Directory.EnumerateDirectories(folderPath))
            {
                var dirName = Path.GetFileName(subDir);
                FolderToClass.TryGetValue(dirName, out var defaultClass);
                foreach (var yamlFile in Directory.EnumerateFiles(subDir, "*.yaml"))
                    LoadObjectYaml(yamlFile, defaultClass);
            }

            // YAML files directly in root (besides behavior.yaml)
            foreach (var yamlFile in Directory.EnumerateFiles(folderPath, "*.yaml")
                .Where(f => !f.EndsWith("behavior.yaml", StringComparison.OrdinalIgnoreCase)))
                LoadObjectYaml(yamlFile, null);

            return behaviorName;
        }

        // ── behavior.yaml ─────────────────────────────────────────────────────────
        // Handles the root behavior file which may also contain
        // inline variables: and events: list sections.

        private string LoadBehaviorRoot(string yamlPath)
        {
            var text = File.ReadAllText(yamlPath);
            var doc = YamlDocument.Parse(text);

            string behaviorName = "behavior";

            // ── Root hkbBehaviorGraph object ──────────────────────────────────────
            var behaviorSection = doc.GetSection("behavior");
            if (behaviorSection != null)
            {
                behaviorName = behaviorSection.GetScalar("name")?.Trim('"') ?? behaviorName;

                var graphObj = new HkObject
                {
                    Id = AllocId(),
                    ClassName = "hkbBehaviorGraph",
                    Params = new List<HkParam>()
                };
                foreach (var (k, v) in behaviorSection.Scalars)
                    graphObj.Params.Add(new HkParam { Name = k, Value = v });

                RegisterObject(graphObj, behaviorName);
            }

            // ── Inline variables: section ─────────────────────────────────────────
            // Pandora format:
            //   variables:
            //     - name: iSyncIdleLocomotion
            //       type: VARIABLE_TYPE_INT32
            //       value: 0
            var varItems = doc.GetObjectList("variables");
            if (varItems.Count > 0)
                BuildVariableObjects(varItems);

            // ── Inline events: section ────────────────────────────────────────────
            // Pandora format:
            //   events:
            //     - name: moveStart
            var eventItems = doc.GetObjectList("events");
            if (eventItems.Count > 0)
                BuildEventObject(eventItems);

            return behaviorName;
        }

        // ── Build hkbBehaviorGraphStringData + hkbBehaviorGraphData + hkbVariableValueSet
        //    from inline variable definitions ─────────────────────────────────────

        private void BuildVariableObjects(List<Dictionary<string, string>> varItems)
        {
            // hkbBehaviorGraphStringData — holds variableNames list
            var strData = FindOrCreateStringData();
            var namesParam = strData.Params.FirstOrDefault(p => p.Name == "variableNames");
            if (namesParam == null)
            {
                namesParam = new HkParam
                {
                    Name = "variableNames",
                    Strings = new List<string>(),
                    NumElements = "0"
                };
                strData.Params.Add(namesParam);
            }

            // hkbBehaviorGraphData — holds variableInfos (type info)
            var graphData = FindOrCreateGraphData();
            var infosParam = graphData.Params.FirstOrDefault(p => p.Name == "variableInfos");
            if (infosParam == null)
            {
                infosParam = new HkParam
                {
                    Name = "variableInfos",
                    Children = new List<HkObject>(),
                    NumElements = "0"
                };
                graphData.Params.Add(infosParam);
            }

            // hkbVariableValueSet — holds initial values as bit patterns
            var valueSet = FindOrCreateValueSet();
            var valuesParam = valueSet.Params.FirstOrDefault(p => p.Name == "wordVariableValues");
            if (valuesParam == null)
            {
                valuesParam = new HkParam
                {
                    Name = "wordVariableValues",
                    Children = new List<HkObject>(),
                    NumElements = "0"
                };
                valueSet.Params.Add(valuesParam);
            }

            foreach (var item in varItems)
            {
                var name = item.GetValueOrDefault("name", "");
                var type = item.GetValueOrDefault("type", "VARIABLE_TYPE_REAL");
                var value = item.GetValueOrDefault("value", "0");

                namesParam.Strings.Add(name);

                infosParam.Children.Add(new HkObject
                {
                    Params = new List<HkParam>
                    {
                        new HkParam { Name = "role", Value = "{ 0 0 0 }" },
                        new HkParam { Name = "type", Value = type }
                    }
                });

                // Encode value: FLOAT needs IEEE 754 bit pattern
                var encodedValue = type.Contains("FLOAT")
                    ? EncodeFloat(value)
                    : value;

                valuesParam.Children.Add(new HkObject
                {
                    Params = new List<HkParam>
                    {
                        new HkParam { Name = "value", Value = encodedValue }
                    }
                });
            }

            namesParam.NumElements = namesParam.Strings.Count.ToString();
            infosParam.NumElements = infosParam.Children.Count.ToString();
            valuesParam.NumElements = valuesParam.Children.Count.ToString();
        }

        private void BuildEventObject(List<Dictionary<string, string>> eventItems)
        {
            var strData = FindOrCreateStringData();
            var evParam = strData.Params.FirstOrDefault(p => p.Name == "eventNames");
            if (evParam == null)
            {
                evParam = new HkParam
                {
                    Name = "eventNames",
                    Strings = new List<string>(),
                    NumElements = "0"
                };
                strData.Params.Add(evParam);
            }

            foreach (var item in eventItems)
            {
                // flags: is optional — just grab the name
                if (item.TryGetValue("name", out var name) && !string.IsNullOrEmpty(name))
                    evParam.Strings.Add(name);
            }

            evParam.NumElements = evParam.Strings.Count.ToString();
        }

        // ── Find-or-create the standard data objects ──────────────────────────────

        private HkObject FindOrCreateStringData()
        {
            var existing = _allObjects.FirstOrDefault(
                o => o.ClassName == "hkbBehaviorGraphStringData");
            if (existing != null) return existing;

            var obj = new HkObject
            {
                Id = AllocId(),
                ClassName = "hkbBehaviorGraphStringData",
                Params = new List<HkParam>()
            };
            RegisterObject(obj, "graphdata_strings");
            return obj;
        }

        private HkObject FindOrCreateGraphData()
        {
            var existing = _allObjects.FirstOrDefault(
                o => o.ClassName == "hkbBehaviorGraphData");
            if (existing != null) return existing;

            var obj = new HkObject
            {
                Id = AllocId(),
                ClassName = "hkbBehaviorGraphData",
                Params = new List<HkParam>()
            };
            RegisterObject(obj, "graphdata");
            return obj;
        }

        private HkObject FindOrCreateValueSet()
        {
            var existing = _allObjects.FirstOrDefault(
                o => o.ClassName == "hkbVariableValueSet");
            if (existing != null) return existing;

            var obj = new HkObject
            {
                Id = AllocId(),
                ClassName = "hkbVariableValueSet",
                Params = new List<HkParam>()
            };
            RegisterObject(obj, "valueset");
            return obj;
        }

        // ── Generic object YAML loader ────────────────────────────────────────────

        private void LoadObjectYaml(string yamlPath, string defaultClass)
        {
            var text = File.ReadAllText(yamlPath);
            var doc = YamlDocument.Parse(text);

            // Filename without extension is used as the fallback name AND for registration
            var fileName = Path.GetFileNameWithoutExtension(yamlPath);

            var className = doc.GetScalar("class") ?? defaultClass
                ?? GuessClassFromContent(doc);

            var objectName = doc.GetScalar("name") ?? fileName;

            // ── Special case: file contains top-level variables: or events: lists ────
            // This is the Pandora graphdata.yaml pattern — no class field, just lists.
            var varItems = doc.GetObjectList("variables");
            var eventItems = doc.GetObjectList("events");

            if (varItems.Count > 0 || eventItems.Count > 0)
            {
                if (varItems.Count > 0)
                    BuildVariableObjects(varItems);

                if (eventItems.Count > 0)
                    BuildEventObject(eventItems);

                // Register the hkbBehaviorGraphData under the filename so
                // "data: graphdata" in behavior.yaml resolves correctly.
                var graphData = FindOrCreateGraphData();
                if (!_nameToObject.ContainsKey(fileName))
                    _nameToObject[fileName] = graphData;

                // Also register string data under filename_strings in case needed
                var strData = FindOrCreateStringData();
                var strKey = fileName + "_strings";
                if (!_nameToObject.ContainsKey(strKey))
                    _nameToObject[strKey] = strData;

                // If the file ALSO has regular object fields (unusual but possible),
                // fall through to create a normal object below.
                // If it has NO class at all, we're done.
                if (string.IsNullOrEmpty(className)) return;
            }

            // ── Normal object loading ─────────────────────────────────────────────────
            if (string.IsNullOrEmpty(className)) return;

            var obj = new HkObject
            {
                Id = AllocId(),
                ClassName = className,
                Params = new List<HkParam>()
            };

            // Scalar params
            foreach (var (k, v) in doc.Scalars)
            {
                if (k == "class") continue;
                obj.Params.Add(new HkParam { Name = k, Value = v });
            }

            // String list params (eventNames, variableNames, etc.)
            foreach (var listField in StringListFields)
            {
                var items = doc.GetStringList(listField);
                if (items.Count == 0) continue;

                obj.Params.RemoveAll(p => p.Name == listField);
                obj.Params.Add(new HkParam
                {
                    Name = listField,
                    Strings = items,
                    NumElements = items.Count.ToString()
                });
            }

            // ── Block 1: Convert YAML string lists → space-separated scalar params ────────
            // Handles: states, generators, modifiers, ChildrenA
            // (anything in MultiRefFields that Pandora stores as a list rather than inline)
            foreach (var refField in MultiRefFields)
            {
                var listItems = doc.GetStringList(refField);
                if (listItems.Count == 0) continue;

                // Remove any scalar version already added above
                obj.Params.RemoveAll(p => p.Name == refField);

                obj.Params.Add(new HkParam
                {
                    Name = refField,
                    Value = string.Join(" ", listItems),   // resolved to IDs in Pass 2
                    NumElements = listItems.Count.ToString()
                });
            }


            // ── Block 2: Convert YAML object lists → HkParam.Children ────────────────────
            // Handles the transitions array inside hkbStateMachineTransitionInfoArray.
            // Also handles children arrays in hkbBlenderGenerator etc.
            var objectListFields = new[] { "transitions", "children", "bindings" };

            foreach (var listField in objectListFields)
            {
                var objectItems = doc.GetObjectList(listField);
                if (objectItems.Count == 0) continue;

                obj.Params.RemoveAll(p => p.Name == listField);

                var listParam = new HkParam
                {
                    Name = listField,
                    Children = new List<HkObject>(),
                    NumElements = objectItems.Count.ToString()
                };

                foreach (var item in objectItems)
                {
                    var child = new HkObject { Params = new List<HkParam>() };

                    foreach (var (k, v) in item)
                    {
                        // "transition" and "condition" are single-name references → resolved in Pass 2
                        child.Params.Add(new HkParam { Name = k, Value = v });
                    }

                    // Wrap triggerInterval / initiateInterval defaults if missing
                    // (RefreshLookups expects these to exist on transition children)
                    if (listField == "transitions")
                    {
                        if (!child.Params.Any(p => p.Name == "triggerInterval"))
                            child.Params.Add(new HkParam
                            {
                                Name = "triggerInterval",
                                Children = new List<HkObject>
                    {
                        new HkObject { Params = new List<HkParam>
                        {
                            new HkParam { Name = "enterEventId", Value = "-1" },
                            new HkParam { Name = "exitEventId",  Value = "-1" },
                            new HkParam { Name = "enterTime",    Value = "0.000000" },
                            new HkParam { Name = "exitTime",     Value = "0.000000" }
                        }}
                    }
                            });

                        if (!child.Params.Any(p => p.Name == "initiateInterval"))
                            child.Params.Add(new HkParam
                            {
                                Name = "initiateInterval",
                                Children = new List<HkObject>
                    {
                        new HkObject { Params = new List<HkParam>
                        {
                            new HkParam { Name = "enterEventId", Value = "-1" },
                            new HkParam { Name = "exitEventId",  Value = "-1" },
                            new HkParam { Name = "enterTime",    Value = "0.000000" },
                            new HkParam { Name = "exitTime",     Value = "0.000000" }
                        }}
                    }
                            });

                        // Default missing optional fields
                        if (!child.Params.Any(p => p.Name == "condition"))
                            child.Params.Add(new HkParam { Name = "condition", Value = "null" });
                        if (!child.Params.Any(p => p.Name == "fromNestedStateId"))
                            child.Params.Add(new HkParam { Name = "fromNestedStateId", Value = "0" });
                        if (!child.Params.Any(p => p.Name == "toNestedStateId"))
                            child.Params.Add(new HkParam { Name = "toNestedStateId", Value = "0" });
                        if (!child.Params.Any(p => p.Name == "priority"))
                            child.Params.Add(new HkParam { Name = "priority", Value = "0" });
                    }

                    listParam.Children.Add(child);
                }

                obj.Params.Add(listParam);
            }


            // Trigger arrays
            var triggers = doc.GetObjectList("triggers");
            if (triggers.Count > 0)
            {
                var triggersParam = new HkParam
                {
                    Name = "triggers",
                    Children = new List<HkObject>(),
                    NumElements = triggers.Count.ToString()
                };

                foreach (var t in triggers)
                {
                    var child = new HkObject { Params = new List<HkParam>() };
                    foreach (var (k, v) in t)
                        if (k != "event")
                            child.Params.Add(new HkParam { Name = k, Value = v });

                    if (t.TryGetValue("event", out var evName))
                    {
                        child.Params.Add(new HkParam
                        {
                            Name = "event",
                            Children = new List<HkObject>
                    {
                        new HkObject { Params = new List<HkParam>
                        {
                            new HkParam { Name = "id",      Value = evName },
                            new HkParam { Name = "payload", Value = "null" }
                        }}
                    }
                        });
                    }
                    triggersParam.Children.Add(child);
                }

                obj.Params.RemoveAll(p => p.Name == "triggers");
                obj.Params.Add(triggersParam);
            }

            // Expression arrays (hkbExpressionDataArray files like BowZoomStart_EEM)
            var expressions = doc.GetObjectList("expressionsData");
            if (expressions.Count > 0)
            {
                var expParam = new HkParam
                {
                    Name = "expressionsData",
                    Children = new List<HkObject>(),
                    NumElements = expressions.Count.ToString()
                };
                foreach (var ex in expressions)
                {
                    var child = new HkObject { Params = new List<HkParam>() };
                    foreach (var (k, v) in ex)
                        child.Params.Add(new HkParam { Name = k, Value = v });
                    expParam.Children.Add(child);
                }
                obj.Params.RemoveAll(p => p.Name == "expressionsData");
                obj.Params.Add(expParam);
            }

            RegisterObject(obj, objectName);

            // Also register under filename so e.g. "data: graphdata" resolves
            // even when the object has a different internal name param
            if (objectName != fileName && !_nameToObject.ContainsKey(fileName))
                _nameToObject[fileName] = obj;
        }


        // ── Pass 2: Name → ID resolution ─────────────────────────────────────────

        private void ResolveAllReferences()
        {
            foreach (var obj in _allObjects)
                ResolveParams(obj.Params);
        }
        private void ResolveVariableBindings()
        {
            // Build name → index lookup from the string data we loaded
            var strData = _allObjects.FirstOrDefault(
                o => o.ClassName == "hkbBehaviorGraphStringData");
            if (strData == null) return;

            var namesParam = strData.Params.FirstOrDefault(p => p.Name == "variableNames");
            if (namesParam?.Strings == null) return;

            var nameToIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < namesParam.Strings.Count; i++)
                nameToIndex[namesParam.Strings[i]] = i.ToString();

            // Walk all binding children and convert variable: name → variableIndex: N
            foreach (var obj in _allObjects)
            {
                var bindingsParam = obj.Params.FirstOrDefault(p => p.Name == "bindings");
                if (bindingsParam?.Children == null) continue;

                foreach (var binding in bindingsParam.Children)
                {
                    var varNameParam = binding.Params.FirstOrDefault(p => p.Name == "variable");
                    if (varNameParam == null) continue;

                    var varName = varNameParam.Value;
                    binding.Params.Remove(varNameParam);

                    if (nameToIndex.TryGetValue(varName, out var idx))
                        binding.Params.Add(new HkParam { Name = "variableIndex", Value = idx });
                    else
                        binding.Params.Add(new HkParam { Name = "variableIndex", Value = "-1" });

                    // Ensure bindingType exists
                    if (!binding.Params.Any(p => p.Name == "bindingType"))
                        binding.Params.Add(new HkParam
                        {
                            Name = "bindingType",
                            Value = "BINDING_TYPE_VARIABLE"
                        });
                }
            }
        }

        private void WireInlineBindings()
        {
            // Some YAML objects have inline bindings: lists instead of a variableBindingSet: ref.
            // For each object that has a bindings param but no variableBindingSet param,
            // create a hkbVariableBindingSet and wire it up.
            var objectsWithInlineBindings = _allObjects
                .Where(o => o.Params.Any(p => p.Name == "bindings" && p.Children?.Count > 0)
                         && !o.Params.Any(p => p.Name == "variableBindingSet"
                                            && !string.IsNullOrEmpty(p.Value)
                                            && p.Value != "null"))
                .ToList();

            foreach (var obj in objectsWithInlineBindings)
            {
                var bindingsParam = obj.Params.FirstOrDefault(p => p.Name == "bindings");
                if (bindingsParam == null) continue;

                // Create a hkbVariableBindingSet to hold these bindings
                var bindingSet = new HkObject
                {
                    Id = AllocId(),
                    ClassName = "hkbVariableBindingSet",
                    Params = new List<HkParam>
            {
                new HkParam
                {
                    Name = "bindings",
                    Children = bindingsParam.Children,
                    NumElements = bindingsParam.Children.Count.ToString()
                },
                new HkParam { Name = "indexOfBindingToEnable", Value = "-1" }
            }
                };
                _allObjects.Add(bindingSet);

                // Replace the inline bindings param with a variableBindingSet reference
                obj.Params.Remove(bindingsParam);
                obj.Params.Add(new HkParam
                {
                    Name = "variableBindingSet",
                    Value = bindingSet.Id
                });
            }
        }
        private void ResolveTransitionFields()
        {
            // Build event name → index from hkbBehaviorGraphStringData
            var eventNameToIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var strData = _allObjects.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            if (strData != null)
            {
                var evParam = strData.Params.FirstOrDefault(p => p.Name == "eventNames");
                if (evParam?.Strings != null)
                    for (int i = 0; i < evParam.Strings.Count; i++)
                        if (!string.IsNullOrEmpty(evParam.Strings[i]))
                            eventNameToIndex[evParam.Strings[i]] = i.ToString();
            }

            // Build state name → stateId from all hkbStateMachineStateInfo objects
            var stateNameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in _allObjects.Where(o => o.ClassName == "hkbStateMachineStateInfo"))
            {
                var sname = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value;
                var sid = obj.Params.FirstOrDefault(p => p.Name == "stateId")?.Value;
                if (!string.IsNullOrEmpty(sname) && !string.IsNullOrEmpty(sid)
                    && !stateNameToId.ContainsKey(sname))
                    stateNameToId[sname] = sid;
            }

            // Walk every object's transitions param and convert names → ids
            foreach (var obj in _allObjects)
            {
                var transParam = obj.Params.FirstOrDefault(p => p.Name == "transitions");
                if (transParam?.Children == null || transParam.Children.Count == 0) continue;

                foreach (var tr in transParam.Children)
                {
                    // event: EventName → eventId: N
                    var eventNameParam = tr.Params.FirstOrDefault(p => p.Name == "event");
                    if (eventNameParam != null)
                    {
                        var evName = eventNameParam.Value ?? "";
                        tr.Params.Remove(eventNameParam);
                        eventNameToIndex.TryGetValue(evName, out var evIdx);
                        tr.Params.Add(new HkParam { Name = "eventId", Value = evIdx ?? "-1" });
                    }

                    // toState: StateName → toStateId: N
                    var toStateNameParam = tr.Params.FirstOrDefault(p => p.Name == "toState");
                    if (toStateNameParam != null)
                    {
                        var sname = toStateNameParam.Value ?? "";
                        tr.Params.Remove(toStateNameParam);
                        stateNameToId.TryGetValue(sname, out var resolvedId);
                        tr.Params.Add(new HkParam { Name = "toStateId", Value = resolvedId ?? "0" });
                    }

                    // Ensure required fields exist with defaults
                    if (!tr.Params.Any(p => p.Name == "triggerInterval"))
                        tr.Params.Add(new HkParam
                        {
                            Name = "triggerInterval",
                            Children = new List<HkObject>
                    {
                        new HkObject { Params = new List<HkParam>
                        {
                            new HkParam { Name = "enterEventId", Value = "-1" },
                            new HkParam { Name = "exitEventId",  Value = "-1" },
                            new HkParam { Name = "enterTime",    Value = "0.000000" },
                            new HkParam { Name = "exitTime",     Value = "0.000000" }
                        }}
                    }
                        });

                    if (!tr.Params.Any(p => p.Name == "initiateInterval"))
                        tr.Params.Add(new HkParam
                        {
                            Name = "initiateInterval",
                            Children = new List<HkObject>
                    {
                        new HkObject { Params = new List<HkParam>
                        {
                            new HkParam { Name = "enterEventId", Value = "-1" },
                            new HkParam { Name = "exitEventId",  Value = "-1" },
                            new HkParam { Name = "enterTime",    Value = "0.000000" },
                            new HkParam { Name = "exitTime",     Value = "0.000000" }
                        }}
                    }
                        });

                    if (!tr.Params.Any(p => p.Name == "condition"))
                        tr.Params.Add(new HkParam { Name = "condition", Value = "null" });
                    if (!tr.Params.Any(p => p.Name == "fromNestedStateId"))
                        tr.Params.Add(new HkParam { Name = "fromNestedStateId", Value = "0" });
                    if (!tr.Params.Any(p => p.Name == "toNestedStateId"))
                        tr.Params.Add(new HkParam { Name = "toNestedStateId", Value = "0" });
                    if (!tr.Params.Any(p => p.Name == "priority"))
                        tr.Params.Add(new HkParam { Name = "priority", Value = "0" });
                }
            }
        }

        private void WireStateTransitions()
        {
            var stateObjects = _allObjects
                .Where(o => o.ClassName == "hkbStateMachineStateInfo")
                .ToList();

            foreach (var stateObj in stateObjects)
            {
                var transParam = stateObj.Params.FirstOrDefault(p => p.Name == "transitions");

                // No transitions param at all → add null ref
                if (transParam == null)
                {
                    stateObj.Params.Add(new HkParam { Name = "transitions", Value = "null" });
                    continue;
                }

                // Already a scalar ref (was resolved from a name to an ID) → leave it
                if (transParam.Children == null || transParam.Children.Count == 0)
                {
                    // If value is empty or still a name string, set null
                    if (string.IsNullOrEmpty(transParam.Value) || !transParam.Value.StartsWith("#"))
                        transParam.Value = "null";
                    continue;
                }

                // Has inline children → wrap in a TransitionInfoArray
                var arrayObj = new HkObject
                {
                    Id = AllocId(),
                    ClassName = "hkbStateMachineTransitionInfoArray",
                    Signature = "0xe397b11e",
                    Params = new List<HkParam>
            {
                new HkParam
                {
                    Name = "transitions",
                    Children = transParam.Children,
                    NumElements = transParam.Children.Count.ToString()
                }
            }
                };
                _allObjects.Add(arrayObj);

                // Replace inline param with ID reference
                stateObj.Params.Remove(transParam);
                stateObj.Params.Add(new HkParam
                {
                    Name = "transitions",
                    Value = arrayObj.Id
                });
            }
        }

        private void ResolveParams(List<HkParam> paramList)
        {
            if (paramList == null) return;
            foreach (var param in paramList)
            {
                if (SingleRefFields.Contains(param.Name))
                    param.Value = ResolveNameToId(param.Value);
                else if (MultiRefFields.Contains(param.Name) && !string.IsNullOrEmpty(param.Value))
                {
                    var parts = param.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    param.Value = string.Join(" ", parts.Select(ResolveNameToId));
                }

                if (param.Children != null)
                    foreach (var child in param.Children)
                        ResolveParams(child.Params);
            }
        }

        private string ResolveNameToId(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "null") return value;
            if (value.StartsWith("#")) return value;
            if (_nameToObject.TryGetValue(value, out var obj)) return obj.Id;
            return value;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void RegisterObject(HkObject obj, string name)
        {
            _allObjects.Add(obj);
            if (!string.IsNullOrEmpty(name) && !_nameToObject.ContainsKey(name))
                _nameToObject[name] = obj;

            var nameParam = obj.Params?.FirstOrDefault(p => p.Name == "name");
            if (nameParam != null && !string.IsNullOrEmpty(nameParam.Value)
                && !_nameToObject.ContainsKey(nameParam.Value))
                _nameToObject[nameParam.Value] = obj;
        }

        private string AllocId() => $"#{_nextId++:D4}";

        private static string EncodeFloat(string value)
        {
            if (float.TryParse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float f))
                return BitConverter.SingleToInt32Bits(f).ToString();
            return "0";
        }

        private static string GuessClassFromContent(YamlDocument doc)
        {
            if (doc.HasScalar("animationName")) return "hkbClipGenerator";
            if (doc.HasScalar("blendParameter")) return "hkbBlenderGenerator";
            if (doc.HasScalar("startStateId")) return "hkbStateMachine";
            if (doc.HasScalar("stateId")) return "hkbStateMachineStateInfo";
            if (doc.HasScalar("behaviorName")) return "hkbBehaviorReferenceGenerator";
            if (doc.HasScalar("duration")) return "hkbBlendingTransitionEffect";
            if (doc.HasScalar("pDefaultGenerator")) return "BSiStateTaggingGenerator";
            if (doc.HasScalar("iStateToSetAs")) return "BSiStateTaggingGenerator";
            if (doc.HasScalar("selfTransitionMode")) return "hkbBlendingTransitionEffect";
            return null;
        }
    }

    // ── Minimal YAML document model ───────────────────────────────────────────────
    // Handles the subset of YAML Pandora uses without requiring YamlDotNet.
    //
    // Supported:
    //   key: value                  → scalar
    //   list_field:                 → list section
    //     - simple_string           → string list item
    //     - name: foo               → object list item
    //       type: bar
    //   section:                    → named section
    //     key: value

    internal class YamlDocument
    {
        // Top-level key: value pairs (not inside a list)
        public Dictionary<string, string> Scalars { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Named sections (section_name: followed by indented keys)
        private readonly Dictionary<string, YamlDocument> _sections =
            new(StringComparer.OrdinalIgnoreCase);

        // Named string lists  (field: followed by - string items)
        private readonly Dictionary<string, List<string>> _stringLists =
            new(StringComparer.OrdinalIgnoreCase);

        // Named object lists  (field: followed by - key: val / key: val items)
        private readonly Dictionary<string, List<Dictionary<string, string>>> _objectLists =
            new(StringComparer.OrdinalIgnoreCase);

        // ── Accessors ─────────────────────────────────────────────────────────────

        public string GetScalar(string key) =>
            Scalars.TryGetValue(key, out var v) ? v : null;

        public bool HasScalar(string key) => Scalars.ContainsKey(key);

        public YamlDocument GetSection(string name) =>
            _sections.TryGetValue(name, out var s) ? s : null;

        public List<string> GetStringList(string name) =>
            _stringLists.TryGetValue(name, out var l) ? l : new List<string>();

        public List<Dictionary<string, string>> GetObjectList(string name) =>
            _objectLists.TryGetValue(name, out var l)
                ? l
                : new List<Dictionary<string, string>>();

        // ── Parser ────────────────────────────────────────────────────────────────

        public static YamlDocument Parse(string yaml)
        {
            var doc = new YamlDocument();
            var lines = yaml.Split('\n');

            string currentSection = null;        // e.g. "behavior"
            string currentListField = null;      // e.g. "variables" or "eventNames"
            bool currentListIsObjects = false;   // true if items have sub-keys
            Dictionary<string, string> currentItem = null;

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i].TrimEnd();
                if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("#"))
                    continue;

                var indent = raw.Length - raw.TrimStart().Length;
                var trimmed = raw.TrimStart();

                // ── List item continuation ────────────────────────────────────────
                if (currentListField != null && indent >= 2)
                {
                    if (trimmed.StartsWith("- "))
                    {
                        // Commit previous item if any
                        if (currentItem != null)
                            FlushItem(doc, currentListField, currentItem,
                                ref currentListIsObjects);

                        var rest = trimmed[2..].Trim();
                        if (rest.Contains(':'))
                        {
                            // Object item: - key: value
                            currentItem = new Dictionary<string, string>(
                                StringComparer.OrdinalIgnoreCase);
                            ParseKv(rest, currentItem);
                            currentListIsObjects = true;
                        }
                        else
                        {
                            // Simple string item: - somestring
                            currentItem = null;
                            AddStringItem(doc, currentListField, StripComment(rest).Trim('"', '\''));
                            currentListIsObjects = false;
                        }
                        continue;
                    }

                    if (currentItem != null && indent >= 4)
                    {
                        // Sub-key of current object item
                        ParseKv(trimmed, currentItem);
                        continue;
                    }

                    // Dedented — close the list
                    if (currentItem != null)
                        FlushItem(doc, currentListField, currentItem,
                            ref currentListIsObjects);
                    currentItem = null;
                    currentListField = null;
                }

                // ── Section continuation ──────────────────────────────────────────
                if (currentSection != null && indent > 0)
                {
                    if (!doc._sections.TryGetValue(currentSection, out var sec))
                    {
                        sec = new YamlDocument();
                        doc._sections[currentSection] = sec;
                    }
                    var colonIdx2 = trimmed.IndexOf(':');
                    if (colonIdx2 > 0)
                    {
                        var k2 = trimmed[..colonIdx2].Trim();
                        var v2 = StripComment(trimmed[(colonIdx2 + 1)..]).Trim().Trim('"', '\'');
                        if (!string.IsNullOrEmpty(v2))
                            sec.Scalars[k2] = v2;
                    }
                    continue;
                }

                // ── Top-level key ─────────────────────────────────────────────────
                if (indent == 0)
                {
                    currentSection = null;
                    var colonIdx = trimmed.IndexOf(':');
                    if (colonIdx <= 0) continue;

                    var key = trimmed[..colonIdx].Trim();
                    var valPart = StripComment(trimmed[(colonIdx + 1)..]).Trim();

                    if (string.IsNullOrEmpty(valPart))
                    {
                        // Could be a section header or a list field
                        // Peek at next non-empty line to decide
                        var peek = PeekNextNonEmpty(lines, i + 1);
                        if (peek != null && peek.TrimStart().StartsWith("- "))
                        {
                            currentListField = key;
                            currentItem = null;
                            currentListIsObjects = false;
                        }
                        else
                        {
                            currentSection = key;
                            if (!doc._sections.ContainsKey(key))
                                doc._sections[key] = new YamlDocument();
                        }
                    }
                    else
                    {
                        doc.Scalars[key] = valPart.Trim('"', '\'');
                    }
                }
            }

            // Commit any trailing item
            if (currentItem != null && currentListField != null)
                FlushItem(doc, currentListField, currentItem, ref currentListIsObjects);

            return doc;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static void FlushItem(YamlDocument doc, string field,
            Dictionary<string, string> item, ref bool isObjects)
        {
            if (isObjects)
            {
                if (!doc._objectLists.TryGetValue(field, out var ol))
                    doc._objectLists[field] = ol = new List<Dictionary<string, string>>();
                ol.Add(new Dictionary<string, string>(item, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                if (item.TryGetValue("name", out var n))
                    AddStringItem(doc, field, n);
            }
        }

        private static void AddStringItem(YamlDocument doc, string field, string value)
        {
            if (!doc._stringLists.TryGetValue(field, out var sl))
                doc._stringLists[field] = sl = new List<string>();
            sl.Add(value);
        }

        private static void ParseKv(string line, Dictionary<string, string> target)
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) return;
            var k = line[..idx].Trim();
            var v = StripComment(line[(idx + 1)..]).Trim().Trim('"', '\'');
            target[k] = v;
        }

        private static string StripComment(string s)
        {
            var idx = s.IndexOf(" #", StringComparison.Ordinal);
            return idx > 0 ? s[..idx] : s;
        }

        private static string PeekNextNonEmpty(string[] lines, int from)
        {
            for (int i = from; i < lines.Length; i++)
                if (!string.IsNullOrWhiteSpace(lines[i])) return lines[i];
            return null;
        }
    }
}