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
        /// <summary>Seconds between frames: stored by spline clips, derived from
        /// duration/(numFrames-1) for interleaved clips.</summary>
        public float FrameDuration;
        public int NumFrames;
        public int NumTracks;                                       // numberOfTransformTracks
        public HkTransform[][] Frames = Array.Empty<HkTransform[]>(); // [frame][bone] LOCAL space, skeleton-sized
        public List<AnimationAnnotation> Annotations = new();
        public List<string> AnnotationTrackNames = new();           // one entry per annotation track ("" when unnamed)
        public bool TrackCountExceedsBones;                         // real warning (vs. benign "fewer tracks")

        public int FrameAt(double timeSeconds, bool loop = true)
        {
            if (NumFrames <= 1) return 0;
            double dur = Duration > 0 ? Duration : 1.0;
            double r = loop ? timeSeconds % dur : Math.Clamp(timeSeconds, 0, dur);
            if (r < 0) r += dur;
            if (!loop && r >= dur) return NumFrames - 1;
            double dt = FrameDuration > 0 ? FrameDuration : dur / (NumFrames - 1);
            int f = (int)Math.Floor(r / dt + 1e-6);
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
        internal static bool IsSupportedAnimation(string? className) =>
            className is "hkaSplineCompressedAnimation" or "hkaInterleavedUncompressedAnimation";

        public static AnimationClip Parse(string xmlPath, Skeleton skeleton)
        {
            var doc = XDocument.Load(xmlPath);

            var anim = doc.Descendants("hkobject")
                .FirstOrDefault(o => IsSupportedAnimation((string?)o.Attribute("class")));
            if (anim == null)
                throw new AnimationParseException(
                    "No supported animation found. Expected hkaSplineCompressedAnimation " +
                    "or hkaInterleavedUncompressedAnimation.");

            string P(string n) => anim.Elements("hkparam")
                .FirstOrDefault(p => (string?)p.Attribute("name") == n)?.Value?.Trim() ?? "";

            float duration = ParseF(P("duration"), 1f);
            HkTransform[][] trackFrames;
            int numFrames, numTracks;
            float frameDuration;
            if ((string?)anim.Attribute("class") == "hkaInterleavedUncompressedAnimation")
            {
                duration = ReadFiniteFloat(P("duration"), "duration");
                if (duration < 0) throw new AnimationParseException("duration cannot be negative.");
                numTracks = ReadTrackCount(anim, "numberOfTransformTracks");
                trackFrames = DecodeInterleaved(anim, numTracks);
                numFrames = trackFrames.Length;
                if (numFrames > 1 && duration <= 0)
                    throw new AnimationParseException("A multi-frame animation needs a positive duration.");
                frameDuration = numFrames > 1 ? duration / (numFrames - 1) : 1f / 30f;
            }
            else
            {
                trackFrames = DecodeSpline(anim, out numFrames, out numTracks, out frameDuration);
            }

            int[]? trackToBone = ParseTrackToBone(doc, anim); // null = identity (track i → bone i)
            int boneCount = skeleton.ReferencePose.Length;

            var frames = new HkTransform[numFrames][];
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
                        local[bone] = decoded[t];
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
                TrackCountExceedsBones = numTracks > boneCount
            };

            ReadAnnotations(anim, clip);
            return clip;
        }

        private static HkTransform[][] DecodeSpline(XElement anim,
            out int numFrames, out int numTracks, out float frameDuration)
        {
            string P(string n) => Param(anim, n)?.Value.Trim() ?? "";
            numFrames = ParseI(P("numFrames"), 0);
            frameDuration = ParseF(P("frameDuration"), 0f);
            int numBlocks = ParseI(P("numBlocks"), 1);
            int maxFramesPerBlock = ParseI(P("maxFramesPerBlock"), 256);
            int maskSize = ParseI(P("maskAndQuantizationSize"), 0);
            numTracks = ParseI(P("numberOfTransformTracks"), maskSize / 4);

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

        // Havok stores one (translation)(quaternion)(scale) per transform, in
        // frame-major order: all tracks for frame 0, then all tracks for frame 1.
        // Unlike spline animations, there is no numFrames or frameDuration field.
        private static HkTransform[][] DecodeInterleaved(XElement anim, int numTracks)
        {
            var transforms = Param(anim, "transforms");
            string raw = transforms?.Value ?? "";
            var groups = Regex.Matches(raw, @"\(([^()]*)\)");
            if (groups.Count % 3 != 0 || !string.IsNullOrWhiteSpace(Regex.Replace(raw, @"\([^()]*\)", "")))
                throw new AnimationParseException("transforms must contain complete (translation)(rotation)(scale) triples.");
            int count = groups.Count / 3;
            CheckElementCount(transforms, count, "transforms");
            if (numTracks == 0 ? count != 0 : count % numTracks != 0)
                throw new AnimationParseException($"{count} transforms do not form complete frames of {numTracks} tracks.");
            int numFrames = numTracks > 0 ? count / numTracks : 0;

            int floatTracks = ReadTrackCount(anim, "numberOfFloatTracks", optional: true);
            var floats = Param(anim, "floats");
            var floatValues = Tokens(floats?.Value ?? "");
            CheckElementCount(floats, floatValues.Length, "floats");
            foreach (string value in floatValues) ReadFiniteFloat(value, "floats");
            if (floatTracks == 0 ? floatValues.Length != 0 : floatValues.Length % floatTracks != 0)
                throw new AnimationParseException("floats do not form complete frames of numberOfFloatTracks.");
            if (floatTracks > 0)
            {
                int floatFrames = floatValues.Length / floatTracks;
                if (numTracks == 0) numFrames = floatFrames;
                else if (floatFrames != numFrames)
                    throw new AnimationParseException("Transform and float tracks have different frame counts.");
            }
            if (numFrames == 0) throw new AnimationParseException("No frames in interleaved animation.");

            float[] Group(int index, int size, string label)
            {
                var values = Tokens(groups[index].Groups[1].Value);
                if (values.Length != size)
                    throw new AnimationParseException($"Transform {index / 3}: {label} needs {size} components.");
                return values.Select(v => ReadFiniteFloat(v, label)).ToArray();
            }

            var frames = new HkTransform[numFrames][];
            for (int f = 0; f < numFrames; f++)
            {
                frames[f] = new HkTransform[numTracks];
                for (int t = 0; t < numTracks; t++)
                {
                    int i = (f * numTracks + t) * 3;
                    var p = Group(i, 3, "translation");
                    var q = Group(i + 1, 4, "rotation");
                    var s = Group(i + 2, 3, "scale");
                    var rotation = new Quaternion(q[0], q[1], q[2], q[3]);
                    if (rotation.LengthSquared() < 1e-12f || !float.IsFinite(rotation.LengthSquared()))
                        throw new AnimationParseException($"Transform {i / 3}: invalid rotation quaternion.");
                    frames[f][t] = new HkTransform
                    {
                        Translation = new Vector3(p[0], p[1], p[2]),
                        Rotation = Quaternion.Normalize(rotation),
                        ScaleVector = new Vector3(s[0], s[1], s[2])
                    };
                }
            }
            return frames;
        }

        private static XElement? Param(XElement obj, string name) =>
            obj.Elements("hkparam").FirstOrDefault(p => (string?)p.Attribute("name") == name);

        private static string[] Tokens(string value) =>
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        private static int ReadTrackCount(XElement anim, string name, bool optional = false)
        {
            var param = Param(anim, name);
            if (param == null && optional) return 0;
            if (!int.TryParse(param?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0)
                throw new AnimationParseException($"{name} must be a non-negative integer.");
            return n;
        }

        private static void CheckElementCount(XElement? param, int actual, string name)
        {
            var declared = param?.Attribute("numelements");
            if (declared != null && (!int.TryParse(declared.Value, out int n) || n != actual))
                throw new AnimationParseException($"{name} declares {declared.Value} elements but contains {actual}.");
        }

        private static float ReadFiniteFloat(string value, string name)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                || !float.IsFinite(result))
                throw new AnimationParseException($"{name} contains a missing or invalid number: '{value}'.");
            return result;
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

        private static int[]? ParseTrackToBone(XDocument doc, XElement anim)
        {
            var bindings = doc.Descendants("hkobject")
                .Where(o => (string?)o.Attribute("class") == "hkaAnimationBinding").ToList();
            string? id = (string?)anim.Attribute("name");
            var binding = id == null ? null : bindings.FirstOrDefault(o => Param(o, "animation")?.Value.Trim() == id);
            // Older hand-authored XML may omit the animation reference entirely.
            if (binding == null && bindings.Count == 1 && Param(bindings[0], "animation") == null)
                binding = bindings[0];
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
