using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using HKX2;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Services;
using SageHavokEditor.Models;
using Type = System.Type;   // HKX2 declares its own Type enum

// Checks the array-kind metadata behind the property editor's "+ Add element".
//
// An array param that is empty in the loaded file carries no evidence of what
// its elements look like — an empty inline-struct array and an empty ref array
// are the same bytes — so the affordance now comes from HKX2's declared shape
// instead of from the data. Three things have to hold, and none is provable by
// reading the code:
//
//   1. the IL read of HKX2's XML writer agrees with the class metadata Havok's
//      own exporter left in the autogen comments, on every array member;
//   2. every inline-struct element class can actually produce a default element,
//      so the button can't appear and then dead-end on "no template";
//   3. on a real file, the arrays that gain the button are the ones that were
//      empty at load — and no ref array gains it.
//
//   dotnet run --project tools/hkx-array-kinds -- [behavior.hkx|behavior.xml]
//                                                 [--autogen <HKX2/Autogen dir>]
//
// Both arguments are optional: without --autogen the Autogen folder is found by
// walking up from the build output, and without a file the third check is
// skipped and says so.

var failed = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}

string? autogenDir = null;
{
    int at = Array.IndexOf(args, "--autogen");
    if (at >= 0)
    {
        if (at + 1 < args.Length) autogenDir = args[at + 1];
        args = args.Where((_, i) => i != at && i != at + 1).ToArray();
    }
}
autogenDir ??= FindAutogen();

var havokTypes = typeof(hkbModifier).Assembly.GetTypes()
    .Where(t => t is { IsClass: true } && typeof(IHavokObject).IsAssignableFrom(t))
    .ToList();

// -- 1. the IL read against Havok's own class metadata --------------------
// The autogen comments are a separate transcription of the same fact - the
// exporter's TYPE_ARRAY/TYPE_POINTER pair, copied in when the classes were
// generated. They are not compiled into anything, so they cannot have been
// derived from the IL, which makes them a real second opinion.
{
    Console.WriteLine();
    Console.WriteLine("== IL read of WriteXml agrees with the autogen class metadata ==");

    if (autogenDir == null || !Directory.Exists(autogenDir))
    {
        Check("found the Autogen sources", false, "pass --autogen <dir>");
    }
    else
    {
        var fromComments = new Dictionary<(string Class, string Param), HkArrayKind>();
        var ignored = new List<(string Class, string Param)>();
        var member = new Regex(
            @"// m_(?<p>\w+) m_class: (?<c>\w+) Type\.TYPE_ARRAY Type\.TYPE_(?<k>POINTER|STRUCT)"
            + @".*flags: (?<f>\S+)");
        var classDecl = new Regex(@"public (?:partial )?class (?<n>\w+)");

        foreach (var file in Directory.EnumerateFiles(autogenDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            var cls = classDecl.Match(text);
            if (!cls.Success) continue;
            foreach (Match m in member.Matches(text))
            {
                var key = (cls.Groups["n"].Value, m.Groups["p"].Value);
                // SERIALIZE_IGNORED members are never written to XML, so they are
                // never a param in the editor and WriteXml has no array call for
                // them. Expecting the IL read to find one is expecting the wrong
                // thing - it cost this check a red on the first run.
                if (m.Groups["f"].Value.Contains("SERIALIZE_IGNORED")) { ignored.Add(key); continue; }
                fromComments[key] = m.Groups["k"].Value == "POINTER"
                    ? HkArrayKind.Pointer
                    : HkArrayKind.InlineStruct;
            }
        }

        Check("read the class metadata", fromComments.Count > 100,
            $"{fromComments.Count} serialized array members, {ignored.Count} SERIALIZE_IGNORED");

        var disagreed = new List<string>();
        var unseen = new List<string>();
        foreach (var ((cls, param), want) in fromComments)
        {
            var type = havokTypes.FirstOrDefault(t => t.Name == cls);
            if (type == null) { unseen.Add($"{cls} (no such type)"); continue; }
            var got = HavokArrayKinds.ForType(type).GetValueOrDefault(param, HkArrayKind.None);
            if (got == HkArrayKind.None) unseen.Add($"{cls}.{param}");
            else if (got != want) disagreed.Add($"{cls}.{param}: IL says {got}, metadata says {want}");
        }

        Check("no member is classified differently by the two sources",
            disagreed.Count == 0, disagreed.Count == 0 ? null : string.Join("; ", disagreed.Take(5)));
        foreach (var u in unseen) Console.WriteLine($"          -> not found by the IL read: {u}");
        Check("every documented array member is found by the IL read", unseen.Count == 0,
            $"{unseen.Count} missed");

        var leaked = ignored
            .Where(k => havokTypes.FirstOrDefault(t => t.Name == k.Class) is { } t
                        && HavokArrayKinds.ForType(t).ContainsKey(k.Param))
            .ToList();
        Check("and nothing SERIALIZE_IGNORED is classified as an editable array",
            leaked.Count == 0,
            leaked.Count == 0 ? null : string.Join(", ", leaked.Select(k => $"{k.Class}.{k.Param}")));

        // What the obvious inference would have cost. Recorded rather than used:
        // "pointer arrays hold hkReferencedObjects" is nearly right, and nearly
        // right is what the roadmap keeps warning about.
        var wrong = new List<string>();
        foreach (var ((cls, param), want) in fromComments)
        {
            var type = havokTypes.FirstOrDefault(t => t.Name == cls);
            var prop = type?.GetProperty("m_" + param,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var elem = prop?.PropertyType is { IsGenericType: true } pt
                ? pt.GetGenericArguments().FirstOrDefault()
                : null;
            if (elem == null) continue;
            var guess = typeof(hkReferencedObject).IsAssignableFrom(elem)
                ? HkArrayKind.Pointer
                : HkArrayKind.InlineStruct;
            if (guess != want) wrong.Add($"{cls}.{param} ({elem.Name})");
        }
        Console.WriteLine($"          -> the hkReferencedObject inference misses "
                          + $"{wrong.Count} of {fromComments.Count}: {string.Join(", ", wrong)}");
    }
}

// -- 2. the classes the button has to build -------------------------------
// "+ Add element" builds a fresh element from ModifierCatalog.CreateDefault and
// only falls back to cloning a sibling. An array that is empty at load has no
// sibling, so for these classes the default is the whole affordance.
{
    Console.WriteLine();
    Console.WriteLine("== every inline-struct element class can produce a default element ==");

    var elementClasses = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var type in havokTypes)
        foreach (var (param, kind) in HavokArrayKinds.ForType(type))
        {
            if (kind != HkArrayKind.InlineStruct) continue;
            var prop = type.GetProperty("m_" + param, BindingFlags.Public | BindingFlags.Instance);
            if (prop?.PropertyType is { IsGenericType: true } pt)
                elementClasses.Add(pt.GetGenericArguments()[0].Name);
        }

    Check("found the element classes", elementClasses.Count > 20,
        $"{elementClasses.Count} classes");

    var noTemplate = new List<string>();
    foreach (var cls in elementClasses)
    {
        HkObject? def = null;
        try { def = ModifierCatalog.CreateDefault(cls); }
        catch (Exception ex) { noTemplate.Add($"{cls} ({ex.GetType().Name})"); continue; }
        if (def == null || def.Params.Count == 0) noTemplate.Add(cls);
    }
    Check("all of them build", noTemplate.Count == 0,
        noTemplate.Count == 0 ? null : string.Join(", ", noTemplate));
}

// -- 3. what changes on a real file ---------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("== on a real file, the button lands on the right params ==");

    if (args.Length < 1)
    {
        Console.WriteLine("  (skipped - pass a behaviour file to run this)");
    }
    else
    {
        var manager = Load(args[0]);
        HavokTypeCatalog.AnnotateAll(manager.ObjectMap.Values);

        int structArrays = 0, emptyStruct = 0, unflagged = 0, pointerArrays = 0, pointerWithButton = 0;
        var examples = new List<string>();

        foreach (var obj in manager.ObjectMap.Values)
            foreach (var p in obj.Params)
            {
                var kind = p.TypeInfo?.ArrayKind ?? HkArrayKind.None;
                if (kind == HkArrayKind.Pointer)
                {
                    pointerArrays++;
                    if (p.IsInlineStructArray) pointerWithButton++;
                }
                if (kind != HkArrayKind.InlineStruct) continue;
                structArrays++;
                if (p.Children.Count != 0) continue;
                emptyStruct++;
                if (examples.Count < 6)
                    examples.Add($"{obj.ClassName}.{p.Name} (numelements={p.NumElements})");
                if (!p.IsInlineStructArray) unflagged++;
            }

        Console.WriteLine($"          -> {emptyStruct} of this file's {structArrays} inline-struct "
                          + $"arrays are empty, against {pointerArrays} ref arrays");
        foreach (var e in examples) Console.WriteLine($"            {e}");

        if (emptyStruct == 0)
            Console.WriteLine("          (nothing to gain the button here - a ragdoll or "
                              + "skeleton file has no empty inline arrays)");
        Check("every one of them is marked", unflagged == 0, $"{unflagged} unmarked");
        Check("no ref array is marked as an inline-struct array",
            pointerWithButton == 0, $"{pointerWithButton} of {pointerArrays}");

        // The button's own path, on a param that had nothing to clone.
        var target = manager.ObjectMap.Values
            .SelectMany(o => o.Params)
            .FirstOrDefault(p => p.TypeInfo?.ArrayKind == HkArrayKind.InlineStruct
                                 && p.Children.Count == 0
                                 && p.TypeInfo.ElementClassName != null);
        if (target != null)
        {
            var def = ModifierCatalog.CreateDefault(target.TypeInfo!.ElementClassName!);
            Check($"a fresh {target.TypeInfo.ElementClassName} element is available for {target.Name}",
                def is { Params.Count: > 0 },
                def == null ? "no default" : $"{def!.Params.Count} params");
        }
    }
}

// -- Result ----------------------------------------------------------------
Console.WriteLine();
Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} check(s) FAILED.");
return failed == 0 ? 0 : 1;

static string? FindAutogen()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "libs", "HKX2Library", "HKX2", "Autogen");
        if (Directory.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

static HavokManager Load(string path)
{
    HkPackfile packfile;
    var xml = new System.Xml.Serialization.XmlSerializer(typeof(HkPackfile));

    if (HkxConversionService.DetectFormat(path) == HkxFormat.HKX)
    {
        using var fs = File.OpenRead(path);
        var des = new PackFileDeserializer();
        var root = (hkRootLevelContainer)des.Deserialize(new BinaryReaderEx(fs));

        using var ms = new MemoryStream();
        new HKX2.XmlSerializer().Serialize(root, des._header, ms);
        ms.Position = 0;
        packfile = (HkPackfile?)xml.Deserialize(ms) ?? throw new InvalidDataException(path);
    }
    else
    {
        using var fs = File.OpenRead(path);
        packfile = (HkPackfile?)xml.Deserialize(fs) ?? throw new InvalidDataException(path);
    }

    var manager = new HavokManager();
    manager.BuildGraph(packfile);
    return manager;
}
