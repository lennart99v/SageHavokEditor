using System.Text;
using SageHavokEditor.Core.AnimData;

// hkx-animdata — pin AnimationDataFile against a real animationdatasinglefile.txt.
//
// animationdatasinglefile.txt is positional and count-prefixed, so a parser that
// mis-slices does not fail: it shifts, and every answer after the shift is
// confidently wrong. Counting records therefore proves very little on its own —
// the only check that actually catches a shift is a parse -> emit -> compare that
// comes back byte-identical, because the emitter recomputes every count from the
// structure the parser built. A slice that moved by one line cannot survive it.
//
// The emitter exists here and only here. We read this format to answer a question
// and we never write it — Nemesis/Pandora own that half, and there is a maintained
// implementation of it in Skyrim Content Tools. Keeping emit in the harness is what
// makes "read-only" a property of the shipped assembly rather than a promise.
//
//   dotnet run --project tools/hkx-animdata -- <animationdatasinglefile.txt>

int failures = 0;
int checks = 0;

void Check(bool ok, string what, string? detail = null)
{
    checks++;
    if (ok) { Console.WriteLine($"  ok   {what}"); return; }
    failures++;
    Console.WriteLine($"  FAIL {what}{(detail is null ? "" : "\n         " + detail)}");
}

var path = args.FirstOrDefault(a => !a.StartsWith("--"));
if (path is null)
{
    Console.Error.WriteLine("usage: hkx-animdata <animationdatasinglefile.txt>");
    return 2;
}
if (!File.Exists(path))
{
    Console.Error.WriteLine($"not found: {path}");
    return 2;
}

// --locate <openfile>: what the editor would find and resolve from a file on
// disk. This is the diagnostic for "the check is silently off" — the one failure
// mode a clean report cannot be told apart from.
var locateArg = args.SkipWhile(a => a != "--locate").Skip(1).FirstOrDefault();
if (locateArg != null)
{
    Console.WriteLine($"locating from: {locateArg}");
    var found = AnimationDataLocator.LocateFrom(locateArg);
    Console.WriteLine($"  cache: {found ?? "(none found)"}");
    if (found != null)
    {
        var cache = AnimationDataFile.Load(found);
        Console.WriteLine($"  parsed: {cache.Projects.Count} projects");
        var resolved = AnimationDataLocator.ResolveProjects(cache, null, locateArg);
        Console.WriteLine($"  projects for this behaviour: "
            + (resolved.Count == 0 ? "(none)" : string.Join(", ", resolved.Select(p => p.Name))));
    }
    return 0;
}


var raw = File.ReadAllBytes(path);
var text = Encoding.Latin1.GetString(raw);
Console.WriteLine($"{path}\n  {raw.Length:N0} bytes\n");

// ── Parse ────────────────────────────────────────────────────────────────────
Console.WriteLine("Parse");
AnimationDataFile file;
try
{
    file = AnimationDataFile.Parse(text);
}
catch (AnimDataParseException ex)
{
    Console.WriteLine($"  FAIL parse threw: {ex.Message}");
    return 1;
}

int clipCount = file.Projects.Sum(p => p.Clips.Count);
int motionCount = file.Projects.Sum(p => p.Motions.Count);
int headerOnly = file.Projects.Count(p => !p.HasAnimData);

Console.WriteLine($"  {file.Projects.Count} projects, {clipCount:N0} clips, {motionCount:N0} motions, "
                + $"{headerOnly} header-only, canonical CRLF: {file.IsCanonicalCrLf}");
Check(file.IsCanonicalCrLf, "every line CRLF-terminated including the last, no BOM");

// The counts the grammar in AnimationData.h was verified against. A file that is
// not stock vanilla will differ — that is reported, not failed.
bool looksVanilla = file.Projects.Count == 429;
if (looksVanilla)
{
    Check(clipCount == 10_597, "vanilla clip count is 10,597", $"got {clipCount:N0}");
    Check(motionCount == 6_725, "vanilla motion count is 6,725", $"got {motionCount:N0}");
    Check(headerOnly == 380, "vanilla header-only project count is 380", $"got {headerOnly}");
}
else
{
    Console.WriteLine($"  note this is not stock vanilla (429 projects); "
                    + "skipping the fixed-count checks");
}

// ── Round trip ───────────────────────────────────────────────────────────────
Console.WriteLine("\nRound trip");
var emitted = Encoding.Latin1.GetBytes(Emit(file));
if (emitted.AsSpan().SequenceEqual(raw))
{
    Check(true, $"emit reproduces all {raw.Length:N0} bytes exactly");
}
else
{
    int at = 0;
    while (at < Math.Min(emitted.Length, raw.Length) && emitted[at] == raw[at]) at++;
    int line = text.Take(at).Count(c => c == '\n') + 1;
    Check(false, "emit reproduces the file byte-for-byte",
        $"{emitted.Length:N0} bytes vs {raw.Length:N0}; first difference at byte {at:N0} (line {line})");
}

// ── Case-sensitive clip lookup ───────────────────────────────────────────────
//
// Vanilla carries two clips whose names differ only by one letter's case and
// which point at DIFFERENT animations. Both spellings are genuinely used by
// vanilla graphs, so a case-insensitive lookup silently answers one with the
// other's animation. This is the check that holds FindClip's exact-match-first
// rule in place.
Console.WriteLine("\nCase-sensitive clip lookup");
var male = file.FindProject("DefaultMale");
if (male is null)
{
    Console.WriteLine("  note no DefaultMale project; skipping");
}
else
{
    var upper = male.FindClip("CrossBow_IdleHeld", out bool ambigUpper);
    var lower = male.FindClip("Crossbow_IdleHeld", out bool ambigLower);

    Check(upper is not null && lower is not null,
        "both case variants of CrossBow_IdleHeld resolve");
    Check(!ambigUpper && !ambigLower,
        "an exact match is never reported ambiguous");

    if (upper is not null && lower is not null)
    {
        Check(upper.Name == "CrossBow_IdleHeld" && lower.Name == "Crossbow_IdleHeld",
            "each spelling returns its own record, not the other's");
        Check(upper.AnimIndex != lower.AnimIndex,
            "the two spellings really do point at different animations",
            $"both came back as animIndex {upper.AnimIndex} — if vanilla changed, "
            + "re-check whether case still matters before relaxing FindClip");
        Console.WriteLine($"         CrossBow_IdleHeld → #{upper.AnimIndex}, "
                        + $"Crossbow_IdleHeld → #{lower.AnimIndex}");
    }

    // Tor_Idle / TOR_Idle share an animIndex, so the case-insensitive fallback
    // has nothing to get wrong — but it must still refuse to pick between them.
    var tor = male.FindClip("tor_idle", out bool ambigTor);
    Check(tor is null && ambigTor,
        "a name matching several records only case-insensitively is refused, not guessed");

    Check(male.FindClip("MT_Jump") is not null, "an ordinary clip still resolves");
    Check(male.FindClip("NoSuchClipAnywhere") is null, "an absent clip resolves to null");
}

// ── Project lookup ───────────────────────────────────────────────────────────
Console.WriteLine("\nProject lookup");
Check(file.FindProject("DefaultMale.txt") == file.FindProject("defaultmale"),
    "stem lookup ignores case and a trailing extension");

var masters = file.ProjectsForAsset(@"Behaviors\0_Master.hkx");
Check(masters.Count >= 1, "0_Master.hkx maps back to the projects that list it",
    $"got {masters.Count}");
Console.WriteLine($"         Behaviors\\0_Master.hkx → {string.Join(", ", masters.Select(p => p.Name))}");

// Duplicate project names are vanilla (ten of them). Assert what makes taking
// the first one safe — that the duplicates agree — rather than that they exist.
foreach (var group in file.Projects.GroupBy(p => p.Stem, StringComparer.OrdinalIgnoreCase)
                                   .Where(g => g.Count() > 1))
{
    var first = group.First();
    bool allAgree = group.All(p =>
        p.HasAnimData == first.HasAnimData
        && p.Clips.Count == first.Clips.Count
        && p.Motions.Count == first.Motions.Count
        && p.AssetPaths.SequenceEqual(first.AssetPaths, StringComparer.Ordinal));
    Check(allAgree, $"the {group.Count()} '{first.Name}' entries are the same record",
        "FindProject takes the first; if they ever disagree it has to report instead");
}

// ── Fault injection ──────────────────────────────────────────────────────────
//
// A positional parser that never rejects anything is indistinguishable from one
// that slices correctly, so break the file on purpose and require a diagnosis.
Console.WriteLine("\nFault injection");
var lines = text.Split("\r\n").ToList();

// expectContains is the wording the diagnosis has to contain, and is null where
// the honest expectation is only "it was rejected". A shift in a positional
// format is often detected some distance downstream — the parser cannot know the
// count above it was the lie rather than the line in front of it — so demanding
// specific wording there would be a check that asserts a coincidence.
void Inject(string what, Func<List<string>, bool> mutate, string? expectContains)
{
    var copy = new List<string>(lines);
    if (!mutate(copy)) { Console.WriteLine($"  skip {what} (no site in this file)"); return; }
    try
    {
        AnimationDataFile.Parse(string.Join("\r\n", copy));
        Check(false, what, "parsed clean — the corruption went undetected");
    }
    catch (AnimDataParseException ex)
    {
        bool named = expectContains is null
                  || ex.Message.Contains(expectContains, StringComparison.OrdinalIgnoreCase);
        Check(named, what, named ? null : $"rejected, but the message missed "
            + $"\"{expectContains}\": {ex.Message}");
        if (named) Console.WriteLine($"         {ex.Message}");
    }
}

// A section-A line count one short: every project after it shifts.
Inject("a short section-A line count is caught",
    c =>
    {
        for (int i = 430; i < c.Count; i++)
            if (int.TryParse(c[i], out int v) && v > 4) { c[i] = (v - 1).ToString(); return true; }
        return false;
    },
    null);

// A trigger count one long: eats the blank separator.
Inject("a trigger count that eats the blank separator is caught",
    c =>
    {
        int at = c.FindIndex(l => l == "JumpFall:0.833333");
        if (at <= 0) return false;
        c[at - 1] = "2";
        return true;
    },
    "blank separator");

// The project count itself.
Inject("a project count larger than the header list is caught",
    c => { c[0] = (int.Parse(c[0]) + 1).ToString(); return true; },
    null);

// Truncation.
Inject("a truncated file is caught",
    c => { c.RemoveRange(c.Count - 50, 49); return true; },
    "remain in the file");

// A count line that is not a number at all — the one corruption a positional
// parser can diagnose exactly where it happens.
Inject("a non-numeric count is named where it stands",
    c =>
    {
        int at = c.FindIndex(l => l == "JumpFall:0.833333");
        if (at <= 0) return false;
        c[at - 1] = "three";
        return true;
    },
    "trigger count for clip 'MT_Jump'");

// ── The verdict, against real cache records ──────────────────────────────────
//
// ClipCacheCheck resolves a clip's animIndex through the character's roster and
// compares the result with the animationName the graph carries. The roster here
// is synthetic — no vanilla character file ships loose, and inventing 2,520
// animation paths would prove nothing about the parser anyway — but the CLIP
// RECORDS are real, so what is under test is the resolution rule against the
// indices vanilla actually stores.
//
// The pair that matters is CrossBow_IdleHeld / Crossbow_IdleHeld again. They sit
// at different indices, so against one roster they name different animations —
// the concrete bug a case-insensitive lookup causes, demonstrated end to end
// rather than asserted about a helper.
Console.WriteLine("\nClip cache verdicts");
if (male is null)
{
    Console.WriteLine("  note no DefaultMale project; skipping");
}
else
{
    int rosterSize = male.Clips.Select(c => c.Index ?? -1).Max() + 1;
    var roster = Enumerable.Range(0, rosterSize).Select(i => $@"Animations\slot_{i}.hkx").ToList();
    string Slot(int i) => $@"Animations\slot_{i}.hkx";

    var check = new ClipCacheCheck(male, roster);
    var jump = male.FindClip("MT_Jump")!;
    int jumpIdx = jump.Index!.Value;

    Check(check.Check("MT_Jump", Slot(jumpIdx)).Status == ClipCacheStatus.Registered,
        "a clip whose animationName matches its cached index is Registered");

    var mismatch = check.Check("MT_Jump", Slot(jumpIdx + 1));
    Check(mismatch.Status == ClipCacheStatus.AnimationMismatch,
        "a clip whose animationName is not what the index resolves to is a mismatch",
        $"got {mismatch.Status}");
    Check(mismatch.CachedAnimation == Slot(jumpIdx),
        "the mismatch names the animation the cache actually points at");

    Check(check.Check("NoSuchClipAnywhere", Slot(0)).Status == ClipCacheStatus.NotInCache,
        "a clip with no cache record is NotInCache");
    Check(check.Check("tor_idle", Slot(0)).Status == ClipCacheStatus.NameAmbiguous,
        "a case-only-ambiguous name is reported, not guessed");

    // The whole point, end to end: ask about the lowercase-b spelling while
    // naming the animation the uppercase-B one resolves to. A case-insensitive
    // lookup would call this Registered.
    int upperIdx = male.FindClip("CrossBow_IdleHeld")!.Index!.Value;
    var crossed = check.Check("Crossbow_IdleHeld", Slot(upperIdx));
    Check(crossed.Status == ClipCacheStatus.AnimationMismatch,
        "the two CrossBow spellings do not satisfy each other's animation",
        $"got {crossed.Status} — a case-insensitive FindClip would report Registered here");

    // A roster shorter than the cache expects: the runtime would read past it.
    var shortCheck = new ClipCacheCheck(male, roster.Take(10).ToList());
    Check(shortCheck.Check("MT_Jump", Slot(jumpIdx)).Status == ClipCacheStatus.IndexOutOfRange,
        "an animIndex past the end of the roster is caught, not dereferenced");

    // No roster at all is a fact about us, not about the graph.
    var blind = new ClipCacheCheck(male, Array.Empty<string>()).Check("MT_Jump", Slot(0));
    Check(blind.Status == ClipCacheStatus.Registered && !blind.RosterChecked,
        "with no character roster the weaker question is answered and said to be weaker");

    Check(new ClipCacheCheck((AnimDataProject?)null).Check("MT_Jump", Slot(0)).Status
              == ClipCacheStatus.Unknown,
        "with no cache the verdict is Unknown rather than a clean bill of health");

    // A graph shared by several projects: a clip registered in any of them is
    // fine, and a complaint needs all of them to agree.
    var female = file.FindProject("DefaultFemale");
    if (female is not null)
    {
        var both = new ClipCacheCheck(new[] { female, male }, roster);
        Check(!both.Check("MT_Jump", Slot(jumpIdx)).IsProblem,
            "a clip registered in one of several candidate projects is not reported");
        Check(both.Check("NoSuchClipAnywhere", Slot(0)).Status == ClipCacheStatus.NotInCache,
            "a clip in none of them still is");
    }
}

// ── Locator ──────────────────────────────────────────────────────────────────
//
// The cache sits at <Data>\meshes\ and a behaviour four levels below it, so the
// walk has to cross actors\<race>\behaviors\ without being told the layout.
Console.WriteLine("\nLocator");
var tmp = Path.Combine(Path.GetTempPath(), "hkx-animdata-" + Guid.NewGuid().ToString("N"));
try
{
    var meshes = Path.Combine(tmp, "Data", "meshes");
    var behaviors = Path.Combine(meshes, "actors", "character", "behaviors");
    Directory.CreateDirectory(behaviors);
    File.WriteAllText(Path.Combine(meshes, AnimationDataLocator.FileName), "0\r\n");
    var graph = Path.Combine(behaviors, "0_master.hkx");
    File.WriteAllText(graph, "");

    var found = AnimationDataLocator.LocateFrom(graph);
    Check(found != null, "the cache is found four levels above an open behaviour");
    Check(found != null && Path.GetFullPath(found)
            == Path.GetFullPath(Path.Combine(meshes, AnimationDataLocator.FileName)),
        "and it is the one in meshes\\, not something else");

    Check(AnimationDataLocator.LocateFrom(Path.Combine(tmp, "Data", "x.hkx")) != null,
        "being handed the Data folder finds the meshes\\ copy below it");

    var orphan = Path.Combine(Path.GetTempPath(), "hkx-animdata-none-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(orphan);
    Check(AnimationDataLocator.LocateFrom(Path.Combine(orphan, "x.hkx")) == null,
        "no cache anywhere above returns null rather than reaching for a stray one");
    Directory.Delete(orphan, true);

    Check(AnimationDataLocator.StemForProjectFile(@"c:\x\defaultmale.hkx") == "defaultmale",
        "a project file's stem drops one extension");
}
finally
{
    try { Directory.Delete(tmp, true); } catch { /* best effort */ }
}


Console.WriteLine($"\n{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;

// ── The round-trip oracle ────────────────────────────────────────────────────

// Rebuild the file from the parsed model. Every count is recomputed from the
// structure rather than remembered, which is what makes a byte-identical result
// evidence that the parser sliced where the format says and not merely that it
// read back what it stored. Content lines go out verbatim — the numeric fields
// were never turned into numbers, so there is nothing to reformat.
static string Emit(AnimationDataFile file)
{
    var sb = new StringBuilder();
    void Line(string s) => sb.Append(s).Append("\r\n");

    Line(file.Projects.Count.ToString());
    foreach (var p in file.Projects) Line(p.Name);

    foreach (var p in file.Projects)
    {
        var a = new List<string> { p.FieldX, p.AssetPaths.Count.ToString() };
        a.AddRange(p.AssetPaths);
        a.Add(p.HasAnimData ? "1" : "0");

        if (p.HasAnimData)
        {
            foreach (var c in p.Clips)
            {
                a.Add(c.Name);
                a.Add(c.AnimIndex);
                a.Add(c.PlaybackSpeed);
                a.Add(c.CropStart);
                a.Add(c.CropEnd);
                a.Add(c.Triggers.Count.ToString());
                a.AddRange(c.Triggers);
                a.Add("");                       // the separator the counts include
            }
        }

        Line(a.Count.ToString());
        foreach (var l in a) Line(l);

        if (!p.HasAnimData) continue;

        var b = new List<string>();
        foreach (var m in p.Motions)
        {
            b.Add(m.AnimIndex);
            b.Add(m.Duration);
            b.Add(m.Translations.Count.ToString());
            b.AddRange(m.Translations);
            b.Add(m.Rotations.Count.ToString());
            b.AddRange(m.Rotations);
            b.Add("");
        }

        Line(b.Count.ToString());
        foreach (var l in b) Line(l);
    }

    return sb.ToString();
}
