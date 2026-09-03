using System.Collections.Generic;
using SageHavokEditor.Models;

namespace SageHavokEditor.Core.Validation
{
    /// <summary>
    /// Walks an object's params — the inline (anonymous) children included — so a
    /// scan sees every <c>#ref</c> the object actually carries.
    ///
    /// The distinction matters: a transition array keeps its effect and its event
    /// intervals a level below the param, and <c>hkRootLevelContainer</c> keeps the
    /// only reference to the behaviour graph inside an inline <c>namedVariants</c>
    /// struct. A scan of top-level <c>param.Value</c> alone misses both, which is
    /// how the orphan check used to flag <c>hkbBehaviorGraph</c> in every file.
    /// </summary>
    internal static class HkRefWalk
    {
        /// <summary>Every <c>#NNNN</c> token in an object, with the param path it sits on.</summary>
        public static IEnumerable<(string Path, string RefId)> EnumerateRefs(HkObject obj)
        {
            foreach (var (path, param) in EnumerateParams(obj))
                foreach (var tok in HkRefList.Tokens(param.Value))
                    if (tok.StartsWith("#"))
                        yield return (path, tok);
        }

        /// <summary>
        /// Every param of an object, recursing into inline children only. A child
        /// carrying an id is a cached resolved ref — a top-level object in its own
        /// right, reached through the ref token rather than walked into here.
        /// </summary>
        public static IEnumerable<(string Path, HkParam Param)> EnumerateParams(HkObject obj)
        {
            foreach (var p in obj.Params)
            {
                yield return (p.Name, p);
                for (int i = 0; i < p.Children.Count; i++)
                {
                    var c = p.Children[i];
                    if (!string.IsNullOrEmpty(c.Id)) continue;  // cached resolved ref, not inline
                    foreach (var (subPath, sp) in EnumerateParams(c))
                        yield return ($"{p.Name}[{i}].{subPath}", sp);
                }
            }
        }
    }
}
