using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HKX2;
using SageHavokEditor.Models;
using Type = System.Type;   // HKX2 declares its own Type enum

namespace SageHavokEditor.Core.Services
{
    /// <summary>
    /// Per-(class, param) type metadata for the property editor, reflected from the
    /// bundled HKX2 class definitions. HKX2's XmlSerializer strips the m_ prefix, so
    /// property m_startStateId ↔ hkparam name "startStateId".
    ///
    /// Enum quirk: HKX2 declares enum members as sbyte/int — the enum type only shows
    /// in the Read/WriteXml bodies. Detection instead serializes a default instance
    /// (ModifierCatalog.CreateDefault): an integer-declared param whose default text
    /// is a word (e.g. "START_STATE_MODE_DEFAULT") is an enum, and the word is looked
    /// up in the assembly-wide enum-member index to recover the member list.
    /// </summary>
    public static class HavokTypeCatalog
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, Dictionary<string, HkParamTypeInfo>?> _byClass =
            new(StringComparer.Ordinal);
        private static Dictionary<string, Type>? _enumByMember;
        private static Dictionary<string, Type>? _havokTypesByName;

        /// <summary>Annotate every object in a loaded file. Call after HavokManager.BuildGraph.</summary>
        public static void AnnotateAll(IEnumerable<HkObject> objects)
        {
            foreach (var o in objects)
                Annotate(o, o.ClassName);
        }

        /// <summary>Annotate one object's params (and inline child structs, recursively).</summary>
        public static void Annotate(HkObject obj, string? className)
        {
            // Structural pass — independent of type metadata, so it also runs for
            // classes outside the HKX2 type set. Feeds the "+ Add element" UI.
            foreach (var p in obj.Params)
                if (!string.IsNullOrWhiteSpace(p.NumElements) &&
                    p.Children.Any(c => string.IsNullOrEmpty(c.Id)))
                    p.IsInlineStructArray = true;

            if (string.IsNullOrEmpty(className)) return;
            var map = GetClassMap(className);
            if (map == null) return;

            foreach (var p in obj.Params)
            {
                if (!map.TryGetValue(p.Name, out var info)) continue;
                p.TypeInfo = info;

                // What the structural pass above can't see: an array that is empty
                // in this file still has a declared element shape, and for an
                // inline-struct array that is what "+ Add element" needs.
                if (info.ArrayKind == HkArrayKind.InlineStruct)
                    p.IsInlineStructArray = true;

                if (info.ElementClassName == null) continue;
                foreach (var child in p.Children)
                    if (string.IsNullOrEmpty(child.Id))   // inline structs only, not cached refs
                        Annotate(child, info.ElementClassName);
            }
        }

        private static Dictionary<string, HkParamTypeInfo>? GetClassMap(string className)
        {
            lock (_lock)
            {
                if (_byClass.TryGetValue(className, out var cached)) return cached;
                var map = BuildClassMap(className);
                // Cache BEFORE the enum upgrade: UpgradeEnums → CreateDefault →
                // Annotate re-enters GetClassMap for this same class, and must
                // find the (pre-upgrade) map instead of recursing forever.
                _byClass[className] = map;
                if (map != null)
                {
                    UpgradeEnums(className, map);
                    ApplySemantics(className, map);
                }
                return map;
            }
        }

        private static Dictionary<string, HkParamTypeInfo>? BuildClassMap(string className)
        {
            EnsureIndexes();
            if (!_havokTypesByName!.TryGetValue(className, out var type)) return null;

            var map = new Dictionary<string, HkParamTypeInfo>(StringComparer.Ordinal);
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.Name.StartsWith("m_", StringComparison.Ordinal)) continue;
                var paramName = prop.Name.Substring(2);
                var info = Classify(prop.PropertyType);
                if (info != null) map[paramName] = info;
            }
            ApplyArrayKinds(type, map);
            return map;
        }

        /// <summary>
        /// Mark which array params hold inline structs and which hold #id refs.
        /// Reflection can't tell them apart (both are IList&lt;T&gt;), so the answer
        /// comes from HKX2's own XML writer — see HavokArrayKinds.
        /// </summary>
        private static void ApplyArrayKinds(Type type, Dictionary<string, HkParamTypeInfo> map)
        {
            var kinds = HavokArrayKinds.ForType(type);
            foreach (var (name, kind) in kinds)
            {
                if (!map.TryGetValue(name, out var info)) continue;
                map[name] = new HkParamTypeInfo
                {
                    Kind = info.Kind,
                    EnumChoices = info.EnumChoices,
                    ElementClassName = info.ElementClassName,
                    Semantic = info.Semantic,
                    Min = info.Min,
                    Max = info.Max,
                    ArrayKind = kind,
                };
            }
        }

        private static HkParamTypeInfo? Classify(Type t)
        {
            if (t == typeof(bool))
                return new HkParamTypeInfo { Kind = HkParamKind.Bool };
            if (t == typeof(float) || t == typeof(double))
                return new HkParamTypeInfo { Kind = HkParamKind.Real };
            if (t == typeof(sbyte)) return IntInfo(sbyte.MinValue, sbyte.MaxValue);
            if (t == typeof(byte)) return IntInfo(byte.MinValue, byte.MaxValue);
            if (t == typeof(short)) return IntInfo(short.MinValue, short.MaxValue);
            if (t == typeof(ushort)) return IntInfo(ushort.MinValue, ushort.MaxValue);
            if (t == typeof(int)) return IntInfo(int.MinValue, int.MaxValue);
            if (t == typeof(uint)) return IntInfo(uint.MinValue, uint.MaxValue);
            if (t == typeof(long)) return IntInfo(long.MinValue, long.MaxValue);
            if (t == typeof(ulong)) return IntInfo(0, long.MaxValue);
            if (t.IsEnum)
                return new HkParamTypeInfo { Kind = HkParamKind.Enum, EnumChoices = Enum.GetNames(t) };

            var underlying = Nullable.GetUnderlyingType(t);
            if (underlying != null) return Classify(underlying);

            if (typeof(IHavokObject).IsAssignableFrom(t))
                return new HkParamTypeInfo { Kind = HkParamKind.Text, ElementClassName = t.Name };

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IList<>))
            {
                var elem = t.GetGenericArguments()[0];
                if (typeof(IHavokObject).IsAssignableFrom(elem))
                    return new HkParamTypeInfo { Kind = HkParamKind.Text, ElementClassName = elem.Name };
            }

            // Vector4, Matrix4x4, string, object … — plain text as far as the editor cares.
            return new HkParamTypeInfo { Kind = HkParamKind.Text };
        }

        private static HkParamTypeInfo IntInfo(long min, long max) =>
            new() { Kind = HkParamKind.Int, Min = min, Max = max };

        /// <summary>
        /// Reclassify integer-declared params that are really enums, using the default
        /// instance's serialized text. Flags fields whose default is "0" stay Int —
        /// their pipe-joined values pass through KnownEnumMember instead.
        /// </summary>
        private static void UpgradeEnums(string className, Dictionary<string, HkParamTypeInfo> map)
        {
            HkObject? def = null;
            try { def = ModifierCatalog.CreateDefault(className); }
            catch { /* class without a serializable default — keep the reflection-only map */ }
            if (def == null) return;

            foreach (var p in def.Params)
            {
                if (!map.TryGetValue(p.Name, out var info) || info.Kind != HkParamKind.Int) continue;
                var v = (p.Value ?? "").Trim();
                if (v.Length == 0 || long.TryParse(v, out _)) continue;
                if (_enumByMember!.TryGetValue(v, out var enumType))
                    map[p.Name] = new HkParamTypeInfo
                    {
                        Kind = HkParamKind.Enum,
                        EnumChoices = Enum.GetNames(enumType)
                    };
            }
        }

        /// <summary>
        /// Mark the integer params that are really indices into the file's event or
        /// variable table, so the editor can offer a name picker instead of a number.
        ///
        /// Havok gives no metadata for this — the members are plain ints — so it goes
        /// by name, which is safe because the naming is consistent across the class
        /// set: every event index ends in EventId (eventId, enterEventId,
        /// transitionToNextHigherStateEventId, …) and every variable index in
        /// VariableIndex (variableIndex, syncVariableIndex, assignmentVariableIndex).
        /// The two exceptions are hkbExpressionData's assignment pair and
        /// hkbEventBase.id — the "id" of an hkbEvent / hkbEventProperty, which is
        /// where notify events, clip triggers and event ranges keep theirs.
        /// </summary>
        private static void ApplySemantics(string className, Dictionary<string, HkParamTypeInfo> map)
        {
            var isEventClass = _havokTypesByName!.TryGetValue(className, out var type)
                               && typeof(hkbEventBase).IsAssignableFrom(type);

            foreach (var name in map.Keys.ToList())
            {
                var info = map[name];
                if (info.Kind != HkParamKind.Int) continue;   // enums/flags keep their dropdown

                var semantic = Semantic(name, isEventClass);
                if (semantic == HkParamSemantic.None) continue;

                map[name] = new HkParamTypeInfo
                {
                    Kind = info.Kind,
                    EnumChoices = info.EnumChoices,
                    ElementClassName = info.ElementClassName,
                    Min = info.Min,
                    Max = info.Max,
                    Semantic = semantic,
                    ArrayKind = info.ArrayKind,
                };
            }
        }

        private static HkParamSemantic Semantic(string paramName, bool isEventClass)
        {
            if (paramName == "eventId"
                || paramName.EndsWith("EventId", StringComparison.Ordinal)
                || paramName == "assignmentEventIndex"
                || (isEventClass && paramName == "id"))
                return HkParamSemantic.EventId;

            if (paramName == "variableIndex"
                || paramName.EndsWith("VariableIndex", StringComparison.Ordinal)
                || paramName == "variableId")
                return HkParamSemantic.VariableIndex;

            return HkParamSemantic.None;
        }

        private static void EnsureIndexes()
        {
            if (_havokTypesByName != null) return;

            Type[] all;
            try { all = typeof(hkbModifier).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            { all = ex.Types.Where(t => t != null).ToArray()!; }

            _havokTypesByName = new Dictionary<string, Type>(StringComparer.Ordinal);
            _enumByMember = new Dictionary<string, Type>(StringComparer.Ordinal);

            foreach (var t in all)
            {
                if (t == null) continue;
                if (t.IsEnum)
                {
                    foreach (var name in Enum.GetNames(t))
                        _enumByMember.TryAdd(name, t);   // collisions: first wins, fine for lookup
                }
                else if (typeof(IHavokObject).IsAssignableFrom(t))
                {
                    _havokTypesByName.TryAdd(t.Name, t);
                }
            }

            var members = _enumByMember;
            HkParamTypeInfo.KnownEnumMember = m => members.ContainsKey(m);
        }
    }
}
