using System.Reflection;
using System.Text.RegularExpressions;
using HKX2;
using SysType = System.Type;

// Works out which graph traversal hkxcmd uses to assign #NNNN object IDs, by
// scoring candidate traversals against a reference XML hkxcmd produced.
//
//   dotnet run --project tools/hkx-idorder -- <file.hkx> <hkxcmd-reference.xml>

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: hkx-idorder <file.hkx> <reference.xml>");
    return 1;
}

var xml = File.ReadAllText(args[1]);
var truth = Regex.Matches(xml, "<hkobject name=\"#(\\d+)\" class=\"(\\w+)\"")
    .Select(m => (Id: int.Parse(m.Groups[1].Value), Cls: m.Groups[2].Value))
    .OrderBy(t => t.Id)
    .Select(t => t.Cls)
    .ToList();
var baseId = Regex.Match(xml, "toplevelobject=\"#(\\d+)\"").Groups[1].Value;
// hkxcmd's *document* order, which is a separate pass from its ID assignment.
var docOrder = Regex.Matches(xml, "<hkobject name=\"#(\\d+)\" class=\"(\\w+)\"")
    .Select(m => m.Groups[2].Value).ToList();
Console.WriteLine($"reference: {truth.Count} objects, base #{baseId}, root={truth[0]}");

using var fs = File.OpenRead(args[0]);
var des = new PackFileDeserializer();
var root = (hkRootLevelContainer)des.Deserialize(new BinaryReaderEx(fs));

var named = new HashSet<string>(truth.Distinct());
var memberCache = new Dictionary<SysType, List<PropertyInfo>>();

List<PropertyInfo> Members(SysType t)
{
    if (memberCache.TryGetValue(t, out var cached)) return cached;
    var chain = new List<SysType>();
    for (var c = t; c != null && c != typeof(object); c = c.BaseType) chain.Add(c);
    chain.Reverse();
    var props = new List<PropertyInfo>();
    foreach (var c in chain)
        props.AddRange(c.GetProperties(BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(p => p.GetIndexParameters().Length == 0
                                    && p.Name.StartsWith("m_")));
    memberCache[t] = props;
    return props;
}

List<IHavokObject> Children(IHavokObject o)
{
    var kids = new List<IHavokObject>();
    foreach (var p in Members(o.GetType()))
    {
        object v;
        try { v = p.GetValue(o); } catch { continue; }
        if (v is IHavokObject c) kids.Add(c);
        else if (v is System.Collections.IEnumerable e and not string)
            foreach (var i in e) if (i is IHavokObject ic) kids.Add(ic);
    }
    return kids;
}

List<string> Dfs(bool preOrder, bool reverse)
{
    var seen = new HashSet<IHavokObject>(ReferenceEqualityComparer.Instance);
    var outp = new List<string>();
    void Rec(IHavokObject o)
    {
        if (o is null || !seen.Add(o)) return;
        var emit = named.Contains(o.GetType().Name);
        if (preOrder && emit) outp.Add(o.GetType().Name);
        var kids = Children(o);
        if (reverse) kids.Reverse();
        foreach (var k in kids) Rec(k);
        if (!preOrder && emit) outp.Add(o.GetType().Name);
    }
    Rec(root);
    return outp;
}

List<string> Bfs(bool reverse)
{
    var seen = new HashSet<IHavokObject>(ReferenceEqualityComparer.Instance);
    var q = new Queue<IHavokObject>();
    var outp = new List<string>();
    q.Enqueue(root); seen.Add(root);
    while (q.Count > 0)
    {
        var o = q.Dequeue();
        if (named.Contains(o.GetType().Name)) outp.Add(o.GetType().Name);
        var kids = Children(o);
        if (reverse) kids.Reverse();
        foreach (var k in kids) if (seen.Add(k)) q.Enqueue(k);
    }
    return outp;
}

static int Prefix(List<string> a, List<string> b)
{
    var n = Math.Min(a.Count, b.Count);
    var i = 0;
    while (i < n && a[i] == b[i]) i++;
    return i;
}

// Some traversals put the root last (post-order); hkxcmd numbers it first, so
// also score each candidate with the root hoisted to the front.
var candidates = new (string Name, List<string> Seq)[]
{
    ("pre-order  decl",    Dfs(true,  false)),
    ("pre-order  reverse", Dfs(true,  true)),
    ("post-order decl",    Dfs(false, false)),
    ("post-order reverse", Dfs(false, true)),
    ("bfs        decl",    Bfs(false)),
    ("bfs        reverse", Bfs(true)),
};

Console.WriteLine($"{"traversal",-22}{"root",-6}{"count",7}{"byID",8}{"byDOC",8}   first divergence (vs ID)");
foreach (var (name, seq) in candidates)
{
    foreach (var hoist in new[] { false, true })
    {
        var s = seq;
        if (hoist)
        {
            var idx = seq.FindLastIndex(x => x == "hkRootLevelContainer");
            if (idx <= 0) continue;
            s = new List<string> { seq[idx] };
            s.AddRange(seq.Where((_, i) => i != idx));
        }
        var p = Prefix(truth, s);
        var pd = Prefix(docOrder, s);
        var div = p < truth.Count && p < s.Count ? $"want {truth[p]}, got {s[p]}" : "-";
        Console.WriteLine($"{name,-22}{(hoist ? "first" : "as-is"),-6}{s.Count,7}{p,8}{pd,8}   {div}");
        if (name == "post-order reverse" && !hoist && Environment.GetEnvironmentVariable("HKX_DUMP") == "1")
        {
            Console.WriteLine($"\n{"i",4}  {"byID(truth)",-38}{"document",-38}post-order-reverse");
            for (var i = 0; i < 44; i++)
                Console.WriteLine($"{i,4}  {(i < truth.Count ? truth[i] : ""),-38}" +
                                  $"{(i < docOrder.Count ? docOrder[i] : ""),-38}" +
                                  $"{(i < s.Count ? s[i] : "")}");
            Console.WriteLine();
        }
    }
}
return 0;
