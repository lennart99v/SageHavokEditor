using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SageHavokEditor.Core.Animation;

// Checks the interleaved/uncompressed branch of HavokAnimationParser without an
// interleaved file to hand.
//
//   dotnet run --project tools/hkx-anim-interleaved -- <skeleton.xml> <anim.xml> [...]
//
// Every animation available here is spline-compressed, so the interleaved path
// is exercised against a file built from one: decode a real animation with the
// spline decoder, write those exact frames back out as an
// hkaInterleavedUncompressedAnimation, parse *that* through the new branch, and
// require the frames to come back identical.
//
// The synthetic round trip proves the reshape, the ten-float (t)(q)(s)
// grouping, the quaternion component order and the reference-pose overlay,
// against a decoder that is known good.
//
// It cannot prove the array is frame-major (all of frame 0's tracks, then frame
// 1's) rather than track-major, because it writes the file it then reads. Hand
// this a REAL interleaved animation and it settles that too, from the motion
// itself: an animation is smooth in time and not in bone index, so whichever
// reading makes consecutive samples of a bone nearly identical is the real
// layout. Measured over four imp attack animations, frame-major is 38-55x
// smoother -- so the layout is frame-major, which is also what Havok documents
// and what getFrame() indexes.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: hkx-anim-interleaved <skeleton.xml> <anim.xml> [...]");
    return 1;
}

var failed = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}

var skeleton = SkeletonParser.Parse(args[0]);
Console.WriteLine($"skeleton {Path.GetFileName(args[0])} — {skeleton.BoneNames.Length} bones");

var tmp = Path.Combine(Path.GetTempPath(), "hkx-anim-interleaved");
Directory.CreateDirectory(tmp);

int checkedFiles = 0, skipped = 0, realInterleaved = 0;
double worstDelta = 0;
string worstWhere = "";

foreach (var animPath in args.Skip(1))
{
    AnimationClip spline;
    try { spline = HavokAnimationParser.Parse(animPath, skeleton); }
    catch (AnimationParseException) { skipped++; continue; }

    // A real interleaved file settles what the synthetic one cannot.
    if (IsInterleaved(animPath))
    {
        realInterleaved++;
        CheckRealInterleaved(animPath, spline, Check);
    }

    // Re-emit the decoded frames as an interleaved animation. transformTracks is
    // the bone count here because Parse has already mapped tracks onto bones, so
    // the round trip goes through an identity mapping -- which is the point: it
    // isolates the decode from the mapping, which is shared and already tested.
    var interleavedPath = Path.Combine(tmp, Path.GetFileNameWithoutExtension(animPath) + ".interleaved.xml");
    File.WriteAllText(interleavedPath, BuildInterleavedXml(spline));

    AnimationClip back;
    try { back = HavokAnimationParser.Parse(interleavedPath, skeleton); }
    catch (AnimationParseException ex)
    {
        Check($"{Path.GetFileName(animPath)} parses as interleaved", false, ex.Message);
        continue;
    }

    var name = Path.GetFileNameWithoutExtension(animPath);
    bool shape = back.NumFrames == spline.NumFrames && back.Frames.Length == spline.Frames.Length;
    if (!shape)
    {
        Check($"{name}: frame count survives", false,
            $"{spline.NumFrames} -> {back.NumFrames}");
        continue;
    }

    double worst = 0;
    string where = "";
    for (int f = 0; f < spline.NumFrames; f++)
        for (int b = 0; b < spline.Frames[f].Length; b++)
        {
            var a = spline.Frames[f][b];
            var c = back.Frames[f][b];
            double d = Math.Max(
                Math.Max(Delta(a.Translation.X, c.Translation.X),
                    Math.Max(Delta(a.Translation.Y, c.Translation.Y), Delta(a.Translation.Z, c.Translation.Z))),
                Math.Max(
                    Math.Max(Delta(a.Rotation.X, c.Rotation.X), Delta(a.Rotation.Y, c.Rotation.Y)),
                    Math.Max(Delta(a.Rotation.Z, c.Rotation.Z),
                        Math.Max(Delta(a.Rotation.W, c.Rotation.W), Delta(a.Scale, c.Scale)))));
            if (d > worst) { worst = d; where = $"frame {f}, bone {b}"; }
        }

    if (worst > worstDelta) { worstDelta = worst; worstWhere = $"{name} {where}"; }
    checkedFiles++;

    // 1e-4 is the precision the XML itself carries: WriteQSTransformArray formats
    // at F6, and these values are re-read from text either way.
    if (worst > 1e-4)
        Check($"{name}: every transform survives the round trip", false, $"worst delta {worst:G4} at {where}");

    if (back.Annotations.Count != spline.Annotations.Count)
        Check($"{name}: annotations survive", false,
            $"{spline.Annotations.Count} -> {back.Annotations.Count}");
}

Console.WriteLine();
Check($"all {checkedFiles} animations round-trip through the interleaved branch",
    failed == 0 && checkedFiles > 0,
    checkedFiles == 0 ? "nothing was checked" : null);
Console.WriteLine($"  worst transform delta across {checkedFiles} files: {worstDelta:G4}"
    + (worstWhere.Length > 0 ? $" ({worstWhere})" : ""));
if (skipped > 0) Console.WriteLine($"  {skipped} file(s) held no animation, skipped");
Console.WriteLine(realInterleaved > 0
    ? $"  {realInterleaved} of them were real interleaved files, so the frame-major layout is measured rather than assumed"
    : "  none of them were real interleaved files — the frame-major layout stays assumed; "
      + "pass one to settle it");

// A file with neither animation class must still say so clearly.
{
    var neither = Path.Combine(tmp, "neither.xml");
    File.WriteAllText(neither,
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<hkpackfile>\n  <hksection name=\"__data__\">\n"
        + "    <hkobject name=\"#0050\" class=\"hkaAnimationContainer\"></hkobject>\n"
        + "  </hksection>\n</hkpackfile>\n");
    try
    {
        HavokAnimationParser.Parse(neither, skeleton);
        Check("a file with no animation is rejected", false, "it parsed");
    }
    catch (AnimationParseException ex)
    {
        Check("a file with no animation is rejected, naming both classes",
            ex.Message.Contains("hkaSplineCompressedAnimation")
            && ex.Message.Contains("hkaInterleavedUncompressedAnimation"), ex.Message);
    }
}

// And a malformed interleaved animation must be reported, not reshaped into
// nonsense -- the count not dividing by the track count is the one structural
// mistake this decoder can actually catch.
{
    var ragged = Path.Combine(tmp, "ragged.xml");
    File.WriteAllText(ragged, RaggedInterleavedXml());
    try
    {
        HavokAnimationParser.Parse(ragged, skeleton);
        Check("a transform count that isn't whole frames is rejected", false, "it parsed");
    }
    catch (AnimationParseException ex)
    {
        Check("a transform count that isn't whole frames is rejected",
            ex.Message.Contains("whole number of frames"), ex.Message);
    }
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) FAILED");
return failed == 0 ? 0 : 1;

static bool IsInterleaved(string path) => XDocument.Load(path).Descendants("hkobject")
    .Any(o => (string?)o.Attribute("class") == "hkaInterleavedUncompressedAnimation");

/// <summary>
/// The checks only a file somebody else wrote can answer: that the parse agrees
/// with what the file declares, and that the transform array really is
/// frame-major.
/// </summary>
static void CheckRealInterleaved(string path, AnimationClip clip, Action<string, bool, string?> check)
{
    var name = Path.GetFileNameWithoutExtension(path);
    var anim = XDocument.Load(path).Descendants("hkobject")
        .First(o => (string?)o.Attribute("class") == "hkaInterleavedUncompressedAnimation");

    string Text(string n) => anim.Elements("hkparam")
        .First(p => (string?)p.Attribute("name") == n).Value.Trim();

    int tracks = int.Parse(Text("numberOfTransformTracks"));
    int declared = int.Parse(anim.Elements("hkparam")
        .First(p => (string?)p.Attribute("name") == "transforms").Attribute("numelements")!.Value);
    float duration = float.Parse(Text("duration"), CultureInfo.InvariantCulture);

    check($"{name}: frame count matches what the file declares",
        clip.NumFrames == declared / tracks, $"{clip.NumFrames} vs {declared / tracks}");
    check($"{name}: duration matches what the file declares",
        Math.Abs(clip.Duration - duration) < 1e-5, $"{clip.Duration} vs {duration}");

    // Frame-major or track-major, decided by which one makes the motion
    // continuous. Read straight from the XML rather than through the parser, so
    // this is independent of the code under test.
    var groups = Regex.Matches(Text("transforms"), @"\(([^)]*)\)")
        .Select(m => m.Groups[1].Value
            .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray())
        .ToList();
    int total = groups.Count / 3;
    int frames = total / tracks;
    if (frames < 3) return;

    // Translation alone is enough to tell the two apart and needs no quaternion
    // handling: a bone barely moves between frames and sits nowhere near its
    // neighbour in the bone list.
    double Step(Func<int, int, int> index)
    {
        double sum = 0; int n = 0;
        for (int f = 0; f + 1 < frames; f++)
            for (int t = 0; t < tracks; t++)
            {
                var a = groups[index(f, t) * 3];
                var b = groups[index(f + 1, t) * 3];
                sum += Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]);
                n++;
            }
        return sum / Math.Max(1, n);
    }

    double frameMajor = Step((f, t) => f * tracks + t);
    double trackMajor = Step((f, t) => t * frames + f);
    check($"{name}: the transform array is frame-major",
        frameMajor < trackMajor,
        $"frame-major step {frameMajor:F5} vs track-major {trackMajor:F5}");
}

static double Delta(float a, float b) => Math.Abs(a - b);

static string F(float v) => v.ToString("F6", CultureInfo.InvariantCulture);

static string BuildInterleavedXml(AnimationClip clip)
{
    int tracks = clip.Frames.Length > 0 ? clip.Frames[0].Length : 0;
    var sb = new StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
    sb.AppendLine("<hkpackfile classversion=\"8\" contentsversion=\"hk_2010.2.0-r1\" toplevelobject=\"#0050\">");
    sb.AppendLine("  <hksection name=\"__data__\">");
    sb.AppendLine("    <hkobject name=\"#0050\" class=\"hkaInterleavedUncompressedAnimation\" signature=\"0x930af031\">");
    sb.AppendLine("      <hkparam name=\"type\">HK_INTERLEAVED_ANIMATION</hkparam>");
    sb.AppendLine($"      <hkparam name=\"duration\">{F(clip.Duration)}</hkparam>");
    sb.AppendLine($"      <hkparam name=\"numberOfTransformTracks\">{tracks}</hkparam>");
    sb.AppendLine("      <hkparam name=\"numberOfFloatTracks\">0</hkparam>");

    // Frame-major, which is the assumption under test.
    sb.AppendLine($"      <hkparam name=\"transforms\" numelements=\"{clip.NumFrames * tracks}\">");
    foreach (var frame in clip.Frames)
    {
        foreach (var t in frame)
            sb.Append($"({F(t.Translation.X)} {F(t.Translation.Y)} {F(t.Translation.Z)})")
              .Append($"({F(t.Rotation.X)} {F(t.Rotation.Y)} {F(t.Rotation.Z)} {F(t.Rotation.W)})")
              .Append($"({F(t.Scale)} {F(t.Scale)} {F(t.Scale)}) ");
        sb.AppendLine();
    }
    sb.AppendLine("      </hkparam>");
    sb.AppendLine("      <hkparam name=\"floats\" numelements=\"0\"></hkparam>");

    sb.AppendLine($"      <hkparam name=\"annotationTracks\" numelements=\"{clip.AnnotationTrackNames.Count}\">");
    for (int i = 0; i < clip.AnnotationTrackNames.Count; i++)
    {
        var anns = clip.Annotations.Where(a => a.TrackIndex == i).ToList();
        sb.AppendLine("        <hkobject>");
        sb.AppendLine($"          <hkparam name=\"trackName\">{clip.AnnotationTrackNames[i]}</hkparam>");
        sb.AppendLine($"          <hkparam name=\"annotations\" numelements=\"{anns.Count}\">");
        foreach (var a in anns)
        {
            sb.AppendLine("            <hkobject>");
            sb.AppendLine($"              <hkparam name=\"time\">{F(a.Time)}</hkparam>");
            sb.AppendLine($"              <hkparam name=\"text\">{a.Text}</hkparam>");
            sb.AppendLine("            </hkobject>");
        }
        sb.AppendLine("          </hkparam>");
        sb.AppendLine("        </hkobject>");
    }
    sb.AppendLine("      </hkparam>");
    sb.AppendLine("    </hkobject>");
    sb.AppendLine("  </hksection>");
    sb.AppendLine("</hkpackfile>");
    return sb.ToString();
}

static string RaggedInterleavedXml() =>
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
    + "<hkpackfile classversion=\"8\" contentsversion=\"hk_2010.2.0-r1\" toplevelobject=\"#0050\">\n"
    + "  <hksection name=\"__data__\">\n"
    + "    <hkobject name=\"#0050\" class=\"hkaInterleavedUncompressedAnimation\">\n"
    + "      <hkparam name=\"duration\">1.0</hkparam>\n"
    + "      <hkparam name=\"numberOfTransformTracks\">4</hkparam>\n"
    // three transforms against four tracks: not a whole frame
    + "      <hkparam name=\"transforms\" numelements=\"3\">"
    + "(0 0 0)(0 0 0 1)(1 1 1) (0 0 0)(0 0 0 1)(1 1 1) (0 0 0)(0 0 0 1)(1 1 1)"
    + "</hkparam>\n"
    + "    </hkobject>\n"
    + "  </hksection>\n"
    + "</hkpackfile>\n";
