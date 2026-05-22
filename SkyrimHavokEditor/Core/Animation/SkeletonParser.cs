using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SkyrimHavokEditor.Core.Animation
{
    public sealed class Skeleton
    {
        public string[] BoneNames;
        public int[] ParentIndices;
        public HkTransform[] ReferencePose;   // local space
    }

    public static class SkeletonParser
    {
        // matches "(a b c)" groups
        private static readonly Regex GroupRx = new(@"\(([^)]*)\)", RegexOptions.Compiled);

        public static Skeleton Parse(string xmlPath)
        {
            var doc = XDocument.Load(xmlPath);

            // First hkaSkeleton with a non-empty referencePose (skip empty #0053-style stubs)
            var skel = doc.Descendants("hkobject")
                .Where(o => (string)o.Attribute("class") == "hkaSkeleton")
                .FirstOrDefault(o =>
                {
                    var rp = o.Elements("hkparam").FirstOrDefault(p => (string)p.Attribute("name") == "referencePose");
                    return rp != null && !string.IsNullOrWhiteSpace(rp.Value);
                })
                ?? throw new Exception("No hkaSkeleton with a reference pose found.");

            string Param(string n) =>
                skel.Elements("hkparam").FirstOrDefault(p => (string)p.Attribute("name") == n)?.Value ?? "";

            var parents = Param("parentIndices")
                .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).ToArray();

            var names = skel.Elements("hkparam")
                .First(p => (string)p.Attribute("name") == "bones")
                .Elements("hkobject")
                .Select(o => o.Elements("hkparam")
                    .First(p => (string)p.Attribute("name") == "name").Value.Trim())
                .ToArray();

            // referencePose: groups of (t)(q)(s) — 3 groups per bone
            var groups = GroupRx.Matches(Param("referencePose"))
                .Select(m => m.Groups[1].Value
                    .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
                    .ToArray())
                .ToList();

            int boneCount = groups.Count / 3;
            var pose = new HkTransform[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var t = groups[i * 3 + 0];     // tx ty tz
                var q = groups[i * 3 + 1];     // qx qy qz qw
                var s = groups[i * 3 + 2];     // sx sy sz
                pose[i] = new HkTransform
                {
                    Translation = new Vector3(t[0], t[1], t[2]),
                    Rotation = new Quaternion(q[0], q[1], q[2], q[3]),  // (x,y,z,w)
                    Scale = s[0]   // uniform; Skyrim scales are 1,1,1
                };
            }

            return new Skeleton { BoneNames = names, ParentIndices = parents, ReferencePose = pose };
        }
    }
}