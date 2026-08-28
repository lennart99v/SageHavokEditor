using System.Xml.Serialization;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Services;
using SageHavokEditor.Models;

// Checks StateDuplicator against a real behaviour file: does a duplicated state
// come out fully rewired (every ref inside the copy pointing at the copy), does
// the original come out untouched, and does the result survive a save/reload —
// which is where a stale numelements or an unwired object would show up.
//
//   dotnet run --project tools/hkx-duplicate-state -- <behavior.xml> [stateName]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hkx-duplicate-state <behavior.xml> [stateName]");
    return 1;
}

var failed = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}

var ser = new XmlSerializer(typeof(HkPackfile));

HavokManager Load(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
    var pf = (HkPackfile?)ser.Deserialize(fs) ?? throw new InvalidDataException(path);
    var m = new HavokManager();
    m.BuildGraph(pf);
    return m;
}

var manager = Load(args[0]);
Console.WriteLine($"loaded {Path.GetFileName(args[0])} — {manager.ObjectMap.Count} objects");

// Pick a state worth copying: one with both a generator and transitions.
HkObject? state = null;
if (args.Length > 1)
    state = manager.ObjectMap.Values.FirstOrDefault(o =>
        o.ClassName == "hkbStateMachineStateInfo" && o.DisplayName == args[1]);
else
    state = manager.ObjectMap.Values.FirstOrDefault(o =>
        o.ClassName == "hkbStateMachineStateInfo"
        && Ref(o, "generator") != null && Ref(o, "transitions") != null);

if (state == null) { Console.Error.WriteLine("no suitable state found"); return 1; }

var machine = manager.ObjectMap.Values.First(o =>
    o.ClassName == "hkbStateMachine"
    && HkRefList.Tokens(o.Params.FirstOrDefault(p => p.Name == "states")?.Value).Contains(state.Id));

Console.WriteLine($"duplicating '{state.DisplayName}' ({state.Id}) from machine '{machine.DisplayName}'");

// Snapshot the whole file so "the original is untouched" is checked against
// everything, not just the state.
var before = manager.ObjectMap.Values.ToDictionary(o => o.Id, Flatten);
var originalIds = manager.ObjectMap.Keys.ToHashSet();

var subtree = StateDuplicator.CollectSubtree(manager, state, true, true);
Console.WriteLine($"subtree: {subtree.Count} objects — "
    + string.Join(", ", subtree.Take(8).Select(o => $"{o.Id}:{o.ClassName}"))
    + (subtree.Count > 8 ? ", …" : ""));

var result = StateDuplicator.Duplicate(manager, state, machine, "DupTest_State", true, true);

Console.WriteLine("== ids ==");
Check("every copy has a fresh id",
    result.Created.All(o => !originalIds.Contains(o.Id)));
Check("copy ids are unique among themselves",
    result.Created.Select(o => o.Id).Distinct().Count() == result.Created.Count);
Check("one copy per collected object",
    result.Created.Count == subtree.Count, $"{result.Created.Count} vs {subtree.Count}");

// Commit the way the graph view does, so the reload below sees a wired file.
foreach (var o in result.Created) manager.ObjectMap[o.Id] = o;
var statesParam = machine.Params.First(p => p.Name == "states");
if (statesParam.Children.Count > 0) statesParam.Children.Add(result.NewState);
var newStates = statesParam.Children.Count > 0
    ? string.Join(" ", statesParam.Children.Select(c => c.Id))
    : statesParam.Value + " " + result.NewState.Id;
statesParam.Value = newStates;
statesParam.NumElements = HkRefList.Tokens(newStates).Length.ToString();

Console.WriteLine("== rewiring ==");
var copiedOriginals = subtree.Select(o => o.Id).ToHashSet();
var danglingIntoOriginal = result.Created
    .SelectMany(o => AllRefs(o).Select(r => (o.Id, r)))
    .Where(t => copiedOriginals.Contains(t.r))
    .ToList();
Check("no copy references an object that was itself copied",
    danglingIntoOriginal.Count == 0,
    danglingIntoOriginal.Count == 0 ? null
        : string.Join(", ", danglingIntoOriginal.Take(5).Select(t => $"{t.Id}→{t.r}")));

Check("every ref inside the copies resolves",
    result.Created.SelectMany(AllRefs).All(r => manager.ObjectMap.ContainsKey(r)),
    string.Join(", ", result.Created.SelectMany(AllRefs)
        .Where(r => !manager.ObjectMap.ContainsKey(r)).Distinct().Take(5)));

// The transition effect is deliberately shared, so the copied transitions must
// still point at the original effect object.
var origEffects = TransitionEffects(manager, state).ToHashSet();
var copyEffects = TransitionEffects(manager, result.NewState).ToHashSet();
Check("transition effects are shared, not copied",
    origEffects.Count == 0 || copyEffects.SetEquals(origEffects),
    $"original [{string.Join(" ", origEffects)}] vs copy [{string.Join(" ", copyEffects)}]");

Console.WriteLine("== the original ==");
var changed = before.Where(kv => manager.ObjectMap.TryGetValue(kv.Key, out var now)
                                 && Flatten(now) != kv.Value)
    .Select(kv => kv.Key).ToList();
Check("only the machine's states list changed",
    changed.Count == 1 && changed[0] == machine.Id,
    string.Join(", ", changed));

Console.WriteLine("== the copy ==");
Check("copy is a state info with the given name",
    result.NewState.ClassName == "hkbStateMachineStateInfo"
    && result.NewState.DisplayName == "DupTest_State");
var machineStateIds = HkRefList.Tokens(statesParam.Value)
    .Select(r => manager.ObjectMap.TryGetValue(r, out var so)
        ? so.Params.FirstOrDefault(p => p.Name == "stateId")?.Value : null)
    .Where(v => v != null).ToList();
Check("stateId is unique within the machine",
    machineStateIds.Count == machineStateIds.Distinct().Count(),
    $"stateId {result.NewStateId} among {machineStateIds.Count} states");
Check("copy's generator is a copy, not the original's",
    Ref(result.NewState, "generator") != Ref(state, "generator"),
    $"{Ref(state, "generator")} → {Ref(result.NewState, "generator")}");
Check("copy's transitions array is a copy",
    Ref(result.NewState, "transitions") != Ref(state, "transitions"));
// Vanilla files already reuse a name across objects (a state and its clip often
// share one), so the test is that the copies add no NEW collision.
var dupNamesBefore = originalIds
    .Select(id => manager.ObjectMap[id].DisplayName)
    .GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
var newCollisions = result.Created
    .Select(o => o.DisplayName)
    .Where(n => !dupNamesBefore.Contains(n)
                && manager.ObjectMap.Values.Count(o => o.DisplayName == n) > 1)
    .Distinct().ToList();
Check("the copies introduce no new name collision",
    newCollisions.Count == 0, string.Join(", ", newCollisions.Take(5)));

Console.WriteLine("== save / reload ==");
var tmp = Path.Combine(Path.GetTempPath(), "hkx-duplicate-state");
Directory.CreateDirectory(tmp);
var outPath = Path.Combine(tmp, "duplicated.xml");
using (var w = new StreamWriter(outPath)) HkXml.Write(manager.NewPackfile(), w);

var reloaded = Load(outPath);
Check("every copy survives the round-trip",
    result.Created.All(o => reloaded.ObjectMap.ContainsKey(o.Id)));
Check("the copy is still in the machine's states list",
    HkRefList.Tokens(reloaded.ObjectMap[machine.Id].Params
        .First(p => p.Name == "states").Value).Contains(result.NewState.Id));
Check("states numelements matches the list",
    reloaded.ObjectMap[machine.Id].Params.First(p => p.Name == "states").NumElements
        == HkRefList.Tokens(reloaded.ObjectMap[machine.Id].Params
            .First(p => p.Name == "states").Value).Length.ToString());
Check("the copy's params survive byte-for-byte",
    Flatten(reloaded.ObjectMap[result.NewState.Id]) == Flatten(result.NewState));
foreach (var o in result.Created)
    if (Flatten(reloaded.ObjectMap[o.Id]) != Flatten(o))
        Console.WriteLine($"    differs: {o.Id} ({o.ClassName})");

// The other end of the dialog: share the generator, drop the transitions. Run it
// on a clean load so the checks above can't have disturbed it.
Console.WriteLine("== shallow copy (share generator, no transitions) ==");
var m2 = Load(args[0]);
var state2 = m2.ObjectMap[state.Id];
var machine2 = m2.ObjectMap[machine.Id];
var shallow = StateDuplicator.Duplicate(m2, state2, machine2, "DupTest_Shallow", false, false);
Check("copies only the state and its own small arrays",
    shallow.Created.Count < 5, $"{shallow.Created.Count} objects");
Check("shares the original's generator",
    Ref(shallow.NewState, "generator") == Ref(state2, "generator"),
    Ref(shallow.NewState, "generator"));
Check("starts with no outgoing transitions",
    shallow.NewState.Params.First(p => p.Name == "transitions").Value == "null");
Check("still gets its own notify-event arrays",
    Ref(state2, "enterNotifyEvents") == null
    || Ref(shallow.NewState, "enterNotifyEvents") != Ref(state2, "enterNotifyEvents"));

// A one-state machine keeps its states ref cached in Children, where the Value
// getter reads from — appending to the text alone would not stick.
var m3 = Load(args[0]);
var single = m3.ObjectMap.Values.FirstOrDefault(o =>
    o.ClassName == "hkbStateMachine"
    && HkRefList.Tokens(o.Params.FirstOrDefault(p => p.Name == "states")?.Value).Length == 1);
if (single != null)
{
    Console.WriteLine($"== one-state machine ('{single.DisplayName}') ==");
    var sp = single.Params.First(p => p.Name == "states");
    Check("its states ref is cached in Children", sp.Children.Count == 1);

    var only = m3.ObjectMap[HkRefList.Tokens(sp.Value)[0]];
    var dup = StateDuplicator.Duplicate(m3, only, single, "DupTest_Single", true, true);
    foreach (var o in dup.Created) m3.ObjectMap[o.Id] = o;
    sp.Children.Add(dup.NewState);
    sp.Value = string.Join(" ", sp.Children.Select(c => c.Id));
    sp.NumElements = sp.Children.Count.ToString();

    Check("the machine now lists two states",
        HkRefList.Tokens(sp.Value).Length == 2, sp.Value);
    Check("stateIds differ",
        only.Params.First(p => p.Name == "stateId").Value != dup.NewStateId.ToString(),
        $"{only.Params.First(p => p.Name == "stateId").Value} vs {dup.NewStateId}");
}

Console.WriteLine($"\n{(failed == 0 ? "ALL PASS" : $"{failed} FAILED")}  (wrote {outPath})");
return failed == 0 ? 0 : 1;

static string? Ref(HkObject o, string param)
{
    var v = o.Params.FirstOrDefault(p => p.Name == param)?.Value;
    return string.IsNullOrEmpty(v) || v == "null" || !v.StartsWith("#") ? null : v;
}

// Every #ref an object carries, nested inline elements included.
static IEnumerable<string> AllRefs(HkObject o) => o.Params.SelectMany(ParamRefs);

static IEnumerable<string> ParamRefs(HkParam p)
{
    foreach (var t in HkRefList.Tokens(p.Value))
        if (t.StartsWith("#")) yield return t;
    foreach (var c in p.Children)
        if (string.IsNullOrEmpty(c.Id))
            foreach (var r in c.Params.SelectMany(ParamRefs))
                yield return r;
}

static IEnumerable<string> TransitionEffects(HavokManager m, HkObject stateInfo)
{
    var arr = stateInfo.Params.FirstOrDefault(p => p.Name == "transitions")?.Value;
    if (arr == null || !m.ObjectMap.TryGetValue(arr, out var a)) yield break;
    foreach (var t in a.Params.Where(p => p.Name == "transitions").SelectMany(p => p.Children))
    {
        var e = t.Params.FirstOrDefault(p => p.Name == "transition")?.Value;
        if (!string.IsNullOrEmpty(e) && e.StartsWith("#")) yield return e;
    }
}

// Value-only text of an object, for "did anything change" comparisons.
static string Flatten(HkObject o) =>
    $"{o.Id}|{o.ClassName}|{o.Signature}|"
    + string.Join(";", o.Params.Select(FlattenParam));

static string FlattenParam(HkParam p) =>
    $"{p.Name}={p.Value}#{p.NumElements}"
    + (p.Strings.Count > 0 ? "[" + string.Join(",", p.Strings) + "]" : "")
    + (p.Children.Any(c => string.IsNullOrEmpty(c.Id))
        ? "{" + string.Join("/", p.Children.Select(c => string.Join(";", c.Params.Select(FlattenParam)))) + "}"
        : "");
