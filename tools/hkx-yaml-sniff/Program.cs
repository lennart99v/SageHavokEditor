using System.Text;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Services;

// Checks that the editor decides what a file is by reading it, not by its name.
//
//   dotnet run --project tools/hkx-yaml-sniff -- [--corpus <src_behavior root>]
//                                                [--havok <folder of real files>]
//
// A ".hkx" path in Cassie's compiler is a compile *target*: a <stem>.hkx folder
// is a multi-file unit, and a <stem>.hkx file may be YAML — that is how her
// animations are authored. Fed one of those, this editor used to hand it to
// PackFileDeserializer and report a perfectly well-formed file as a corrupt
// packfile. YamlSourceProbe is the fix, and this is what holds it honest.
//
// The synthesised half always runs and needs nothing on disk: it is where the
// rule itself is pinned, including the cases that are easy to get wrong (an
// indented `behavior:` is not a behaviour document; a BOM does not hide the '<'
// of Havok XML). The two corpus halves run when pointed at real content.
//
// The expectations over the corpus are deliberately derived from the *folder
// layout* — behaviors/ holds behaviours, characters/ holds characters — and
// never from the probe, because a check that asks the code under test what it is
// looking at goes green by agreeing with a bug. (Same disease as the shared-state
// delete test and hkx-graph-doctor's injection 8.)

var failed = 0;
var checks = 0;

void Check(string what, bool ok, string? detail = null)
{
    checks++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}

string? Arg(string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

var corpusRoot = Arg("--corpus");
var havokDir = Arg("--havok");

var scratch = Path.Combine(Path.GetTempPath(), "hkx-yaml-sniff", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(scratch);

// ── The documents, as her compiler writes them ────────────────────────────────
// Trimmed from real files: behavior.yaml and character.yaml are vanilla units,
// the animation shape is AnimationYamlLoader.h's documented schema.

const string BehaviorDoc = """
packfile:
  classversion: 8
  contentsversion: "hk_2010.2.0-r1"

behavior:
  name: "SprintBehavior.hkb"
  variableMode: VARIABLE_MODE_DISCARD_WHEN_INACTIVE
  rootGenerator: SprintRootBehavior
  data: graphdata
""";

const string CharacterDoc = """
packfile:
  classversion: 8

character:
  name: DragonTEST
  rig: "Character Assets\\Skeleton.hkx"
  behavior: "Behaviors\\DragonBehavior.hkx"
""";

const string ProjectDoc = """
project:
  name: DragonProject
  characters:
    - "Characters\\DragonTEST.hkx"
""";

const string AnimationWrapped = """
# Authored by hand.
---
animation:
  name: WeaponThrow
  duration: 1.5
  skeleton: "Character Assets\\Skeleton.hkx"
  tracks:
    - bone: NPC Root
      translation: [ { time: 0.0, value: [0, 0, 0] } ]
""";

const string AnimationBare = """
name: WeaponThrow
duration: 1.5
skeleton: "Character Assets\\Skeleton.hkx"
compression:
  rotationTolerance: 0.001
tracks:
  - bone: NPC Root
    rotation: [ { time: 0.0, value: [0, 0, 0, 1] } ]
""";

const string AnimationFloatOnly = """
name: EyesOnly
duration: 0.5
floatTracks:
  - name: BlinkAmount
    keyframes: [ { time: 0.0, value: 0.0 } ]
""";

// The trap the column-0 rule exists for: a document that merely *mentions*
// behavior/character/animation somewhere nested is none of them.
const string NestedDecoys = """
metadata:
  behavior: SprintBehavior
  character: DragonTEST
  tracks: 4
notes: "nothing at the top level names a document"
""";

const string HavokXml = """
<?xml version="1.0" encoding="ascii"?>
<hkpackfile classversion="8" contentsversion="hk_2010.2.0-r1">
	<hksection name="__data__">
		<hkobject name="#0010" class="hkRootLevelContainer" signature="0x2772c11e">
""";

string Write(string name, string text, bool bom = false)
{
    var path = Path.Combine(scratch, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var bytes = new List<byte>();
    if (bom) bytes.AddRange(new byte[] { 0xEF, 0xBB, 0xBF });
    bytes.AddRange(Encoding.UTF8.GetBytes(text));
    File.WriteAllBytes(path, bytes.ToArray());
    return path;
}

string WriteBytes(string name, byte[] bytes)
{
    var path = Path.Combine(scratch, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
    return path;
}

// ── 1. The rule ───────────────────────────────────────────────────────────────

Console.WriteLine("Synthesised documents");

void Kind(string label, string file, string text, YamlSourceKind expected, bool bom = false)
{
    var path = Write(file, text, bom);
    var got = YamlSourceProbe.ProbeFile(path);
    Check($"{label} → {expected}", got == expected, got.ToString());
}

Kind("behavior.yaml (BOM, as she writes it)", "b/behavior.yaml", BehaviorDoc, YamlSourceKind.Behavior, bom: true);
Kind("character.yaml", "c/character.yaml", CharacterDoc, YamlSourceKind.Character);
Kind("project.yaml", "p/project.yaml", ProjectDoc, YamlSourceKind.Project);
Kind("animation, animation: wrapper", "a1/wrapped.yaml", AnimationWrapped, YamlSourceKind.Animation);
Kind("animation, no wrapper (tracks:)", "a2/bare.yaml", AnimationBare, YamlSourceKind.Animation);
Kind("animation, floatTracks only", "a3/float.yaml", AnimationFloatOnly, YamlSourceKind.Animation);
Kind("YAML naming no document", "u/unknown.yaml", NestedDecoys, YamlSourceKind.Unknown);
Kind("Havok XML", "x/file.xml", HavokXml, YamlSourceKind.None);
Kind("Havok XML behind a BOM", "x/bom.xml", HavokXml, YamlSourceKind.None, bom: true);
Kind("prose, no mapping", "t/readme.txt", "Just some notes about behaviours.\nNothing structured here.\n", YamlSourceKind.None);

// A packfile, and a binary that isn't one: neither may read as text.
var packHead = new byte[64];
packHead[0] = 0x57; packHead[1] = 0xE0; packHead[2] = 0xE0; packHead[3] = 0x57;
var packPath = WriteBytes("bin/real.hkx", packHead);
Check("packfile magic → None", YamlSourceProbe.ProbeFile(packPath) == YamlSourceKind.None);

var notPack = Encoding.UTF8.GetBytes("name: x\nduration: 1\ntracks:\n").Concat(new byte[] { 0, 1, 2 }).ToArray();
var notPackPath = WriteBytes("bin/other.hkx", notPack);
Check("binary that isn't a packfile → None", YamlSourceProbe.ProbeFile(notPackPath) == YamlSourceKind.None);

Check("a file that isn't there → None",
    YamlSourceProbe.ProbeFile(Path.Combine(scratch, "absent.hkx")) == YamlSourceKind.None);
Check("an empty file → None", YamlSourceProbe.ProbeFile(WriteBytes("bin/empty.hkx", Array.Empty<byte>()))
    == YamlSourceKind.None);

// ── 2. The item itself: the name says .hkx and the contents say otherwise ─────

Console.WriteLine();
Console.WriteLine("A .hkx that is not a packfile");

var animHkx = Write("Animations/WeaponThrow.hkx", AnimationBare);
Check("YAML animation named .hkx → Yaml, not HKX",
    HkxConversionService.DetectFormat(animHkx) == HkxFormat.Yaml,
    HkxConversionService.DetectFormat(animHkx).ToString());
Check("…and it classifies as an animation",
    YamlSourceProbe.ProbeFile(animHkx) == YamlSourceKind.Animation);

Check("a real packfile named .hkx → HKX",
    HkxConversionService.DetectFormat(packPath) == HkxFormat.HKX);
Check("Havok XML named .hkx → XML",
    HkxConversionService.DetectFormat(Write("Animations/converted.hkx", HavokXml)) == HkxFormat.XML);

// PrepareXmlAsync is what the load path calls, and it is where the old failure
// surfaced as "corrupt packfile". It must now refuse by name.
var prep = await new HkxConversionService().PrepareXmlAsync(animHkx);
Check("PrepareXmlAsync refuses YAML source", !prep.Success);
Check("…and its message says what the file is",
    prep.Error?.Contains("YAML source", StringComparison.OrdinalIgnoreCase) == true,
    prep.Error);

// ── 3. Units ──────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Units");

var behaviorUnit = Path.Combine(scratch, "units", "sprintbehavior.hkx");
Directory.CreateDirectory(Path.Combine(behaviorUnit, "clips"));
File.WriteAllText(Path.Combine(behaviorUnit, "behavior.yaml"), BehaviorDoc);
File.WriteAllText(Path.Combine(behaviorUnit, "clips", "Sprint.yaml"), "clip:\n  name: Sprint\n");

Check("a <stem>.hkx folder is a behaviour unit",
    YamlSourceProbe.ProbeUnit(behaviorUnit) == YamlSourceKind.Behavior);
Check("a document inside it names its unit",
    YamlSourceProbe.OwningUnit(Path.Combine(behaviorUnit, "behavior.yaml")) == behaviorUnit);

var characterUnit = Path.Combine(scratch, "units", "defaultmale.hkx");
Directory.CreateDirectory(characterUnit);
File.WriteAllText(Path.Combine(characterUnit, "character.yaml"), CharacterDoc);
Check("a character unit is a character, not a behaviour",
    YamlSourceProbe.ProbeUnit(characterUnit) == YamlSourceKind.Character);

// Node folders alone still count — that was the old folder test, and a unit that
// opened before must keep opening.
var bareUnit = Path.Combine(scratch, "units", "legacy.hkx");
Directory.CreateDirectory(Path.Combine(bareUnit, "generators"));
Check("node folders alone are still a behaviour unit",
    YamlSourceProbe.ProbeUnit(bareUnit) == YamlSourceKind.Behavior);

Check("an ordinary folder is not a unit",
    YamlSourceProbe.ProbeUnit(Path.Combine(scratch, "bin")) == YamlSourceKind.None);

// The write time a cached read is invalidated against has to move when a *node*
// changes, not only when the folder's own entry list does.
var before = YamlSourceProbe.UnitWriteTimeUtc(behaviorUnit);
var clip = Path.Combine(behaviorUnit, "clips", "Sprint.yaml");
File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(5));
Check("editing a clip moves the unit's write time",
    YamlSourceProbe.UnitWriteTimeUtc(behaviorUnit) > before);

// ── 4. Her corpus ─────────────────────────────────────────────────────────────

if (corpusRoot != null && Directory.Exists(corpusRoot))
{
    Console.WriteLine();
    Console.WriteLine($"Corpus: {corpusRoot}");

    int behaviours = 0, characters = 0, wrong = 0;
    var misread = new List<string>();

    foreach (var unit in Directory.EnumerateDirectories(corpusRoot, "*.hkx", SearchOption.AllDirectories))
    {
        // Expectation from the layout, never from the probe.
        var parent = Path.GetFileName(Path.GetDirectoryName(unit)!) ?? "";
        YamlSourceKind expected;
        if (parent.Equals("behaviors", StringComparison.OrdinalIgnoreCase)) expected = YamlSourceKind.Behavior;
        else if (parent.Equals("characters", StringComparison.OrdinalIgnoreCase)) expected = YamlSourceKind.Character;
        else continue;

        var got = YamlSourceProbe.ProbeUnit(unit);
        if (got != expected)
        {
            wrong++;
            if (misread.Count < 5) misread.Add($"{Path.GetFileName(unit)}: {got} ≠ {expected}");
            continue;
        }
        if (expected == YamlSourceKind.Behavior) behaviours++; else characters++;
    }

    Check($"every behaviour unit reads as one ({behaviours})", behaviours > 0 && wrong == 0,
        wrong == 0 ? null : string.Join("; ", misread));
    Check($"every character unit reads as one ({characters})", characters > 0);

    // The documents themselves, read as files rather than as folders.
    int docs = 0, docWrong = 0;
    foreach (var doc in Directory.EnumerateFiles(corpusRoot, "behavior.yaml", SearchOption.AllDirectories))
    {
        docs++;
        if (YamlSourceProbe.ProbeFile(doc) != YamlSourceKind.Behavior) docWrong++;
        if (docs >= 200) break;      // a sample is enough; there are thousands
    }
    Check($"behavior.yaml reads as a behaviour document ({docs} sampled)", docs > 0 && docWrong == 0,
        docWrong == 0 ? null : $"{docWrong} misread");

    // Nothing in a source tree may be mistaken for a packfile.
    int yamlFiles = 0, saidBinary = 0;
    foreach (var f in Directory.EnumerateFiles(corpusRoot, "*.yaml", SearchOption.AllDirectories))
    {
        if (++yamlFiles > 500) break;
        if (HkxConversionService.DetectFormat(f) == HkxFormat.HKX) saidBinary++;
    }
    Check($"no YAML document reads as a packfile ({yamlFiles} sampled)", saidBinary == 0);
}
else
{
    Console.WriteLine();
    Console.WriteLine("  (no --corpus given — her source tree was not checked)");
}

// ── 5. Real Havok files ───────────────────────────────────────────────────────

if (havokDir != null && Directory.Exists(havokDir))
{
    Console.WriteLine();
    Console.WriteLine($"Havok files: {havokDir}");

    int binaries = 0, xml = 0, misclassified = 0;
    var bad = new List<string>();

    foreach (var f in Directory.EnumerateFiles(havokDir, "*.*", SearchOption.AllDirectories))
    {
        var ext = Path.GetExtension(f).ToLowerInvariant();
        if (ext != ".hkx" && ext != ".xml") continue;

        var fmt = HkxConversionService.DetectFormat(f);
        if (YamlSourceProbe.ProbeFile(f) != YamlSourceKind.None)
        {
            misclassified++;
            if (bad.Count < 5) bad.Add(Path.GetFileName(f));
            continue;
        }
        if (fmt == HkxFormat.HKX) binaries++;
        else if (fmt == HkxFormat.XML) xml++;
    }

    // Both counts matter: a run that saw only one kind proves only half the rule,
    // and a green line over an empty folder proves none of it.
    Check($"real packfiles are still packfiles ({binaries})", binaries > 0);
    Check($"real Havok XML is still XML ({xml})", xml > 0);
    Check("no real Havok file reads as YAML source", misclassified == 0,
        misclassified == 0 ? null : string.Join(", ", bad));
}
else
{
    Console.WriteLine();
    Console.WriteLine("  (no --havok given — real binaries were not checked)");
}

try { Directory.Delete(scratch, recursive: true); } catch { }

Console.WriteLine();
Console.WriteLine(failed == 0
    ? $"All {checks} checks passed."
    : $"{failed} of {checks} checks FAILED.");
return failed == 0 ? 0 : 1;
