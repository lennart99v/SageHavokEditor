using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using HKX2;
using SysType = System.Type;

// Recovers hkxcmd's #NNNN object id for every object in a binary .hkx by walking
// the deserialized graph and the tagfile in lockstep.
//
// Havok packfiles don't store the #NNNN names — the converter invents them, and
// ours disagrees with the numbering Nemesis/Pandora patches are keyed to (see
// ROADMAP, "hkxcmd-compatible object numbering for XML export"). Rather than
// reproduce hkxcmd's traversal, this recovers the correspondence structurally:
// the graph shape is identical on both sides, so pairing (binary object, tagfile
// element) from the two roots and descending through matching fields hands every
// object its id.
//
// Fields are paired by NAME (HKX2's m_foo <-> <hkparam name="foo">), and only the
// slots *within* one field positionally. Cassie's C++ idalign walks both sides
// purely positionally; matching on the name first means a SERIALIZE_IGNORED
// member — which the tagfile writes as a comment and HKX2 still carries as a
// property — can't desync the walk, and a field that does mismatch is reported
// against a field name instead of an offset.
//
//   dotnet run --project tools/hkx-idalign -- <file.hkx> <reference.xml> [-o map.csv]
//
// The binary and the tagfile must be the SAME extraction: convert the very file
// you pass in. A one-object drift desyncs the walk from that point down.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: hkx-idalign <file.hkx> <hkxcmd-reference.xml> [-o map.csv]");
    return 1;
}

var hkxPath = args[0];
var xmlPath = args[1];
var mapPath = Array.IndexOf(args, "-o") is var oi && oi >= 0 && oi + 1 < args.Length ? args[oi + 1] : null;

// ---------------------------------------------------------------- the tagfile

var doc = XDocument.Load(xmlPath, LoadOptions.None);
var packfile = doc.Root ?? throw new Exception("no root element in the reference XML");
var topLevel = packfile.Attribute("toplevelobject")?.Value
               ?? throw new Exception("reference XML has no toplevelobject attribute");

var byId = new Dictionary<string, XElement>();
foreach (var e in packfile.Descendants("hkobject"))
{
    var name = e.Attribute("name")?.Value;
    if (name is not null) byId[name] = e;
}
Console.WriteLine($"tagfile:  {byId.Count} named objects, root {topLevel}, " +
                  $"{packfile.Descendants("hkobject").Count() - byId.Count} inline");

// ----------------------------------------------------------------- the binary

using var fs = File.OpenRead(hkxPath);
var des = new PackFileDeserializer();
IHavokObject root;
try
{
    root = des.Deserialize(new BinaryReaderEx(fs));
}
catch (Exception ex)
{
    // A .hkx that is really XML under a binary extension is a common sample-set
    // trap, and the deserializer's assert doesn't say so.
    fs.Position = 0;
    var head = new byte[5];
    _ = fs.Read(head, 0, head.Length);
    var looksXml = System.Text.Encoding.ASCII.GetString(head).StartsWith("<?xml", StringComparison.Ordinal);
    Console.Error.WriteLine(looksXml
        ? $"{hkxPath} is XML, not a binary packfile — pass the binary this tagfile was converted from."
        : $"{hkxPath}: {ex.Message}");
    return 1;
}

// The deserializer's offset map is every object the packfile gave a virtual
// fixup — i.e. exactly the set the tagfile names. Inline structs aren't in it.
var offsetOf = new Dictionary<IHavokObject, uint>(RefEq.Instance);
var offsetField = typeof(PackFileDeserializer)
    .GetField("_deserializedObjects", BindingFlags.NonPublic | BindingFlags.Instance);
if (offsetField?.GetValue(des) is Dictionary<uint, IHavokObject> objMap)
    foreach (var (off, o) in objMap) offsetOf[o] = off;
Console.WriteLine($"binary:   {offsetOf.Count} packfile objects, root {root.GetType().Name}");

// -------------------------------------------------------------------- the walk

var ids = new Dictionary<IHavokObject, string>(RefEq.Instance);   // object -> "#NNNN"
var owner = new Dictionary<string, IHavokObject>();               // "#NNNN" -> object
var visited = new HashSet<IHavokObject>(RefEq.Instance);
var problems = new List<string>();
var corroborated = 0;
var memberCache = new Dictionary<SysType, List<PropertyInfo>>();

Assign(root, topLevel, "<root>");
Descend(root, byId[topLevel], "<root>");

// ------------------------------------------------------------------ the report

var mapped = ids.Count;
var missing = byId.Keys.Where(id => !owner.ContainsKey(id)).ToList();
var unmapped = offsetOf.Keys.Where(o => !ids.ContainsKey(o)).ToList();

Console.WriteLine();
Console.WriteLine($"mapped:   {mapped}/{byId.Count} tagfile ids  ({mapped}/{offsetOf.Count} packfile objects)");
Console.WriteLine($"scalars:  {corroborated} field values cross-checked against the pairing");
Console.WriteLine($"problems: {problems.Count}");

foreach (var p in problems.Take(20)) Console.WriteLine($"  {p}");
if (problems.Count > 20) Console.WriteLine($"  … and {problems.Count - 20} more");

if (missing.Count > 0)
{
    Console.WriteLine($"\ntagfile ids never reached ({missing.Count}):");
    foreach (var id in missing.Take(15))
        Console.WriteLine($"  {id} {byId[id].Attribute("class")?.Value}");
    if (missing.Count > 15) Console.WriteLine($"  … and {missing.Count - 15} more");
}

if (unmapped.Count > 0)
{
    Console.WriteLine($"\npackfile objects with no id ({unmapped.Count}):");
    foreach (var o in unmapped.Take(15))
        Console.WriteLine($"  0x{offsetOf[o]:X} {o.GetType().Name}");
    if (unmapped.Count > 15) Console.WriteLine($"  … and {unmapped.Count - 15} more");
}

if (mapPath is not null)
{
    var rows = ids.Where(kv => offsetOf.ContainsKey(kv.Key))
                  .Select(kv => (Id: kv.Value, Offset: offsetOf[kv.Key], Class: kv.Key.GetType().Name))
                  .OrderBy(r => r.Id, StringComparer.Ordinal);
    using var w = new StreamWriter(mapPath);
    w.WriteLine("id,offset,class");
    foreach (var r in rows) w.WriteLine($"{r.Id},0x{r.Offset:X},{r.Class}");
    Console.WriteLine($"\nwrote {mapPath}");
}

var clean = problems.Count == 0 && missing.Count == 0 && unmapped.Count == 0;
Console.WriteLine($"\n{(clean ? "OK — every object aligned" : "INCOMPLETE — see above")}");
return clean ? 0 : 1;

// ---------------------------------------------------------------------- pieces

void Assign(IHavokObject o, string id, string path)
{
    var cls = byId.TryGetValue(id, out var e) ? e.Attribute("class")?.Value : null;
    if (cls is not null && cls != o.GetType().Name)
        problems.Add($"{path}: {id} is {cls} in the tagfile, {o.GetType().Name} in the binary");

    if (ids.TryGetValue(o, out var had) && had != id)
        problems.Add($"{path}: {o.GetType().Name} reached as both {had} and {id}");
    else if (owner.TryGetValue(id, out var prev) && !ReferenceEquals(prev, o))
        problems.Add($"{path}: {id} claimed by two different objects");
    else { ids[o] = id; owner[id] = o; }
}

// xe describes bin: either its named <hkobject> or the inline one written in place.
void Descend(IHavokObject bin, XElement xe, string path)
{
    if (!visited.Add(bin)) return;

    // The tagfile says which members it left out: "<!-- cachedBindables
    // SERIALIZE_IGNORED -->". HKX2 still carries those as properties (an inline
    // struct member is never null), so take the file's word for it rather than
    // reporting a divergence that isn't one.
    var ignored = xe.Nodes().OfType<XComment>()
                    .Select(c => c.Value.Trim())
                    .Where(v => v.EndsWith("SERIALIZE_IGNORED", StringComparison.Ordinal))
                    .Select(v => v.Split(' ')[0])
                    .ToHashSet(StringComparer.Ordinal);

    foreach (var prop in Members(bin.GetType()))
    {
        var kids = RefSlots(prop, bin);
        if (kids is null) { Corroborate(prop, bin, xe, path); continue; }

        var field = prop.Name[2..];                       // m_generator -> generator
        var param = xe.Elements("hkparam")
                      .FirstOrDefault(p => p.Attribute("name")?.Value == field);
        if (param is null)
        {
            if (ignored.Contains(field)) continue;
            if (kids.Any(k => k is not null))
                problems.Add($"{path}.{field}: {kids.Count(k => k is not null)} object(s) in the binary, no hkparam in the tagfile");
            continue;
        }

        var slots = Slots(param);
        if (slots.Count != kids.Count)
        {
            problems.Add($"{path}.{field}: {kids.Count} in the binary, {slots.Count} in the tagfile");
            continue;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            var here = kids.Count == 1 ? $"{path}.{field}" : $"{path}.{field}[{i}]";
            var (kind, id, inline) = slots[i];
            var kid = kids[i];

            if (kind == Kind.Null)
            {
                if (kid is not null) problems.Add($"{here}: {kid.GetType().Name} in the binary, null in the tagfile");
            }
            else if (kid is null)
            {
                problems.Add($"{here}: null in the binary, {(kind == Kind.Ref ? id : "an inline object")} in the tagfile");
            }
            else if (kind == Kind.Inline)
            {
                Descend(kid, inline!, here);              // inline structs carry no id
            }
            else if (!byId.TryGetValue(id!, out var target))
            {
                problems.Add($"{here}: tagfile references {id}, which the file never defines");
            }
            else
            {
                Assign(kid, id!, here);
                Descend(kid, target, id!);
            }
        }
    }
}

// Independent evidence that a pairing is the RIGHT pairing, not merely a
// consistent one: the walk never reads a scalar, so comparing the scalars of two
// objects it paired is a check on the pairing itself. Floats are skipped (the
// tagfile's decimal text isn't the binary value), as are enum and flag words,
// which are written by name.
void Corroborate(PropertyInfo prop, IHavokObject bin, XElement xe, string path)
{
    var field = prop.Name[2..];
    var param = xe.Elements("hkparam").FirstOrDefault(p => p.Attribute("name")?.Value == field);
    if (param is null) return;

    var v = Value(prop, bin);
    switch (v)
    {
        case string s:
            Check(param.Value.Trim() == s.Trim(), $"{path}.{field}: binary '{s}', tagfile '{param.Value.Trim()}'");
            break;

        case bool b:
            Check(param.Value.Trim() == (b ? "true" : "false"), $"{path}.{field}: binary {b}, tagfile '{param.Value.Trim()}'");
            break;

        case sbyte or byte or short or ushort or int or uint or long:
            var text = param.Value.Trim();
            if (long.TryParse(text, out var n))            // enums/flags are words, skip those
                Check(n == Convert.ToInt64(v), $"{path}.{field}: binary {v}, tagfile {text}");
            break;

        case IEnumerable<string> strs:
            var xs = param.Elements("hkcstring").Select(e => e.Value).ToList();
            var bs = strs.ToList();
            if (xs.Count != bs.Count)
            {
                Check(false, $"{path}.{field}: {bs.Count} strings in the binary, {xs.Count} in the tagfile");
                break;
            }
            for (var i = 0; i < xs.Count; i++)
                Check(xs[i] == (bs[i] ?? ""), $"{path}.{field}[{i}]: binary '{bs[i]}', tagfile '{xs[i]}'");
            break;
    }
}

void Check(bool ok, string complaint)
{
    corroborated++;
    if (!ok) problems.Add(complaint);
}

// The object-valued slots of one field, in read order, nulls kept — or null when
// the field holds no objects at all. Read order is declaration order: the
// generated classes emit their properties in the same sequence their Read() body
// consumes them, base class first, which is the order the tagfile writes too.
List<IHavokObject?>? RefSlots(PropertyInfo prop, IHavokObject o)
{
    var t = prop.PropertyType;

    if (typeof(IHavokObject).IsAssignableFrom(t))
        return new List<IHavokObject?> { Value(prop, o) as IHavokObject };

    if (t.IsGenericType && t.GetGenericArguments() is [var elem]
        && typeof(IHavokObject).IsAssignableFrom(elem)
        && typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
    {
        var list = new List<IHavokObject?>();
        if (Value(prop, o) is System.Collections.IEnumerable seq)
            foreach (var x in seq) list.Add(x as IHavokObject);
        return list;
    }

    return null;
}

object? Value(PropertyInfo prop, object o)
{
    try { return prop.GetValue(o); } catch { return null; }
}

// What one hkparam holds, in document order: inline <hkobject> children if it has
// any, otherwise its #NNNN / null tokens.
List<(Kind Kind, string? Id, XElement? Inline)> Slots(XElement param)
{
    var inline = param.Elements("hkobject").ToList();
    if (inline.Count > 0)
        return inline.Select(e => (Kind.Inline, (string?)null, (XElement?)e)).ToList();

    var slots = new List<(Kind, string?, XElement?)>();
    foreach (var tok in param.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
    {
        if (tok.StartsWith('#')) slots.Add((Kind.Ref, tok, null));
        else if (tok == "null") slots.Add((Kind.Null, null, null));
        else slots.Add((Kind.Other, tok, null));          // count mismatch will flag it
    }
    return slots;
}

List<PropertyInfo> Members(SysType t)
{
    if (memberCache.TryGetValue(t, out var cached)) return cached;

    var chain = new List<SysType>();
    for (var c = t; c is not null && c != typeof(object); c = c.BaseType) chain.Add(c);
    chain.Reverse();                                       // base class fields are read first

    var props = new List<PropertyInfo>();
    foreach (var c in chain)
        props.AddRange(c.GetProperties(BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(p => p.GetIndexParameters().Length == 0 && p.Name.StartsWith("m_")));

    memberCache[t] = props;
    return props;
}

enum Kind { Ref, Null, Inline, Other }

// HKX2's Equals is content-based, so two distinct objects with the same contents
// compare equal. Every map here has to key on identity instead.
sealed class RefEq : IEqualityComparer<IHavokObject>
{
    public static readonly RefEq Instance = new();
    public bool Equals(IHavokObject? a, IHavokObject? b) => ReferenceEquals(a, b);
    public int GetHashCode(IHavokObject o) => RuntimeHelpers.GetHashCode(o);
}
