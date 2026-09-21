// hkx-hky-export — does an edit made in the editor survive going home?
//
// Import a Community Behaviors unit, export it back out, import the result, and
// compare the two graphs. A round trip is the only test worth much here: reading
// the emitted YAML proves it looks right, and this domain's whole failure mode is
// output that looks right and binds to nothing.
//
//   dotnet run --project tools/hkx-hky-export -- <unit.hkx folder> [-o <outdir>]

using System.Globalization;
using HKX2;
using SageHavokEditor.Core;
using SageHavokEditor.Models;
using SageHavokEditor.Core.Services;

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
// A folder is YAML source; a file is a graph the editor loaded from a packfile.
// The second is the case that matters most — an edit made on a real .hkx going
// home — and it was the one nothing exercised, which is how a one-element array
// written as a scalar got out.
var fromPackfile = File.Exists(source);

var m1 = new HavokManager();
if (fromPackfile)
{
    var ser = new System.Xml.Serialization.XmlSerializer(typeof(HkPackfile));
    using var fs = new FileStream(source, FileMode.Open, FileAccess.Read);
    var pf = (HkPackfile?)ser.Deserialize(fs) ?? throw new InvalidDataException(source);
    m1.BuildGraph(pf);
}
else
{
    new YamlBehaviorImporter().Import(source, m1);
}
Console.WriteLine($"  loaded {m1.ObjectMap.Count} objects"
                  + (fromPackfile ? "  (from a packfile)" : "  (from YAML source)"));

// ── 2. out ───────────────────────────────────────────────────────────────────
if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
var exporter = new YamlBehaviorExporter();

// Only a behaviour graph has an .hky form. A skeleton, an animation, a project
// or a character file is a reasonable thing to point this at by accident —
// hkxworking_64 is full of them — and it should say so rather than end in a
// stack trace.
if (!m1.ObjectMap.Values.Any(o => o.ClassName == "hkbBehaviorGraph"))
{
    Console.WriteLine("  not a behaviour graph (no hkbBehaviorGraph) — nothing to export");
    return 0;
}

var res = exporter.Export(m1, outDir);
Console.WriteLine($"  exported {res.Nodes} nodes + {res.Sidecars} data files "
                  + $"({res.Flattened} flattened into their owners) → {outDir}");

// ── 3. and back ──────────────────────────────────────────────────────────────
var m2 = new HavokManager();
var imp2 = new YamlBehaviorImporter();
imp2.Import(outDir, m2);
Console.WriteLine($"  re-imported {m2.ObjectMap.Count} objects");

// ── 4. compare ───────────────────────────────────────────────────────────────
// Against what the export is *for*, which is not the whole of m1. The exporter
// writes only what the graph can reach, on the grounds that an .hkx save drops
// the rest anyway — so an object the root cannot reach is one the source itself
// would lose, and holding the round trip to it measures the sample, not the
// code. Vanilla mt_behavior carries 54 such objects: a whole dead IdleChisel
// subtree, two blend effects and a modifier, none of them named by anything.
//
// Comparing against all of m1 is what these checks used to do, and it cost more
// than a wrong verdict — three of them were red on every large unit, and the one
// line among them that meant something (hkbStringEventPayload 282→254, where all
// 282 *were* reachable) read like more of the same noise. It was a real defect,
// and it sat there behind them.
static Dictionary<string, int> ClassCensus(HavokManager m, HashSet<string>? only = null)
{
    var d = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var o in m.ObjectMap.Values)
    {
        if (only != null && (o.Id == null || !only.Contains(o.Id))) continue;
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

var carried = Reachable(m1);
var orphans = m1.ObjectMap.Count - carried.Ids.Count;
if (orphans > 0)
    Console.WriteLine($"  {orphans} object(s) the root can't reach — not exported, "
                    + "and an .hkx save would drop them too");

var c1 = ClassCensus(m1, carried.Ids);
var c2 = ClassCensus(m2);

// The graph-data trio and the root container are rebuilt by the importer, and
// the flattened wrappers are rebuilt from the inline form, so a census that
// matches on the *node* classes is the real question.
var interesting = c1.Keys.Union(c2.Keys)
    .Where(k => k.Length > 0)
    .OrderBy(k => k, StringComparer.Ordinal)
    .ToList();

var Shareable = ShareableSet();
var drift = new List<string>();
foreach (var k in interesting)
{
    c1.TryGetValue(k, out var a);
    c2.TryGetValue(k, out var b);
    if (a == b) continue;
    if (b > a && Shareable.Contains(k) && b - a <= ExtraCopiesFromSharing(m1, k)) continue;
    drift.Add($"{k} {a}→{b}");
}

// Classes her format writes *in place* on the owner rather than pointing at. A
// Havok graph may share one of these between owners — vanilla dragonbehavior
// puts one 'NPC RLegFoot' payload behind two clip triggers, and the modded
// dragonbehavior_paad shares two binding sets, two notify arrays and two
// transition arrays — and an inlined form has no way to say so, so the re-import
// necessarily builds one per site. Her own writer splits them the same way: the
// dragon unit in Skyrim.hky writes that payload string on both triggers.
//
// Tolerated only up to the number of extra copies the sharing actually accounts
// for, counted on the source. One more than that is a duplicate nobody asked for.
static HashSet<string> ShareableSet() => new(StringComparer.Ordinal)
{
    "hkbStringEventPayload", "hkbVariableBindingSet", "hkbExpressionCondition",
    "hkbStateMachineEventPropertyArray", "hkbStateMachineTransitionInfoArray",
    "hkbClipTriggerArray",
};

static int ExtraCopiesFromSharing(HavokManager m, string cls)
{
    var inbound = new Dictionary<string, int>(StringComparer.Ordinal);
    void Walk(HkObject o)
    {
        foreach (var p in o.Params)
        {
            foreach (var tok in (p.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (tok.StartsWith("#") && m.ObjectMap.TryGetValue(tok, out var t) && t.ClassName == cls)
                    inbound[tok] = inbound.TryGetValue(tok, out var n) ? n + 1 : 1;
            foreach (var c in p.Children ?? new List<HkObject>())
                if (string.IsNullOrEmpty(c.Id)) Walk(c);
        }
    }
    foreach (var o in m.ObjectMap.Values) Walk(o);
    return inbound.Values.Where(v => v > 1).Sum(v => v - 1);
}

static List<string> PayloadTexts(HavokManager m) => m.ObjectMap.Values
    .Where(o => o.ClassName == "hkbStringEventPayload")
    .Select(o => o.Params.FirstOrDefault(p => p.Name == "data")?.Value ?? "")
    .Distinct(StringComparer.Ordinal)
    .OrderBy(s => s, StringComparer.Ordinal)
    .ToList();

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
static List<string> NodeNamesIn(HavokManager m, HashSet<string> only) =>
    m.ObjectMap.Values
        .Where(o => o.Id != null && only.Contains(o.Id))
        .Select(o => o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "")
        .Where(s => s.Length > 0)
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

static List<string> NodeNames(HavokManager m) =>
    m.ObjectMap.Values
        .Select(o => o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "")
        .Where(s => s.Length > 0)
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

var n1 = NodeNamesIn(m1, carried.Ids);
var n2 = NodeNames(m2);
var lost = n1.Except(n2).ToList();
var gained = n2.Except(n1).ToList();
Check(lost.Count == 0 && gained.Count == 0, "no node name is lost or invented",
      lost.Count + gained.Count == 0
          ? $"{n1.Count} named objects"
          : $"-{lost.Count} +{gained.Count}: {string.Join(", ", lost.Take(3).Concat(gained.Take(3)))}");

// A name that will not resolve is left as the name, so the value ends up holding
// no #ref at all — which is why "every #ref resolves" can pass on a graph whose
// root reaches almost nothing. This asks the opposite question: is there a slot
// that should hold a reference and holds a bare word instead?
static List<string> Unresolved(HavokManager m, HashSet<string> only)
{
    var bad = new List<string>();
    foreach (var o in m.ObjectMap.Values)
    {
        if (o.Id == null || !only.Contains(o.Id)) continue;
        foreach (var p in o.Params)
        {
            var info = HavokTypeCatalog.Lookup(o.ClassName ?? "", p.Name ?? "");
            if (info?.ElementClassName == null) continue;
            var v = p.Value ?? "";
            // "-1" is Havok's "none" in a slot that also takes a reference, not a
            // name that failed to resolve.
            if (v.Length == 0 || v == "null" || v == "-1") continue;
            foreach (var tok in v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (!tok.StartsWith("#"))
                { bad.Add($"{o.ClassName}.{p.Name} = '{tok}'"); break; }
        }
    }
    return bad;
}

// Reachability, measured on both sides. The .hkx save keeps only what the root
// can reach, so a graph that holds every object and reaches none of them writes
// a file with almost nothing in it.
static (HashSet<string> Ids, string Root) Reachable(HavokManager m)
{
    var top = m.ObjectMap.Values.FirstOrDefault(o => o.ClassName == "hkRootLevelContainer");
    if (top == null) return (new HashSet<string>(), "no hkRootLevelContainer");
    var seen = new HashSet<string>();
    var stack = new Stack<HkObject>();
    stack.Push(top);
    while (stack.Count > 0)
    {
        var o = stack.Pop();
        if (string.IsNullOrEmpty(o.Id) || !seen.Add(o.Id)) continue;
        foreach (var (_, refId) in HkRefWalkShim(o))
            if (m.ObjectMap.TryGetValue(refId, out var t)) stack.Push(t);
    }
    return (seen, top.Id);
}

static IEnumerable<(string, string)> HkRefWalkShim(HkObject obj)
{
    foreach (var p in Params(obj))
        foreach (var tok in (p.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (tok.StartsWith("#")) yield return (p.Name, tok);

    static IEnumerable<HkParam> Params(HkObject o)
    {
        foreach (var p in o.Params)
        {
            yield return p;
            foreach (var c in p.Children)
                if (string.IsNullOrEmpty(c.Id))
                    foreach (var sp in Params(c)) yield return sp;
        }
    }
}

var r2 = Reachable(m2);
Check(r2.Ids.Count >= carried.Ids.Count, "the root still reaches as much of the graph as it did",
      $"{carried.Ids.Count} of {m1.ObjectMap.Count} -> {r2.Ids.Count} of {m2.ObjectMap.Count}");

// Asked as a difference, not as an absolute. Her vanilla dragon unit names 13
// modifiers it never defines — FootIKModifier, BSTweenerModifier_TakeOff_* —
// and a reference that dangled going in is not the export's doing. What would
// be the export's doing is a *new* one.
var stuck1 = Unresolved(m1, carried.Ids);
var stuck2 = Unresolved(m2, r2.Ids);
var fresh = stuck2.Except(stuck1).ToList();
Check(fresh.Count == 0, "no reference slot came back holding a name that resolved before",
      fresh.Count == 0
          ? (stuck1.Count == 0 ? "" : $"{stuck1.Count} dangled in the source too")
          : $"{fresh.Count}: " + string.Join("; ", fresh.Take(4)));

// The check that was missing, and the reason the export could lose every clip
// trigger's event without a word: an object count cannot see inside an inline
// struct. A trigger whose id came back -1 still counts as a trigger, still
// carries its clip's name, and fires nothing in the game.
static (int Total, int Dead, int Payloads) Triggers(HavokManager m)
{
    int total = 0, dead = 0, payloads = 0;
    foreach (var arr in m.ObjectMap.Values.Where(o => o.ClassName == "hkbClipTriggerArray"))
    foreach (var p in arr.Params.Where(p => p.Name == "triggers"))
    foreach (var t in p.Children)
    {
        var ev = t.Params.FirstOrDefault(x => x.Name == "event")?.Children.FirstOrDefault();
        var id = ev?.Params.FirstOrDefault(x => x.Name == "id")?.Value ?? "";
        total++;
        if (id.Length == 0 || id == "-1") dead++;
        if ((ev?.Params.FirstOrDefault(x => x.Name == "payload")?.Value ?? "").StartsWith("#"))
            payloads++;
    }
    return (total, dead, payloads);
}

var t1 = Triggers(m1);
var t2 = Triggers(m2);
Check(t2.Dead <= t1.Dead, "every clip trigger still fires the event it fired",
      $"{t1.Total} triggers, {t1.Dead} firing nothing -> {t2.Total}, {t2.Dead} firing nothing");
Check(t2.Payloads >= t1.Payloads, "and still carries its payload",
      $"{t1.Payloads} -> {t2.Payloads}");

var pay1 = PayloadTexts(m1);
var pay2 = PayloadTexts(m2);
Check(pay1.All(pay2.Contains), "every payload string came back",
      $"{pay1.Count} distinct -> {pay2.Count}"
      + (pay1.All(pay2.Contains) ? "" : ": " + string.Join(", ", pay1.Except(pay2).Take(3))));

Console.WriteLine();
Console.WriteLine(fail == 0 ? "all checks passed" : $"{fail} check(s) failed");
return fail == 0 ? 0 : 1;
