using System.Diagnostics;
using System.Xml.Serialization;
using SageHavokEditor.Core;
using SageHavokEditor.Models;
using SageHavokEditor.UI;

// Why the behaviour tree used to hang on some files and not others, and proof
// that it no longer does.
//
//   dotnet run --project tools/hkx-treebuild-perf -- <behavior.xml> [...]
//
// A behaviour graph is a DAG. BehaviorTreeBuilder used to guard its recursion
// with the path it was currently walking, which stops a cycle but not the
// re-expansion of everything reachable by more than one path — and on vanilla
// 0_master.hkx that is a combinatorial explosion, not a slow loop. This runs
// both: the old path-scoped walk (counting only, with a cap, so a blow-up
// reports a number instead of taking the machine down with it) and the real
// builder as it stands now. Size is not the predictor — mt_behavior.hkx is
// twice as many objects as 0_master.hkx and was always fine.

const long Cap = 20_000_000;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hkx-treebuild-perf <behavior.xml> [...]");
    return 1;
}

int bad = 0;

foreach (var path in args)
{
    Console.WriteLine($"── {Path.GetFileName(path)} ───────────────────────────────");

    var ser = new XmlSerializer(typeof(HkPackfile));
    HkPackfile? packfile;
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        packfile = (HkPackfile?)ser.Deserialize(fs);
    if (packfile == null) { Console.Error.WriteLine("  deserialize failed"); bad++; continue; }

    var mgr = new HavokManager();
    mgr.BuildGraph(packfile);
    Console.WriteLine($"  {mgr.ObjectMap.Count} objects");

    LegacyWalk(mgr);

    // The builder the window actually runs, on the UI thread, before it paints.
    var before = GC.GetTotalAllocatedBytes();
    var sw = Stopwatch.StartNew();
    var root = new BehaviorTreeBuilder(mgr).BuildTree("");
    sw.Stop();
    var allocated = GC.GetTotalAllocatedBytes() - before;

    int nodes = CountNodes(root), shared = CountShared(root);
    Console.WriteLine($"  now       {nodes,10:N0} nodes ({shared:N0} shared refs)   "
                    + $"{sw.Elapsed.TotalMilliseconds:F0} ms, {allocated / 1024.0 / 1024.0:F1} MB allocated");

    // Each object may be expanded once, and the tree adds a folder node per
    // state with a generator or transitions — so a few times the object count
    // is the honest ceiling, and anything near the cap is the old blow-up back.
    if (nodes > mgr.ObjectMap.Count * 4)
    {
        Console.Error.WriteLine($"  FAIL: {nodes:N0} nodes for {mgr.ObjectMap.Count:N0} objects");
        bad++;
    }
    Console.WriteLine();
}

return bad == 0 ? 0 : 1;

static int CountNodes(BehaviorNodeData n) => 1 + n.Children.Sum(CountNodes);
static int CountShared(BehaviorNodeData n) =>
    (n.Name.Contains('↗') ? 1 : 0) + n.Children.Sum(CountShared);

// The traversal as it was before the fix — same top-level selection, same
// path-scoped guard, same generic every-ref recursion — counting instead of
// allocating so the blow-up is measurable without being fatal.
void LegacyWalk(HavokManager mgr)
{
    var childRefs = new HashSet<string>();
    foreach (var parent in mgr.ObjectMap.Values)
    {
        if (parent.ClassName == "hkbBehaviorGraph") continue;
        foreach (var p in parent.Params)
            foreach (var tok in HkRefList.Tokens(p.Value))
                if (tok.StartsWith("#")) childRefs.Add(tok);
    }

    var tops = mgr.ObjectMap.Values
        .Where(o => o.ClassName == "hkbStateMachine" && !childRefs.Contains(o.Id))
        .ToList();

    long nodes = 1;                                // the "Behavior Graph" root
    bool capped = false;
    var visits = new Dictionary<string, long>();   // how often each object is re-expanded

    var sw = Stopwatch.StartNew();
    foreach (var sm in tops)
    {
        WalkSm(sm, new HashSet<string>());
        if (capped) break;
    }
    sw.Stop();

    Console.WriteLine(capped
        ? $"  was       >{Cap,9:N0} nodes                      "
          + $"{sw.Elapsed.TotalMilliseconds:F0} ms just to reach the cap — never finishes"
        : $"  was       {nodes,10:N0} nodes                      {sw.Elapsed.TotalMilliseconds:F0} ms");

    foreach (var (id, n) in visits.OrderByDescending(kv => kv.Value).Take(3))
    {
        var o = mgr.Resolve(id);
        var name = o?.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? id;
        Console.WriteLine($"              re-expanded {n,12:N0}x  {id} {o?.ClassName} \"{name}\"");
    }

    void Bump(string id)
    {
        if (++nodes >= Cap) capped = true;
        visits[id] = visits.TryGetValue(id, out var v) ? v + 1 : 1;
    }

    void WalkSm(HkObject sm, HashSet<string> path)
    {
        if (capped) return;
        Bump(sm.Id);
        if (!path.Add(sm.Id)) return;

        var states = sm.Params.FirstOrDefault(p => p.Name == "states");
        if (states != null)
            foreach (var id in HkRefList.Tokens(states.Value))
                if (mgr.TryResolve(id, out var st) && st != null) WalkState(st, path);

        path.Remove(sm.Id);
    }

    void WalkState(HkObject state, HashSet<string> path)
    {
        if (capped) return;
        Bump(state.Id);

        // The builder wraps each of these in a folder node; count those too,
        // or "was" and "now" are not the same tree measured two ways.
        var gen = state.Params?.FirstOrDefault(p => p.Name == "generator");
        if (gen != null && mgr.TryResolve(gen.Value, out var g) && g != null)
        {
            nodes++;                                   // "Logic (Generator)"
            WalkGen(g, path);
        }

        var tr = state.Params?.FirstOrDefault(p => p.Name == "transitions");
        if (tr != null && mgr.TryResolve(tr.Value, out var arr) && arr != null)
        {
            long before = nodes;
            foreach (var p in arr.Params)
                if (mgr.TryResolve(p.Value, out var t) && t != null) Bump(t.Id);
            if (nodes > before) nodes++;               // "Transitions", only when non-empty
        }
    }

    void WalkGen(HkObject g, HashSet<string> path)
    {
        if (capped) return;
        if (g.ClassName == "hkbStateMachine") { WalkSm(g, path); return; }

        Bump(g.Id);
        if (!path.Add(g.Id)) return;

        foreach (var param in g.Params)
            foreach (var tok in HkRefList.Tokens(param.Value))
                if (tok.StartsWith("#") && mgr.TryResolve(tok, out var child) && child != null)
                {
                    WalkGen(child, path);
                    if (capped) return;
                }

        path.Remove(g.Id);
    }
}
