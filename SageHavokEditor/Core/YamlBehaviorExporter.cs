using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SageHavokEditor.Core.Services;
using SageHavokEditor.Models;

namespace SageHavokEditor.Core
{
    /// <summary>
    /// Writes a loaded behaviour graph back out as a Community Behaviors unit: the
    /// id-keyed YAML form that lives at a graph's real serve path,
    /// <c>meshes/actors/…/&lt;graph&gt;.hkx/</c>, holding <c>behavior.yaml</c> plus one
    /// file per node under the category folders.
    ///
    /// The inverse of <see cref="YamlBehaviorImporter"/>, and deliberately not its
    /// mirror image. The importer accepts both generations of the format; this
    /// writes only the id-keyed one, because that is what her compiler consumes.
    /// </summary>
    public sealed class YamlBehaviorExporter
    {
        // ── Which folder a class belongs in ───────────────────────────────────────
        // Measured, not guessed: every node file across the 513 behaviour units in
        // Skyrim.hky at f5ddbaa was read and grouped by its own `class:`. Two of the
        // importer's folder defaults disagree with what she actually writes —
        // `selectors` is hkbManualSelectorGenerator (not BSiStateTaggingGenerator),
        // and `transitions` holds hkbBlendingTransitionEffect, not the transition
        // info array, which never appears as a file at all. Those defaults only bite
        // a file with no `class:` key, which is why nothing had noticed.
        private static readonly Dictionary<string, string> ExactFolder =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["hkbClipGenerator"] = "clips",
                ["hkbStateMachine"] = "states",
                ["hkbStateMachineStateInfo"] = "states",
                ["hkbBehaviorReferenceGenerator"] = "references",
                ["hkbManualSelectorGenerator"] = "selectors",
                ["BSiStateTaggingGenerator"] = "tagging",
                ["hkbBlendingTransitionEffect"] = "transitions",
                ["hkbExpressionDataArray"] = "data",
                ["hkbBoneIndexArray"] = "data",
                ["hkbEventRangeDataArray"] = "data",
            };

        // ── Objects that never get a file of their own ────────────────────────────
        // The source flattens these into whatever owns them, which is why none of
        // them appears as a node file anywhere in Skyrim.hky. Writing one would
        // produce a unit her compiler has no folder for.
        private static readonly HashSet<string> Flattened =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "hkbStateMachineTransitionInfoArray",
                "hkbVariableBindingSet",
                "hkbClipTriggerArray",
                "hkbStringEventPayload",
                "hkbExpressionCondition",
            };

        // ── How a flattened object folds into its owner ───────────────────────────
        // Each carries one payload member, and the source writes that member in place
        // of the reference. An array payload keeps its own name (a state's
        // transitions:, a clip's triggers:, a binding set's bindings:); a scalar one
        // takes the owner's param name, so an hkbExpressionCondition reached through
        // `condition` is written as condition: 'x == 1'.
        private static readonly Dictionary<string, string> FlattenPayload =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["hkbStateMachineTransitionInfoArray"] = "transitions",
                ["hkbVariableBindingSet"] = "bindings",
                ["hkbClipTriggerArray"] = "triggers",
                ["hkbExpressionCondition"] = "expression",
                ["hkbStringEventPayload"] = "data",
            };

        // The graph and its three data objects are the header, not nodes.
        private static readonly HashSet<string> HeaderClasses =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "hkRootLevelContainer",
                "hkbBehaviorGraph",
                "hkbBehaviorGraphData",
                "hkbBehaviorGraphStringData",
                "hkbVariableValueSet",
            };

        private const char Lf = '\n';

        private readonly Dictionary<string, HkObject> _byId = new();
        private readonly Dictionary<string, int> _localId = new();
        private readonly List<string> _notes = new();

        /// <summary>What an export produced, and what it declined to write.</summary>
        public sealed class Result
        {
            public int Nodes { get; set; }
            public int Sidecars { get; set; }
            public int Flattened { get; set; }
            public string UnitFolder { get; set; } = "";
            public List<string> Notes { get; } = new();
        }

        public Result Export(HavokManager manager, string unitFolder)
        {
            if (manager?.ObjectMap == null || manager.ObjectMap.Count == 0)
                throw new InvalidOperationException("nothing loaded to export");

            _byId.Clear(); _localId.Clear(); _notes.Clear();
            foreach (var o in manager.ObjectMap.Values)
                if (!string.IsNullOrEmpty(o.Id)) _byId[o.Id] = o;

            var graph = _byId.Values.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraph")
                ?? throw new InvalidOperationException(
                    "no hkbBehaviorGraph — only behaviour graph units can be exported");

            // Everything the graph can reach. Anything else would be dropped by an
            // .hkx save anyway, so writing it would put objects in the source that
            // the round trip can never bring back.
            var reachable = Reach(graph);

            // Node files get a unit-local integer id. Assignment order is the
            // reachability walk, so it is stable between runs on the same graph —
            // her ids are not dense and their values carry no meaning, but a stable
            // one keeps a re-export diffable against the last.
            int next = 0;
            foreach (var o in reachable)
                if (IsNodeFile(o)) _localId[o.Id] = next++;

            // The id/name pairs below are written against this file's own tables.
            var strData = reachable.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            _eventNames = Strings(strData, "eventNames");
            _variableNames = Strings(strData, "variableNames");

            Directory.CreateDirectory(unitFolder);
            var result = new Result { UnitFolder = unitFolder };

            WriteBehaviorYaml(unitFolder, manager, graph);
            WriteGraphData(unitFolder, reachable);

            foreach (var o in reachable)
            {
                if (!IsNodeFile(o))
                {
                    if (Flattened.Contains(o.ClassName)) result.Flattened++;
                    continue;
                }

                var folder = FolderFor(o.ClassName);
                var dir = Path.Combine(unitFolder, folder);
                Directory.CreateDirectory(dir);

                var stem = _localId[o.Id].ToString(CultureInfo.InvariantCulture);
                File.WriteAllText(Path.Combine(dir, stem + ".yaml"), NodeYaml(o), new UTF8Encoding(false));
                if (folder == "data") result.Sidecars++; else result.Nodes++;
            }

            foreach (var n in _notes) result.Notes.Add(n);
            return result;
        }

        // ── Reachability ──────────────────────────────────────────────────────────

        private List<HkObject> Reach(HkObject from)
        {
            // Identity, not Id: an inline child has no Id of its own, and skipping
            // it skips everything it points at — which is how a transition's
            // hkbBlendingTransitionEffect went missing from the first export.
            var seen = new HashSet<HkObject>(ReferenceEqualityComparer.Instance);
            var order = new List<HkObject>();
            var stack = new Stack<HkObject>();
            stack.Push(from);

            while (stack.Count > 0)
            {
                var o = stack.Pop();
                if (!seen.Add(o)) continue;
                if (!string.IsNullOrEmpty(o.Id)) order.Add(o);

                foreach (var p in o.Params)
                {
                    foreach (var child in p.Children ?? new List<HkObject>())
                        stack.Push(child);
                    foreach (var id in RefIds(p))
                        if (_byId.TryGetValue(id, out var target)) stack.Push(target);
                }
            }
            return order;
        }

        private static IEnumerable<string> RefIds(HkParam p)
        {
            var v = p.Value;
            if (string.IsNullOrEmpty(v) || !v.Contains('#')) yield break;
            foreach (var tok in v.Split(new[] { ' ', '\t', '\n', '\r' },
                                        StringSplitOptions.RemoveEmptyEntries))
                if (tok.StartsWith("#", StringComparison.Ordinal)) yield return tok;
        }

        private bool IsNodeFile(HkObject o) =>
            !HeaderClasses.Contains(o.ClassName) && !Flattened.Contains(o.ClassName);

        private string FolderFor(string className)
        {
            if (ExactFolder.TryGetValue(className, out var f)) return f;
            if (className.EndsWith("Modifier", StringComparison.OrdinalIgnoreCase)
                || className.Equals("hkbModifierGenerator", StringComparison.OrdinalIgnoreCase)
                || className.Equals("hkbModifierList", StringComparison.OrdinalIgnoreCase))
                return "modifiers";
            return "generators";
        }

        // ── behavior.yaml ─────────────────────────────────────────────────────────

        private void WriteBehaviorYaml(string unitFolder, HavokManager manager, HkObject graph)
        {
            var sb = new StringBuilder();
            sb.Append("packfile:\n");
            sb.Append("  classversion: ").Append(Scalar(graph, "__classversion") ?? "8").Append('\n');
            sb.Append("  contentsversion: \"hk_2010.2.0-r1\"\n\n");

            sb.Append("behavior:\n");
            sb.Append("  name: ").Append(Quote(Scalar(graph, "name") ?? "behavior")).Append('\n');

            var mode = Scalar(graph, "variableMode");
            if (!string.IsNullOrEmpty(mode)) sb.Append("  variableMode: ").Append(mode).Append('\n');

            var root = RefOf(graph, "rootGenerator");
            if (root != null) sb.Append("  rootGenerator: ").Append(root).Append('\n');

            sb.Append("  data: graphdata\n");
            File.WriteAllText(Path.Combine(unitFolder, "behavior.yaml"), sb.ToString(), new UTF8Encoding(false));
        }

        // ── data/graphdata.yaml ───────────────────────────────────────────────────
        // The variables and events are written as lists here rather than as three
        // linked objects, which is the shape the importer's "Pandora graphdata.yaml
        // pattern" reads back. Both halves have to agree or a round trip loses every
        // name in the file.

        private void WriteGraphData(string unitFolder, List<HkObject> reachable)
        {
            var strData = reachable.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            var data = reachable.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphData");
            var values = reachable.FirstOrDefault(o => o.ClassName == "hkbVariableValueSet");

            var varNames = Strings(strData, "variableNames");
            var eventNames = Strings(strData, "eventNames");

            var sb = new StringBuilder();

            sb.Append("variables:");
            if (varNames.Count == 0) sb.Append(" []\n");
            else
            {
                sb.Append('\n');
                var infos = ChildrenOf(data, "variableInfos");
                var vals = ChildrenOf(values, "wordVariableValues");
                for (int i = 0; i < varNames.Count; i++)
                {
                    sb.Append("  - name: ").Append(Quote(varNames[i])).Append('\n');
                    var type = i < infos.Count ? Scalar(infos[i], "type") : null;
                    if (!string.IsNullOrEmpty(type)) sb.Append("    type: ").Append(type).Append('\n');
                    var val = i < vals.Count ? Scalar(vals[i], "value") : null;
                    if (!string.IsNullOrEmpty(val) && val != "0")
                        sb.Append("    value: ").Append(val).Append('\n');
                }
            }

            sb.Append('\n').Append("events:");
            if (eventNames.Count == 0) sb.Append(" []\n");
            else
            {
                sb.Append('\n');
                var infos = ChildrenOf(data, "eventInfos");
                for (int i = 0; i < eventNames.Count; i++)
                {
                    sb.Append("  - name: ").Append(Quote(eventNames[i])).Append('\n');
                    var flags = i < infos.Count ? Scalar(infos[i], "flags") : null;
                    sb.Append("    flags: ").Append(string.IsNullOrEmpty(flags) ? "0" : flags).Append('\n');
                }
            }

            var dir = Path.Combine(unitFolder, "data");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "graphdata.yaml"), sb.ToString(), new UTF8Encoding(false));
        }

        // ── One node file ─────────────────────────────────────────────────────────

        private string NodeYaml(HkObject o)
        {
            var sb = new StringBuilder();
            sb.Append("id: ").Append(_localId[o.Id]).Append(Lf);
            sb.Append("class: ").Append(o.ClassName).Append(Lf);

            var name = Scalar(o, "name");
            if (!string.IsNullOrEmpty(name)) sb.Append("name: ").Append(Quote(name)).Append(Lf);

            foreach (var p in o.Params)
            {
                // `id` and `name` are the header, already written. The source's own
                // id is not carried over: ours is unit-local and reassigned.
                if (string.IsNullOrEmpty(p.Name) || p.Name == "name" || p.Name == "id") continue;
                Emit(sb, o, p, "", "");
            }
            return sb.ToString();
        }

        /// <summary>
        /// One param. <paramref name="firstPrefix"/> opens the line — a list item
        /// passes "- " so the first key of an inline struct sits on the dash — and
        /// <paramref name="pad"/> indents every line after it.
        /// </summary>
        private void Emit(StringBuilder sb, HkObject owner, HkParam p, string firstPrefix, string pad)
        {
            var open = firstPrefix.Length > 0 ? firstPrefix : pad;

            var refs = RefIds(p).ToList();

            // A reference to something written inline rather than as its own file.
            if (refs.Count == 1 && _byId.TryGetValue(refs[0], out var target)
                && Flattened.Contains(target.ClassName))
            {
                Flatten(sb, p, target, open, pad);
                return;
            }

            if (refs.Count > 0)
            {
                var mapped = refs.Select(LocalOf).Where(x => x != null).ToList();
                if (mapped.Count == 0) return;
                if (refs.Count == 1)
                {
                    sb.Append(open).Append(p.Name).Append(": ").Append(mapped[0]).Append(Lf);
                }
                else
                {
                    sb.Append(open).Append(p.Name).Append(":").Append(Lf);
                    foreach (var m in mapped) sb.Append(pad).Append("  - ").Append(m).Append(Lf);
                }
                return;
            }

            if (p.Strings != null && p.Strings.Count > 0)
            {
                sb.Append(open).Append(p.Name).Append(":").Append(Lf);
                foreach (var str in p.Strings)
                    sb.Append(pad).Append("  - ").Append(Quote(str)).Append(Lf);
                return;
            }

            if (p.Children != null && p.Children.Count > 0)
            {
                sb.Append(open).Append(p.Name).Append(":").Append(Lf);
                EmitStructList(sb, p.Children, pad + "  ",
                    HavokTypeCatalog.Lookup(owner.ClassName, p.Name)?.ElementClassName);
                return;
            }

            var value = p.Value ?? "";
            if (value.Length == 0 || value == "null") return;

            sb.Append(open).Append(p.Name).Append(": ").Append(value).Append(Lf);

            // Symbol references are written twice: the index the runtime reads and
            // the name a human does. Her writer emits both and the importer resolves
            // whichever it finds, so dropping the name round-trips but leaves source
            // nobody can read.
            var readable = ReadableFor(owner, p, value);
            if (readable != null)
                sb.Append(pad).Append(readable.Value.Key).Append(": ")
                  .Append(Quote(readable.Value.Value)).Append(Lf);
        }

        private void Flatten(StringBuilder sb, HkParam p, HkObject target, string open, string pad)
        {
            if (!FlattenPayload.TryGetValue(target.ClassName, out var payloadName)) return;
            var payload = target.Params.FirstOrDefault(x => x.Name == payloadName);
            if (payload == null) return;

            if (payload.Children != null && payload.Children.Count > 0)
            {
                // An array payload keeps its own name, which is why a state machine's
                // wildcardTransitions and a state's transitions both land on
                // `transitions:` — exactly what the source writes.
                sb.Append(open).Append(payloadName).Append(":").Append(Lf);
                EmitStructList(sb, payload.Children, pad + "  ",
                    HavokTypeCatalog.Lookup(target.ClassName, payloadName)?.ElementClassName);
                return;
            }

            var text = payload.Value ?? "";
            if (text.Length == 0) return;
            sb.Append(open).Append(p.Name).Append(": ").Append(Quote(text)).Append(Lf);
        }

        /// <summary>
        /// A list of inline structs. The importer keeps a nested mapping under a
        /// dotted path (initiateInterval.enterEventId), so re-nesting here is what
        /// turns it back into the shape the source actually has.
        /// </summary>
        private void EmitStructList(StringBuilder sb, List<HkObject> items, string pad,
                                    string? elementClass = null)
        {
            foreach (var item in items)
            {
                var owner = string.IsNullOrEmpty(item.ClassName) && elementClass != null
                    ? new HkObject { ClassName = elementClass, Params = item.Params }
                    : item;

                var wrote = false;
                string lastGroup = "";

                foreach (var ip in item.Params)
                {
                    if (string.IsNullOrEmpty(ip.Name)) continue;

                    var dot = ip.Name.IndexOf('.');
                    if (dot > 0)
                    {
                        var group = ip.Name.Substring(0, dot);
                        var leaf = ip.Name.Substring(dot + 1);
                        if (group != lastGroup)
                        {
                            sb.Append(wrote ? pad + "  " : pad + "- ").Append(group).Append(":").Append(Lf);
                            lastGroup = group;
                            wrote = true;
                        }
                        // The leaf keeps its own name; only the path is rebuilt.
                        var leafParam = new HkParam { Name = leaf, Value = ip.Value };
                        Emit(sb, owner, leafParam, "", pad + "    ");
                        continue;
                    }

                    lastGroup = "";
                    var before = sb.Length;
                    Emit(sb, owner, ip, wrote ? pad + "  " : pad + "- ", pad + "  ");
                    if (sb.Length > before) wrote = true;
                }
                if (!wrote) sb.Append(pad).Append("- {}").Append(Lf);
            }
        }

        /// <summary>The name half of an id/name pair, when the catalog says this
        /// param is an index into the event or variable table.</summary>
        private KeyValuePair<string, string>? ReadableFor(HkObject owner, HkParam p, string value)
        {
            var info = HavokTypeCatalog.Lookup(owner.ClassName, p.Name);
            if (info == null) return null;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) || i < 0)
                return null;

            List<string> table;
            string key;
            if (info.Semantic == HkParamSemantic.EventId)
            {
                table = _eventNames; key = StripSuffix(p.Name, "Id");
            }
            else if (info.Semantic == HkParamSemantic.VariableIndex)
            {
                table = _variableNames; key = StripSuffix(p.Name, "Index");
            }
            else return null;

            if (i >= table.Count) return null;
            if (key == p.Name) return null;   // nothing to shorten to
            return new KeyValuePair<string, string>(key, table[i]);
        }

        private static string StripSuffix(string name, string suffix) =>
            name.EndsWith(suffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;

        private List<string> _eventNames = new();
        private List<string> _variableNames = new();

        // ── Small helpers ─────────────────────────────────────────────────────────

        private string? LocalOf(string refId) =>
            _localId.TryGetValue(refId, out var n)
                ? n.ToString(CultureInfo.InvariantCulture)
                : null;

        private string? RefOf(HkObject o, string param)
        {
            var p = o.Params.FirstOrDefault(x => x.Name == param);
            if (p == null) return null;
            var first = RefIds(p).FirstOrDefault();
            return first == null ? null : LocalOf(first);
        }

        private static string? Scalar(HkObject? o, string param) =>
            o?.Params.FirstOrDefault(p => p.Name == param)?.Value;

        private static List<string> Strings(HkObject? o, string param) =>
            o?.Params.FirstOrDefault(p => p.Name == param)?.Strings ?? new List<string>();

        private static List<HkObject> ChildrenOf(HkObject? o, string param) =>
            o?.Params.FirstOrDefault(p => p.Name == param)?.Children ?? new List<HkObject>();

        /// <summary>Single-quoted the way her writer quotes a name, with the one
        /// escape YAML needs inside single quotes.</summary>
        private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        private static string Quote2(string s) => "\"" + s + "\"";
    }
}
