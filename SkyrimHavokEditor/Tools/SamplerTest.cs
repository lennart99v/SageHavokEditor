using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Core.Animation;

namespace SkyrimHavokEditor.Tools
{
    /// <summary>
    /// Throwaway sanity harness for the headless animation sampler.
    /// Call SamplerTest.Run(skeletonHkxOrXml, animHkxOrXml) from anywhere
    /// (e.g. a temporary button handler) and read the Output/console window.
    /// </summary>
    public static class SamplerTest
    {
        public static async Task Run(string skeletonPath, string animPath)
        {
            void Log(string s) => System.Diagnostics.Debug.WriteLine(s);  // or Console.WriteLine in a console app

            var conv = new HkxConversionService();

            // 1. Convert + parse skeleton
            var skXmlRes = await conv.PrepareXmlAsync(skeletonPath);
            if (!skXmlRes.Success) { Log($"SKELETON CONVERT FAILED: {skXmlRes.Error}"); return; }
            var skeleton = SkeletonParser.Parse(skXmlRes.XmlPath);

            Log($"Skeleton: {skeleton.BoneNames.Length} bones");
            Log($"  bone[0] = {skeleton.BoneNames[0]}  parent={skeleton.ParentIndices[0]}");
            Log($"  bone[1] = {skeleton.BoneNames[1]}  parent={skeleton.ParentIndices[1]}");
            Log($"  ref[0].T = {Fmt(skeleton.ReferencePose[0].Translation)}");
            Log($"  ref[1].T = {Fmt(skeleton.ReferencePose[1].Translation)}   (local, should be COM offset from root)");

            // 2. Convert + parse animation
            var anXmlRes = await conv.PrepareXmlAsync(animPath);
            if (!anXmlRes.Success) { Log($"ANIM CONVERT FAILED: {anXmlRes.Error}"); return; }

            AnimationClip clip;
            try { clip = HavokAnimationParser.Parse(anXmlRes.XmlPath, skeleton); }
            catch (Exception ex) { Log($"ANIM PARSE FAILED: {ex.Message}"); return; }

            Log($"\nClip: dur={clip.Duration:F4}s  frames={clip.NumFrames}  tracks={clip.NumTracks}" +
                $"  exceedsBones={clip.TrackCountExceedsBones}");
            Log($"  annotations ({clip.Annotations.Count}):");
            foreach (var a in clip.Annotations.Take(8))
                Log($"    t={a.Time:F3}  {a.Text}  (track {a.TrackIndex})");

            // 3. Sample world at t=0 and t=dur/2 — bone 0 (root) and bone 1 (COM)
            var w0 = clip.SampleWorld(skeleton, 0.0);
            var wh = clip.SampleWorld(skeleton, clip.Duration / 2.0);

            Log($"\n--- World positions ---");
            Log($"frame@0       bone0 {skeleton.BoneNames[0],-16} = {Fmt(w0[0].Translation)}");
            Log($"frame@0       bone1 {skeleton.BoneNames[1],-16} = {Fmt(w0[1].Translation)}");
            Log($"frame@dur/2   bone0 {skeleton.BoneNames[0],-16} = {Fmt(wh[0].Translation)}");
            Log($"frame@dur/2   bone1 {skeleton.BoneNames[1],-16} = {Fmt(wh[1].Translation)}");

            // 4. Bounding box of all bones at t=0 — catches mirror/explode bugs
            var xs = w0.Select(t => t.Translation.X).ToArray();
            var ys = w0.Select(t => t.Translation.Y).ToArray();
            var zs = w0.Select(t => t.Translation.Z).ToArray();
            Log($"\nframe@0 bbox  X[{xs.Min():F1}, {xs.Max():F1}]  " +
                $"Y[{ys.Min():F1}, {ys.Max():F1}]  Z[{zs.Min():F1}, {zs.Max():F1}]");

            // 5. Did anything actually move between the two times?
            double maxDelta = 0;
            for (int b = 0; b < w0.Length; b++)
                maxDelta = Math.Max(maxDelta, (w0[b].Translation - wh[b].Translation).Length());
            Log($"max bone movement t=0 → t=dur/2: {maxDelta:F3}  (0 = nothing animated = wrong)");
        }

        private static string Fmt(System.Numerics.Vector3 v)
            => $"({v.X,9:F3}, {v.Y,9:F3}, {v.Z,9:F3})";
    }
}