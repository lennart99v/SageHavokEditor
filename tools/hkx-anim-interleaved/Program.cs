using System.Globalization;
using System.Text;
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
// What this does and does not prove is worth being clear about, because the
// feature was written without a sample. It proves the reshape, the ten-float
// (t)(q)(s) grouping, the quaternion component order and the reference-pose
// overlay, all against a decoder that is known good. It does NOT prove the one
// thing no file here can settle: that a real Havok file stores the array
// frame-major (all of frame 0's tracks, then frame 1's) rather than track-major.
// That is Havok's documented layout and what getFrame() indexes, but it is an
// assumption until a real interleaved file is run through this.

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

int checkedFiles = 0, skipped = 0;
double worstDelta = 0;
string worstWhere = "";

foreach (var animPath in args.Skip(1))
{
    AnimationClip spline;
    try { spline = HavokAnimationParser.Parse(animPath, skeleton); }
    catch (AnimationParseException) { skipped++; continue; }

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
if (skipped > 0) Console.WriteLine($"  {skipped} file(s) the spline path could not read, skipped");

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
