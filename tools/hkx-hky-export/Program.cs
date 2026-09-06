// hkx-hky-export — does an edit made in the editor survive going home?
//
// Import a Community Behaviors unit, export it back out, import the result, and
// compare the two graphs. A round trip is the only test worth much here: reading
// the emitted YAML proves it looks right, and this domain's whole failure mode is
// output that looks right and binds to nothing.
//
//   dotnet run --project tools/hkx-hky-export -- <unit.hkx folder> [-o <outdir>]

using System.Globalization;
using SageHavokEditor.Core;
using SageHavokEditor.Models;

if (args.Length < 1)
{
    Console.WriteLine("usage: hkx-hky-export <unit.hkx folder> [-o <outdir>]");
    return 2;
}

string source = args[0].TrimEnd('\\', '/');
string outDir = Array.IndexOf(args, "-o") is var i && i >= 0 && i + 1 < args.Length
    ? args[i + 1]
    : Path.Combine(Path.GetTempPath(), "hky-export", Path.GetFileName(source));

int pass = 0, fail = 0;
void Check(bool ok, string what, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    if (ok) pass++; else fail++;
}

Console.WriteLine($"== {Path.GetFileName(source)} ==");

// ── 1. in ────────────────────────────────────────────────────────────────────
var m1 = new HavokManager();
var imp1 = new YamlBehaviorImporter();
imp1.Import(source, m1);
Console.WriteLine($"  imported {m1.ObjectMap.Count} objects");

// ── 2. out ───────────────────────────────────────────────────────────────────
if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
var exporter = new YamlBehaviorExporter();
var res = exporter.Export(m1, outDir);
Console.WriteLine($"  exported {res.Nodes} nodes + {res.Sidecars} data files "
                  + $"({res.Flattened} flattened into their owners) → {outDir}");

// ── 3. and back ──────────────────────────────────────────────────────────────
var m2 = new HavokManager();
var imp2 = new YamlBehaviorImporter();
imp2.Import(outDir, m2);
Console.WriteLine($"  re-imported {m2.ObjectMap.Count} objects");

// ── 4. compare ───────────────────────────────────────────────────────────────
static Dictionary<string, int> ClassCensus(HavokManager m)
{
    var d = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var o in m.ObjectMap.Values)
    {
        var c = o.ClassName ?? "";
        d[c] = d.TryGetValue(c, out var n) ? n + 1 : 1;
    }
    return d;
}

static List<string> NamesOf(HavokManager m, string cls, string param)
{
    var o = m.ObjectMap.Values.FirstOrDefault(x => x.ClassName == cls);
    return o?.Params.FirstOrDefault(p => p.Name == param)?.Strings ?? new List<string>();
}

var c1 = ClassCensus(m1);
var c2 = ClassCensus(m2);

// The graph-data trio and the root container are rebuilt by the importer, and
// the flattened wrappers are rebuilt from the inline form, so a census that
// matches on the *node* classes is the real question.
var interesting = c1.Keys.Union(c2.Keys)
    .Where(k => k.Length > 0)
    .OrderBy(k => k, StringComparer.Ordinal)
    .ToList();

var drift = new List<string>();
foreach (var k in interesting)
{
    c1.TryGetValue(k, out var a);
    c2.TryGetValue(k, out var b);
    if (a != b) drift.Add($"{k} {a}→{b}");
}

Check(drift.Count == 0, "every class comes back with the same object count",
      drift.Count == 0 ? $"{interesting.Count} classes" : string.Join("; ", drift.Take(6)));

var ev1 = NamesOf(m1, "hkbBehaviorGraphStringData", "eventNames");
var ev2 = NamesOf(m2, "hkbBehaviorGraphStringData", "eventNames");
Check(ev1.SequenceEqual(ev2), "the event table survives, in order",
      $"{ev1.Count} → {ev2.Count}");

var va1 = NamesOf(m1, "hkbBehaviorGraphStringData", "variableNames");
var va2 = NamesOf(m2, "hkbBehaviorGraphStringData", "variableNames");
Check(va1.SequenceEqual(va2), "the variable table survives, in order",
      $"{va1.Count} → {va2.Count}");

// Names are not identity, but a name that vanished is a node that vanished.
static List<string> NodeNames(HavokManager m) =>
    m.ObjectMap.Values
        .Select(o => o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "")
        .Where(s => s.Length > 0)
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

var n1 = NodeNames(m1);
var n2 = NodeNames(m2);
var lost = n1.Except(n2).ToList();
var gained = n2.Except(n1).ToList();
Check(lost.Count == 0 && gained.Count == 0, "no node name is lost or invented",
      lost.Count + gained.Count == 0
          ? $"{n1.Count} named objects"
          : $"-{lost.Count} +{gained.Count}: {string.Join(", ", lost.Take(3).Concat(gained.Take(3)))}");

Console.WriteLine();
Console.WriteLine(fail == 0 ? "all checks passed" : $"{fail} check(s) failed");
return fail == 0 ? 0 : 1;
