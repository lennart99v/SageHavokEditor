using System;
using System.Collections.Generic;
using System.IO;
using SageHavokEditor.Core.Services;

namespace SageHavokEditor.Core.AnimData
{
    /// <summary>
    /// Finding the <c>animationdatasinglefile.txt</c> that belongs to the files
    /// someone has open.
    ///
    /// <para>The cache lives at <c>&lt;Data&gt;\meshes\animationdatasinglefile.txt</c>
    /// and a behaviour lives at
    /// <c>&lt;Data&gt;\meshes\actors\&lt;race&gt;\behaviors\&lt;graph&gt;.hkx</c>, so
    /// walking up from the open file reaches it in four steps. We walk rather
    /// than compute the path, because the same layout appears under a mod
    /// manager's overwrite folder, a Nemesis/Pandora output folder and a loose
    /// extract, and only the walk finds all three.</para>
    ///
    /// <para><b>Which copy matters.</b> The one worth reading is the
    /// <i>patched</i> output for the user's actual load order — that is the file
    /// the game reads and the only one that knows about a mod's clips. Vanilla's
    /// copy will report every modded clip as unregistered, which is true of
    /// vanilla and useless as a warning, so the walk deliberately starts at the
    /// open file rather than at a game install.</para>
    /// </summary>
    public static class AnimationDataLocator
    {
        public const string FileName = "animationdatasinglefile.txt";

        /// <summary>How far up to walk. Four steps reach <c>meshes\</c> from a
        /// behaviour; the rest is slack for deeper mod layouts.</summary>
        private const int MaxDepth = 8;

        /// <summary>
        /// The nearest <c>animationdatasinglefile.txt</c> at or above any of the
        /// given open files, or null. Paths are tried in order, so pass the most
        /// specific file first.
        /// </summary>
        public static string? Locate(params string?[] openFilePaths)
        {
            foreach (var p in openFilePaths)
            {
                var hit = LocateFrom(p);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>The nearest cache at or above one file, or null.</summary>
        public static string? LocateFrom(string? openFilePath)
        {
            if (string.IsNullOrWhiteSpace(openFilePath)) return null;

            string? dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(openFilePath)); }
            catch { return null; }

            for (int i = 0; i < MaxDepth && !string.IsNullOrEmpty(dir); i++)
            {
                // Beside us, and one level into a meshes\ sibling — the latter
                // catches being handed the Data folder itself.
                var here = HkxPathResolver.FindFileCaseInsensitive(Path.Combine(dir, FileName));
                if (here != null) return here;

                var inMeshes = HkxPathResolver.FindFileCaseInsensitive(
                    Path.Combine(dir, "meshes", FileName));
                if (inMeshes != null) return inMeshes;

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        /// <summary>
        /// The project stem to look up for a loaded project file — its basename
        /// minus one extension, which is what the engine keys on
        /// (<c>defaultmale.hkx</c> → <c>defaultmale</c> → <c>DefaultMale.txt</c>).
        /// </summary>
        public static string? StemForProjectFile(string? projectFilePath)
            => string.IsNullOrWhiteSpace(projectFilePath)
                ? null
                : Path.GetFileNameWithoutExtension(projectFilePath);

        /// <summary>
        /// The projects a loaded behaviour could belong to: by the project file's
        /// stem when one is open, otherwise by finding the projects that list this
        /// behaviour among their assets.
        ///
        /// <para>Expect more than one from the asset route — <c>0_Master.hkx</c>
        /// belongs to <c>DefaultMale</c>, <c>DefaultFemale</c> and
        /// <c>FirstPerson</c> alike — which is a reason to say which project an
        /// answer came from rather than to pick one silently.</para>
        /// </summary>
        public static IReadOnlyList<AnimDataProject> ResolveProjects(
            AnimationDataFile cache, string? projectFilePath, string? behaviorFilePath)
        {
            if (cache == null) return Array.Empty<AnimDataProject>();

            var byStem = cache.ProjectsWithStem(StemForProjectFile(projectFilePath));
            if (byStem.Count > 0) return byStem;

            if (string.IsNullOrWhiteSpace(behaviorFilePath)) return Array.Empty<AnimDataProject>();

            // Asset paths are project-relative ("Behaviors\0_Master.hkx"), so
            // compare on the last two segments of what we have open.
            var name = Path.GetFileName(behaviorFilePath);
            var folder = Path.GetFileName(Path.GetDirectoryName(behaviorFilePath) ?? "");
            var relative = string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name);

            return cache.ProjectsForAsset(relative);
        }
    }
}
