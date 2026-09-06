using System;

namespace SageHavokEditor.Core.Services
{
    /// <summary>
    /// The player character is two behaviour projects, not one. Third person lives
    /// under <c>meshes\actors\character\</c>; the arms you see holding a sword live
    /// in a wholly separate project under <c>meshes\actors\character\_1stperson\</c>,
    /// with its own <c>0_master</c>, its own event table and its own animations.
    ///
    /// Nothing links them. A patch against the third-person graph does not reach
    /// first person, and the failure is the quiet kind: the animation plays
    /// perfectly in third person, the player switches view, and nothing happens —
    /// no error, nothing in a log, and no reason to suspect the patch rather than
    /// the animation.
    /// </summary>
    public static class FirstPersonProject
    {
        /// <summary>
        /// True when the path is inside the player's *third-person* project — the
        /// one whose patches stop at the view switch.
        ///
        /// Deliberately decided from the path alone rather than by looking for the
        /// sibling folder on disk: the first-person project usually lives inside a
        /// BSA or behind a mod manager's virtual file system, so "the folder isn't
        /// there" says nothing about whether the game has one. It always does.
        /// </summary>
        public static bool IsThirdPersonCharacter(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = path.Replace('/', '\\').ToLowerInvariant();
            return p.Contains("\\actors\\character\\") && !p.Contains("\\_1stperson\\");
        }

        /// <summary>The reminder itself, phrased as something to check rather than a fault.</summary>
        public const string Reminder =
            "This is the third-person character project. First person is a separate project under " +
            "_1stperson\\, with its own behaviour files, its own event table and its own animations — " +
            "nothing here reaches it.\n\n" +
            "If the change should apply while the player is looking down their own arms, it has to " +
            "be made in _1stperson\\ as well. Missing that looks exactly like the animation being " +
            "broken: fine in third person, nothing at all in first, and no error either way.";
    }
}
