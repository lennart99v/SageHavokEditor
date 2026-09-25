using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SageHavokEditor.Core.Animation
{
    public sealed class AnimationAnnotation
    {
        public float Time;             // seconds
        public string Text = "";       // e.g. "FootLeft"
        public int TrackIndex;
    }

    public sealed class AnimationClip
    {
        public float Duration;
        /// <summary>Seconds between frames, from the animation's own frameDuration.
        /// Deriving it from duration/numFrames is the classic off-by-one-frame timing bug.</summary>
        public float FrameDuration;
        public int NumFrames;
        public int NumTracks;                                       // numberOfTransformTracks
        public HkTransform[][] Frames = Array.Empty<HkTransform[]>(); // [frame][bone] LOCAL space, skeleton-sized
        public List<AnimationAnnotation> Annotations = new();
        public List<string> AnnotationTrackNames = new();           // one entry per annotation track ("" when unnamed)
        public bool TrackCountExceedsBones;                         // real warning (vs. benign "fewer tracks")

        /// <summary>Skeleton-sized: true where some transform track wrote to the bone, i.e.
        /// the bones this clip actually drives. Null when a clip is built by hand instead of
        /// parsed (the harnesses do that), which is why callers keep a fallback.</summary>
        public bool[]? DrivenBones;

        public int FrameAt(double timeSeconds)
        {
            if (NumFrames <= 1) return 0;
            double dur = Duration > 0 ? Duration : 1.0;
            double r = timeSeconds % dur; if (r < 0) r += dur;
            int f = (int)((r / dur) * NumFrames);
            return Math.Clamp(f, 0, NumFrames - 1);
        }

        /// <summary>World-space bone transforms at time t (looping).</summary>
        public HkTransform[] SampleWorld(Skeleton skeleton, double timeSeconds)
            => HkTransform.ComputeWorld(Frames[FrameAt(timeSeconds)], skeleton.ParentIndices);
    }

    public sealed class AnimationParseException : Exception
    {
        public AnimationParseException(string m) : base(m) { }
    }

    public static class HavokAnimationParser
    {
        /// <summary>
        /// The two ways a Skyrim animation stores its motion.
        ///
        /// Spline-compressed is the packed form Bethesda ships and the one this
        /// parser was written for: the curves have to be decompressed to get a
        /// frame back, which is what <see cref="HavokSplineDecoder"/> does.
        /// Interleaved is the uncompressed form — every track's transform written
        /// out verbatim for every frame — which tools that author or convert
        /// animations often emit, and which needs no decoding at all.
        ///
        /// Only the decode step differs. Track-to-bone mapping, the reference-pose
        /// overlay, annotations and everything downstream in the preview are
        /// shared, so this is a choice of decoder rather than a second parser.
        /// </summary>
        public static AnimationClip Parse(string xmlPath, Skeleton skeleton)
        {
            var doc = XDocument.Load(xmlPath);

            XElement? Find(string cls) => doc.Descendants("hkobject")
                .FirstOrDefault(o => (string?)o.Attribute("class") == cls);

            var spline = Find("hkaSplineCompressedAnimation");
            if (spline != null) return Build(doc, spline, skeleton, DecodeSpline(spline));

            var interleaved = Find("hkaInterleavedUncompressedAnimation");
            if (interleaved != null)
                return Build(doc, interleaved, skeleton, DecodeInterleaved(interleaved));

            throw new AnimationParseException(
                "No animation found in this file — expected an hkaSplineCompressedAnimation " +
                "or an hkaInterleavedUncompressedAnimation.");
        }

        private static string Param(XElement anim, string n) => anim.Elements("hkparam")
            .FirstOrDefault(p => (string?)p.Attribute("name") == n)?.Value?.Trim() ?? "";

        /// <summary>
        /// The uncompressed form: <c>transforms</c> is already the frames.
        ///
        /// An <c>hkQsTransform</c> array is written as groups of
        /// <c>(tx ty tz)(qx qy qz qw)(sx sy sz)</c> — three groups, ten floats, per
        /// transform. That is the same shape a skeleton's <c>referencePose</c>
        /// uses, so this reads it the way <see cref="SkeletonParser"/> already
        /// does rather than inventing a second reading of the same format, and
        /// HKX2's own <c>ReadQSTransformArray</c> agrees on the layout.
        ///
        /// The array is frame-major: all of frame 0's tracks, then all of frame
        /// 1's. That is the one thing here not checkable against something else in
        /// this repo, so it is asserted rather than assumed — a count that isn't a
        /// whole number of frames is reported instead of being reshaped into
        /// nonsense.
        /// </summary>
        private static HkTransform[][] DecodeInterleaved(XElement anim)
        {
            int numTracks = ParseI(Param(anim, "numberOfTransformTracks"), 0);
            if (numTracks <= 0)
                throw new AnimationParseException(
                    $"numberOfTransformTracks is {numTracks} — nothing to animate.");

            var groups = GroupRx.Matches(Param(anim, "transforms"))
                .Select(m => m.Groups[1].Value
                    .Split(SplitChars, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => ParseF(t, 0f))
                    .ToArray())
                .ToList();

            if (groups.Count == 0)
                throw new AnimationParseException("The animation has no transforms.");
            if (groups.Count % 3 != 0)
                throw new AnimationParseException(
                    $"transforms holds {groups.Count} groups, which is not a whole number of "
                    + "(translation)(rotation)(scale) triples.");

            int total = groups.Count / 3;
            if (total % numTracks != 0)
                throw new AnimationParseException(
                    $"transforms holds {total} transforms, which is not a whole number of frames "
                    + $"at {numTracks} track(s) per frame.");

            int numFrames = total / numTracks;
            var frames = new HkTransform[numFrames][];
            for (int f = 0; f < numFrames; f++)
            {
                var row = new HkTransform[numTracks];
                for (int t = 0; t < numTracks; t++)
                {
                    var g = (f * numTracks + t) * 3;
                    var tr = groups[g];        // tx ty tz
                    var q = groups[g + 1];     // qx qy qz qw
                    var sc = groups[g + 2];    // sx sy sz
                    row[t] = new HkTransform
                    {
                        Translation = new Vector3(At(tr, 0), At(tr, 1), At(tr, 2)),
                        Rotation = new Quaternion(At(q, 0), At(q, 1), At(q, 2), At(q, 3)),
                        // Uniform, as everywhere else here: Skyrim's scales are 1,1,1
                        // and HkTransform carries a single factor.
                        Scale = sc.Length > 0 ? sc[0] : 1f,
                    };
                }
                frames[f] = row;
            }
            return frames;

            static float At(float[] a, int i) => i < a.Length ? a[i] : 0f;
        }

        private static readonly Regex GroupRx = new(@"\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly char[] SplitChars = { ' ', '\n', '\r', '\t' };

        private static HkTransform[][] DecodeSpline(XElement anim)
        {
            string P(string n) => Param(anim, n);

            int numFrames = ParseI(P("numFrames"), 0);
            int numBlocks = ParseI(P("numBlocks"), 1);
            int maxFramesPerBlock = ParseI(P("maxFramesPerBlock"), 256);
            int maskSize = ParseI(P("maskAndQuantizationSize"), 0);

            if (numFrames <= 0) throw new AnimationParseException($"No frames (numFrames={numFrames}).");
            if (maskSize <= 0) throw new AnimationParseException("maskAndQuantizationSize missing or zero.");

            byte[] data = ParseBytes(P("data"));
            if (data.Length < maskSize)
                throw new AnimationParseException("data blob smaller than mask table.");

            uint[] blockOffsets = ParseUInts(P("blockOffsets"));
            if (numBlocks > 1 && blockOffsets.Length < numBlocks)
                throw new AnimationParseException(
                    $"Multi-block animation declares {numBlocks} blocks but only " +
                    $"{blockOffsets.Length} block offset(s).");

            try
            {
                return HavokSplineDecoder.DecodeBlocks(
                    data, numFrames, maskSize, numBlocks, maxFramesPerBlock, blockOffsets);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                throw new AnimationParseException(
                    $"Could not decode the animation's {numBlocks} block(s): {ex.Message}");
            }
        }

        /// <summary>
        /// Everything after decoding, which both formats share: lay each frame's
        /// tracks over the skeleton's reference pose, and read the annotations.
        /// </summary>
        private static AnimationClip Build(XDocument doc, XElement anim, Skeleton skeleton,
            HkTransform[][] trackFrames)
        {
            float duration = ParseF(Param(anim, "duration"), 1f);
            float frameDuration = ParseF(Param(anim, "frameDuration"), 0f);
            int numFrames = trackFrames.Length;
            int numTracks = ParseI(Param(anim, "numberOfTransformTracks"),
                trackFrames.Length > 0 ? trackFrames[0].Length : 0);

            int[]? trackToBone = ParseTrackToBone(doc);     // null = identity (track i → bone i)
            int boneCount = skeleton.ReferencePose.Length;

            var frames = new HkTransform[numFrames][];
            var driven = new bool[boneCount];
            for (int f = 0; f < numFrames; f++)
            {
                // Every bone starts at its reference pose; animated tracks override.
                var local = (HkTransform[])skeleton.ReferencePose.Clone();
                var decoded = trackFrames[f];
                int tracks = Math.Min(numTracks, decoded.Length);
                for (int t = 0; t < tracks; t++)
                {
                    int bone = (trackToBone != null && t < trackToBone.Length) ? trackToBone[t] : t;
                    if (bone >= 0 && bone < boneCount)
                    { local[bone] = decoded[t]; driven[bone] = true; }
                }
                frames[f] = local;
            }

            var clip = new AnimationClip
            {
                Duration = duration,
                FrameDuration = frameDuration > 0 ? frameDuration
                    : (numFrames > 1 ? duration / (numFrames - 1) : 1f / 30f),
                NumFrames = numFrames,
                NumTracks = numTracks,
                Frames = frames,
                DrivenBones = driven,
                TrackCountExceedsBones = numTracks > boneCount
            };

            ReadAnnotations(anim, clip);
            return clip;
        }

        private static void ReadAnnotations(XElement anim, AnimationClip clip)
        {
            var annTracks = anim.Elements("hkparam")
                .FirstOrDefault(p => (string?)p.Attribute("name") == "annotationTracks");
            if (annTracks == null) return;

            int ti = 0;
            foreach (var track in annTracks.Elements("hkobject"))
            {
                clip.AnnotationTrackNames.Add(track.Elements("hkparam")
                    .FirstOrDefault(p => (string?)p.Attribute("name") == "trackName")?.Value?.Trim() ?? "");
                var anns = track.Elements("hkparam")
                    .FirstOrDefault(p => (string?)p.Attribute("name") == "annotations");
                if (anns != null)
                {
                    foreach (var a in anns.Elements("hkobject"))
                    {
                        var time = a.Elements("hkparam").FirstOrDefault(p => (string?)p.Attribute("name") == "time")?.Value;
                        var text = a.Elements("hkparam").FirstOrDefault(p => (string?)p.Attribute("name") == "text")?.Value;
                        if (time != null && !string.IsNullOrWhiteSpace(text))
                            clip.Annotations.Add(new AnimationAnnotation
                            {
                                Time = ParseF(time, 0f),
                                Text = text.Trim(),
                                TrackIndex = ti
                            });
                    }
                }
                ti++;
            }
            clip.Annotations = clip.Annotations.OrderBy(a => a.Time).ToList();
        }

        private static int[]? ParseTrackToBone(XDocument doc)
        {
            var binding = doc.Descendants("hkobject")
                .FirstOrDefault(o => (string?)o.Attribute("class") == "hkaAnimationBinding");
            var raw = binding?.Elements("hkparam")
                .FirstOrDefault(p => (string?)p.Attribute("name") == "transformTrackToBoneIndices")?.Value?.Trim();
            if (string.IsNullOrEmpty(raw)) return null;     // empty = identity mapping
            var arr = raw.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(int.Parse).ToArray();
            return arr.Length == 0 ? null : arr;
        }

        private static uint[] ParseUInts(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? Array.Empty<uint>()
                : s.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(t => uint.TryParse(t, out var v) ? v : 0u).ToArray();

        // fast byte-list parse (same approach as the standalone's ParseBytesFast)
        private static byte[] ParseBytes(string s)
        {
            var result = new List<byte>(s.Length / 3);
            int i = 0, len = s.Length;
            while (i < len)
            {
                while (i < len && (s[i] == ' ' || s[i] == '\n' || s[i] == '\r' || s[i] == '\t')) i++;
                if (i >= len) break;
                int v = 0;
                while (i < len && s[i] >= '0' && s[i] <= '9') v = v * 10 + (s[i++] - '0');
                result.Add((byte)v);
            }
            return result.ToArray();
        }

        private static float ParseF(string s, float dflt) =>
            float.TryParse(s.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : dflt;
        private static int ParseI(string s, int dflt) =>
            int.TryParse(s, out var v) ? v : dflt;
    }
}
