using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SageHavokEditor.Core.Services;
using SageHavokEditor.Core.Skeletons;
using SageHavokEditor.Models;

namespace SageHavokEditor.Core
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
        // Only a fallback now. Which params are references is a fact about the
        // class, and HavokTypeCatalog knows it for every class HKX2 ships; these two
        // lists are what a class outside that set falls back on. Kept because a
        // hand-written or modded class does turn up, and dropped from the decision
        // wherever the metadata can answer — the lists were missing pClipGenerator,
        // pOnActivateModifier and pOnDeactivateModifier, so a paired killmove's clip
        // and a BSModifyOnceModifier's modifiers were left as names and never
        // resolved: 80 slots in vanilla 0_master, 28 in dragonbehavior.
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
        // Every object a name can mean, not the first one seen. Havok node names
        // are not unique, and this source tree makes that worse by keying files on
        // them: mt_behavior has 656 names used by two files in different folders,
        // AltarIdle_Enter being both a state and the clip it plays. Which one a
        // reference means is decided by the slot it sits in — see ResolveNameToId.
        private readonly Dictionary<string, List<HkObject>> _byName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<HkObject> _allObjects = new();

        // Name lists waiting to be resolved, kept as a list rather than joined into
        // the param's text. A Havok object name may contain a space — 141 of them
        // in mt_behavior, "Paired OffsetBoundStandingCut" among them — and a
        // space-joined name list can't be taken apart again: 110 of that unit's
        // 1234 list entries came back as two tokens that resolved to two wrong
        // objects, or to none. Ids never contain a space, so the joined form is
        // safe once the names are gone.
        private readonly Dictionary<HkParam, List<string>> _pendingRefLists = new();

        // Objects loaded from data/, with the filename they came from. That name is
        // the only thing linking them to their owner — see AttachDataSidecars.
        private readonly List<(string Stem, HkObject Object)> _dataSidecars = new();

        // behavior.yaml's packfile: section. Havok's own header, carried through
        // rather than assumed: a unit from another Havok version says so here, and
        // the SkyrimSE defaults would silently mislabel it.
        private string _classVersion = "";
        private string _contentsVersion = "";

        // ── Public entry point ────────────────────────────────────────────────────

        /// <summary>Bone names in animation-skeleton order, for boneWeights. See Import.</summary>
        private IReadOnlyList<string> _skeletonBones = Array.Empty<string>();

        /// <summary>
        /// How many bone-weight maps were read but not built, for want of a
        /// skeleton. Nonzero means the graph is structurally complete and
        /// behaviourally not: a blender child with no weights blends the whole
        /// body where it should blend an arm.
        /// </summary>
        public int UnbuiltBoneWeights { get; private set; }

        public string Import(string folderPath, HavokManager manager)
            => Import(folderPath, manager, null);

        /// <summary>
        /// <paramref name="skeletonBones"/> is the character project's animation
        /// skeleton, in bone order — see HkxSkeletonReader. Bone weights are written
        /// by name in the source and indexed by position in the file, so without it
        /// they cannot be built at all; the import still produces a correct graph
        /// and reports the count through <see cref="UnbuiltBoneWeights"/>.
        /// </summary>
        public string Import(string folderPath, HavokManager manager,
            IReadOnlyList<string>? skeletonBones)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

            _nextId = 1;
            _byName.Clear();
            _pendingRefLists.Clear();
            _skeletonBones = skeletonBones ?? Array.Empty<string>();
            UnbuiltBoneWeights = 0;
            _synthesisedByClass.Clear();
            SynthesisedFromClassName = 0;
            _dataSidecars.Clear();
            _allObjects.Clear();

            _classVersion = "";
            _contentsVersion = "";

            // Shape first, names last. Every pass below rearranges objects into the
            // shape Havok declares, and only then are the name references resolved —
            // which matters because resolution is class-aware, and an inline child's
            // class is only knowable once it sits under the param it belongs to.
            // Resolving first was quietly wrong: a transition's `transition:` was
            // read while the transitions were still inline on a state, where the
            // declared class is hkbStateMachineTransitionInfoArray and says nothing
            // about them, so a name two objects shared could resolve to a blender
            // and the conversion died on an InvalidCastException instead of naming
            // the bad reference.
            string behaviorName = LoadAllYaml(folderPath);
            AttachDataSidecars();         // data/<owner>_*.yaml → the owner's null member
            NormalizeEmptyArrays();       // boneIndices: [] → numelements="0"
            ResolveTransitionFields();    // event: Name → eventId: N, toState: Name → toStateId: N
            WireStateTransitions();       // wrap inline transition lists → TransitionInfoArray objects
            WireExpressionConditions();   // condition: "x == 1" → hkbExpressionCondition
            // Both of the two above have to come after the wrapping: until the array
            // object exists, a state machine still has a `transitions` param, which
            // is not one of its members — so the class walk carries the wrong class
            // down and misses the conditions, and the "one pointer array" fallback
            // takes the list for the machine's `states`.
            HoistPointerArrayChildren();  // inline children: → objects the owner points at
            ResolveVariableBindings();    // variable: Name → variableIndex: N
            WireInlineBindings();         // inline bindings: → hkbVariableBindingSet objects
            WireClipTriggers();           // inline triggers: → hkbClipTriggerArray objects
            WireGraphData();              // graph data → its string data and value set
            ResolveNameKeyedIndices();    // syncVariable: Name → syncVariableIndex: N, and friends
            ResolveAllReferences();       // object name refs: generator: Name → #ID
            var rootId = BuildRootContainer();   // the scaffold an .hkx is read through

            // Through BuildGraph rather than by filling ObjectMap directly: that is
            // what carries the header and the root over, resolves single #refs into
            // the Children cache the way an XML load does, and attaches the declared
            // types the property editor reads. An import used to get none of it.
            manager.BuildGraph(new HkPackfile
            {
                ClassVersion = string.IsNullOrEmpty(_classVersion)
                    ? HkPackfile.SkyrimClassVersion : _classVersion,
                ContentsVersion = string.IsNullOrEmpty(_contentsVersion)
                    ? HkPackfile.SkyrimContentsVersion : _contentsVersion,
                TopLevelObject = rootId,
                Sections = new List<HkSection>
                {
                    new HkSection { Name = "__data__", Objects = _allObjects.ToList() }
                }
            });

            return behaviorName;
        }

        // ── Pointer-array children ────────────────────────────────────────────────
        // A blender's children are written inline on the blender, where Havok's
        // member is an array of *pointers* to hkbBlenderGeneratorChild objects.
        // Same failure as the clip triggers: it saves as XML and then HKX2 reads
        // the element text as a reference symbol — '#01911.0000001.000000', a ref
        // and two weights run together — which is where every unit's conversion was
        // ending once the earlier fixes were in.
        //
        // The member is found by shape, not by name, because the source doesn't
        // always use Havok's name: BSBoneSwitchGenerator's member is ChildrenA and
        // the source writes children:. Each of these classes has exactly one array
        // of pointers, so "the class's one pointer array" identifies it without a
        // table of aliases.

        private void HoistPointerArrayChildren()
        {
            foreach (var owner in _allObjects.ToList())
            {
                if (string.IsNullOrEmpty(owner.ClassName)) continue;

                foreach (var param in owner.Params.ToList())
                {
                    if (param.Children.Count == 0) continue;
                    if (param.Children.Any(c => !string.IsNullOrEmpty(c.Id))) continue;

                    var target = PointerArrayTarget(owner.ClassName, param.Name);
                    if (target == null) continue;

                    var ids = param.Children
                        .Select(child => BuildElement(child, target.Value.ElementClass).Id)
                        .ToList();

                    owner.Params.Remove(param);
                    owner.Params.Add(new HkParam
                    {
                        Name = target.Value.Member,
                        Value = string.Join(" ", ids),
                        NumElements = ids.Count.ToString()
                    });
                }
            }
        }

        /// <summary>
        /// Where an inline list on <paramref name="className"/> belongs, when it
        /// belongs in an array of pointers. The declared member wins if the source
        /// used its real name; otherwise the class's single pointer array is it.
        /// Two of them and there is nothing to choose between, so the list is left
        /// where it is rather than put somewhere plausible.
        /// </summary>
        private static (string Member, string ElementClass)? PointerArrayTarget(
            string className, string paramName)
        {
            var declared = HavokTypeCatalog.Lookup(className, paramName);
            if (declared is { ArrayKind: HkArrayKind.Pointer, ElementClassName: { } element })
                return (paramName, element);
            if (declared != null) return null;   // a real member of some other shape
            if (BelongsToAFlattenedWrapper(className, paramName)) return null;

            var pointerArrays = HavokTypeCatalog.ParamsOf(className)
                .Where(kv => kv.Value.ArrayKind == HkArrayKind.Pointer
                             && kv.Value.ElementClassName != null)
                .ToList();
            return pointerArrays.Count == 1
                ? (pointerArrays[0].Key, pointerArrays[0].Value.ElementClassName!)
                : null;
        }

        /// <summary>
        /// Is this inline list the contents of a wrapper object the source flattened
        /// away, rather than the class's own array? A blender writes
        /// <c>bindings:</c> on itself where Havok keeps them one level down, in the
        /// hkbVariableBindingSet its variableBindingSet points at — and
        /// hkbBlenderGenerator's one array of pointers is <c>children</c>, so without
        /// this the bindings were hoisted in as a blender child: 40 empty
        /// hkbBlenderGeneratorChild objects in vanilla dragonbehavior, 137 in
        /// 0_master, each of them a node that plays nothing.
        ///
        /// The test is the same shape as the flattening: some single-pointer member
        /// of this class targets a class that declares an inline-struct array of
        /// exactly this name. Those lists have their own passes; this only has to
        /// keep its hands off them.
        /// </summary>
        private static bool BelongsToAFlattenedWrapper(string className, string paramName)
        {
            foreach (var member in HavokTypeCatalog.ParamsOf(className).Values)
            {
                if (member.ArrayKind != HkArrayKind.None || member.ElementClassName == null)
                    continue;
                var inner = HavokTypeCatalog.ParamsOf(member.ElementClassName);
                if (inner.TryGetValue(paramName, out var innerMember)
                    && innerMember.ArrayKind == HkArrayKind.InlineStruct)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// One element of a pointer array, as its own object. Built from HKX2's
        /// default so the signature and every member Havok expects are present, then
        /// overwritten with what the source gave — a member the source omits keeps
        /// the vanilla default rather than disappearing.
        /// </summary>
        private HkObject BuildElement(HkObject source, string elementClass)
        {
            var element = ModifierCatalog.CreateDefault(elementClass)
                          ?? new HkObject { ClassName = elementClass, Params = new List<HkParam>() };
            element.Id = AllocId();
            element.ClassName = elementClass;
            _allObjects.Add(element);
            AddName(source.Params.FirstOrDefault(p => p.Name == "name")?.Value, element);

            var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            // Written but empty is not the same as absent. Eight children in vanilla
            // 0_master say `boneWeights: named:` with no bones under it, which is a
            // weight array of zeros — the child contributes nothing per bone — and
            // is a different thing from a child with no boneWeights at all, which is
            // a null pointer.
            var sawBoneWeights = false;
            var bindings = new SortedDictionary<string, List<HkParam>>(StringComparer.Ordinal);

            foreach (var p in source.Params)
            {
                // boneWeights.named.<bone> — the dotted path the parser keeps for a
                // nested mapping. The bare boneWeights / boneWeights.named markers
                // carry no value and are skipped with it.
                const string prefix = "boneWeights.named.";
                if (p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (float.TryParse(p.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var w))
                        weights[p.Name.Substring(prefix.Length)] = w;
                    continue;
                }
                if (p.Name.StartsWith("boneWeights", StringComparison.OrdinalIgnoreCase))
                {
                    sawBoneWeights = true;
                    continue;
                }
                if (p.Name.StartsWith("bindings.", StringComparison.OrdinalIgnoreCase))
                {
                    // bindings.<n>.<key> — put back together as the inline list the
                    // later passes expect, so a binding on a blender child reaches
                    // hkbVariableBindingSet like any other.
                    var rest = p.Name.Substring("bindings.".Length);
                    var dot = rest.IndexOf('.');
                    if (dot <= 0) continue;
                    var index = rest.Substring(0, dot);
                    var key = rest.Substring(dot + 1);
                    if (key.Contains('.')) continue;
                    if (!bindings.TryGetValue(index, out var fields))
                        bindings[index] = fields = new List<HkParam>();
                    fields.Add(new HkParam { Name = key, Value = p.Value });
                    continue;
                }
                if (p.Name.Contains('.')) continue;   // some other nesting we don't model

                var existing = element.Params.FirstOrDefault(x => x.Name == p.Name);
                if (existing != null) existing.Value = p.Value;
                else if (HavokTypeCatalog.Lookup(elementClass, p.Name) != null)
                    element.Params.Add(new HkParam { Name = p.Name, Value = p.Value });
            }

            if (sawBoneWeights) AttachBoneWeights(element, elementClass, weights);

            if (bindings.Count > 0)
            {
                element.Params.RemoveAll(x => x.Name == "bindings");
                element.Params.Add(new HkParam
                {
                    Name = "bindings",
                    NumElements = bindings.Count.ToString(),
                    Children = bindings.Values
                        .Select(fields => new HkObject { Params = fields })
                        .ToList()
                });
            }

            return element;
        }

        /// <summary>
        /// Builds the hkbBoneWeightArray for one element and points its bone-weight
        /// member at it. The member is found by class rather than by name — it is
        /// boneWeights on a blender child and spBoneWeight on a bone-switch child,
        /// while the source calls both boneWeights.
        ///
        /// Needs the skeleton: the source names its bones and the file indexes them
        /// by position, so there is nothing to write without the bone order. Without
        /// it the map is counted and dropped, which is a blend that covers the whole
        /// body instead of an arm — wrong, but visibly wrong, where a guessed
        /// ordering would be wrong invisibly.
        /// </summary>
        private void AttachBoneWeights(HkObject element, string elementClass,
            Dictionary<string, float> weights)
        {
            if (_skeletonBones.Count == 0) { UnbuiltBoneWeights++; return; }

            var slot = HavokTypeCatalog.ParamsOf(elementClass)
                .Where(kv => kv.Value.ElementClassName == "hkbBoneWeightArray")
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (slot == null) { UnbuiltBoneWeights++; return; }

            var floats = new float[_skeletonBones.Count];
            for (int i = 0; i < _skeletonBones.Count; i++)
                if (weights.TryGetValue(_skeletonBones[i], out var w))
                    floats[i] = w;

            var array = new HkObject
            {
                Id = AllocId(),
                ClassName = "hkbBoneWeightArray",
                Signature = "0xcd902b77",
                Params = new List<HkParam>
                {
                    new HkParam
                    {
                        Name = "boneWeights",
                        NumElements = floats.Length.ToString(),
                        Value = string.Join(" ", floats.Select(
                            f => f.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)))
                    }
                }
            };
            _allObjects.Add(array);

            var param = element.Params.FirstOrDefault(x => x.Name == slot);
            if (param != null) param.Value = array.Id;
            else element.Params.Add(new HkParam { Name = slot, Value = array.Id });
        }

        // ── Transition conditions ─────────────────────────────────────────────────
        // A transition's condition is written as the expression itself —
        // condition: "isInFurniture == 0" — where Havok wants a pointer to an
        // hkbCondition object holding that text. Left as text it is the same
        // failure as an inline array in a pointer slot: HKX2 reads it as a
        // reference symbol and stops with "Reference symbol 'isInFurniture == 0'
        // not found", which is where mt_behavior's conversion was ending.
        //
        // One object per site rather than one per distinct expression. Sharing
        // would save 12 objects across the whole vanilla corpus and cost the thing
        // that matters more: editing one transition's condition would silently
        // change every transition that happened to read the same.

        private void WireExpressionConditions()
        {
            foreach (var obj in _allObjects.ToList())
                WireConditionsIn(obj.Params, obj.ClassName);
        }

        private void WireConditionsIn(List<HkParam> paramList, string? ownerClass)
        {
            if (paramList == null) return;
            foreach (var param in paramList)
            {
                var info = HavokTypeCatalog.Lookup(ownerClass ?? "", param.Name);

                if (info is { ArrayKind: HkArrayKind.None, ElementClassName: "hkbCondition" }
                    && !string.IsNullOrEmpty(param.Value)
                    && param.Value != "null"
                    && !param.Value.StartsWith("#", StringComparison.Ordinal))
                {
                    var condition = new HkObject
                    {
                        Id = AllocId(),
                        ClassName = "hkbExpressionCondition",
                        Signature = "0x1c3c1045",
                        Params = new List<HkParam>
                        {
                            new HkParam { Name = "expression", Value = param.Value }
                        }
                    };
                    _allObjects.Add(condition);
                    param.Value = condition.Id;
                }

                foreach (var child in param.Children.Where(c => string.IsNullOrEmpty(c.Id)))
                    WireConditionsIn(child.Params,
                        string.IsNullOrEmpty(child.ClassName)
                            ? info?.ElementClassName
                            : child.ClassName);
            }
        }

        // ── Empty array literals ──────────────────────────────────────────────────
        // YAML writes an empty array as []. Left as the text "[]" the param goes out
        // with no numelements at all and the conversion stops on it — "numelemnets
        // is not vaild number", HKX2's own spelling. Seven sites across the vanilla
        // corpus (six boneIndices, one legs), all of them array members, and "[]" is
        // not a value anything else in this format takes.

        private void NormalizeEmptyArrays()
        {
            foreach (var obj in _allObjects)
                foreach (var param in obj.Params)
                    if (param.Value == "[]")
                    {
                        param.Value = "";
                        param.NumElements = "0";
                    }
        }

        // ── data/ sidecars ────────────────────────────────────────────────────────
        // An expression list, a bone-index list and an event-range list live in
        // their own file under data/, and nothing in the source references them:
        // the owner writes the member as `null` and the only link is the filename.
        // Unattached they are unreachable from the root, so an .hkx save drops
        // them and the modifier evaluates nothing — 41 objects in vanilla
        // dragonbehavior, 17 in 0_master.
        //
        // The filename is <owner> followed by a note about what it is, and that
        // note is not the member name: _expressions happens to match
        // hkbEvaluateExpressionModifier.expressions, but _ranges stands for
        // eventRanges and _boneIndex for bones or keyframedBonesList. So the
        // member is not read out of the name at all. The owner is the longest
        // object name that prefixes the stem, and the member is whichever of its
        // members is declared to point at exactly this file's class and is still
        // null — which for every class involved here is precisely one. Where it is
        // more than one, or none, the file is left unattached rather than guessed
        // at: a wrong link here is silent, and an unreferenced object at least
        // shows up in the doctor's pruning report.

        private void AttachDataSidecars()
        {
            foreach (var (stem, sidecar) in _dataSidecars)
            {
                if (string.IsNullOrEmpty(sidecar.ClassName)) continue;

                foreach (var owner in OwnerCandidates(stem, sidecar))
                {
                    var slots = owner.Params
                        .Where(param =>
                        {
                            var info = HavokTypeCatalog.Lookup(owner.ClassName, param.Name);
                            return info != null
                                   && info.ArrayKind == HkArrayKind.None
                                   && info.ElementClassName == sidecar.ClassName
                                   && (string.IsNullOrEmpty(param.Value) || param.Value == "null");
                        })
                        .ToList();

                    if (slots.Count != 1) break;   // ambiguous, or this owner has no room
                    slots[0].Value = sidecar.Id;
                    break;
                }
            }
        }

        /// <summary>
        /// Objects whose name prefixes the file stem at an underscore, longest
        /// first — Foo_Bar_expressions is Foo_Bar's before it is Foo's. The sidecar
        /// itself is skipped: a bone-index file is named after the member it fills
        /// (DriveRagdollRB_bones), so it prefixes its own stem.
        /// </summary>
        private IEnumerable<HkObject> OwnerCandidates(string stem, HkObject sidecar)
        {
            for (int i = stem.Length - 1; i > 0; i--)
            {
                if (stem[i] != '_') continue;
                if (!_byName.TryGetValue(stem.Substring(0, i), out var owners)) continue;
                foreach (var owner in owners)
                    if (!ReferenceEquals(owner, sidecar))
                        yield return owner;
            }
        }

        // ── Name-keyed index fields ───────────────────────────────────────────────
        // The source writes the readable half of a pair: syncVariable where Havok's
        // member is syncVariableIndex, startPlayingEvent where it is
        // startPlayingEventId. Its own writer knows the difference — the same files
        // carry startMatchingEventId: -1 where there is no event to name — so the
        // suffix is dropped exactly when there is a name to put in its place.
        //
        // Rather than a list of the five fields this happens to affect today, the
        // rule is asked of the class: a param the class doesn't declare, whose name
        // plus Id or Index *is* a member the catalog has marked as an index into
        // the event or variable table, is that member written the readable way.
        // HavokTypeCatalog already marks those (HkParamSemantic), for the property
        // editor's name pickers, so nothing new has to be decided here.
        //
        // These are read positionally at runtime. A name left in place is not a
        // rougher version of the right answer: it fails the conversion, and would
        // otherwise be a modifier that never activates.

        private void ResolveNameKeyedIndices()
        {
            var eventIndex = BuildEventIndex();
            var variableIndex = BuildVariableIndex();

            foreach (var obj in _allObjects)
            {
                if (string.IsNullOrEmpty(obj.ClassName)) continue;

                foreach (var param in obj.Params.ToList())
                {
                    if (string.IsNullOrEmpty(param.Value)) continue;
                    if (int.TryParse(param.Value, out _)) continue;   // already an index
                    if (HavokTypeCatalog.Lookup(obj.ClassName, param.Name) != null) continue;

                    foreach (var suffix in new[] { "Id", "Index" })
                    {
                        var member = param.Name + suffix;
                        var info = HavokTypeCatalog.Lookup(obj.ClassName, member);
                        var table = info?.Semantic switch
                        {
                            HkParamSemantic.EventId => eventIndex,
                            HkParamSemantic.VariableIndex => variableIndex,
                            _ => null
                        };
                        if (table == null) continue;

                        // -1 rather than dropping the param: the member exists either
                        // way, and Havok's "no event / no variable" sentinel is what
                        // the source itself writes when it has no name to give.
                        table.TryGetValue(param.Value, out var resolved);
                        obj.Params.Remove(param);
                        obj.Params.Add(new HkParam { Name = member, Value = resolved ?? "-1" });
                        break;
                    }
                }
            }
        }

        /// <summary>Variable name → its index in hkbBehaviorGraphStringData.variableNames.</summary>
        private Dictionary<string, string> BuildVariableIndex()
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var strData = _allObjects.FirstOrDefault(
                o => o.ClassName == "hkbBehaviorGraphStringData");
            var names = strData?.Params.FirstOrDefault(p => p.Name == "variableNames")?.Strings;
            if (names == null) return index;

            for (int i = 0; i < names.Count; i++)
                if (!string.IsNullOrEmpty(names[i]) && !index.ContainsKey(names[i]))
                    index[names[i]] = i.ToString();
            return index;
        }

        // ── The three graph-data objects ──────────────────────────────────────────
        // hkbBehaviorGraphData holds the variable types, and points at the string
        // data that holds every event and variable *name* and at the value set that
        // holds their starting values. The import built all three and linked none of
        // them, so the names — which is what the whole editor is about — hung off
        // nothing: unreachable from the root, dropped by an .hkx save, and a
        // converted file whose events had no names at all. It converted, which is
        // exactly why this went unnoticed.

        private void WireGraphData()
        {
            var graphData = _allObjects.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphData");
            if (graphData == null) return;

            Link("stringData", "hkbBehaviorGraphStringData");
            Link("variableInitialValues", "hkbVariableValueSet");

            void Link(string member, string className)
            {
                var target = _allObjects.FirstOrDefault(o => o.ClassName == className);
                if (target == null) return;

                var param = graphData.Params.FirstOrDefault(p => p.Name == member);
                if (param == null)
                    graphData.Params.Add(new HkParam { Name = member, Value = target.Id });
                else if (string.IsNullOrEmpty(param.Value) || param.Value == "null")
                    param.Value = target.Id;
            }
        }

        // ── Clip triggers ─────────────────────────────────────────────────────────
        // The YAML flattens the wrapper away: a clip's triggers: is the list
        // itself, where Havok's hkbClipGenerator.triggers is a *pointer* to an
        // hkbClipTriggerArray that holds it. Left inline the file still saves as
        // XML — and then HKX2's deserializer reads the element's run-together text
        // as a reference symbol and reports a missing '-0.900000trueJumpFallnull',
        // which is the mash this looked like from the outside.
        //
        // Two things have to be built as well as moved. An event is a name here and
        // a positional index at runtime, the same treatment transitions already get
        // — an unresolved name is not a lesser answer, it is a trigger that fires
        // nothing. And a payload ("HitFrame, payload: Left" — the hand a hit came
        // from) is a pointer to an hkbStringEventPayload, which has to become an
        // object of its own or it is dropped on the .hkx save with the rest of the
        // unreferenced.

        private void WireClipTriggers()
        {
            var eventIndex = BuildEventIndex();

            foreach (var clip in _allObjects
                         .Where(o => o.ClassName == "hkbClipGenerator")
                         .ToList())
            {
                var param = clip.Params.FirstOrDefault(p => p.Name == "triggers");
                if (param?.Children == null || param.Children.Count == 0) continue;
                if (param.Children.Any(c => !string.IsNullOrEmpty(c.Id))) continue;  // already a ref

                var triggers = param.Children
                    .Select(t => BuildClipTrigger(t, eventIndex))
                    .ToList();

                var arrayObj = new HkObject
                {
                    Id = AllocId(),
                    ClassName = "hkbClipTriggerArray",
                    Signature = "0x59c23a0f",
                    Params = new List<HkParam>
                    {
                        new HkParam
                        {
                            Name = "triggers",
                            Children = triggers,
                            NumElements = triggers.Count.ToString()
                        }
                    }
                };
                _allObjects.Add(arrayObj);

                clip.Params.Remove(param);
                clip.Params.Add(new HkParam { Name = "triggers", Value = arrayObj.Id });
            }
        }

        /// <summary>
        /// One hkbClipTrigger, in Havok's member order and with every member
        /// present. The three booleans are absent from the YAML whenever they are
        /// false, which is most of the time; writing them explicitly keeps the
        /// element the same shape as one that came out of a real file.
        /// </summary>
        private HkObject BuildClipTrigger(
            HkObject source, Dictionary<string, string> eventIndex)
        {
            string Read(string name) =>
                source.Params.FirstOrDefault(p => p.Name == name)?.Value ?? "";

            var eventName = Read("event");
            eventIndex.TryGetValue(eventName, out var eventId);

            var payloadText = Read("payload");
            var payloadRef = "null";
            if (!string.IsNullOrEmpty(payloadText) && payloadText != "null")
            {
                var payload = new HkObject
                {
                    Id = AllocId(),
                    ClassName = "hkbStringEventPayload",
                    Signature = "0xed04256a",
                    Params = new List<HkParam>
                    {
                        new HkParam { Name = "data", Value = payloadText }
                    }
                };
                _allObjects.Add(payload);
                payloadRef = payload.Id;
            }

            var eventParam = new HkParam { Name = "event" };
            eventParam.Children.Add(new HkObject
            {
                Params = new List<HkParam>
                {
                    new HkParam { Name = "id",      Value = eventId ?? "-1" },
                    new HkParam { Name = "payload", Value = payloadRef },
                }
            });

            return new HkObject
            {
                Params = new List<HkParam>
                {
                    new HkParam { Name = "localTime", Value = Read("localTime") is { Length: > 0 } t
                        ? t : "0.000000" },
                    eventParam,
                    new HkParam { Name = "relativeToEndOfClip", Value = Bool(Read("relativeToEndOfClip")) },
                    new HkParam { Name = "acyclic",             Value = Bool(Read("acyclic")) },
                    new HkParam { Name = "isAnnotation",        Value = Bool(Read("isAnnotation")) },
                }
            };
        }

        private static string Bool(string? v) =>
            string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

        /// <summary>Event name → its index in hkbBehaviorGraphStringData.eventNames.</summary>
        private Dictionary<string, string> BuildEventIndex()
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var strData = _allObjects.FirstOrDefault(
                o => o.ClassName == "hkbBehaviorGraphStringData");
            var names = strData?.Params.FirstOrDefault(p => p.Name == "eventNames")?.Strings;
            if (names == null) return index;

            for (int i = 0; i < names.Count; i++)
                if (!string.IsNullOrEmpty(names[i]) && !index.ContainsKey(names[i]))
                    index[names[i]] = i.ToString();
            return index;
        }

        // ── The root scaffold ─────────────────────────────────────────────────────
        // Nothing in the source tree describes it, because it isn't behaviour: an
        // .hkx is one hkRootLevelContainer whose namedVariants name the graph, and
        // every reader starts there. Without it a saved XML declared
        // toplevelobject="#0050" — an id that happens to exist but is whichever
        // object the importer numbered fiftieth — and HKX2's deserializer died on
        // the header before reaching any content. So the .hkx save was broken for
        // every YAML folder, independently of anything else in the import.

        private string BuildRootContainer()
        {
            var graph = _allObjects.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraph");
            if (graph == null) return "";   // not a behaviour unit — leave it alone

            var existing = _allObjects.FirstOrDefault(o => o.ClassName == "hkRootLevelContainer");
            if (existing != null) return existing.Id;

            var variant = new HkObject
            {
                Params = new List<HkParam>
                {
                    new HkParam { Name = "name",      Value = "hkbBehaviorGraph" },
                    new HkParam { Name = "className", Value = "hkbBehaviorGraph" },
                    new HkParam { Name = "variant",   Value = graph.Id },
                }
            };

            var root = new HkObject
            {
                Id = AllocId(),
                ClassName = "hkRootLevelContainer",
                Signature = "0x2772c11e",
                Params = new List<HkParam>
                {
                    new HkParam
                    {
                        Name = "namedVariants",
                        NumElements = "1",
                        Children = new List<HkObject> { variant },
                    }
                }
            };
            _allObjects.Add(root);
            return root.Id;
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

            // ── packfile: header ──────────────────────────────────────────────────
            var packfileSection = doc.GetSection("packfile");
            if (packfileSection != null)
            {
                _classVersion = packfileSection.GetScalar("classversion") ?? "";
                _contentsVersion = packfileSection.GetScalar("contentsversion") ?? "";
            }

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

        private void LoadObjectYaml(string yamlPath, string? defaultClass)
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(yamlPath) ?? "");
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
                AddName(fileName, graphData);

                // Also register string data under filename_strings in case needed
                var strData = FindOrCreateStringData();
                AddName(fileName + "_strings", strData);

                // Done — unless the file carries fields of its own beyond the lists,
                // which would make it a real object as well. Falling through on the
                // class alone was wrong: data/ defaults every classless file to
                // hkbBehaviorGraphData, so graphdata.yaml built the variable objects
                // and then a second, empty hkbBehaviorGraphData on top of them.
                if (!doc.Scalars.Keys.Any(k => !k.Equals("class", StringComparison.OrdinalIgnoreCase)))
                    return;
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

                var listParam = new HkParam
                {
                    Name = refField,
                    NumElements = listItems.Count.ToString()
                };
                _pendingRefLists[listParam] = listItems;   // resolved to ids in Pass 2
                obj.Params.Add(listParam);
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


            // Trigger lists. Kept as the YAML wrote them — localTime / event /
            // relativeToEndOfClip / payload, all flat — because the shape Havok
            // wants needs the event table, which isn't loaded yet. WireClipTriggers
            // builds the hkbClipTriggerArray once everything is in.
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
                        child.Params.Add(new HkParam { Name = k, Value = v });
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

            if (string.Equals(folder, "data", StringComparison.OrdinalIgnoreCase))
                _dataSidecars.Add((fileName, obj));

            // Also register under filename so e.g. "data: graphdata" resolves
            // even when the object has a different internal name param
            if (objectName != fileName) AddName(fileName, obj);
        }


        // ── Pass 2: Name → ID resolution ─────────────────────────────────────────

        private void ResolveAllReferences()
        {
            // Snapshotted: resolving can invent an object (see ClassNamedReference)
            // and append it, and the new one needs no resolving of its own.
            foreach (var obj in _allObjects.ToList())
                ResolveParams(obj.Params, obj.ClassName);
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

        // A transition list is written inline on its owner, and where it lands
        // depends on the owner. On a state it is that state's own transitions; on a
        // state machine the same `transitions:` key means the machine's *wildcard*
        // transitions, which is a differently named member — the source never
        // writes `wildcardTransitions:` at all. Either way Havok wants a pointer to
        // an hkbStateMachineTransitionInfoArray, so both get one.
        //
        // Machines were missed entirely before: their transitions stayed inline in a
        // member Havok doesn't have, so every wildcard transition in the file — the
        // ones that fire from any state, which is how a behaviour reacts to
        // anything at all — was dropped on conversion.

        private void WireStateTransitions()
        {
            foreach (var stateObj in _allObjects
                         .Where(o => o.ClassName == "hkbStateMachineStateInfo").ToList())
                WrapTransitionList(stateObj, "transitions");

            foreach (var machine in _allObjects
                         .Where(o => o.ClassName == "hkbStateMachine").ToList())
                WrapTransitionList(machine, "wildcardTransitions");
        }

        /// <summary>
        /// Moves an owner's inline <c>transitions:</c> list into an
        /// hkbStateMachineTransitionInfoArray of its own and points
        /// <paramref name="targetMember"/> at it. A list that already resolved to a
        /// #ref is left alone; anything else in that slot becomes null, which is
        /// Havok's "no transitions".
        /// </summary>
        private void WrapTransitionList(HkObject owner, string targetMember)
        {
            var transParam = owner.Params.FirstOrDefault(p => p.Name == "transitions");

            if (transParam == null)
            {
                if (!owner.Params.Any(p => p.Name == targetMember))
                    owner.Params.Add(new HkParam { Name = targetMember, Value = "null" });
                return;
            }

            if (transParam.Children == null || transParam.Children.Count == 0)
            {
                // A name here is a reference to a shared array and resolution hasn't
                // run yet, so it is carried over rather than flattened to null —
                // which is what the old ordering could afford and this one can't.
                owner.Params.Remove(transParam);
                owner.Params.Add(new HkParam
                {
                    Name = targetMember,
                    Value = string.IsNullOrEmpty(transParam.Value) ? "null" : transParam.Value
                });
                return;
            }

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

            owner.Params.Remove(transParam);
            owner.Params.Add(new HkParam { Name = targetMember, Value = arrayObj.Id });
        }

        private void ResolveParams(List<HkParam> paramList, string? ownerClass)
        {
            if (paramList == null) return;
            foreach (var param in paramList)
            {
                var info = HavokTypeCatalog.Lookup(ownerClass ?? "", param.Name);
                var expected = info?.ElementClassName;

                // What the declared type says, with the hand lists as the fallback
                // for a class HKX2 has no definition for.
                bool isSingleRef = info != null
                    ? info.ArrayKind == HkArrayKind.None && info.ElementClassName != null
                    : SingleRefFields.Contains(param.Name);
                bool isRefList = info != null
                    ? info.ArrayKind == HkArrayKind.Pointer
                    : MultiRefFields.Contains(param.Name);

                if (_pendingRefLists.TryGetValue(param, out var names))
                {
                    param.Value = string.Join(" ",
                        names.Select(n => ResolveNameToId(n, expected)));
                    _pendingRefLists.Remove(param);
                }
                else if (isSingleRef)
                    param.Value = ResolveNameToId(param.Value, expected);
                else if (isRefList && !string.IsNullOrEmpty(param.Value))
                {
                    var parts = param.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    param.Value = string.Join(" ", parts.Select(t => ResolveNameToId(t, expected)));
                }

                if (param.Children != null)
                    foreach (var child in param.Children)
                        ResolveParams(child.Params,
                            string.IsNullOrEmpty(child.ClassName) ? expected : child.ClassName);
            }
        }

        /// <summary>
        /// The object a name means in a slot declared over
        /// <paramref name="expectedClass"/>. A candidate of exactly that class wins,
        /// then one deriving from it, then — when the class is unknown, or nothing
        /// fits, which is not a case to invent an answer for — the first registered,
        /// which is what this did for every reference before.
        /// </summary>
        private string ResolveNameToId(string? value, string? expectedClass)
        {
            if (string.IsNullOrEmpty(value) || value == "null") return value ?? "";
            if (value.StartsWith("#")) return value;
            if (!_byName.TryGetValue(value, out var candidates) || candidates.Count == 0)
                return ClassNamedReference(value, expectedClass) ?? value;
            if (candidates.Count == 1 || string.IsNullOrEmpty(expectedClass))
                return candidates[0].Id;

            var exact = candidates.FirstOrDefault(o => o.ClassName == expectedClass);
            if (exact != null) return exact.Id;

            var derived = candidates.FirstOrDefault(
                o => HavokTypeCatalog.IsKindOf(o.ClassName, expectedClass));
            if (derived != null) return derived.Id;

            // Nothing of the declared class carries this name. Where the class is one
            // HKX2 knows, that is evidence and not ignorance, so the name is left
            // unresolved rather than pointed at a candidate of the wrong class: the
            // conversion then says "reference symbol not found", naming it, instead
            // of dying on an InvalidCastException from a blender in a transition
            // effect's slot. Where the class is unknown there is no evidence, and the
            // first registered wins as it always did.
            return HavokTypeCatalog.IsKindOf(expectedClass, expectedClass)
                ? value
                : candidates[0].Id;
        }

        /// <summary>
        /// A reference to a name no file carries, where the name is exactly a Havok
        /// class that fits the slot. Vanilla dragonbehavior's modifier lists name a
        /// BSGetTimeStepModifier that has no file, because the object it came from
        /// had no name of its own and the class is all there was to call it — and
        /// the class holds nothing but a runtime value, so a default instance is
        /// the object. Created once and reused, since two lists naming the same
        /// class mean the same shared modifier.
        ///
        /// Deliberately narrow: the name must *be* an HKX2 class, that class must
        /// fit the declared slot, and nothing may already carry the name. A
        /// reference that misses for any other reason stays a miss.
        /// </summary>
        private string? ClassNamedReference(string name, string? expectedClass)
        {
            if (expectedClass == null) return null;
            if (!HavokTypeCatalog.IsKindOf(name, expectedClass)) return null;

            if (_synthesisedByClass.TryGetValue(name, out var existing)) return existing.Id;

            var obj = ModifierCatalog.CreateDefault(name);
            if (obj == null) return null;
            obj.Id = AllocId();
            obj.ClassName = name;
            _allObjects.Add(obj);
            _synthesisedByClass[name] = obj;
            AddName(name, obj);
            SynthesisedFromClassName++;
            return obj.Id;
        }

        private readonly Dictionary<string, HkObject> _synthesisedByClass =
            new(StringComparer.Ordinal);

        /// <summary>How many objects the import had to invent from a class name.</summary>
        public int SynthesisedFromClassName { get; private set; }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void RegisterObject(HkObject obj, string name)
        {
            _allObjects.Add(obj);
            AddName(name, obj);
            AddName(obj.Params?.FirstOrDefault(p => p.Name == "name")?.Value, obj);
        }

        /// <summary>
        /// Records one more thing a name can mean. Registration order is kept,
        /// because it is the tie-break when the declared class doesn't separate two
        /// candidates — and it is what the old first-wins map resolved to, so a
        /// reference the class can't decide lands exactly where it used to.
        /// </summary>
        private void AddName(string? name, HkObject obj)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!_byName.TryGetValue(name, out var list))
                _byName[name] = list = new List<HkObject>();
            if (!list.Contains(obj)) list.Add(obj);
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

        private static string? GuessClassFromContent(YamlDocument doc)
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

        public string? GetScalar(string key) =>
            Scalars.TryGetValue(key, out var v) ? v : null;

        public bool HasScalar(string key) => Scalars.ContainsKey(key);

        public YamlDocument? GetSection(string name) =>
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

            string? currentSection = null;        // e.g. "behavior"
            string? currentListField = null;      // e.g. "variables" or "eventNames"
            bool currentListIsObjects = false;   // true if items have sub-keys
            Dictionary<string, string>? currentItem = null;
            // Open sub-mappings inside the item being read, innermost last. A list
            // item can nest — boneWeights: → named: → "NPC Root [Root]": 1.0 — and
            // flattening that loses which bone the number belongs to, so a nested
            // key is stored under its dotted path.
            var nest = new List<(int Indent, string Key)>();
            // Where this list's own items start, so a deeper "- " can be told from
            // the next item. Without it a list nested inside an item — a blender
            // child's bindings — split the item in two, and the second half was a
            // sibling made of the binding's own keys: 137 empty
            // hkbBlenderGeneratorChild objects out of vanilla 0_master.
            int itemIndent = -1;
            var nestedCounts = new Dictionary<string, int>(StringComparer.Ordinal);

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
                    if (trimmed.StartsWith("- ") && itemIndent >= 0 && indent > itemIndent)
                    {
                        // An item of a list nested inside this one. Recorded under a
                        // numbered path (bindings.0.memberPath) so the shape survives;
                        // the item being read carries on.
                        while (nest.Count > 0 && nest[^1].Indent >= indent)
                            nest.RemoveAt(nest.Count - 1);
                        if (currentItem != null)
                        {
                            var path = string.Join(".", nest.Select(n => n.Key));
                            nestedCounts.TryGetValue(path, out var n);
                            nestedCounts[path] = n + 1;
                            nest.Add((indent, n.ToString()));
                            ParseKv(trimmed[2..].Trim(), currentItem, nest, indent);
                        }
                        continue;
                    }

                    if (trimmed.StartsWith("- "))
                    {
                        // Commit previous item if any
                        if (currentItem != null)
                            FlushItem(doc, currentListField, currentItem,
                                ref currentListIsObjects);

                        if (itemIndent < 0) itemIndent = indent;
                        var rest = trimmed[2..].Trim();
                        nest.Clear();
                        nestedCounts.Clear();
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
                        // Sub-key of the current object item, at whatever depth.
                        while (nest.Count > 0 && nest[^1].Indent >= indent)
                            nest.RemoveAt(nest.Count - 1);
                        ParseKv(trimmed, currentItem, nest, indent);
                        continue;
                    }

                    // Dedented — close the list
                    if (currentItem != null)
                        FlushItem(doc, currentListField, currentItem,
                            ref currentListIsObjects);
                    currentItem = null;
                    currentListField = null;
                    itemIndent = -1;
                }
                else if (currentListField != null)
                {
                    // Dedented all the way to a top-level key. The branch above
                    // never sees this line, so without closing the list here the
                    // item still being read was silently thrown away — and a
                    // one-item list vanished entirely, which is what happened to
                    // every state machine's wildcard transitions.
                    if (currentItem != null)
                        FlushItem(doc, currentListField, currentItem,
                            ref currentListIsObjects);
                    currentItem = null;
                    currentListField = null;
                    currentListIsObjects = false;
                    itemIndent = -1;
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
                            itemIndent = -1;
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

        /// <summary>
        /// One <c>key: value</c> line into <paramref name="target"/>. With a
        /// <paramref name="nest"/> the key is written under its dotted path
        /// (<c>boneWeights.named.NPC Root [Root]</c>) and a key with no value opens
        /// a level rather than closing one — the flat form kept only the leaf, which
        /// is fine for a transition and useless for a bone weight. The empty
        /// placeholder is still written, so anything reading the flat key sees what
        /// it always saw.
        /// </summary>
        private static void ParseKv(string line, Dictionary<string, string> target,
            List<(int Indent, string Key)>? nest = null, int indent = 0)
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) return;
            var k = line[..idx].Trim().Trim('"', '\'');
            var v = StripComment(line[(idx + 1)..]).Trim().Trim('"', '\'');

            var path = nest is { Count: > 0 }
                ? string.Join(".", nest.Select(n => n.Key)) + "." + k
                : k;
            target[path] = v;

            if (nest != null && v.Length == 0) nest.Add((indent, k));
        }

        private static string StripComment(string s)
        {
            var idx = s.IndexOf(" #", StringComparison.Ordinal);
            return idx > 0 ? s[..idx] : s;
        }

        private static string? PeekNextNonEmpty(string[] lines, int from)
        {
            for (int i = from; i < lines.Length; i++)
                if (!string.IsNullOrWhiteSpace(lines[i])) return lines[i];
            return null;
        }
    }
}
