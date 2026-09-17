using System.Globalization;
using System.Text.RegularExpressions;
using SageHavokEditor.Core;

// hkx-varvalues — pin the hkbVariableValueSet word codec.
//
// wordVariableValues[i].value is one hkInt32 per variable whatever the variable
// is. A VARIABLE_TYPE_REAL keeps its float bit-cast into it; everything else is
// the plain number. Nothing in the value says which — only hkbVariableInfo.type
// does — so the only property that matters is that decode -> encode is the
// identity for every word, under the variable's declared type.
//
// That is worth a harness rather than an eyeball because the failure is silent.
// The old rules turned 1065353216 (1.0f) into the integer 1, which is a valid
// hkInt32, so nothing downstream had grounds to complain: the file saved, it
// converted, and the variable was simply a different number afterwards.
//
//   dotnet run --project tools/hkx-varvalues -- [<a behaviour .xml>...]

int checks = 0, failures = 0;

void Check(bool ok, string what, string? detail = null)
{
    checks++;
    if (ok) { Console.WriteLine($"  ok   {what}"); return; }
    failures++;
    Console.WriteLine($"  FAIL {what}{(detail is null ? "" : "\n         " + detail)}");
}

const string REAL = "VARIABLE_TYPE_REAL";
const string INT = "VARIABLE_TYPE_INT32";
const string BOOL = "VARIABLE_TYPE_BOOL";
const string PTR = "VARIABLE_TYPE_POINTER";

string Round(int word, string type) => HavokVariableValue.Encode(
    HavokVariableValue.Decode(word.ToString(CultureInfo.InvariantCulture), type), type);

// ── The identity that matters ────────────────────────────────────────────────
Console.WriteLine("Decode -> Encode is the identity");

// The values from the file this was found in, plus the boundaries.
int[] interesting =
{
    0, 1, -1, 2, 1000, -1000,
    1053609165,   // 0.4f
    1036831949,   // 0.1f
    1056964608,   // 0.5f
    1065353216,   // 1.0f  — the one that used to become the integer 1
    -1073490166,  // -2.06f — the one that used to become 3221477130
    int.MaxValue, int.MinValue, int.MaxValue - 1, int.MinValue + 1,
    0x7F7FFFFF,   // float.MaxValue
    unchecked((int)0xFF7FFFFF), // float.MinValue
    0x00800000,   // smallest normal float
    1,            // smallest denormal as REAL
};

// A NaN's payload is the one thing no float text carries — every NaN spelling
// parses back to the canonical 0x7FC00000 — so NaN words are excluded here and
// checked separately below. Nothing in the editor rewrites a word the user has
// not edited, which is what keeps an exotic NaN in a real file intact.
static bool IsNanWord(int w) => float.IsNaN(BitConverter.Int32BitsToSingle(w));

foreach (var type in new[] { REAL, INT, BOOL, PTR })
{
    var bad = interesting
        .Where(v => !(type == REAL && IsNanWord(v)))
        .Where(v => Round(v, type) != v.ToString(CultureInfo.InvariantCulture)).ToList();
    Check(bad.Count == 0, $"every sample word survives a round trip as {type}",
        bad.Count == 0 ? null
            : string.Join(", ", bad.Select(v => $"{v} -> {Round(v, type)}")));
}

// The infinities do round-trip, and are worth pinning because the obvious way to
// carve NaN out drags them along with it.
foreach (var (bits, label) in new[]
         { (0x7F800000, "+Infinity"), (unchecked((int)0xFF800000), "-Infinity") })
    Check(Round(bits, REAL) == bits.ToString(CultureInfo.InvariantCulture),
        $"{label} round-trips exactly", $"{bits} -> {Round(bits, REAL)}");

Check(HavokVariableValue.Decode("-1", REAL) == "NaN",
    "a REAL word that is NaN reads as NaN, not as the integer -1",
    "showing the raw word there is what made \"-1\" typed for -1.0 ambiguous");

// A sweep, because the interesting list is chosen by hand and the failure mode
// was a value nobody thought to choose.
var rng = new Random(20260917);
int swept = 0, sweptBad = 0;
string? firstBad = null;
for (int i = 0; i < 200_000; i++)
{
    int v = rng.Next(int.MinValue, int.MaxValue);
    foreach (var type in new[] { REAL, INT })
    {
        if (type == REAL && IsNanWord(v)) continue;
        swept++;
        var got = Round(v, type);
        if (got == v.ToString(CultureInfo.InvariantCulture)) continue;
        sweptBad++;
        firstBad ??= $"{v} as {type} -> {got}";
    }
}
Check(sweptBad == 0, $"a {swept:N0}-word random sweep round-trips as REAL and INT",
    firstBad);

// ── The two specific corruptions ─────────────────────────────────────────────
Console.WriteLine("\nThe two shapes this fixes");

Check(HavokVariableValue.Decode("1065353216", REAL) == "1",
    "1.0f still shows as \"1\" — the display was never the problem",
    $"got \"{HavokVariableValue.Decode("1065353216", REAL)}\"");
Check(HavokVariableValue.Encode("1", REAL) == "1065353216",
    "...and \"1\" typed against a REAL encodes back to the bit pattern, not the integer 1",
    $"got \"{HavokVariableValue.Encode("1", REAL)}\"");
Check(HavokVariableValue.Encode("1", INT) == "1",
    "while \"1\" against an INT stays the integer 1");

Check(HavokVariableValue.Encode("-2.06", REAL) == "-1073490166",
    "a negative float encodes to a SIGNED hkInt32, in range",
    $"got \"{HavokVariableValue.Encode("-2.06", REAL)}\"");
Check(long.Parse(HavokVariableValue.Encode("-2.06", REAL)) is >= int.MinValue and <= int.MaxValue,
    "...which is the whole reason the HKX save used to be refused");

// Opening a file the old encoder already damaged should show the right number.
Check(HavokVariableValue.Decode("3221477130", REAL) == "-2.06",
    "the unsigned form an older save wrote is read back as the value it means",
    $"got \"{HavokVariableValue.Decode("3221477130", REAL)}\"");
Check(HavokVariableValue.Encode(HavokVariableValue.Decode("3221477130", REAL), REAL)
        == "-1073490166",
    "...and saving such a file repairs it rather than preserving the overflow");

// Precision: the old formatter truncated at three decimals.
foreach (var probe in new[] { 0.123456f, 1234.5678f, 1e-8f, 3.14159265f })
{
    int bits = BitConverter.SingleToInt32Bits(probe);
    Check(Round(bits, REAL) == bits.ToString(CultureInfo.InvariantCulture),
        $"{probe} keeps its bits (the old \"0.###\" format did not)",
        $"{bits} -> {Round(bits, REAL)}");
}

// ── What the old rules did, on the same words ────────────────────────────────
//
// Re-implemented here so the harness can say what was actually wrong with real
// data, and so it goes red if anyone reintroduces either rule.
static string OldDecode(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return "0";
    if (long.TryParse(raw, out long l))
    {
        int v = (int)l;
        if (Math.Abs(v) > 1000000 || v < 0)
        {
            float f = BitConverter.Int32BitsToSingle(v);
            if (!float.IsNaN(f) && !float.IsInfinity(f))
                return f.ToString("0.###", CultureInfo.InvariantCulture);
        }
        return v.ToString();
    }
    return raw;
}

static string OldEncode(string s)
{
    if (string.IsNullOrWhiteSpace(s)) return "0";
    if (s.Contains('.') && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
        return BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString();
    return s;
}

Console.WriteLine("\nThe old rules, for contrast");
Check(OldEncode(OldDecode("1065353216")) == "1",
    "the old pair really did turn 1.0f into the integer 1");
Check(OldEncode(OldDecode("-1073490166")) == "3221477130",
    "and a negative float into an out-of-range unsigned word");

// ── Against real files ───────────────────────────────────────────────────────
//
// Read with a regex rather than the editor's loader on purpose: the subject of
// this test must not be located by the code under test.
var files = args.Where(a => !a.StartsWith("--")).ToList();
foreach (var path in files)
{
    Console.WriteLine($"\n{Path.GetFileName(path)}");
    if (!File.Exists(path)) { Console.WriteLine("  (not found)"); continue; }

    var xml = File.ReadAllText(path);

    // Every variableInfos element nests its own <hkparam name="role"> block, so
    // matching to the next </hkparam> closes on the wrong tag and finds nothing.
    // Take a window sized from the array's declared numelements instead.
    var infosAt = xml.IndexOf(@"<hkparam name=""variableInfos""", StringComparison.Ordinal);
    int declared = infosAt < 0 ? 0
        : int.Parse(Regex.Match(xml.Substring(infosAt, 120), @"numelements=""(\d+)""")
            .Groups[1].Value);
    var types = infosAt < 0 ? new List<string>()
        : Regex.Matches(
                xml.Substring(infosAt, Math.Min(400 * Math.Max(declared, 1), xml.Length - infosAt)),
                @"<hkparam name=""type"">([^<]*)</hkparam>")
            .Select(m => m.Groups[1].Value.Trim()).Take(declared).ToList();

    var setIdx = xml.IndexOf("class=\"hkbVariableValueSet\"", StringComparison.Ordinal);
    if (setIdx < 0) { Console.WriteLine("  (no hkbVariableValueSet)"); continue; }
    var wordIdx = xml.IndexOf("wordVariableValues", setIdx, StringComparison.Ordinal);
    var words = Regex.Matches(xml.Substring(wordIdx, Math.Min(60000, xml.Length - wordIdx)),
            @"<hkparam name=""value"">([^<]*)</hkparam>")
        .Select(m => m.Groups[1].Value.Trim()).ToList();

    var names = Regex.Matches(
            Regex.Match(xml, @"<hkparam name=""variableNames""[^>]*>(.*?)</hkparam>",
                RegexOptions.Singleline).Groups[1].Value,
            @"<hkcstring>([^<]*)</hkcstring>")
        .Select(m => m.Groups[1].Value).ToList();

    int n = Math.Min(types.Count, words.Count);
    if (n == 0) { Console.WriteLine("  (could not read types and words)"); continue; }
    Console.WriteLine($"  {n} variables ({types.Count(t => HavokVariableValue.IsReal(t))} real)");

    // Compare the 32 bits, not the text. A file an older save damaged holds
    // 3221477130 where -1073490166 belongs, and those are the same word — so
    // rewriting it is a repair of the spelling, not a change of value, and the
    // check has to be able to tell the two apart.
    static long Bits(string s) => long.TryParse(s, out long v) ? unchecked((int)(uint)v) : long.MinValue;

    var broken = new List<string>();
    var repaired = new List<string>();
    var oldBroken = new List<string>();
    for (int i = 0; i < n; i++)
    {
        var name = i < names.Count ? names[i] : $"[{i}]";
        var w = words[i];
        var after = HavokVariableValue.Encode(HavokVariableValue.Decode(w, types[i]), types[i]);

        if (Bits(after) != Bits(w)) broken.Add($"{name} ({types[i]}): {w} -> {after}");
        else if (after != w) repaired.Add($"{name}: {w} -> {after}");

        if (OldEncode(OldDecode(w)) != w)
            oldBroken.Add($"{name}: {w} -> {OldEncode(OldDecode(w))}");
    }

    Check(broken.Count == 0,
        $"no word in {Path.GetFileName(path)} changes value across a save",
        string.Join("\n         ", broken));

    if (repaired.Count > 0)
    {
        Console.WriteLine($"  {repaired.Count} word(s) rewritten to the same bits in signed form "
                        + "(repairing an older save):");
        foreach (var r in repaired) Console.WriteLine($"         {r}");
    }

    Console.WriteLine($"  the old rules would have changed {oldBroken.Count} of these:");
    foreach (var b in oldBroken) Console.WriteLine($"         {b}");
}

Console.WriteLine($"\n{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;
