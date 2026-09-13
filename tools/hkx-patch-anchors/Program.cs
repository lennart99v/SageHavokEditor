using System.Reflection;
using System.Xml.Serialization;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Patching;
using SageHavokEditor.Models;

// Does a patch anchor name the object it was made from?
//
//   dotnet run --project tools/hkx-patch-anchors -- <behavior.xml> [...]
//
// A patch says which object it means by an anchor rather than by id, because the
// file it is applied to has different ids from the one it was authored against.
// So the anchor is the whole patch system's correctness: one that resolves to
// the wrong object produces a patch that applies cleanly, converts, and makes an
// actor do something inexplicable with nothing in any log.
//
// The check is a round trip over every object in the file: make the anchor the
// exporter would write, resolve it the way the applier does, and require the
// object that comes back to be the one it started from. Nothing is mutated.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hkx-patch-anchors <behavior.xml> [...]");
    return 1;
}

var failed = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}

// Both are private: the anchor format is internal to the patch layer, and this
// harness is checking that layer rather than a public contract of it.
var makeAnchor = typeof(PatchGenerator).GetMethod("MakeAnchor",
    BindingFlags.NonPublic | BindingFlags.Static)!;
var resolveAnchor = typeof(PatchApplier).GetMethod("ResolveAnchor",
    BindingFlags.NonPublic | BindingFlags.Instance)!;

var ser = new XmlSerializer(typeof(HkPackfile));
int totalObjects = 0, totalWrong = 0, totalUnresolved = 0, totalFiles = 0;

foreach (var path in args)
{
    HavokManager manager;
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
    {
        var pf = (HkPackfile?)ser.Deserialize(fs) ?? throw new InvalidDataException(path);
        manager = new HavokManager();
        manager.BuildGraph(pf);
    }

    var applier = new PatchApplier(manager);
    var result = new ApplyResult();

    string Anchor(HkObject o) => (string)makeAnchor.Invoke(null, new object[] { o.Id, o })!;
    HkObject? Resolve(string a) => (HkObject?)resolveAnchor.Invoke(applier, new object?[] { a, result });

    var wrong = new List<string>();
    var unresolved = new List<string>();
    int named = 0;

    foreach (var obj in manager.ObjectMap.Values)
    {
        var anchor = Anchor(obj);
        if (anchor.StartsWith("id:")) continue;   // says the id outright, nothing to test
        named++;

        var back = Resolve(anchor);
        if (back == null) unresolved.Add($"{obj.Id} '{anchor}'");
        else if (back.Id != obj.Id)
            wrong.Add($"{obj.Id} ({obj.ClassName}) anchored '{anchor}' resolved to {back.Id} ({back.ClassName})");
    }

    Console.WriteLine();
    Console.WriteLine($"== {Path.GetFileName(path)} — {manager.ObjectMap.Count} objects, {named} anchored by content ==");
    Check($"every one of the {named} anchors resolves to the object it was made from",
        wrong.Count == 0, string.Join("; ", wrong.Take(3)));
    Check("and every one of them resolves at all",
        unresolved.Count == 0, string.Join("; ", unresolved.Take(3)));

    // The anchors are class-qualified, and that is the fix: without the class a
    // name picks whichever object the map enumerates first. Measured across the
    // vanilla character behaviours, the troll and a modded dragon, 2,782 of
    // 11,895 named objects share a name with an object of another class.
    var legacyAmbiguous = 0;
    foreach (var group in manager.ObjectMap.Values
                 .Where(o => (o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "").Length > 0)
                 .GroupBy(o => o.Params.First(p => p.Name == "name").Value))
        if (group.Count() > 1) legacyAmbiguous++;

    if (legacyAmbiguous == 0)
    {
        Console.WriteLine("  [SKIP] no name in this file is shared, so it cannot show the difference");
    }
    else
    {
        // The same names, anchored the old way, and what the applier does with
        // them now: still resolved (patches written before the class was recorded
        // have to keep working), but reported rather than resolved by luck.
        var sample = manager.ObjectMap.Values
            .Where(o => (o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "").Length > 0)
            .GroupBy(o => o.Params.First(p => p.Name == "name").Value)
            .First(g => g.Count() > 1);

        var before = result.Warnings.Count;
        var legacy = Resolve("name:" + sample.Key);
        Check($"a legacy unqualified anchor still resolves ('{sample.Key}')", legacy != null);
        Check("but says so, instead of picking one in silence",
            result.Warnings.Count > before,
            result.Warnings.Count > before ? Trim(result.Warnings[^1]) : "no warning raised");

        // And the qualified form picks each of them deliberately.
        var ok = sample.All(o => Resolve($"name:{o.ClassName}:{sample.Key}")?.Id == o.Id);
        Check($"the class picks the right one of the {sample.Count()} that share '{sample.Key}'", ok,
            string.Join(", ", sample.Select(o => $"{o.Id}:{o.ClassName}")));
    }

    Console.WriteLine($"  {legacyAmbiguous} name(s) in this file are shared by more than one object");

    totalObjects += named; totalWrong += wrong.Count; totalUnresolved += unresolved.Count; totalFiles++;
}

Console.WriteLine();
Console.WriteLine($"{totalObjects} anchors over {totalFiles} file(s): "
    + $"{totalWrong} resolved to the wrong object, {totalUnresolved} did not resolve");
Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) FAILED");
return failed == 0 ? 0 : 1;

static string Trim(string s) => s.Length <= 110 ? s : s[..109] + "…";
