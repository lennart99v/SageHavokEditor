using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Animation;
using SageHavokEditor.Tools.FbxExport;

// Can an hkaSplineCompressedAnimation plus its skeleton.hkx come back out as FBX?
//
//   dotnet run --project tools/hkx-fbx-export -- <animation.hkx|xml> <skeleton.hkx|xml>
//                                                [-o out.fbx] [--scale N] [--ascii]
//   dotnet run --project tools/hkx-fbx-export -- --selftest <skeleton.hkx|xml> [-o out.fbx]
//
// The decode half already exists and is validated by the clip preview
// (HavokSplineDecoder + HavokAnimationParser hand back [frame][bone] local
// transforms). What was missing is a serialiser, so this writes FBX 7.4 by hand
// rather than binding a C library — a bone-only animation file is Models, curve
// nodes and curves and nothing else, which is a few hundred lines against a
// native build plus P/Invoke plus a shipped x64 DLL.
//
// It writes BINARY FBX. The first cut wrote the ASCII form on the theory that it
// is readable and every tool takes it; Blender 4.5 answers "ASCII FBX files are
// not supported" and refuses the file outright, so ASCII survives only behind
// --ascii as a debug view of the same record tree.
//
// The risk moved with the format. Nothing here can get the spline maths wrong;
// what it CAN get wrong is the quaternion→Euler conversion FBX forces on us, so
// the checks below round-trip every rotation of every frame back to a quaternion
// and report the worst error, and separately report the largest frame-to-frame
// Euler jump left after unwrapping. Those two numbers are the difference between
// "Blender shows something" and "Blender shows the right thing".
//
// --selftest needs no animation file. It drives a synthetic clip (one bone
// through two full turns, the root sliding) over a real skeleton, which is
// enough to exercise the writer, the hierarchy and the unwrap on a machine that
// has a skeleton.hkx but no animations.
//
// Not yet handled, deliberately: numBlocks > 1 (the parser refuses it), scale
// tracks (the decoder skips them), and the Havok-unit → FBX-centimetre scale,
// which is --scale because it wants calibrating against a known clip rather
// than a constant guessed here.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var failed = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failed++;
}

// ── args ─────────────────────────────────────────────────────────────────────
string? outPath = null;
float scale = 1f;
bool selfTest = false, alsoAscii = false;
{
    var rest = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-o" when i + 1 < args.Length: outPath = args[++i]; break;
            case "--scale" when i + 1 < args.Length:
                scale = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--selftest": selfTest = true; break;
            case "--ascii": alsoAscii = true; break;
            default: rest.Add(args[i]); break;
        }
    }
    args = rest.ToArray();
}

if (selfTest ? args.Length < 1 : args.Length < 2)
{
    Console.Error.WriteLine("usage: hkx-fbx-export <animation.hkx|xml> <skeleton.hkx|xml> [-o out.fbx] [--scale N] [--ascii]");
    Console.Error.WriteLine("       hkx-fbx-export --selftest <skeleton.hkx|xml> [-o out.fbx]");
    return 2;
}

var animPath = selfTest ? null : args[0];
var skeletonPath = selfTest ? args[0] : args[1];

var conv = new HkxConversionService();

// ── the skeleton ─────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== the skeleton ==");

Skeleton skeleton;
try
{
    var prep = await conv.PrepareXmlAsync(skeletonPath);
    if (!prep.Success || prep.XmlPath is null)
    { Check("skeleton converts to XML", false, prep.Error); return 1; }
    Check("skeleton converts to XML", true, Path.GetFileName(prep.XmlPath));
    skeleton = SkeletonParser.Parse(prep.XmlPath);
}
catch (Exception ex) { Check("skeleton parses", false, ex.Message); return 1; }

Check("it has bones", skeleton.ReferencePose.Length > 0,
    $"{skeleton.ReferencePose.Length} bones, root {skeleton.BoneNames.FirstOrDefault()}");

// A parent must exist and must come BEFORE its child, or ComputeWorld silently
// composes against an unwritten slot and the FBX hierarchy loops.
var badParent = Enumerable.Range(0, skeleton.ParentIndices.Length)
    .Where(i => skeleton.ParentIndices[i] >= i).ToList();
Check("every parent precedes its child", badParent.Count == 0,
    badParent.Count == 0 ? $"{skeleton.ParentIndices.Count(p => p < 0)} root(s)"
                         : $"{badParent.Count} out of order, first at index {badParent[0]}");

Check("names line up with the pose", skeleton.BoneNames.Length == skeleton.ReferencePose.Length,
    $"{skeleton.BoneNames.Length} names vs {skeleton.ReferencePose.Length} poses");

// Nothing in the file says what a Havok unit is worth, so print the rest pose's
// extent and let the number decide --scale rather than guessing a constant here.
// Blender reads UnitScaleFactor 1 as centimetres and divides by 100 on import,
// so a bone at Havok 100 lands at 1.0 Blender unit unless --scale says otherwise.
{
    var world = HkTransform.ComputeWorld(skeleton.ReferencePose, skeleton.ParentIndices);
    var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
    foreach (var w in world) { min = Vector3.Min(min, w.Translation); max = Vector3.Max(max, w.Translation); }
    var span = max - min;
    Console.WriteLine($"          -> rest pose spans {span.X:0.#} x {span.Y:0.#} x {span.Z:0.#} Havok units; " +
        $"at --scale {scale:0.###} that is {span.X * scale / 100:0.##} x {span.Y * scale / 100:0.##} x " +
        $"{span.Z * scale / 100:0.##} Blender units");
}

// ── the clip ─────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine(selfTest ? "== the synthetic clip ==" : "== the animation ==");

AnimationClip clip;
double frameDuration;

if (selfTest)
{
    const int frames = 61;
    frameDuration = 1.0 / 30.0;
    int nb = skeleton.ReferencePose.Length;
    int spin = Math.Min(1, nb - 1);              // second bone if there is one

    var f = new HkTransform[frames][];
    for (int i = 0; i < frames; i++)
    {
        var row = (HkTransform[])skeleton.ReferencePose.Clone();
        float t = i / (float)(frames - 1);
        // two full turns, so the Euler conversion has to wrap and unwrap repeatedly
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, t * 4f * MathF.PI);
        row[spin] = new HkTransform
        {
            Translation = row[spin].Translation,
            Rotation = Quaternion.Normalize(q * row[spin].Rotation),
            Scale = 1f
        };
        row[0] = new HkTransform
        {
            Translation = row[0].Translation + new Vector3(0, t * 100f, 0),
            Rotation = row[0].Rotation,
            Scale = 1f
        };
        f[i] = row;
    }

    clip = new AnimationClip
    {
        Duration = (float)((frames - 1) * frameDuration),
        NumFrames = frames,
        NumTracks = nb,
        Frames = f
    };
    Check("synthetic clip built", true, $"{frames} frames, spinning bone {spin} " +
        $"({(spin < skeleton.BoneNames.Length ? skeleton.BoneNames[spin] : "?")}) through 720 degrees");
}
else
{
    string animXml;
    try
    {
        var prep = await conv.PrepareXmlAsync(animPath!);
        if (!prep.Success || prep.XmlPath is null)
        { Check("animation converts to XML", false, prep.Error); return 1; }
        animXml = prep.XmlPath;
        Check("animation converts to XML", true, Path.GetFileName(animXml));
    }
    catch (Exception ex) { Check("animation converts to XML", false, ex.Message); return 1; }

    try { clip = HavokAnimationParser.Parse(animXml, skeleton); }
    catch (Exception ex) { Check("animation parses", false, ex.Message); return 1; }

    Check("it has frames", clip.NumFrames > 0,
        $"{clip.NumFrames} frames, {clip.NumTracks} tracks, {clip.Duration:0.###}s");
    Check("tracks fit the skeleton", !clip.TrackCountExceedsBones,
        clip.TrackCountExceedsBones
            ? $"{clip.NumTracks} tracks over {skeleton.ReferencePose.Length} bones"
            : $"{clip.NumTracks} of {skeleton.ReferencePose.Length} bones animated");

    // frameDuration is stored on the animation; deriving it from duration/numFrames
    // is the classic off-by-one-frame timing bug, so prefer the file's own number.
    var stated = ReadFrameDuration(animXml);
    frameDuration = stated ?? (clip.NumFrames > 1 ? clip.Duration / (clip.NumFrames - 1) : 1.0 / 30.0);
    var derived = clip.NumFrames > 1 ? clip.Duration / (clip.NumFrames - 1) : 0;
    Check("frame duration is the file's own", stated is not null,
        $"{frameDuration * 1000:0.###} ms ({1 / frameDuration:0.##} fps), " +
        $"duration/(n-1) would give {derived * 1000:0.###} ms");

    if (clip.Annotations.Count > 0)
        Console.WriteLine($"          -> {clip.Annotations.Count} annotation(s), " +
            $"first \"{clip.Annotations[0].Text}\" at {clip.Annotations[0].Time:0.###}s");
}

// ── the conversion FBX forces on us ──────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== quaternion to Euler ==");

double worstRoundTrip = 0; int worstBone = -1, worstFrame = -1;
double worstJump = 0; int jumpBone = -1, jumpFrame = -1;

for (int b = 0; b < skeleton.ReferencePose.Length; b++)
{
    double px = 0, py = 0, pz = 0;
    for (int f = 0; f < clip.NumFrames; f++)
    {
        var row = clip.Frames[f];
        var q = Quaternion.Normalize(b < row.Length ? row[b].Rotation : skeleton.ReferencePose[b].Rotation);

        var (ex, ey, ez) = FbxAnimationScene.QuatToEulerXyzDegrees(q);

        // does the Euler mean the same rotation? (sign is free — q and -q agree)
        var back = EulerXyzDegreesToQuat(ex, ey, ez);
        double dot = Math.Abs(q.X * back.X + q.Y * back.Y + q.Z * back.Z + q.W * back.W);
        double err = 1.0 - Math.Min(1.0, dot);
        if (err > worstRoundTrip) { worstRoundTrip = err; worstBone = b; worstFrame = f; }

        if (f > 0)
        {
            ex = FbxAnimationScene.Unwrap(ex, px);
            ey = FbxAnimationScene.Unwrap(ey, py);
            ez = FbxAnimationScene.Unwrap(ez, pz);
            double jump = Math.Max(Math.Abs(ex - px), Math.Max(Math.Abs(ey - py), Math.Abs(ez - pz)));
            if (jump > worstJump) { worstJump = jump; jumpBone = b; jumpFrame = f; }
        }
        px = ex; py = ey; pz = ez;
    }
}

Check("every rotation survives the round trip", worstRoundTrip < 1e-6,
    $"worst 1-|dot| = {worstRoundTrip:0.###e+0}" +
    (worstBone < 0 ? "" : $" at bone {worstBone} frame {worstFrame}"));

// After unwrapping, a jump near 180 means the curve genuinely turns that fast in
// one frame — possible, but far more often it is the sign of a decode problem,
// which is exactly the "wrong on some frames" symptom.
Check("no frame-to-frame Euler jump survives the unwrap", worstJump < 170,
    $"worst {worstJump:0.##} deg" +
    (jumpBone < 0 ? "" : $" at bone {jumpBone} ({Name(jumpBone)}) frame {jumpFrame}"));

// ── build and write ──────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== the FBX ==");

outPath ??= Path.ChangeExtension(
    selfTest ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(skeletonPath))!, "selftest")
             : Path.GetFullPath(animPath!), ".fbx");

List<FbxNode> tree;
try
{
    tree = FbxAnimationScene.Build(skeleton, clip, new FbxExportOptions
    {
        TranslationScale = scale,
        FrameDuration = frameDuration,
        TakeName = Path.GetFileNameWithoutExtension(selfTest ? "selftest" : animPath!)
    });
}
catch (Exception ex) { Check("scene builds", false, ex.Message); return 1; }

int nbones = skeleton.ReferencePose.Length;
var all = Flatten(tree).ToList();
Check("one Model per bone", all.Count(n => n.Name == "Model") == nbones,
    $"{all.Count(n => n.Name == "Model")} of {nbones}");
Check("six curves per bone", all.Count(n => n.Name == "AnimationCurve") == nbones * 6,
    $"{all.Count(n => n.Name == "AnimationCurve")} of {nbones * 6}");
Check("two curve nodes per bone", all.Count(n => n.Name == "AnimationCurveNode") == nbones * 2,
    $"{all.Count(n => n.Name == "AnimationCurveNode")} of {nbones * 2}");

// Every id a connection names has to be an object we actually emitted, or the
// importer drops that bone without saying so — the same silent-failure shape the
// rest of this domain has.
var declared = new HashSet<long> { 0 };            // 0 = RootNode
foreach (var n in all)
    if (n.Name is "Model" or "NodeAttribute" or "AnimationStack" or "AnimationLayer"
                or "AnimationCurveNode" or "AnimationCurve"
        && n.Props.Count > 0 && n.Props[0].Code == 'L')
        declared.Add((long)n.Props[0].Value);

var dangling = all.Where(n => n.Name == "C")
    .SelectMany(n => n.Props.Where(p => p.Code == 'L').Select(p => (long)p.Value))
    .Where(id => !declared.Contains(id)).ToList();
Check("every connection names a declared object", dangling.Count == 0,
    dangling.Count == 0 ? $"{declared.Count - 1} objects" : $"{dangling.Count} dangling, first {dangling[0]}");

try { FbxBinarySerializer.Write(outPath, tree); }
catch (Exception ex) { Check("FBX writes", false, ex.Message); return 1; }
Check("FBX writes", File.Exists(outPath), $"{new FileInfo(outPath).Length / 1024.0:0.#} KB -> {outPath}");

// The header is the one thing a reader checks before anything else.
using (var fs = File.OpenRead(outPath))
{
    var head = new byte[27];
    fs.ReadExactly(head);
    var magic = "Kaydara FBX Binary  \0\0";
    bool ok = head.Take(23).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(magic).Take(23));
    uint ver = BitConverter.ToUInt32(head, 23);
    Check("binary header and version", ok && ver == FbxBinarySerializer.Version, $"version {ver}");
}

if (alsoAscii)
{
    var asciiPath = Path.ChangeExtension(outPath, ".ascii.fbx");
    FbxAsciiSerializer.Write(asciiPath, tree);
    Console.WriteLine($"          -> debug view written to {asciiPath} (Blender will NOT import this one)");
}

Console.WriteLine();
Console.WriteLine(failed == 0
    ? "All checks passed. Import it in Blender (File > Import > FBX) and compare against the clip preview."
    : $"{failed} check(s) failed.");
return failed == 0 ? 0 : 1;

// ── local helpers ────────────────────────────────────────────────────────────

string Name(int b) => b >= 0 && b < skeleton.BoneNames.Length ? skeleton.BoneNames[b] : $"bone {b}";

static IEnumerable<FbxNode> Flatten(IEnumerable<FbxNode> nodes)
{
    foreach (var n in nodes)
    {
        yield return n;
        foreach (var c in Flatten(n.Children)) yield return c;
    }
}

/// <summary>hkaSplineCompressedAnimation carries the per-frame time itself.</summary>
static double? ReadFrameDuration(string xmlPath)
{
    try
    {
        var anim = XDocument.Load(xmlPath).Descendants("hkobject")
            .FirstOrDefault(o => (string?)o.Attribute("class") == "hkaSplineCompressedAnimation");
        var raw = anim?.Elements("hkparam")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "frameDuration")?.Value?.Trim();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
            return v;
    }
    catch { }
    return null;
}

/// <summary>Inverse of the writer's extraction: q = qz * qy * qx, Hamilton order.</summary>
static Quaternion EulerXyzDegreesToQuat(double xDeg, double yDeg, double zDeg)
{
    const double ToRad = Math.PI / 180.0;
    double hx = xDeg * ToRad / 2, hy = yDeg * ToRad / 2, hz = zDeg * ToRad / 2;
    var qx = new Quaternion((float)Math.Sin(hx), 0, 0, (float)Math.Cos(hx));
    var qy = new Quaternion(0, (float)Math.Sin(hy), 0, (float)Math.Cos(hy));
    var qz = new Quaternion(0, 0, (float)Math.Sin(hz), (float)Math.Cos(hz));
    return Ham(qz, Ham(qy, qx));

    static Quaternion Ham(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
}
