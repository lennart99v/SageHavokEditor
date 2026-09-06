using HKX2;
using SageHavokEditor.Core.Skeletons;

// Which skeleton actually orders an hkbBoneWeightArray, and does the YAML's bone
// names land on it.
//
//   dotnet run --project tools/hkx-bone-weights -- <character assets folder>
//                                                  [--yaml <behaviour unit folder>]
//
// A blender child's boneWeights are a flat float array indexed by the animation
// skeleton's bone order, and the source writes them by name. So an import needs
// the character project — which is the whole reason this is a separate question
// from every other YAML fix.
//
// The registry the editor already has reads bone order from skeleton.nif. That
// is the wrong authority in principle: the array is indexed by hkaSkeleton, not
// by the mesh. Whether it is also wrong in practice is what this measures,
// because "the same order anyway" and "close enough" are different claims.

var failed = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}

string? yamlUnit = null;
{
    int at = Array.IndexOf(args, "--yaml");
    if (at >= 0)
    {
        if (at + 1 < args.Length) yamlUnit = args[at + 1];
        args = args.Where((_, i) => i != at && i != at + 1).ToArray();
    }
}

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hkx-bone-weights <character assets folder> [--yaml <unit folder>]");
    return 2;
}

var assets = args[0];
var hkxPath = Path.Combine(assets, "skeleton.hkx");
var nifPath = Path.Combine(assets, "skeleton.nif");

Console.WriteLine();
Console.WriteLine("== the two orderings ==");

List<string> fromHkx;
try { fromHkx = ReadHkxSkeleton(hkxPath); }
catch (Exception ex) { Check("skeleton.hkx reads", false, ex.Message); return 1; }
Check("skeleton.hkx reads", fromHkx.Count > 0, $"{fromHkx.Count} bones, first {fromHkx.FirstOrDefault()}");

List<string> fromNif = new();
if (File.Exists(nifPath))
{
    try { fromNif = NifSkeletonReader.ReadBoneOrder(nifPath); }
    catch (Exception ex) { Console.WriteLine($"          -> skeleton.nif failed: {ex.Message}"); }
    Console.WriteLine($"          -> skeleton.nif gives {fromNif.Count} bones");

    var common = Math.Min(fromHkx.Count, fromNif.Count);
    var mismatch = Enumerable.Range(0, common).Where(i => fromHkx[i] != fromNif[i]).ToList();
    Console.WriteLine(mismatch.Count == 0 && fromHkx.Count == fromNif.Count
        ? "          -> the two orderings are identical"
        : $"          -> they differ: {fromHkx.Count} vs {fromNif.Count} bones, "
          + $"{mismatch.Count} of the first {common} at different indices"
          + (mismatch.Count == 0 ? "" : $", first at {mismatch[0]} "
              + $"({fromHkx[mismatch[0]]} vs {fromNif[mismatch[0]]})"));
}
else
{
    Console.WriteLine("          -> no skeleton.nif beside it");
}

// -- do the YAML's names land on it? --------------------------------------
if (yamlUnit != null)
{
    Console.WriteLine();
    Console.WriteLine("== the names the source writes ==");

    if (!Directory.Exists(yamlUnit))
    {
        Check("the unit folder is there", false, yamlUnit);
    }
    else
    {
        var named = new List<string>();
        foreach (var file in Directory.EnumerateFiles(yamlUnit, "*.yaml", SearchOption.AllDirectories))
        {
            bool inNamed = false;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.TrimEnd();
                var trimmed = line.TrimStart();
                if (trimmed == "named:") { inNamed = true; continue; }
                if (!inNamed) continue;
                if (!trimmed.StartsWith("\"", StringComparison.Ordinal)) { inNamed = false; continue; }
                var end = trimmed.IndexOf('"', 1);
                if (end > 1) named.Add(trimmed.Substring(1, end - 1));
            }
        }

        var distinct = named.Distinct(StringComparer.Ordinal).ToList();
        Console.WriteLine($"  {named.Count} weighted bones written, {distinct.Count} distinct names");

        var hkxSet = new HashSet<string>(fromHkx, StringComparer.OrdinalIgnoreCase);
        var missingHkx = distinct.Where(n => !hkxSet.Contains(n)).ToList();
        Check("every name in the source is a bone of skeleton.hkx",
            missingHkx.Count == 0,
            missingHkx.Count == 0
                ? $"{distinct.Count} names"
                : $"{missingHkx.Count} missing — {string.Join(", ", missingHkx.Take(5))}");

        if (fromNif.Count > 0)
        {
            var nifSet = new HashSet<string>(fromNif, StringComparer.OrdinalIgnoreCase);
            var missingNif = distinct.Where(n => !nifSet.Contains(n)).ToList();
            Console.WriteLine($"          -> against skeleton.nif: {missingNif.Count} of "
                              + $"{distinct.Count} names missing"
                              + (missingNif.Count == 0 ? "" : $" — {string.Join(", ", missingNif.Take(5))}"));
        }
    }
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} check(s) FAILED.");
return failed == 0 ? 0 : 1;

static List<string> ReadHkxSkeleton(string path)
{
    using var fs = File.OpenRead(path);
    var des = new PackFileDeserializer();
    var root = (hkRootLevelContainer)des.Deserialize(new BinaryReaderEx(fs));

    var container = root.m_namedVariants
        .Select(v => v?.m_variant)
        .OfType<hkaAnimationContainer>()
        .FirstOrDefault();
    var skeleton = container?.m_skeletons.FirstOrDefault();
    if (skeleton == null) throw new InvalidDataException("no hkaSkeleton in " + path);
    return skeleton.m_bones.Select(b => b.m_name).ToList();
}
