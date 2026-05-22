using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SkyrimHavokEditor.Core.Skeletons
{
    /// <summary>
    /// Reads bone names in order from a Skyrim NIF skeleton file.
    /// The index of each bone in the returned list is its Havok bone weight array index.
    /// Supports NIF version 20.2.0.7 (Skyrim LE/SE/AE).
    /// </summary>
    public static class NifSkeletonReader
    {
        /// <summary>
        /// Parse a .nif skeleton file and return bones in Havok index order.
        /// </summary>
        /// <param name="nifPath">Full path to the .nif file.</param>
        /// <returns>Ordered list of bone names. Index = Havok bone weight array index.</returns>
        public static List<string> ReadBoneOrder(string nifPath)
        {
            if (!File.Exists(nifPath))
                throw new FileNotFoundException($"NIF file not found: {nifPath}");

            var data = File.ReadAllBytes(nifPath);
            return ExtractBoneNames(data);
        }

        /// <summary>
        /// Parse a .nif from a byte array (e.g. from a BSA/BA2 archive).
        /// </summary>
        public static List<string> ReadBoneOrderFromBytes(byte[] data)
        {
            return ExtractBoneNames(data);
        }

        private static List<string> ExtractBoneNames(byte[] data)
        {
            var bones = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            int i = 0;
            while (i < data.Length - 5)
            {
                uint length = BitConverter.ToUInt32(data, i);
                if (length >= 2 && length <= 80)
                {
                    int end = i + 4 + (int)length;
                    if (end <= data.Length)
                    {
                        try
                        {
                            string s = Encoding.ASCII.GetString(data, i + 4, (int)length);
                            if (s.Length == length && !seen.Contains(s) && IsValidAscii(s))
                            {
                                if (IsBoneName(s))
                                {
                                    seen.Add(s);
                                    bones.Add(s);
                                }
                            }
                        }
                        catch { /* skip invalid encodings */ }
                    }
                }
                i++;
            }

            return bones;
        }

        private static bool IsValidAscii(string s)
        {
            foreach (char c in s)
                if (c < 32 || c >= 127) return false;
            return true;
        }

        private static bool IsBoneName(string s)
        {
            if (s.StartsWith("NPC ", StringComparison.Ordinal) && s.Length > 4)
                return true;
            if (s.StartsWith("Dragon:", StringComparison.Ordinal))
                return true;
            if (s == "Saddlebone")
                return true;
            return false;
        }
    }
}
