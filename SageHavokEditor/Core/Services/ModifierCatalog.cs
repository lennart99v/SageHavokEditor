using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HKX2;
using SageHavokEditor.Models;

namespace SageHavokEditor.Core.Services
{
    /// <summary>
    /// Catalog of creatable Havok modifier classes, sourced by reflection from the
    /// bundled HKX2 type set. A default instance of each class is serialized through
    /// HKX2's own XmlSerializer, which yields the correct signature and the complete
    /// default parameter set — the same shape the editor loads from a real .hkx —
    /// so no per-class signature/param table has to be hand-maintained.
    /// </summary>
    public static class ModifierCatalog
    {
        private static List<string>? _classNames;
        // Modifier classes shown in the picker.
        private static readonly Dictionary<string, System.Type> _typesByName =
            new(StringComparer.Ordinal);
        // Every concrete IHavokObject — CreateDefault also builds non-modifier helpers like
        // the hkbModifierGenerator wrapper (which is a generator, not a modifier).
        private static readonly Dictionary<string, System.Type> _allTypesByName =
            new(StringComparer.Ordinal);

        private static void EnsureLoaded()
        {
            if (_classNames != null) return;

            System.Type[] all;
            try { all = typeof(hkbModifier).Assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            { all = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var t in all.Where(t => t != null && !t.IsAbstract
                         && typeof(IHavokObject).IsAssignableFrom(t)
                         && t.GetConstructor(System.Type.EmptyTypes) != null))
                _allTypesByName[t.Name] = t;

            var types = all
                .Where(t => t != null
                            && !t.IsAbstract
                            && t != typeof(hkbModifier)            // base — nothing concrete to place
                            && typeof(hkbModifier).IsAssignableFrom(t)
                            && t.GetConstructor(System.Type.EmptyTypes) != null)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

            foreach (var t in types) _typesByName[t.Name] = t;
            _classNames = types.Select(t => t.Name).ToList();
        }

        /// <summary>All concrete modifier class names, alphabetical.</summary>
        public static IReadOnlyList<string> ClassNames
        {
            get { EnsureLoaded(); return _classNames!; }
        }

        /// <summary>
        /// Builds a fresh default <see cref="HkObject"/> for a modifier class (correct
        /// signature + full default params). The caller assigns the final #id and name and
        /// wires it into the graph. Returns null for an unknown class.
        /// </summary>
        public static HkObject? CreateDefault(string className)
        {
            EnsureLoaded();
            if (!_allTypesByName.TryGetValue(className, out var t)) return null;

            var inst = (IHavokObject)Activator.CreateInstance(t)!;

            using var ms = new MemoryStream();
            new HKX2.XmlSerializer().Serialize(inst, HKXHeader.SkyrimSE(), ms);
            ms.Position = 0;

            var packSer = new System.Xml.Serialization.XmlSerializer(typeof(HkPackfile));
            var pack = (HkPackfile?)packSer.Deserialize(ms);
            var obj = pack?.Sections.SelectMany(s => s.Objects)
                                    .FirstOrDefault(o => o.ClassName == className);
            if (obj == null) return null;

            SanitizeNullStrings(obj);
            return obj;
        }

        // HKX2's XmlSerializer emits U+2400 (␀) as the sentinel for a null .NET string.
        // Scrub it so param values stay clean text.
        private static void SanitizeNullStrings(HkObject obj)
        {
            foreach (var p in obj.Params)
            {
                if (!string.IsNullOrEmpty(p.Value) && p.Value.Contains('␀'))
                    p.Value = p.Value.Replace("␀", "");
                foreach (var child in p.Children) SanitizeNullStrings(child);
            }
        }
    }
}
