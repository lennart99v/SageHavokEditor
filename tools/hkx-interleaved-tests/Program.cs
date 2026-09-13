using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using HKX2;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Animation;

// Synthetic fixtures only: no Skyrim assets required. Compile the shipped code.
// dotnet run --project tools/hkx-interleaved-tests -c Release
var directory = Path.Combine(Path.GetTempPath(), "sage-interleaved-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
int passed = 0, failed = 0;
var skeleton = new Skeleton
{
    BoneNames = new[] { "root", "unchanged", "animated" },
    ParentIndices = new[] { -1, 0, 0 },
    ReferencePose = new[]
    {
        HkTransform.Identity,
        new HkTransform { Translation = new Vector3(0, 7, 0), Rotation = Quaternion.Identity, Scale = 1 },
        HkTransform.Identity
    }
};

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

async Task Test(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}

Task Sync(Action action) { action(); return Task.CompletedTask; }

XElement P(XElement element, string name) =>
    element.Elements("hkparam").Single(p => (string?)p.Attribute("name") == name);
XElement Anim(XDocument doc) => doc.Descendants("hkobject")
    .Single(o => (string?)o.Attribute("class") == "hkaInterleavedUncompressedAnimation");

Matrix4x4 Transform(float x, float y = 0, float z = 0) => new(
    x, y, z, 0,
    0, 0, 0, 1,
    1, 1, 1, 0,
    0, 0, 0, 0);

hkRootLevelContainer Fixture()
{
    var animation = new hkaInterleavedUncompressedAnimation
    {
        m_type = (int)AnimationType.HK_INTERLEAVED_ANIMATION,
        m_duration = 1,
        m_numberOfTransformTracks = 2,
        m_numberOfFloatTracks = 1,
        m_transforms = new[] { Transform(1), Transform(10), Transform(2), Transform(20), Transform(3), Transform(30) },
        m_floats = new[] { .123456789f, float.Epsilon, -.987654321f },
        m_extractedMotion = new hkaDefaultAnimatedReferenceFrame
        {
            m_up = new Vector4(0, 0, 1, 0), m_forward = new Vector4(0, 1, 0, 0),
            m_duration = 1.123456789f,
            m_referenceFrameSamples = new[] { new Vector4(.123456789f, float.Epsilon, 0, .987654321f) }
        },
        m_annotationTracks = new[]
        {
            new hkaAnnotationTrack
            {
                m_trackName = "events",
                m_annotations = new[] { new hkaAnnotationTrackAnnotation { m_time = .123456789f, m_text = "FootLeft" } }
            },
            new hkaAnnotationTrack
            {
                m_trackName = " untouched track ",
                m_annotations = new[] { new hkaAnnotationTrackAnnotation { m_time = .987654321f, m_text = " untouched text " } }
            }
        }
    };
    var container = new hkaAnimationContainer
    {
        m_animations = new[] { animation },
        m_bindings = new[] { new hkaAnimationBinding
        {
            m_originalSkeletonName = "fixture",
            m_animation = animation,
            m_transformTrackToBoneIndices = new short[] { 2, 0 },
            m_floatTrackToFloatSlotIndices = new short[] { 0 }
        } }
    };
    return new hkRootLevelContainer
    {
        m_namedVariants = new[] { new hkRootLevelContainerNamedVariant
        {
            m_name = "Merged Animation Container", m_className = "hkaAnimationContainer", m_variant = container
        } }
    };
}

hkaAnimationContainer Container(hkRootLevelContainer root) =>
    (hkaAnimationContainer)root.m_namedVariants[0]!.m_variant!;
hkaInterleavedUncompressedAnimation Uncompressed(hkRootLevelContainer root) =>
    (hkaInterleavedUncompressedAnimation)Container(root).m_animations[0];

XDocument Xml(hkRootLevelContainer root)
{
    using var stream = new MemoryStream();
    new HKX2.XmlSerializer().Serialize(root, HKXHeader.SkyrimSE(), stream);
    stream.Position = 0;
    return XDocument.Load(stream);
}

AnimationClip Parse(XDocument doc)
{
    string path = Path.Combine(directory, "parser.xml");
    doc.Save(path);
    return HavokAnimationParser.Parse(path, skeleton);
}

void WriteBinary(string path, hkRootLevelContainer root, HkxPlatform platform)
{
    using var stream = File.Create(path);
    new PackFileSerializer().Serialize(root, new BinaryWriterEx(stream), platform.Header());
}

hkRootLevelContainer ReadBinary(string path)
{
    using var stream = File.OpenRead(path);
    return (hkRootLevelContainer)new PackFileDeserializer().Deserialize(
        new BinaryReaderEx(stream) { PreserveFloatPrecision = true });
}

try
{
    await Test("binary reader precision is opt-in for both byte orders", () => Sync(() =>
    {
        foreach (bool bigEndian in new[] { false, true })
        {
            var bytes = BitConverter.GetBytes(.123456789f);
            if (bigEndian) Array.Reverse(bytes);
            var legacy = new BinaryReaderEx(bigEndian, false, bytes);
            var precise = new BinaryReaderEx(bigEndian, false, bytes) { PreserveFloatPrecision = true };
            Check(legacy.ReadSingle() == .123457f, "legacy reader changed");
            Check(precise.ReadSingle() == .123456789f, "precise reader rounded");
        }
    }));

    await Test("frame-major samples, binding, reference pose, annotations and sample period", () => Sync(() =>
    {
        var clip = Parse(Xml(Fixture()));
        Check(clip.NumFrames == 3 && clip.NumTracks == 2 && clip.FrameDuration == .5f, "clip metadata");
        for (int f = 0; f < 3; f++)
        {
            Check(clip.Frames[f][2].Translation.X == f + 1, "mapped track 0");
            Check(clip.Frames[f][0].Translation.X == (f + 1) * 10, "mapped track 1");
            Check(clip.Frames[f][1].Translation.Y == 7, "reference pose fallback");
        }
        Check(clip.Annotations.Count == 2 && clip.Annotations[1].TrackIndex == 1, "annotation tracks");
        Check(!clip.TrackCountExceedsBones, "fewer tracks should not warn");
        Check(skeleton.ReferencePose[0].Translation == Vector3.Zero, "reference pose must not mutate");
    }));

    await Test("binding belongs to the selected animation, not the first binding in the file", () => Sync(() =>
    {
        var doc = Xml(Fixture());
        var binding = doc.Descendants("hkobject").Single(o => (string?)o.Attribute("class") == "hkaAnimationBinding");
        var other = new XElement(binding);
        other.SetAttributeValue("name", "#9998");
        P(other, "animation").Value = "#9999";
        P(other, "transformTrackToBoneIndices").Value = "0 1";
        binding.AddBeforeSelf(other);
        Check(Parse(doc).Frames[0][2].Translation.X == 1, "wrong animation's binding used");
    }));

    await Test("missing or empty binding uses identity mapping", () => Sync(() =>
    {
        var doc = Xml(Fixture());
        var binding = doc.Descendants("hkobject").Single(o => (string?)o.Attribute("class") == "hkaAnimationBinding");
        P(binding, "transformTrackToBoneIndices").Value = "";
        Check(Parse(doc).Frames[0][0].Translation.X == 1, "empty mapping");
        binding.Remove();
        Check(Parse(doc).Frames[0][1].Translation.X == 10, "absent mapping");
    }));

    await Test("invalid bone indices leave reference bones untouched", () => Sync(() =>
    {
        var doc = Xml(Fixture());
        var binding = doc.Descendants("hkobject").Single(o => (string?)o.Attribute("class") == "hkaAnimationBinding");
        P(binding, "transformTrackToBoneIndices").Value = "-1 50";
        Check(Parse(doc).Frames[0][1].Translation.Y == 7, "out-of-range binding");
    }));

    await Test("single-frame zero-duration animation", () => Sync(() =>
    {
        var root = Fixture(); var anim = Uncompressed(root);
        anim.m_duration = 0; anim.m_transforms = anim.m_transforms.Take(2).ToArray(); anim.m_floats = new[] { 0f };
        var clip = Parse(Xml(root));
        Check(clip.NumFrames == 1 && clip.FrameAt(100) == 0 && clip.FrameDuration > 0, "single frame");
    }));

    await Test("float-only clip retains the skeleton reference pose", () => Sync(() =>
    {
        var root = Fixture(); var anim = Uncompressed(root);
        anim.m_numberOfTransformTracks = 0; anim.m_transforms = Array.Empty<Matrix4x4>();
        var clip = Parse(Xml(root));
        Check(clip.NumFrames == 3 && clip.NumTracks == 0 && clip.Frames[2][1].Translation.Y == 7, "float-only frames");
    }));

    await Test("non-uniform scale and XYZW rotation compose in local space", () => Sync(() =>
    {
        var root = Fixture();
        var sample = Transform(10); sample.M31 = 2; sample.M32 = 3; sample.M33 = 4;
        sample.M23 = MathF.Sqrt(.5f); sample.M24 = MathF.Sqrt(.5f);
        Uncompressed(root).m_transforms[1] = sample;
        var clip = Parse(Xml(root));
        var world = HkTransform.ComputeWorld(clip.Frames[0], skeleton.ParentIndices);
        Check(Vector3.Distance(world[2].Translation, new Vector3(10, 2, 0)) < 1e-5, "scale must precede rotation");
        Check(clip.Frames[0][0].ScaleVector == new Vector3(2, 3, 4), "all scale components");
    }));

    await Test("sample timing, negative looping and the final scrub frame", () => Sync(() =>
    {
        var clip = Parse(Xml(Fixture()));
        Check(clip.FrameAt(.4) == 0 && clip.FrameAt(.5) == 1, "sample intervals");
        Check(clip.FrameAt(1) == 0 && clip.FrameAt(-.25) == 1, "looping");
        Check(clip.FrameAt(1, loop: false) == 2 && clip.FrameAt(2, loop: false) == 2, "end scrub");
        Check(clip.FrameAt(-1, loop: false) == 0, "start clamp");
    }));

    await Test("invariant numeric parsing under Turkish culture", () => Sync(() =>
    {
        var doc = Xml(Fixture());
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); Check(Parse(doc).NumFrames == 3, "culture"); }
        finally { CultureInfo.CurrentCulture = previous; }
    }));

    var invalidCases = new (string Name, Action<XElement> Mutate)[]
    {
        ("partial frame", a => P(a, "numberOfTransformTracks").Value = "4"),
        ("stale numelements", a => P(a, "transforms").SetAttributeValue("numelements", "5")),
        ("missing transform group", a => P(a, "transforms").Value = "(0 0 0)(0 0 0 1)"),
        ("truncated quaternion", a => P(a, "transforms").Value = P(a, "transforms").Value.Replace("(0.000000 0.000000 0.000000 1.000000)", "(0 0 1)")),
        ("unexpected text", a => P(a, "transforms").Value += " garbage"),
        ("non-finite sample", a => P(a, "transforms").Value = P(a, "transforms").Value.Replace("1.000000", "NaN")),
        ("zero quaternion", a => P(a, "transforms").Value = P(a, "transforms").Value.Replace("(0.000000 0.000000 0.000000 1.000000)", "(0 0 0 0)")),
        ("negative track count", a => P(a, "numberOfTransformTracks").Value = "-1"),
        ("zero tracks with samples", a => P(a, "numberOfTransformTracks").Value = "0"),
        ("mismatched float frame count", a => { P(a, "floats").Value = "0 1"; P(a, "floats").SetAttributeValue("numelements", "2"); }),
        ("invalid duration", a => P(a, "duration").Value = "NaN"),
        ("zero multi-frame duration", a => P(a, "duration").Value = "0"),
        ("empty animation", a => { P(a, "transforms").Value = ""; P(a, "transforms").SetAttributeValue("numelements", "0"); P(a, "floats").Value = ""; P(a, "floats").SetAttributeValue("numelements", "0"); })
    };
    foreach (var test in invalidCases)
        await Test("reject " + test.Name, () => Sync(() =>
        {
            var doc = Xml(Fixture()); test.Mutate(Anim(doc));
            try { Parse(doc); throw new Exception("malformed input was accepted"); }
            catch (AnimationParseException) { }
        }));

    await Test("spline-compressed parser regression", () => Sync(() =>
    {
        var doc = XDocument.Parse("""
            <hkpackfile><hksection><hkobject class="hkaSplineCompressedAnimation">
              <hkparam name="duration">1</hkparam><hkparam name="numFrames">3</hkparam>
              <hkparam name="frameDuration">0.5</hkparam><hkparam name="numberOfTransformTracks">1</hkparam>
              <hkparam name="maskAndQuantizationSize">4</hkparam><hkparam name="data">0 0 0 0</hkparam>
            </hkobject></hksection></hkpackfile>
            """);
        var clip = Parse(doc);
        Check(clip.NumFrames == 3 && clip.FrameDuration == .5f && clip.Frames[2][0].Scale == 1, "spline decode changed");
    }));

    foreach (var platform in new[] { HkxPlatform.SkyrimLE, HkxPlatform.SkyrimSE })
        await Test(platform + " spline binary annotation regression", async () =>
        {
            var root = Fixture();
            var anim = new hkaSplineCompressedAnimation
            {
                m_type = (int)AnimationType.HK_SPLINE_COMPRESSED_ANIMATION,
                m_duration = 1, m_numberOfTransformTracks = 1,
                m_numFrames = 3, m_numBlocks = 1, m_maxFramesPerBlock = 256,
                m_maskAndQuantizationSize = 4, m_frameDuration = .5f,
                m_data = new byte[] { 0, 0, 0, 0 },
                m_extractedMotion = Uncompressed(root).m_extractedMotion
            };
            Container(root).m_animations = new[] { anim };
            Container(root).m_bindings[0].m_animation = anim;
            string path = Path.Combine(directory, platform + "-spline.hkx");
            WriteBinary(path, root, platform);
            var edit = new AnnotationEdit { Kind = AnnotationEditKind.Add, NewText = "FootRight", NewTime = .25f };
            var editor = new AnimationAnnotationEditor();
            var result = await editor.ApplyAsync(path, edit);
            Check(result.Success, result.Error ?? "spline save failed");
            var saved = (hkaSplineCompressedAnimation)Container(ReadBinary(path)).m_animations[0];
            Check(saved.m_data.SequenceEqual(anim.m_data) && saved.m_frameDuration == .5f, "spline payload changed");
            Check(saved.m_extractedMotion!.Equals(anim.m_extractedMotion), "spline root motion changed");
            Check(saved.m_annotationTracks[0].m_annotations[0].m_text == "FootRight", "spline annotation missing");
            Check(HkxConversionService.DetectPlatform(path) == platform, "spline edition changed");
            result = await editor.ApplyAsync(path, edit.Inverse());
            Check(result.Success, result.Error ?? "spline undo failed");
        });

    await Test("ambiguous binary animation container is not overwritten", async () =>
    {
        var root = Fixture();
        Container(root).m_animations = new hkaAnimation[] { Uncompressed(root), Uncompressed(Fixture()) };
        string path = Path.Combine(directory, "multiple.hkx");
        WriteBinary(path, root, HkxPlatform.SkyrimSE);
        byte[] original = File.ReadAllBytes(path);
        var result = await new AnimationAnnotationEditor().ApplyAsync(path,
            new AnnotationEdit { Kind = AnnotationEditKind.Add, NewText = "Test", NewTime = .5f });
        Check(!result.Success && result.Error!.Contains("multiple"), "ambiguous file accepted");
        Check(File.ReadAllBytes(path).SequenceEqual(original) && !File.Exists(path + ".bak"), "ambiguous file modified");
    });

    foreach (var platform in new[] { HkxPlatform.Unknown, HkxPlatform.SkyrimLE, HkxPlatform.SkyrimSE })
        await Test(platform + " annotation add/edit/delete/replace/undo and sample preservation", async () =>
        {
            bool binary = platform != HkxPlatform.Unknown;
            string path = Path.Combine(directory, platform + (binary ? ".hkx" : ".xml"));
            var root = Fixture();
            // These values detect accidental passage through F6 XML formatting.
            Uncompressed(root).m_transforms[0] = Transform(.123456789f, float.Epsilon, -.987654321f);
            if (binary) WriteBinary(path, root, platform); else Xml(root).Save(path);
            byte[] original = File.ReadAllBytes(path);
            var originalXml = binary ? null : XDocument.Load(path);
            var editor = new AnimationAnnotationEditor();
            async Task Apply(AnnotationEdit edit)
            {
                var result = await editor.ApplyAsync(path, edit);
                Check(result.Success, result.Error ?? "edit failed");
                Check(File.ReadAllBytes(path + ".bak").SequenceEqual(original), "original one-time backup");
                if (binary)
                {
                    var saved = ReadBinary(path); var before = Uncompressed(root); var after = Uncompressed(saved);
                    Check(after.m_transforms.SequenceEqual(before.m_transforms),
                        $"transform precision changed: expected {before.m_transforms[0]}; got {after.m_transforms[0]}");
                    Check(after.m_floats.SequenceEqual(before.m_floats), "float precision changed");
                    Check(after.m_extractedMotion!.Equals(before.m_extractedMotion), "root motion changed");
                    Check(after.m_duration == before.m_duration && after.m_type == before.m_type, "animation metadata changed");
                    Check(Container(saved).m_bindings[0].m_transformTrackToBoneIndices.SequenceEqual(new short[] { 2, 0 }), "binding changed");
                    Check(HkxConversionService.DetectPlatform(path) == platform, "binary platform changed");
                    Check(after.m_annotationTracks[1].m_trackName == " untouched track ", "track name changed");
                }
                else
                {
                    var saved = XDocument.Load(path);
                    Check(P(Anim(saved), "transforms").Value.Trim() == P(Anim(originalXml!), "transforms").Value.Trim(), "XML transforms changed");
                    Check(P(Anim(saved), "floats").Value.Trim() == P(Anim(originalXml!), "floats").Value.Trim(), "XML floats changed");
                }
            }
            async Task<AnimationClip> Reload()
            {
                if (!binary) return HavokAnimationParser.Parse(path, skeleton);
                string xmlPath = path + ".converted.xml";
                await new HkxConversionService().HkxToXmlFileAsync(path, xmlPath);
                return HavokAnimationParser.Parse(xmlPath, skeleton);
            }
            var add = new AnnotationEdit { Kind = AnnotationEditKind.Add, TrackIndex = 0, NewTime = .75f, NewText = "SoundPlay.Test" };
            await Apply(add);
            Check((await Reload()).Annotations.Any(a => a.Text == "SoundPlay.Test" && a.Time == .75f), "add did not persist");
            if (binary)
            {
                var untouched = Uncompressed(ReadBinary(path)).m_annotationTracks[1].m_annotations[0];
                Check(untouched.m_text == " untouched text " && untouched.m_time == .987654321f, "untouched annotation changed");
            }
            var edit = new AnnotationEdit { Kind = AnnotationEditKind.Edit, TrackIndex = 0, OldTime = .75f, OldText = "SoundPlay.Test", NewTime = .5f, NewText = "HitFrame" };
            await Apply(edit);
            Check((await Reload()).Annotations.Any(a => a.Text == "HitFrame" && a.Time == .5f), "edit did not persist");
            await Apply(edit.Inverse());
            await Apply(add.Inverse());
            Check(!(await Reload()).Annotations.Any(a => a.Text == "SoundPlay.Test"), "delete/undo failed");
            var replace = new AnnotationEdit
            {
                Kind = AnnotationEditKind.ReplaceAll, OldSet = (await Reload()).Annotations,
                NewSet = new() { new AnimationAnnotation { TrackIndex = 1, Time = .25f, Text = "Replace" } }
            };
            await Apply(replace);
            Check((await Reload()).Annotations.Single().Text == "Replace", "replace all failed");
            await Apply(replace.Inverse());
            Check((await Reload()).Annotations.Count == 2, "replace undo failed");
            byte[] beforeFailure = File.ReadAllBytes(path);
            var bad = await editor.ApplyAsync(path, new AnnotationEdit { Kind = AnnotationEditKind.Delete, TrackIndex = 0, OldText = "Missing" });
            Check(!bad.Success && File.ReadAllBytes(path).SequenceEqual(beforeFailure), "failed edit wrote the file");
        });
}
finally { Directory.Delete(directory, recursive: true); }

Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
