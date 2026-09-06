using System.Xml.Serialization;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Validation;
using SageHavokEditor.Models;

// Checks GraphDoctor against a real behaviour file. Two things have to hold and
// neither is provable by reading the code: the pass must be quiet on a vanilla
// file (a warning that fires on stock Skyrim content is a warning nobody reads),
// and it must actually catch each fault it claims to — so every check gets its
// bug re-introduced on purpose, one at a time, with the file restored after and
// re-verified against the baseline.
//
//   dotnet run --project tools/hkx-graph-doctor -- <behavior.xml> [character.xml]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hkx-graph-doctor <behavior.xml> [character.xml]");
    return 1;
}

var failed = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}

var ser = new XmlSerializer(typeof(HkPackfile));

HkPackfile LoadPackfile(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
    return (HkPackfile?)ser.Deserialize(fs) ?? throw new InvalidDataException(path);
}

HavokManager Build(HkPackfile pf)
{
    var m = new HavokManager();
    m.BuildGraph(pf);
    return m;
}

var manager = Build(LoadPackfile(args[0]));
Console.WriteLine($"loaded {Path.GetFileName(args[0])} — {manager.ObjectMap.Count} objects");

// The character file is what makes the clip-registration check possible; without
// one the doctor skips it rather than reporting every clip in the file.
var animations = new List<string>();
if (args.Length > 1)
{
    var charMgr = Build(LoadPackfile(args[1]));
    animations = charMgr.ObjectMap.Values
        .FirstOrDefault(o => o.ClassName == "hkbCharacterStringData")
        ?.Params.FirstOrDefault(p => p.Name == "animationNames")?.Strings ?? new List<string>();
    Console.WriteLine($"character {Path.GetFileName(args[1])} — {animations.Count} registered animations");
}

GraphDoctorReport Run() => new GraphDoctor(manager, animations).Run();

// A fingerprint per issue, so "the file came back to where it started" is a set
// comparison rather than a count comparison — a fault that swaps one issue for
// another would slip past a count.
static HashSet<string> Fingerprint(GraphDoctorReport r) =>
    r.Issues.Select(i => $"{i.Severity}|{i.Category}|{i.ObjectId}|{i.Description}").ToHashSet();

// ── Baseline ──────────────────────────────────────────────────────────────

var baseline = Run();
var baseFingerprint = Fingerprint(baseline);

Console.WriteLine();
Console.WriteLine($"== baseline ==  {baseline.Headline}");
foreach (var g in baseline.Issues.GroupBy(i => $"{i.Severity}/{(i.Category.Length == 0 ? "(uncategorised)" : i.Category)}")
                                 .OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"  {g.Count(),5}  {g.Key}");
    foreach (var i in g.Take(3))
        Console.WriteLine($"          {i.ObjectId} {i.ObjectName}: {Trim(i.Description)}");
    if (g.Count() > 3) Console.WriteLine($"          … and {g.Count() - 3} more");
}

Console.WriteLine();
Console.WriteLine("== the new checks are quiet on a stock file ==");
// The pre-existing validator checks are NOT asserted silent: vanilla
// dragonbehavior really does carry duplicate and dangling stateIds, and those
// findings are the 0.6 validator's, not the doctor's.
foreach (var quiet in new[]
         {
             ValidationIssue.CategoryNullGenerator,
             ValidationIssue.CategoryIndexRange,
             ValidationIssue.CategoryBrokenRef,
             ValidationIssue.CategoryAnimation,
         })
{
    var hits = baseline.Issues.Where(i => i.Category == quiet).ToList();
    Check($"nothing reported as {quiet}", hits.Count == 0,
        string.Join("; ", hits.Take(3).Select(i => $"{i.ObjectId} {Trim(i.Description)}")));
}

// The unreachable-state warnings are not asserted absent — vanilla has genuinely
// dead states — but every one of them is re-derived independently here: a state
// the doctor calls unreachable must be targeted by no transition anywhere in the
// file (toStateId or toNestedStateId) and must not be its own machine's start
// state. That catches a walker that simply failed to find the transitions.
{
    var targeted = manager.ObjectMap.Values
        .Where(o => o.ClassName == "hkbStateMachineTransitionInfoArray")
        .SelectMany(a => a.Params.Where(p => p.Name == "transitions").SelectMany(p => p.Children))
        .SelectMany(tr => new[] { ValueOf(tr, "toStateId"), ValueOf(tr, "toNestedStateId") })
        .Where(s => s.Length > 0)
        .ToHashSet();

    var wrong = new List<string>();
    foreach (var issue in baseline.Issues
                 .Where(i => i.Category == ValidationIssue.CategoryUnreachableState))
    {
        var state = manager.ObjectMap[issue.ObjectId];
        var sid = ValueOf(state, "stateId");
        var owner = manager.ObjectMap.Values.FirstOrDefault(o =>
            o.ClassName == "hkbStateMachine"
            && HkRefList.Tokens(ValueOf(o, "states")).Contains(state.Id));
        if (targeted.Contains(sid) || ValueOf(owner!, "startStateId") == sid)
            wrong.Add($"{issue.ObjectId} stateId {sid}");
    }

    var flagged = baseline.Issues.Count(i => i.Category == ValidationIssue.CategoryUnreachableState);
    Check($"all {flagged} unreachable states check out independently",
        wrong.Count == 0, string.Join("; ", wrong.Take(5)));
}

// The prune list gets the same treatment, re-derived from the raw XML with a
// regex instead of the model's ref walker — the two disagree if HkRefWalk misses
// a ref (refs nested inside inline children are exactly what it used to miss).
{
    // PreserveWhitespace is load-bearing: without it XElement.Value glues
    // adjacent text nodes together, so variableBindingSet "#0053" followed by
    // userData "0" reads as the token "#00530" and the ref is lost.
    var doc = System.Xml.Linq.XDocument.Load(args[0], System.Xml.Linq.LoadOptions.PreserveWhitespace);
    var byId = doc.Descendants("hkobject")
        .Where(e => e.Attribute("name") != null)
        .ToDictionary(e => e.Attribute("name")!.Value);

    var root = doc.Root!.Attribute("toplevelobject")!.Value;
    var live = new HashSet<string> { root };
    var queue = new Queue<string>(new[] { root });
    while (queue.Count > 0)
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(byId[queue.Dequeue()].Value, @"#\d+"))
            if (byId.ContainsKey(m.Value) && live.Add(m.Value))
                queue.Enqueue(m.Value);

    var expected = byId.Keys.Where(k => !live.Contains(k)).ToHashSet();
    var reported = baseline.Issues
        .Where(i => i.Category == ValidationIssue.CategoryPruned)
        .Select(i => i.ObjectId).ToHashSet();

    Check($"the {reported.Count} object(s) an .hkx save would drop match a raw-XML walk",
        expected.SetEquals(reported),
        $"+{reported.Except(expected).Count()} / -{expected.Except(reported).Count()}");
}

// The faults below all need a behaviour graph to break. A character, project or
// skeleton file still exercises everything above, which is the point of running
// one through: the doctor must stay quiet on a file that has no graph at all.
if (!manager.ObjectMap.Values.Any(o => o.ClassName == "hkbStateMachine"))
{
    Console.WriteLine();
    Console.WriteLine("no state machines in this file — skipping the fault injection");
    Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) FAILED");
    return failed == 0 ? 0 : 1;
}

// ── Fault injection ───────────────────────────────────────────────────────
// Each fault names the issue it must produce; the doctor is re-run, the new
// issues are diffed against the baseline, and the file is put back.

void Fault(string title, string expectCategory, Func<string> apply, Action undo,
           Func<GraphDoctorReport, string, bool>? extra = null)
{
    Console.WriteLine();
    Console.WriteLine($"== {title} ==");
    var expectedId = apply();
    try
    {
        var report = Run();
        var added = Fingerprint(report).Except(baseFingerprint).ToList();

        var hit = report.Issues.Any(i => i.Category == expectCategory && i.ObjectId == expectedId
                                         && !baseFingerprint.Contains(
                                             $"{i.Severity}|{i.Category}|{i.ObjectId}|{i.Description}"));
        Check($"reports {expectCategory} on {expectedId}", hit,
            hit ? null : $"new issues: {string.Join(" / ", added.Take(3))}");

        var says = report.Issues.FirstOrDefault(i => i.Category == expectCategory && i.ObjectId == expectedId);
        if (says != null) Console.WriteLine($"          → {says.Severity}: {Trim(says.Description, 120)}");

        if (extra != null)
            Check("consequence is reported too", extra(report, expectedId));
    }
    finally { undo(); }

    var restored = Fingerprint(Run());
    Check("removing the fault restores the baseline exactly",
        restored.SetEquals(baseFingerprint),
        $"+{restored.Except(baseFingerprint).Count()} / -{baseFingerprint.Except(restored).Count()}");
}

// -- 1. a state whose generator is null -----------------------------------
{
    var state = manager.ObjectMap.Values.First(o =>
        o.ClassName == "hkbStateMachineStateInfo" && RefOf(o, "generator") != null);
    var param = state.Params.First(p => p.Name == "generator");
    var (oldValue, oldChildren) = Snapshot(param);

    Fault($"a state's generator is null ('{state.DisplayName}')",
        ValidationIssue.CategoryNullGenerator,
        () => { SetRef(param, "null"); return state.Id; },
        () => Restore(param, oldValue, oldChildren),
        // The generator subtree is now unreachable, which is exactly the kind of
        // silent loss the prune report exists to name.
        (r, _) => r.PrunedCount > baseline.PrunedCount);
}

// -- 2. a generator pointing at an id the file doesn't have ---------------
{
    var state = manager.ObjectMap.Values.First(o =>
        o.ClassName == "hkbStateMachineStateInfo" && RefOf(o, "generator") != null);
    var param = state.Params.First(p => p.Name == "generator");
    var (oldValue, oldChildren) = Snapshot(param);

    Fault($"a generator points at a missing object ('{state.DisplayName}')",
        ValidationIssue.CategoryBrokenRef,
        () => { SetRef(param, "#9999"); return state.Id; },
        () => Restore(param, oldValue, oldChildren));
}

// -- 3. a transition listening for an event the file doesn't have ---------
{
    var stringData = manager.ObjectMap.Values.First(o => o.ClassName == "hkbBehaviorGraphStringData");
    int eventCount = stringData.Params.First(p => p.Name == "eventNames").Strings.Count;

    var array = manager.ObjectMap.Values.First(o =>
        o.ClassName == "hkbStateMachineTransitionInfoArray"
        && o.Params.Any(p => p.Name == "transitions" && p.Children.Count > 0));
    var transition = array.Params.First(p => p.Name == "transitions").Children[0];
    var eventId = transition.Params.First(p => p.Name == "eventId");
    var oldEvent = eventId.Value;

    Fault($"a transition's eventId is past the end of eventNames ({eventCount} events)",
        ValidationIssue.CategoryIndexRange,
        () => { eventId.Value = (eventCount + 7).ToString(); return array.Id; },
        () => eventId.Value = oldEvent);
}

// -- 4. a variable binding pointing past the end of the variable table ----
{
    var stringData = manager.ObjectMap.Values.First(o => o.ClassName == "hkbBehaviorGraphStringData");
    int variableCount = stringData.Params.First(p => p.Name == "variableNames").Strings.Count;

    var (owner, param) = manager.ObjectMap.Values
        .SelectMany(o => Params(o).Select(t => (Owner: o, t.Param)))
        .First(t => t.Param.TypeInfo?.Semantic == HkParamSemantic.VariableIndex
                    && int.TryParse(t.Param.Value, out int n) && n >= 0);
    var oldIndex = param.Value;

    Fault($"a binding's variableIndex is past the end of variableNames ({variableCount} variables)",
        ValidationIssue.CategoryIndexRange,
        () => { param.Value = (variableCount + 3).ToString(); return owner.Id; },
        () => param.Value = oldIndex);
}

// -- 5. a state nothing transitions into ----------------------------------
{
    var machine = manager.ObjectMap.Values.First(o =>
        o.ClassName == "hkbStateMachine"
        && HkRefList.Tokens(ValueOf(o, "states")).Length > 1);
    var states = machine.Params.First(p => p.Name == "states");
    var donor = manager.Resolve(HkRefList.Tokens(states.Value)[0])!;

    // The real scenario: a state copied into the machine and never wired up.
    var unwired = new HkObject { Id = "#9001", ClassName = "hkbStateMachineStateInfo" };
    unwired.Params.Add(new HkParam { Name = "name", Value = "DoctorTest_Unwired" });
    unwired.Params.Add(new HkParam { Name = "stateId", Value = "9001" });
    unwired.Params.Add(new HkParam { Name = "generator", Value = ValueOf(donor, "generator") });

    var (oldValue, oldChildren) = Snapshot(states);
    var oldCount = states.NumElements;

    Fault($"a state nothing transitions into (added to '{machine.DisplayName}')",
        ValidationIssue.CategoryUnreachableState,
        () =>
        {
            manager.ObjectMap[unwired.Id] = unwired;
            // The resolved-ref cache is what the Value getter reads, so appending
            // to the text alone would not stick — the usual #ref trap.
            if (states.Children.Count > 0) states.Children.Add(unwired);
            states.Value = string.Join(" ", HkRefList.Tokens(oldValue).Append(unwired.Id));
            states.NumElements = HkRefList.Tokens(states.Value).Length.ToString();
            return unwired.Id;
        },
        () =>
        {
            manager.ObjectMap.Remove(unwired.Id);
            states.Children.Remove(unwired);
            Restore(states, oldValue, oldChildren);
            states.NumElements = oldCount;
        });
}

// -- 6. two dead objects that reference each other ------------------------
// The old orphan check asked "does anything reference this?" and both of these
// answer yes. Reachability from the root is what actually decides the save.
{
    var a = new HkObject { Id = "#9101", ClassName = "hkbStringEventPayload" };
    var b = new HkObject { Id = "#9102", ClassName = "hkbStringEventPayload" };
    a.Params.Add(new HkParam { Name = "data", Value = b.Id });
    b.Params.Add(new HkParam { Name = "data", Value = a.Id });

    Fault("two dead objects referencing each other are still dropped on save",
        ValidationIssue.CategoryPruned,
        () =>
        {
            manager.ObjectMap[a.Id] = a;
            manager.ObjectMap[b.Id] = b;
            return a.Id;
        },
        () => { manager.ObjectMap.Remove(a.Id); manager.ObjectMap.Remove(b.Id); },
        (r, _) => r.PrunedCount == baseline.PrunedCount + 2
                  && r.Issues.Any(i => i.Category == ValidationIssue.CategoryPruned && i.ObjectId == b.Id));
}

// -- 7. a clip naming an unregistered animation ---------------------------
if (animations.Count > 0)
{
    var clip = manager.ObjectMap.Values.First(o =>
        o.ClassName == "hkbClipGenerator"
        && !string.IsNullOrWhiteSpace(ValueOf(o, "animationName")));
    var param = clip.Params.First(p => p.Name == "animationName");
    var oldPath = param.Value;

    Fault($"a clip names an animation the character never registered ('{clip.DisplayName}')",
        ValidationIssue.CategoryAnimation,
        () => { param.Value = @"Animations\DoctorTest_NotRegistered.hkx"; return clip.Id; },
        () => param.Value = oldPath);
}
else
{
    Console.WriteLine();
    Console.WriteLine("== a clip names an unregistered animation ==");
    Console.WriteLine("  [SKIP] no character file given — pass one to exercise this check");
}

// -- 8. a file header pointing at a root that isn't there -----------------
// Not a mutation of the loaded graph: toplevelobject is read at BuildGraph, so
// this one is loaded wrong from the start, the way a hand-edited file would be.
{
    Console.WriteLine();
    Console.WriteLine("== toplevelobject names an object the file doesn't contain ==");
    var pf = LoadPackfile(args[0]);
    pf.TopLevelObject = "#9999";
    var broken = new GraphDoctor(Build(pf), animations).Run();

    var header = broken.Issues.FirstOrDefault(i => i.Category == ValidationIssue.CategoryPruned
                                                   && i.ObjectClass == "hkpackfile");
    Check("reports the missing root", header != null);
    if (header != null) Console.WriteLine($"          → {header.Severity}: {Trim(header.Description, 120)}");
    Check("doesn't then report every object as pruned", broken.PrunedCount == 0,
        $"PrunedCount={broken.PrunedCount}");
}

// ── Result ────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) FAILED");
return failed == 0 ? 0 : 1;

// ── Helpers ───────────────────────────────────────────────────────────────

static string Trim(string s, int max = 90) => s.Length <= max ? s : s[..(max - 1)] + "…";

static string ValueOf(HkObject o, string param) =>
    o.Params.FirstOrDefault(p => p.Name == param)?.Value ?? "";

static string? RefOf(HkObject o, string param)
{
    var v = ValueOf(o, param);
    return v.StartsWith('#') ? v : null;
}

static IEnumerable<(string Path, HkParam Param)> Params(HkObject o)
{
    foreach (var p in o.Params)
    {
        yield return (p.Name, p);
        foreach (var c in p.Children)
        {
            if (!string.IsNullOrEmpty(c.Id)) continue;
            foreach (var t in Params(c)) yield return ($"{p.Name}.{t.Path}", t.Param);
        }
    }
}

static (string Value, List<HkObject> Children) Snapshot(HkParam p) =>
    (p.Value, p.Children.ToList());

// Mutating a #ref means updating the resolved-Children cache as well: the Value
// getter prefers the cache whenever it holds resolved refs, so writing the text
// alone is silently ignored.
static void SetRef(HkParam p, string value)
{
    p.Children.Clear();
    p.Value = value;
}

static void Restore(HkParam p, string value, List<HkObject> children)
{
    p.Children.Clear();
    foreach (var c in children) p.Children.Add(c);
    p.Value = value;
}
