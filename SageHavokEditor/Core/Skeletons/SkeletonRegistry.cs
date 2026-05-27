using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SageHavokEditor.Core.Skeletons
{
    /// <summary>
    /// Central registry for skeleton bone name ↔ index mappings.
    /// Used to convert between Havok's flat float bone weight arrays
    /// and human-readable named bone weight YAML.
    ///
    /// Usage:
    ///   var registry = SkeletonRegistry.Instance;
    ///   registry.LoadFromNif("dragon", @"path\to\skeleton.nif");
    ///
    ///   // Convert flat array to named dict:
    ///   var named = registry.IndexedToNamed("dragon", new float[] { 1f, 0f, 0.5f, ... });
    ///
    ///   // Convert named dict back to flat array:
    ///   var indexed = registry.NamedToIndexed("dragon", namedDict, totalBones: 91);
    /// </summary>
    public class SkeletonRegistry
    {
        private static SkeletonRegistry? _instance;
        public static SkeletonRegistry Instance => _instance ??= new SkeletonRegistry();

        // skeletonId -> ordered bone name list (index = Havok bone index)
        private readonly Dictionary<string, List<string>> _skeletons
            = new(StringComparer.OrdinalIgnoreCase);

        // skeletonId -> reverse lookup: name -> index
        private readonly Dictionary<string, Dictionary<string, int>> _nameToIndex
            = new(StringComparer.OrdinalIgnoreCase);

        private SkeletonRegistry() { }

        // ─── Loading ─────────────────────────────────────────────────────────────

        /// <summary>Load skeleton bone order directly from a .nif file.</summary>
        public void LoadFromNif(string skeletonId, string nifPath)
        {
            var bones = NifSkeletonReader.ReadBoneOrder(nifPath);
            Register(skeletonId, bones);
        }

        /// <summary>Load skeleton from a JSON file produced by ExportToJson.</summary>
        public void LoadFromJson(string jsonPath)
        {
            var json = File.ReadAllText(jsonPath);
            var def = JsonSerializer.Deserialize<SkeletonDefinition>(json);
            if (def == null) throw new InvalidDataException("Invalid skeleton JSON.");
            var bones = def.Bones.OrderBy(b => b.Index).Select(b => b.Name).ToList();
            Register(def.SkeletonId, bones);
        }

        /// <summary>
        /// Scan a game data folder for known skeleton NIFs and auto-load them.
        /// Looks for patterns like: actors\{creature}\character assets\skeleton.nif
        /// </summary>
        public void AutoLoad(string meshesRoot)
        {
            if (!Directory.Exists(meshesRoot)) return;

            var nifFiles = Directory.EnumerateFiles(meshesRoot, "skeleton.nif",
                SearchOption.AllDirectories);

            foreach (var nif in nifFiles)
            {
                // Derive a skeleton ID from the folder path
                // e.g. actors\dragon\character assets\skeleton.nif -> "dragon"
                var parts = nif.Split(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var actorsIdx = Array.FindIndex(parts,
                    p => p.Equals("actors", StringComparison.OrdinalIgnoreCase));

                string id = actorsIdx >= 0 && actorsIdx + 1 < parts.Length
                    ? parts[actorsIdx + 1].ToLowerInvariant()
                    : Path.GetDirectoryName(nif)!.Split(Path.DirectorySeparatorChar).Last();

                try
                {
                    LoadFromNif(id, nif);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"SkeletonRegistry: failed to load {nif}: {ex.Message}");
                }
            }
        }

        private void Register(string id, List<string> bones)
        {
            _skeletons[id] = bones;
            var reverse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < bones.Count; i++)
                reverse[bones[i]] = i;
            _nameToIndex[id] = reverse;
        }

        // ─── Conversion ──────────────────────────────────────────────────────────

        /// <summary>
        /// Convert a Havok flat float bone weight array to a named dictionary.
        /// Only entries with weight != 0 are included (sparse representation).
        /// </summary>
        public Dictionary<string, float> IndexedToNamed(string skeletonId, float[] weights)
        {
            if (!_skeletons.TryGetValue(skeletonId, out var bones))
                throw new KeyNotFoundException($"Skeleton '{skeletonId}' not registered.");

            var result = new Dictionary<string, float>();
            for (int i = 0; i < weights.Length && i < bones.Count; i++)
            {
                if (Math.Abs(weights[i]) > 1e-6f)
                    result[bones[i]] = weights[i];
            }
            return result;
        }

        /// <summary>
        /// Convert a named bone weight dictionary back to a flat float array.
        /// Bones not mentioned in the dict get weight 0.
        /// </summary>
        public float[] NamedToIndexed(string skeletonId,
            Dictionary<string, float> named, int totalBones = -1)
        {
            if (!_skeletons.TryGetValue(skeletonId, out var bones))
                throw new KeyNotFoundException($"Skeleton '{skeletonId}' not registered.");

            int count = totalBones > 0 ? totalBones : bones.Count;
            var result = new float[count];

            if (!_nameToIndex.TryGetValue(skeletonId, out var lookup)) return result;

            foreach (var (boneName, weight) in named)
            {
                if (lookup.TryGetValue(boneName, out int idx) && idx < count)
                    result[idx] = weight;
            }
            return result;
        }

        /// <summary>
        /// Parse a raw space-separated float string (Havok XML format)
        /// and return a named dict.
        /// </summary>
        public Dictionary<string, float> ParseHavokWeightString(
            string skeletonId, string weightString)
        {
            if (string.IsNullOrWhiteSpace(weightString))
                return new Dictionary<string, float>();

            var parts = weightString.Trim().Split(' ',
                StringSplitOptions.RemoveEmptyEntries);
            var floats = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                float.TryParse(parts[i],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out floats[i]);

            return IndexedToNamed(skeletonId, floats);
        }

        /// <summary>
        /// Convert a named dict back to the Havok XML space-separated string.
        /// </summary>
        public string ToHavokWeightString(string skeletonId,
            Dictionary<string, float> named, int totalBones = -1)
        {
            var arr = NamedToIndexed(skeletonId, named, totalBones);
            return string.Join(" ", arr.Select(f =>
                f.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
        }

        // ─── Queries ─────────────────────────────────────────────────────────────

        public bool HasSkeleton(string skeletonId)
            => _skeletons.ContainsKey(skeletonId);

        public IReadOnlyList<string> GetBones(string skeletonId)
            => _skeletons.TryGetValue(skeletonId, out var b)
                ? b.AsReadOnly()
                : Array.Empty<string>();

        public IEnumerable<string> RegisteredSkeletons => _skeletons.Keys;

        public int? GetBoneIndex(string skeletonId, string boneName)
        {
            if (_nameToIndex.TryGetValue(skeletonId, out var lookup) &&
                lookup.TryGetValue(boneName, out int idx))
                return idx;
            return null;
        }

        // ─── Persistence ─────────────────────────────────────────────────────────

        /// <summary>Export a registered skeleton to JSON for caching/shipping.</summary>
        public void ExportToJson(string skeletonId, string outputPath)
        {
            if (!_skeletons.TryGetValue(skeletonId, out var bones))
                throw new KeyNotFoundException($"Skeleton '{skeletonId}' not found.");

            var def = new SkeletonDefinition
            {
                SkeletonId = skeletonId,
                Description = $"Extracted from {skeletonId} skeleton.nif",
                Bones = bones.Select((name, idx) =>
                    new BoneEntry { Index = idx, Name = name }).ToList()
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(def, options));
        }

        // ─── DTOs ─────────────────────────────────────────────────────────────────

        private class SkeletonDefinition
        {
            [JsonPropertyName("skeletonId")] public string SkeletonId { get; set; } = "";
            [JsonPropertyName("description")] public string Description { get; set; } = "";
            [JsonPropertyName("bones")] public List<BoneEntry> Bones { get; set; } = new();
        }

        private class BoneEntry
        {
            [JsonPropertyName("index")] public int Index { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; } = "";
        }
    }
}
