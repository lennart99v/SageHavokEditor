using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace SkyrimHavokEditor.Core.Animation
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
        public int NumFrames;
        public int NumTracks;                                       // numberOfTransformTracks
        public HkTransform[][] Frames = Array.Empty<HkTransform[]>(); // [frame][bone] LOCAL space, skeleton-sized
        public List<AnimationAnnotation> Annotations = new();
        public bool TrackCountExceedsBones;                         // real warning (vs. benign "fewer tracks")

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
        public static AnimationClip Parse(string xmlPath, Skeleton skeleton)
        {
            var doc = XDocument.Load(xmlPath);

            var anim = doc.Descendants("hkobject")
                .FirstOrDefault(o => (string?)o.Attribute("class") == "hkaSplineCompressedAnimation");
            if (anim == null)
                throw new AnimationParseException(
                    "No hkaSplineCompressedAnimation found " +
                    "(interleaved/uncompressed animations aren't supported yet).");

            string P(string n) => anim.Elements("hkparam")
                .FirstOrDefault(p => (string?)p.Attribute("name") == n)?.Value?.Trim() ?? "";

            float duration = ParseF(P("duration"), 1f);
            int numFrames = ParseI(P("numFrames"), 0);
            int numBlocks = ParseI(P("numBlocks"), 1);
            int maskSize = ParseI(P("maskAndQuantizationSize"), 0);
            int numTracks = ParseI(P("numberOfTransformTracks"), maskSize / 4);

            if (numFrames <= 0) throw new AnimationParseException($"No frames (numFrames={numFrames}).");
            if (maskSize <= 0) throw new AnimationParseException("maskAndQuantizationSize missing or zero.");
            if (numBlocks != 1)
                throw new AnimationParseException(
                    $"Multi-block animation (numBlocks={numBlocks}) not yet supported " +
                    "(affects clips longer than ~256 frames).");

            byte[] data = ParseBytes(P("data"));
            if (data.Length < maskSize)
                throw new AnimationParseException("data blob smaller than mask table.");

            var trackFrames = HavokSplineDecoder.Decode(data, numFrames, maskSize);  // [frame][track]

            int[]? trackToBone = ParseTrackToBone(doc);     // null = identity (track i → bone i)
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
                NumFrames = numFrames,
                NumTracks = numTracks,
                Frames = frames,
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
