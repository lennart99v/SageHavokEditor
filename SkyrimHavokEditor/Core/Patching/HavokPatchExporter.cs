using SkyrimHavokEditor.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SkyrimHavokEditor.Core.Patching
{
    public enum PatchEngineTarget { Nemesis, Pandora }

    public class PatchExportOptions
    {
        public string ModCode { get; set; }
        public string ModName { get; set; }
        public string Author { get; set; }
        public string OutputFolder { get; set; }
        public string BehaviorFileName { get; set; }
        public string ProjectName { get; set; }
        public PatchEngineTarget Target { get; set; }
    }

    public class HavokPatchExporter
    {
        private readonly HavokManager _manager;
        private readonly Dictionary<string, ObjectSnapshot> _originalSnapshot;

        public HavokPatchExporter(HavokManager manager,
            Dictionary<string, ObjectSnapshot> originalSnapshot)
        {
            _manager = manager;
            _originalSnapshot = originalSnapshot;
        }

        public static Dictionary<string, ObjectSnapshot> LoadVanillaSnapshot(string xmlPath)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(HkPackfile));
            using var fs = File.OpenRead(xmlPath);
            var packfile = (HkPackfile)serializer.Deserialize(fs);
            var mgr = new HavokManager();
            mgr.BuildGraph(packfile);
            return PatchGenerator.TakeSnapshot(mgr);
        }

        public (int filesWritten, List<string> errors) Export(PatchExportOptions opts)
        {
            var errors = new List<string>();
            int written = 0;

            string behaviorFolder = opts.Target == PatchEngineTarget.Pandora
                && !string.IsNullOrEmpty(opts.ProjectName)
                ? $"{opts.ProjectName}~{opts.BehaviorFileName}"
                : opts.BehaviorFileName;

            string engineRoot = opts.Target == PatchEngineTarget.Nemesis
                ? "Nemesis_Engine" : "Pandora_Engine";

            string modDir = Path.Combine(opts.OutputFolder, engineRoot, "mod", opts.ModCode);
            string patchDir = Path.Combine(modDir, behaviorFolder);
            Directory.CreateDirectory(patchDir);
            WriteInfoIni(modDir, opts);

            foreach (var kv in _manager.ObjectMap)
            {
                var id = kv.Key;
                var current = kv.Value;
                _originalSnapshot.TryGetValue(id, out var original);

                if (original == null)
                {
                    try { WriteNewObjectPatch(patchDir, current); written++; }
                    catch (Exception ex) { errors.Add($"{id}: {ex.Message}"); }
                    continue;
                }

                // ── Fix 1: original.Params is Dictionary<string,ParamSnapshot>
                // so keys are param names, not .Name properties
                bool changed = original.Params.Count != current.Params.Count
                    || current.Params.Any(cp =>
                    {
                        original.Params.TryGetValue(cp.Name, out var snap);
                        return snap == null || snap.Value != (cp.Value ?? "");
                    });

                if (!changed) continue;

                try { WriteChangedObjectPatch(patchDir, current, original); written++; }
                catch (Exception ex) { errors.Add($"{id}: {ex.Message}"); }
            }

            return (written, errors);
        }

        private static void WriteInfoIni(string modDir, PatchExportOptions opts)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"name={opts.ModName}");
            sb.AppendLine($"author={opts.Author ?? ""}");
            sb.AppendLine("site=");
            sb.AppendLine("auto=");
            File.WriteAllText(Path.Combine(modDir, "info.ini"), sb.ToString(), Encoding.UTF8);
        }

        // ── Fix 2: rename inner variable to avoid numElem conflict ───────────
        private static string SerializeParam(HkParam p)
        {
            if (p.Children == null || p.Children.Count == 0)
            {
                string ne = string.IsNullOrEmpty(p.NumElements)
                    ? "" : $" numelements=\"{p.NumElements}\"";
                return $"\t\t<hkparam name=\"{p.Name}\"{ne}>{p.Value}</hkparam>";
            }

            var sb = new StringBuilder();
            string ne2 = string.IsNullOrEmpty(p.NumElements)
                ? "" : $" numelements=\"{p.NumElements}\"";
            sb.AppendLine($"\t\t<hkparam name=\"{p.Name}\"{ne2}>");
            foreach (var child in p.Children)
                sb.Append(SerializeChildObject(child, 3));
            sb.Append("\t\t</hkparam>");
            return sb.ToString();
        }

        private static string SerializeChildObject(HkObject obj, int depth)
        {
            var indent = new string('\t', depth);
            var sb = new StringBuilder();
            sb.AppendLine($"{indent}<hkobject>");
            foreach (var p in obj.Params)
            {
                string ne = string.IsNullOrEmpty(p.NumElements)
                    ? "" : $" numelements=\"{p.NumElements}\"";
                sb.AppendLine($"{indent}\t<hkparam name=\"{p.Name}\"{ne}>{p.Value}</hkparam>");
            }
            sb.AppendLine($"{indent}</hkobject>");
            return sb.ToString();
        }

        private static void WriteNewObjectPatch(string patchDir, HkObject obj)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"<hkobject name=\"{obj.Id}\" class=\"{obj.ClassName}\" signature=\"{obj.Signature}\">");
            sb.AppendLine("\t<!-- NEW -->");
            foreach (var p in obj.Params)
                sb.AppendLine(SerializeParam(p));
            sb.AppendLine("\t<!-- CLOSE -->");
            sb.AppendLine("</hkobject>");

            File.WriteAllText(
                Path.Combine(patchDir, $"{obj.Id}.txt"),
                sb.ToString(), Encoding.UTF8);
        }

        // ── Fix 3: no duplicate valueChanged, correct ParamSnapshot access ───
        private static void WriteChangedObjectPatch(string patchDir, HkObject current,
            ObjectSnapshot original)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"<hkobject name=\"{current.Id}\" class=\"{current.ClassName}\" signature=\"{current.Signature}\">");

            foreach (var cp in current.Params)
            {
                original.Params.TryGetValue(cp.Name, out var op);

                if (op == null)
                {
                    // New param not in original
                    sb.AppendLine("\t<!-- NEW -->");
                    sb.AppendLine(SerializeParam(cp));
                    sb.AppendLine("\t<!-- CLOSE -->");
                    continue;
                }

                bool paramChanged = op.Value != (cp.Value ?? "")
                    || op.NumElements != (cp.NumElements ?? "");

                if (!paramChanged)
                {
                    sb.AppendLine(SerializeParam(cp));
                    continue;
                }

                // Changed — wrap with ORIGINAL / NEW markers
                sb.AppendLine("\t<!-- ORIGINAL -->");
                var origParam = new HkParam
                {
                    Name = cp.Name,
                    Value = op.Value,
                    NumElements = op.NumElements
                };
                sb.AppendLine(SerializeParam(origParam));
                sb.AppendLine("\t<!-- CLOSE -->");
                sb.AppendLine("\t<!-- NEW -->");
                sb.AppendLine(SerializeParam(cp));
                sb.AppendLine("\t<!-- CLOSE -->");
            }

            // Params removed from original — Fix 4: use kvp.Key not kvp.Name
            foreach (var kvp in original.Params)
            {
                if (!current.Params.Any(p => p.Name == kvp.Key))
                {
                    sb.AppendLine("\t<!-- ORIGINAL -->");
                    sb.AppendLine($"\t\t<hkparam name=\"{kvp.Key}\">{kvp.Value.Value}</hkparam>");
                    sb.AppendLine("\t<!-- CLOSE -->");
                }
            }

            sb.AppendLine("</hkobject>");
            File.WriteAllText(
                Path.Combine(patchDir, $"{current.Id}.txt"),
                sb.ToString(), Encoding.UTF8);
        }
    }
}