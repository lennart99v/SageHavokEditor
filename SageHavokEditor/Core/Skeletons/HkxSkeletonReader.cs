using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HKX2;

namespace SageHavokEditor.Core.Skeletons
{
    /// <summary>
    /// Bone order from a character project's <c>skeleton.hkx</c>, and the folder
    /// walk that finds it from a behaviour folder.
    ///
    /// This is the ordering an <c>hkbBoneWeightArray</c> is indexed by: the
    /// animation skeleton's own bone list, not the mesh's. The distinction is not
    /// academic — on the dragon project the two disagree completely, 84 bones
    /// against the NIF's 89 and every one of the 84 at a different index, because
    /// the NIF carries footstep and attachment nodes the animation skeleton has
    /// no place for. A weight array built from the NIF order would be wrong in
    /// every entry and wrong silently, which is why <see cref="SkeletonRegistry"/>
    /// gets an .hkx path here rather than reusing its NIF loader.
    /// </summary>
    public static class HkxSkeletonReader
    {
        /// <summary>Bone names in animation-skeleton order. Throws if there is no skeleton in the file.</summary>
        public static List<string> ReadBoneOrder(string hkxPath)
        {
            using var fs = File.OpenRead(hkxPath);
            var des = new PackFileDeserializer();
            var root = (hkRootLevelContainer)des.Deserialize(new BinaryReaderEx(fs));

            var skeleton = root.m_namedVariants
                .Select(v => v?.m_variant)
                .OfType<hkaAnimationContainer>()
                .SelectMany(c => c.m_skeletons)
                .FirstOrDefault();

            if (skeleton == null)
                throw new InvalidDataException($"No hkaSkeleton in {Path.GetFileName(hkxPath)}.");

            return skeleton.m_bones.Select(b => b.m_name).ToList();
        }

        /// <summary>
        /// The skeleton belonging to the project a behaviour folder sits in:
        /// &lt;project&gt;/behaviors/&lt;unit&gt;/ → &lt;project&gt;/character assets/skeleton.hkx.
        /// Null when the folder isn't laid out that way or the file isn't there —
        /// under a mod manager it legitimately isn't, so the caller reports rather
        /// than fails.
        /// </summary>
        public static string? FindProjectSkeleton(string behaviorFolder)
        {
            var behaviors = Path.GetDirectoryName(
                Path.GetFullPath(behaviorFolder).TrimEnd(Path.DirectorySeparatorChar));
            var project = Path.GetDirectoryName(behaviors);
            if (project == null) return null;

            foreach (var assets in new[] { "character assets", "characters" })
            {
                var candidate = Services.HkxPathResolver.FindFileCaseInsensitive(
                    Path.Combine(project, assets, "skeleton.hkx"));
                if (candidate != null) return candidate;
            }
            return null;
        }
    }
}
