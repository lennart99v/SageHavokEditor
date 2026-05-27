using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SkyrimHavokEditor.Models;

namespace SkyrimHavokEditor.Core.Patching
{
    public class NemesisPatchReader
    {
        // ── Parse all #XXXX.txt files in a folder into a BehaviorPatch ────────
        public static BehaviorPatch ReadFolder(string patchFolder)
        {
            var patch = new BehaviorPatch
            {
                Author = "",
                BaseFile = Path.GetFileName(patchFolder),
                Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Description = $"Imported from {patchFolder}"
            };

            if (!Directory.Exists(patchFolder))
                throw new DirectoryNotFoundException($"Patch folder not found: {patchFolder}");

            foreach (var file in Directory.GetFiles(patchFolder, "#*.txt")
                                          .OrderBy(f => f))
            {
                var ops = ParsePatchFile(file);
                patch.Operations.AddRange(ops);
            }

            return patch;
        }

        // ── Parse a single #XXXX.txt patch file ───────────────────────────────
        private static List<PatchOperation> ParsePatchFile(string path)
        {
            var ops = new List<PatchOperation>();
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0) return ops;

            // First line must be the hkobject header
            var firstLine = lines[0].Trim();
            var idMatch = Regex.Match(firstLine,
                @"<hkobject name=""(#\d+)"" class=""([^""]+)""");
            if (!idMatch.Success) return ops;

            var objectId = idMatch.Groups[1].Value;   // e.g. "#0653"
            var className = idMatch.Groups[2].Value;

            // Detect if this is an entirely new object (all content in NEW block)
            bool isAllNew = lines.Any(l => l.Trim() == "<!-- NEW -->")
                && !lines.Any(l => l.Trim() == "<!-- ORIGINAL -->");

            if (isAllNew)
            {
                // Collect all params from inside the NEW block
                var newParams = ExtractNewBlockParams(lines);
                if (newParams.Count > 0)
                {
                    // We can't reconstruct a full object cleanly without the XML,
                    // so emit a note-only ModifyParam that signals a new object.
                    // For now emit as individual param-level ops targeting the id anchor.
                    foreach (var (name, value) in newParams)
                        ops.Add(new ModifyParamOp
                        {
                            Anchor = $"id:{objectId}",
                            ParamName = name,
                            OldValue = "",
                            NewValue = value,
                            Note = $"[Nemesis NEW] {objectId}.{name}"
                        });
                }
                return ops;
            }

            // Normal case: find ORIGINAL → CLOSE → NEW → CLOSE blocks
            var state = ParseState.Normal;
            string? currentParam = null;
            string? originalValue = null;

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                switch (state)
                {
                    case ParseState.Normal:
                        if (line == "<!-- ORIGINAL -->") { state = ParseState.InOriginal; break; }
                        if (line == "<!-- NEW -->") { state = ParseState.InNewOnly; break; }
                        break;

                    case ParseState.InOriginal:
                        if (line == "<!-- CLOSE -->")
                        {
                            state = ParseState.AfterOriginal;
                            break;
                        }
                        // Extract param name and value
                        var origMatch = Regex.Match(line,
                            @"<hkparam name=""([^""]+)""[^>]*>(.*?)</hkparam>");
                        if (origMatch.Success)
                        {
                            currentParam = origMatch.Groups[1].Value;
                            originalValue = origMatch.Groups[2].Value;
                        }
                        break;

                    case ParseState.AfterOriginal:
                        if (line == "<!-- NEW -->") { state = ParseState.InNew; }
                        break;

                    case ParseState.InNew:
                        if (line == "<!-- CLOSE -->")
                        {
                            state = ParseState.Normal;
                            currentParam = null;
                            originalValue = null;
                            break;
                        }
                        var newMatch = Regex.Match(line,
                            @"<hkparam name=""([^""]+)""[^>]*>(.*?)</hkparam>");
                        if (newMatch.Success && currentParam != null)
                        {
                            var newValue = newMatch.Groups[2].Value;
                            ops.Add(new ModifyParamOp
                            {
                                Anchor = $"id:{objectId}",
                                ParamName = currentParam,
                                OldValue = originalValue ?? "",
                                NewValue = newValue,
                                Note = $"[Nemesis] {objectId}.{currentParam}: " +
                                            $"\"{Truncate(originalValue)}\" → \"{Truncate(newValue)}\""
                            });
                        }
                        break;

                    case ParseState.InNewOnly:
                        // NEW block with no preceding ORIGINAL — param addition
                        if (line == "<!-- CLOSE -->") { state = ParseState.Normal; break; }
                        var addMatch = Regex.Match(line,
                            @"<hkparam name=""([^""]+)""[^>]*>(.*?)</hkparam>");
                        if (addMatch.Success)
                        {
                            ops.Add(new ModifyParamOp
                            {
                                Anchor = $"id:{objectId}",
                                ParamName = addMatch.Groups[1].Value,
                                OldValue = "",
                                NewValue = addMatch.Groups[2].Value,
                                Note = $"[Nemesis NEW] {objectId}.{addMatch.Groups[1].Value}"
                            });
                        }
                        break;
                }
            }

            return ops;
        }

        private static List<(string name, string value)> ExtractNewBlockParams(string[] lines)
        {
            var result = new List<(string, string)>();
            bool inNew = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t == "<!-- NEW -->") { inNew = true; continue; }
                if (t == "<!-- CLOSE -->") { inNew = false; continue; }
                if (!inNew) continue;
                var m = Regex.Match(t, @"<hkparam name=""([^""]+)""[^>]*>(.*?)</hkparam>");
                if (m.Success) result.Add((m.Groups[1].Value, m.Groups[2].Value));
            }
            return result;
        }

        private static string Truncate(string? s, int max = 30)
            => s?.Length > max ? s.Substring(0, max) + "…" : s ?? "";

        private enum ParseState { Normal, InOriginal, AfterOriginal, InNew, InNewOnly }
    }
}
