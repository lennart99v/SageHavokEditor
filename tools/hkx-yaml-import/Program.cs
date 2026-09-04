using System.Text;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Services;
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
    if (properArrays == declared)
        Check("every declared trigger list became an hkbClipTriggerArray", true,
            $"{properArrays} of {declared}");
    else
        Note("a clip's triggers stay inline instead of becoming an hkbClipTriggerArray",
            $"{properArrays} of {declared} clips");

    // -- the name-keyed index fields ----------------------------------------
    var stillNames = new List<string>();
    var resolvedFields = 0;
    foreach (var obj in objects)
        foreach (var p in obj.Params)
        {
            if (!eventNameFields.Contains(p.Name) && p.Name != variableNameField) continue;
            if (int.TryParse(p.Value, out _)) resolvedFields++;
            else stillNames.Add($"{obj.ClassName}.{p.Name} = {p.Value}");
        }
    if (stillNames.Count == 0)
        Check("the name-keyed index fields resolved to indices", true, $"{resolvedFields} resolved");
    else
        Note("name-keyed index fields arrive as names and stay that way",
            $"{resolvedFields} resolved, {stillNames.Count} still names — "
            + string.Join("; ", stillNames.Take(3)));

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
