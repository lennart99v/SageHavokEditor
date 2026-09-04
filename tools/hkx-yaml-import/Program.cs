using System.Text;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Services;
using SageHavokEditor.Core.Validation;
using SageHavokEditor.Models;

// Measures what survives 📂 Open YAML behavior folder… — and what it takes to
// carry the result through to a .hkx.
//
//   dotnet run --project tools/hkx-yaml-import -- <unit folder> [<unit folder> …]
//                                                 [--out <dir>]
//
// A unit folder is one <name>.hkx/ directory of a Behavior Relay source tree:
// behavior.yaml at the top, clips/ states/ generators/ modifiers/ transitions/
// selectors/ references/ tagging/ data/ beneath it.
//
// Import fidelity can't be argued from the code, because the YAML is somebody
// else's format and the only authority on it is real content. So every number
// here comes from importing an actual unit and asking what the graph looks like
// afterwards.
//
// Two kinds of line. [PASS]/[FAIL] is what must hold today. "open:" is a defect
// that is measured but not yet fixed — kept here rather than in a comment so the
// number moves when the fix lands, and so the conversion's stopping point is on
// the record instead of being rediscovered. The end-to-end question is the last
// one: does the unit convert, and if not, what does the deserializer say.
//
// --out keeps the XML and .hkx each unit produced, for looking at by hand.

var failed = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}
void Note(string what, string detail) => Console.WriteLine($"  [open] {what}  ({detail})");

string? outDir = null;
{
    int at = Array.IndexOf(args, "--out");
    if (at >= 0)
    {
        if (at + 1 < args.Length) outDir = args[at + 1];
        args = args.Where((_, i) => i != at && i != at + 1).ToArray();
    }
}
if (outDir != null) Directory.CreateDirectory(outDir);

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hkx-yaml-import <unit folder> [<unit folder> …] [--out <dir>]");
    return 2;
}

// The five fields that arrive as a bare name and have to become an index. The
// runtime reads all of them positionally, so a name left in place is not a
// smaller version of the right answer — it is a parse failure at the conversion,
// and before that a graph pointing at nothing.
var eventNameFields = new[]
{
    "startPlayingEvent", "startMatchingEvent", "activateEvent", "deactivateEvent"
};
const string variableNameField = "syncVariable";

foreach (var folder in args)
{
    Console.WriteLine();
    Console.WriteLine($"== {Path.GetFileName(folder.TrimEnd('\\', '/'))} ==");

    if (!Directory.Exists(folder))
    {
        Check("the folder is there", false, folder);
        continue;
    }

    var manager = new HavokManager();
    string name;
    try { name = new YamlBehaviorImporter().Import(folder, manager); }
    catch (Exception ex) { Check("imports without throwing", false, ex.Message); continue; }

    var objects = manager.ObjectMap.Values.ToList();
    var sourceFiles = Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories).Count();
    Console.WriteLine($"  {sourceFiles} source files → {objects.Count} objects  (\"{name}\")");

    // -- every #ref resolves ------------------------------------------------
    var broken = new List<string>();
    foreach (var obj in objects)
        foreach (var p in obj.Params)
            foreach (var token in Refs(p))
                if (!manager.ObjectMap.ContainsKey(token))
                    broken.Add($"{obj.ClassName}.{p.Name} → {token}");
    Check("every #ref resolves", broken.Count == 0,
        broken.Count == 0 ? null : $"{broken.Count}, e.g. {string.Join("; ", broken.Take(3))}");

    // -- the root scaffold --------------------------------------------------
    var container = objects.FirstOrDefault(o => o.ClassName == "hkRootLevelContainer");
    var packfile = manager.NewPackfile();
    Check("the import built an hkRootLevelContainer", container != null);

    // Not "names an object that exists": before the scaffold was built the header
    // said #0050, which is whichever object the importer happened to number
    // fiftieth, so the weaker check passed on a header pointing at a clip.
    Check("toplevelobject names that container, not whatever landed on #0050",
        container != null && packfile.TopLevelObject == container.Id,
        $"toplevelobject={packfile.TopLevelObject}, container={container?.Id ?? "none"}");

    var graph = objects.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraph");
    var variant = container?.Params.FirstOrDefault(p => p.Name == "namedVariants")
        ?.Children.FirstOrDefault();
    Check("and the container's variant points at the behaviour graph",
        graph != null && variant?.Params.FirstOrDefault(p => p.Name == "variant")?.Value == graph.Id,
        variant?.Params.FirstOrDefault(p => p.Name == "variant")?.Value ?? "no variant");

    Check("the packfile header says what behavior.yaml says",
        packfile.ClassVersion == "8" && packfile.ContentsVersion == "hk_2010.2.0-r1",
        $"classversion={packfile.ClassVersion}, contentsversion={packfile.ContentsVersion}");

    // -- clip triggers ------------------------------------------------------
    // A clip's triggers param is a pointer to an hkbClipTriggerArray. Counted
    // from the source rather than from the import, because an import that keeps
    // them in the wrong shape would otherwise report a clean 0 of 0.
    var clips = objects.Where(o => o.ClassName == "hkbClipGenerator").ToList();
    var clipsDir = Path.Combine(folder, "clips");
    var declared = Directory.Exists(clipsDir)
        ? Directory.EnumerateFiles(clipsDir, "*.yaml")
            .Count(f => File.ReadLines(f).Any(l => l.StartsWith("triggers:", StringComparison.Ordinal)))
        : 0;
    var properArrays = clips
        .Select(c => c.Params.FirstOrDefault(p => p.Name == "triggers"))
        .Count(p => p != null && manager.ObjectMap.TryGetValue(p.Value ?? "", out var t)
                    && t.ClassName == "hkbClipTriggerArray");
    Check("every declared trigger list became an hkbClipTriggerArray",
        properArrays == declared, $"{properArrays} of {declared}");

    // A trigger whose event is still a name fires nothing, and the payload — the
    // hand a HitFrame came from — is a pointer, so it has to be an object of its
    // own or the .hkx save drops it with the rest of the unreferenced.
    var triggerCount = 0;
    var unresolvedIds = new List<string>();
    var payloadRefs = 0;
    var payloadStrings = 0;
    foreach (var clip in clips)
    {
        var p = clip.Params.FirstOrDefault(x => x.Name == "triggers");
        if (p == null || !manager.ObjectMap.TryGetValue(p.Value ?? "", out var array)) continue;
        foreach (var trigger in array.Params.Where(x => x.Name == "triggers")
                     .SelectMany(x => x.Children))
        {
            triggerCount++;
            var ev = trigger.Params.FirstOrDefault(x => x.Name == "event")?.Children.FirstOrDefault();
            var id = ev?.Params.FirstOrDefault(x => x.Name == "id")?.Value;
            if (!int.TryParse(id, out _)) unresolvedIds.Add($"{clip.DisplayName}: {id}");

            var payload = ev?.Params.FirstOrDefault(x => x.Name == "payload")?.Value ?? "null";
            if (payload == "null") continue;
            payloadRefs++;
            if (manager.ObjectMap.TryGetValue(payload, out var po)
                && po.ClassName == "hkbStringEventPayload") payloadStrings++;
        }
    }
    if (triggerCount > 0)
    {
        Check("every trigger's event resolved to an id", unresolvedIds.Count == 0,
            $"{triggerCount} triggers, {unresolvedIds.Count} unresolved"
            + (unresolvedIds.Count == 0 ? "" : $" — {string.Join("; ", unresolvedIds.Take(3))}"));
        Check("and every payload became an hkbStringEventPayload",
            payloadRefs == payloadStrings, $"{payloadStrings} of {payloadRefs}");
    }

    // -- the name-keyed index fields ----------------------------------------
    // Counted from the source, because a resolved field is renamed: the check has
    // to know how many were written the readable way, not how many still are.
    var declaredNameKeyed = Directory
        .EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories)
        .SelectMany(File.ReadLines)
        .Count(l => eventNameFields.Concat(new[] { variableNameField })
            .Any(f => l.StartsWith(f + ":", StringComparison.Ordinal)));

    var stillNames = new List<string>();
    var resolved = 0;
    foreach (var obj in objects)
        foreach (var p in obj.Params)
        {
            if (eventNameFields.Contains(p.Name) || p.Name == variableNameField)
            { stillNames.Add($"{obj.ClassName}.{p.Name} = {p.Value}"); continue; }
            if (eventNameFields.Any(f => p.Name == f + "Id") || p.Name == variableNameField + "Index")
                if (int.TryParse(p.Value, out _)) resolved++;
        }
    Check("every name-keyed index field became the index member it names",
        stillNames.Count == 0 && resolved >= declaredNameKeyed,
        $"{declaredNameKeyed} written by name in the source, {resolved} index members resolved"
        + (stillNames.Count == 0 ? "" : $", {stillNames.Count} left as names — "
                                        + string.Join("; ", stillNames.Take(3))));

    // -- references land on the right object ----------------------------------
    // Names are not unique. This tree makes that worse by keying files on them:
    // mt_behavior has 656 names two files share, AltarIdle_Enter being both a
    // state and the clip it plays. What decides which one a reference means is
    // the slot — hkbStateMachine.states is declared over hkbStateMachineStateInfo
    // and hkbStateMachineStateInfo.generator over hkbGenerator — so the check is
    // that every resolved reference is of the class its slot declares.
    {
        var wrong = new List<string>();
        var checkedRefs = 0;
        foreach (var obj in objects)
            foreach (var p in obj.Params)
            {
                var expected = p.TypeInfo?.ElementClassName;
                if (expected == null) continue;
                foreach (var token in Refs(p))
                {
                    if (!manager.ObjectMap.TryGetValue(token, out var target)) continue;
                    if (!HavokTypeCatalog.IsKindOf(target.ClassName, expected)) continue;
                    checkedRefs++;
                }
                foreach (var token in Refs(p))
                {
                    if (!manager.ObjectMap.TryGetValue(token, out var target)) continue;
                    if (HavokTypeCatalog.IsKindOf(target.ClassName, expected)) continue;
                    // Only a mismatch when HKX2 knows the class at all; an unknown
                    // one is no evidence either way.
                    if (!HavokTypeCatalog.IsKindOf(target.ClassName, target.ClassName)) continue;
                    wrong.Add($"{obj.ClassName}.{p.Name} → {target.ClassName} "
                              + $"({target.DisplayName}), wanted {expected}");
                }
            }
        Check("every reference points at an object of the class its slot declares",
            wrong.Count == 0,
            $"{checkedRefs} checked, {wrong.Count} wrong"
            + (wrong.Count == 0 ? "" : $" — {string.Join("; ", wrong.Take(3))}"));
    }

    // -- a list that ends at a top-level key ----------------------------------
    // The parser only closed an open list when it saw a line indented under one.
    // A list followed by another top-level key left its last item pending and
    // then threw it away — so a one-item list vanished, which is what every
    // state machine's wildcard transitions are.
    {
        var declaredMachines = Directory
            .EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories)
            .Count(f =>
            {
                var lines = File.ReadAllLines(f);
                // Exactly hkbStateMachine: hkbStateMachineStateInfo also declares
                // a transitions: list, and 517 of mt_behavior's do - counting those
                // in is what made this read 116 of 633 instead of 116 of 116.
                return lines.Any(l => l.TrimStart('\uFEFF').Trim() == "class: hkbStateMachine")
                       && lines.Any(l => l.StartsWith("transitions:", StringComparison.Ordinal));
            });
        var imported = objects.Count(o => o.ClassName == "hkbStateMachine"
                                          && o.Params.Any(p => p.Name == "transitions"
                                                               && p.Children.Count > 0));
        Check("every state machine that declares wildcard transitions kept them",
            imported >= declaredMachines, $"{imported} of {declaredMachines}");
    }

    // -- data/ sidecars find their owner --------------------------------------
    // Nothing in the source references these: the owner writes the member as
    // null and the link is the filename. Counted from the folder, because an
    // import that never attached one would otherwise look clean, and checked by
    // asking whether anything points at the object rather than by trusting the
    // rule that attached it.
    {
        var dataDir = Path.Combine(folder, "data");
        var stems = Directory.Exists(dataDir)
            ? Directory.EnumerateFiles(dataDir, "*.yaml")
                .Where(f => File.ReadLines(f).Any(l =>
                    l.TrimStart('﻿').StartsWith("class: ", StringComparison.Ordinal)))
                .Select(Path.GetFileNameWithoutExtension)
                .ToHashSet()
            : new HashSet<string?>();

        var referenced = objects
            .SelectMany(o => o.Params)
            .SelectMany(Refs)
            .ToHashSet();

        var orphaned = objects
            .Where(o => stems.Contains(o.Params.FirstOrDefault(p => p.Name == "name")?.Value)
                        || stems.Any(st => st != null && o.Id != null
                                           && st.StartsWith(o.DisplayName, StringComparison.Ordinal)
                                           && o.ClassName is "hkbExpressionDataArray"
                                               or "hkbBoneIndexArray" or "hkbEventRangeDataArray"))
            .Where(o => !referenced.Contains(o.Id))
            .ToList();

        Check("every data/ sidecar is attached to the member it belongs to",
            orphaned.Count == 0,
            $"{stems.Count} in data/, {orphaned.Count} left unreferenced"
            + (orphaned.Count == 0 ? "" : " — "
                + string.Join("; ", orphaned.Take(3).Select(o => $"{o.ClassName} {o.DisplayName}"))));
    }

    // -- what the import leaves unreachable ----------------------------------
    // The same question an .hkx save asks: can the root reach this object. An
    // import that wires a new object up wrongly shows here as a jump in the
    // count, so it is worth having on the record next to the object total.
    {
        var report = new GraphDoctor(manager, new List<string>()).Run();
        var byClass = report.Issues
            .Where(i => i.Category == ValidationIssue.CategoryPruned)
            .GroupBy(i => i.ObjectClass)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} x{g.Count()}")
            .Take(5);
        Note("objects the root can't reach, which an .hkx save would drop",
            $"{report.PrunedCount} of {objects.Count} — {string.Join(", ", byClass)}");
    }

    // -- the end-to-end question --------------------------------------------
    var stem = Path.GetFileNameWithoutExtension(folder.TrimEnd('\\', '/'));
    var dir = outDir ?? Path.Combine(Path.GetTempPath(), "hkx-yaml-import");
    Directory.CreateDirectory(dir);
    var xmlPath = Path.Combine(dir, stem + ".xml");
    var hkxPath = Path.Combine(dir, stem + ".hkx");

    using (var writer = new StreamWriter(xmlPath, false, Encoding.UTF8))
        HkXml.Write(packfile, writer);
    Check("the graph saves as XML", File.Exists(xmlPath),
        $"{new FileInfo(xmlPath).Length / 1024} KB");

    try
    {
        new HkxConversionService().XmlToHkxAsync(xmlPath, hkxPath).GetAwaiter().GetResult();
        Check("and converts to .hkx", true, $"{new FileInfo(hkxPath).Length / 1024} KB");
    }
    catch (Exception ex)
    {
        // Worth reading rather than counting. The deserializer reports the first
        // param it can't make sense of, so this line is the front of the queue:
        // each fix moves it, and a "reference symbol" made of run-together values
        // is an inline array sitting in a slot that wants a #ref.
        Note("the conversion still stops", $"{ex.GetType().Name}: {Trim(ex.Message, 160)}");
    }
    if (outDir == null) { TryDelete(xmlPath); TryDelete(hkxPath); }
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} check(s) FAILED.");
return failed == 0 ? 0 : 1;

// A param's #ref tokens. Read through the Value getter, which prefers the
// resolved-ref cache over the text, so this sees what a save would write.
static IEnumerable<string> Refs(HkParam p)
{
    var value = p.Value ?? "";
    if (!value.Contains('#')) yield break;
    foreach (var token in value.Split(new[] { ' ', '\t', '\r', '\n' },
                 StringSplitOptions.RemoveEmptyEntries))
        if (token.StartsWith("#", StringComparison.Ordinal))
            yield return token;
}

static string Trim(string s, int n) =>
    s.Length <= n ? s : s.Substring(0, n) + "…";

static void TryDelete(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
}
