using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SageHavokEditor.Models;

namespace SageHavokEditor.Core.Animation
{
    public enum AnnotationEditKind { Add, Edit, Delete, ReplaceAll }

    /// <summary>
    /// One annotation change on an animation file. Add uses New*; Delete identifies
    /// its target via Old*; Edit uses both. ReplaceAll swaps the file's entire
    /// annotation set (all tracks) for NewSet, with OldSet as the undo snapshot.
    /// Inverse() flips the operation so the same service call can drive undo.
    /// </summary>
    public sealed class AnnotationEdit
    {
        public AnnotationEditKind Kind;
        public int TrackIndex;
        public float OldTime;
        public string OldText = "";
        public float NewTime;
        public string NewText = "";
        public List<AnimationAnnotation> OldSet = new();
        public List<AnimationAnnotation> NewSet = new();

        public AnnotationEdit Inverse() => Kind switch
        {
            AnnotationEditKind.Add => new AnnotationEdit
            {
                Kind = AnnotationEditKind.Delete,
                TrackIndex = TrackIndex,
                OldTime = NewTime,
                OldText = NewText
            },
            AnnotationEditKind.Delete => new AnnotationEdit
            {
                Kind = AnnotationEditKind.Add,
                TrackIndex = TrackIndex,
                NewTime = OldTime,
                NewText = OldText
            },
            AnnotationEditKind.ReplaceAll => new AnnotationEdit
            {
                Kind = AnnotationEditKind.ReplaceAll,
                OldSet = NewSet,
                NewSet = OldSet
            },
            _ => new AnnotationEdit
            {
                Kind = AnnotationEditKind.Edit,
                TrackIndex = TrackIndex,
                OldTime = NewTime,
                OldText = NewText,
                NewTime = OldTime,
                NewText = OldText
            },
        };

        public string Describe() => Kind switch
        {
            AnnotationEditKind.Add => $"Add annotation '{NewText}' @ {NewTime:F3}s",
            AnnotationEditKind.Delete => $"Delete annotation '{OldText}' @ {OldTime:F3}s",
            AnnotationEditKind.ReplaceAll => $"Replace annotations ({OldSet.Count} → {NewSet.Count})",
            _ => $"Edit annotation '{OldText}' → '{NewText}' @ {NewTime:F3}s",
        };
    }

    public sealed class AnnotationEditResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// Applies annotation edits to an animation .hkx/.xml on disk. The file is loaded
    /// into the HkPackfile model, the hkaSplineCompressedAnimation's annotationTracks
    /// array is rewritten, and the file is saved back through the same XML↔HKX pipeline
    /// the behavior save uses. Tracks and annotations are INLINE hkobjects (no #id) —
    /// they are created straight into the owning param's Children and wired in the same
    /// action, so nothing can be orphaned/pruned on save. A one-time
    /// "&lt;file&gt;.bak" copy is made before the first overwrite.
    /// </summary>
    public sealed class AnimationAnnotationEditor
    {
        private readonly HkxConversionService _conv;

        public AnimationAnnotationEditor(HkxConversionService conv) => _conv = conv;

        public async Task<AnnotationEditResult> ApplyAsync(string animFullPath, AnnotationEdit edit)
        {
            try
            {
                if (!File.Exists(animFullPath))
                    return Fail($"Animation not found: {animFullPath}");

                bool binary = HkxConversionService.DetectFormat(animFullPath) == HkxFormat.HKX;
                string xml = binary
                    ? await _conv.HkxToXmlAsync(animFullPath)
                    : await File.ReadAllTextAsync(animFullPath);

                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(HkPackfile));
                HkPackfile pack;
                using (var sr = new StringReader(xml))
                    pack = (HkPackfile)serializer.Deserialize(sr)!;

                var anim = pack.Sections.SelectMany(s => s.Objects)
                    .FirstOrDefault(o => o.ClassName == "hkaSplineCompressedAnimation");
                if (anim == null)
                    return Fail("No hkaSplineCompressedAnimation in this file.");

                var err = ApplyToAnimationObject(anim, edit);
                if (err != null) return Fail(err);

                var bak = animFullPath + ".bak";
                if (!File.Exists(bak)) File.Copy(animFullPath, bak);

                var tmpXml = animFullPath + ".tmp.xml";
                using (var w = new StreamWriter(tmpXml, false, Encoding.UTF8))
                    HkXml.Write(pack, w);

                if (binary)
                {
                    await _conv.XmlToHkxAsync(tmpXml, animFullPath);
                    File.Delete(tmpXml);
                }
                else
                {
                    File.Delete(animFullPath);
                    File.Move(tmpXml, animFullPath);
                }

                return new AnnotationEditResult { Success = true };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static string? ApplyToAnimationObject(HkObject anim, AnnotationEdit e)
        {
            var tracksParam = anim.Params.FirstOrDefault(p => p.Name == "annotationTracks");
            if (tracksParam == null)
            {
                tracksParam = new HkParam { Name = "annotationTracks", NumElements = "0" };
                anim.Params.Add(tracksParam);
            }
            var tracks = tracksParam.Children;

            if (e.Kind == AnnotationEditKind.ReplaceAll)
                return ReplaceAllTracks(tracksParam, e.NewSet);

            HkObject? track = e.TrackIndex >= 0 && e.TrackIndex < tracks.Count
                ? tracks[e.TrackIndex] : null;

            if (e.Kind == AnnotationEditKind.Add && track == null)
            {
                if (e.TrackIndex > tracks.Count)
                    return $"Track {e.TrackIndex} doesn't exist (file has {tracks.Count}).";
                track = NewInlineTrack();
                tracks.Add(track);
                tracksParam.NumElements = tracks.Count.ToString(CultureInfo.InvariantCulture);
            }
            if (track == null)
                return $"Annotation track {e.TrackIndex} not found.";

            var annsParam = EnsureAnnotationsParam(track);
            var anns = annsParam.Children;

            static float TimeOf(HkObject a) =>
                float.TryParse(a.Params.FirstOrDefault(p => p.Name == "time")?.Value,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
            static string TextOf(HkObject a) =>
                a.Params.FirstOrDefault(p => p.Name == "text")?.Value?.Trim() ?? "";
            static string Fmt(float t) => t.ToString("F6", CultureInfo.InvariantCulture);

            void InsertSorted(HkObject a, float time)
            {
                int at = anns.FindIndex(x => TimeOf(x) > time);
                if (at < 0) anns.Add(a); else anns.Insert(at, a);
            }

            switch (e.Kind)
            {
                case AnnotationEditKind.Add:
                    InsertSorted(new HkObject
                    {
                        Params =
                        {
                            new HkParam { Name = "time", Value = Fmt(e.NewTime) },
                            new HkParam { Name = "text", Value = e.NewText },
                        }
                    }, e.NewTime);
                    break;

                case AnnotationEditKind.Edit:
                case AnnotationEditKind.Delete:
                {
                    int idx = anns.FindIndex(a =>
                        Math.Abs(TimeOf(a) - e.OldTime) < 1e-4f && TextOf(a) == e.OldText);
                    if (idx < 0)
                        return $"Annotation '{e.OldText}' @ {e.OldTime:F3}s not found " +
                               "(was the file changed outside the editor?).";

                    var ann = anns[idx];
                    anns.RemoveAt(idx);
                    if (e.Kind == AnnotationEditKind.Edit)
                    {
                        var timeP = ann.Params.FirstOrDefault(p => p.Name == "time");
                        var textP = ann.Params.FirstOrDefault(p => p.Name == "text");
                        if (timeP == null) ann.Params.Insert(0, new HkParam { Name = "time", Value = Fmt(e.NewTime) });
                        else timeP.Value = Fmt(e.NewTime);
                        if (textP == null) ann.Params.Add(new HkParam { Name = "text", Value = e.NewText });
                        else textP.Value = e.NewText;
                        InsertSorted(ann, e.NewTime);
                    }
                    break;
                }
            }

            annsParam.NumElements = anns.Count.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        // Inline object: Id stays empty so it serializes nested, like the
        // tracks Havok's own exporter writes.
        private static HkObject NewInlineTrack() => new()
        {
            Params =
            {
                new HkParam { Name = "trackName", Value = "" },
                new HkParam { Name = "annotations", NumElements = "0" },
            }
        };

        private static HkParam EnsureAnnotationsParam(HkObject track)
        {
            var annsParam = track.Params.FirstOrDefault(p => p.Name == "annotations");
            if (annsParam == null)
            {
                annsParam = new HkParam { Name = "annotations", NumElements = "0" };
                track.Params.Add(annsParam);
            }
            return annsParam;
        }

        /// <summary>
        /// Swap the file's whole annotation set: every existing track is cleared
        /// (tracks themselves are kept — they may carry trackName data), missing
        /// tracks are created, and newSet is written per-track sorted by time.
        /// </summary>
        private static string? ReplaceAllTracks(HkParam tracksParam, List<AnimationAnnotation> newSet)
        {
            var tracks = tracksParam.Children;
            int needed = newSet.Count == 0 ? 0 : newSet.Max(a => a.TrackIndex) + 1;
            while (tracks.Count < needed)
                tracks.Add(NewInlineTrack());
            tracksParam.NumElements = tracks.Count.ToString(CultureInfo.InvariantCulture);

            for (int ti = 0; ti < tracks.Count; ti++)
            {
                var annsParam = EnsureAnnotationsParam(tracks[ti]);
                annsParam.Children.Clear();
                foreach (var a in newSet.Where(x => x.TrackIndex == ti).OrderBy(x => x.Time))
                    annsParam.Children.Add(new HkObject
                    {
                        Params =
                        {
                            new HkParam { Name = "time", Value = a.Time.ToString("F6", CultureInfo.InvariantCulture) },
                            new HkParam { Name = "text", Value = a.Text },
                        }
                    });
                annsParam.NumElements = annsParam.Children.Count.ToString(CultureInfo.InvariantCulture);
            }
            return null;
        }

        private static AnnotationEditResult Fail(string msg) =>
            new() { Success = false, Error = msg };
    }

    /// <summary>
    /// hkanno's annotation text format — the de-facto interchange format for Skyrim
    /// animation annotations (Precision, AMR, payload interpreter…). One annotation
    /// per line: "&lt;time&gt; &lt;text&gt;" (text may contain spaces); '#' lines and
    /// blanks are ignored. Dump emits the same header comments hkanno writes so the
    /// output round-trips through "hkanno update" unchanged.
    /// </summary>
    public static class HkannoFormat
    {
        public static string Dump(IEnumerable<AnimationAnnotation> annotations, float duration, int numFrames)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# numOriginalFrames: {numFrames}");
            sb.AppendLine($"# duration: {duration.ToString("F6", CultureInfo.InvariantCulture)}");
            foreach (var a in annotations.OrderBy(a => a.Time))
                sb.AppendLine($"{a.Time.ToString("F6", CultureInfo.InvariantCulture)} {a.Text}");
            return sb.ToString();
        }

        /// <summary>Parses annotation lines; all annotations land on track 0 (hkanno convention).</summary>
        public static List<AnimationAnnotation>? Parse(string text, out string? error)
        {
            error = null;
            var result = new List<AnimationAnnotation>();
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                var timeRaw = sp < 0 ? line : line[..sp];
                var annText = sp < 0 ? "" : line[(sp + 1)..].Trim();

                if (!float.TryParse(timeRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                {
                    error = $"Line {i + 1}: '{timeRaw}' is not a time value.";
                    return null;
                }
                if (annText.Length == 0)
                {
                    error = $"Line {i + 1}: annotation text is missing after the time.";
                    return null;
                }
                result.Add(new AnimationAnnotation { Time = t, Text = annText, TrackIndex = 0 });
            }
            return result;
        }
    }
}
