using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;

namespace SageHavokEditor.Models
{
    // Base class to provide property change notification
    public class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public enum HkParamKind { Text, Bool, Int, Real, Enum }

    /// <summary>
    /// What an integer param's number actually *means*, where the declared type
    /// alone doesn't say. Both of these are positional indices into a table the
    /// file already carries, so the editor can show names instead of numbers.
    /// </summary>
    public enum HkParamSemantic
    {
        None = 0,
        /// <summary>Index into hkbBehaviorGraphStringData.eventNames; -1 = none.</summary>
        EventId,
        /// <summary>Index into hkbBehaviorGraphStringData.variableNames; -1 = none.</summary>
        VariableIndex,
    }

    /// <summary>
    /// Declared Havok type of a param, sourced from the HKX2 class definitions
    /// (see HavokTypeCatalog). One shared instance per (class, param) — never
    /// mutate after construction.
    /// </summary>
    public class HkParamTypeInfo
    {
        public HkParamKind Kind { get; init; }
        public IReadOnlyList<string>? EnumChoices { get; init; }
        /// <summary>Table this int indexes into, when it is an index — see HkParamSemantic.</summary>
        public HkParamSemantic Semantic { get; init; }
        /// <summary>Class of inline child hkobjects (struct members / struct arrays).</summary>
        public string? ElementClassName { get; init; }
        public long Min { get; init; } = long.MinValue;
        public long Max { get; init; } = long.MaxValue;

        /// <summary>
        /// Membership test over every HKX2 enum member name. Flags fields are
        /// declared as plain ints in HKX2 but serialize as "FLAG_A|FLAG_B", so
        /// Int validation falls back to this. Wired up by HavokTypeCatalog.
        /// </summary>
        public static Func<string, bool>? KnownEnumMember { get; set; }

        public string Hint => Kind switch
        {
            HkParamKind.Bool => "Expected: true or false",
            HkParamKind.Int  => Min == long.MinValue
                ? "Expected: a whole number"
                : $"Expected: a whole number ({Min} … {Max})",
            HkParamKind.Real => "Expected: a decimal number",
            HkParamKind.Enum => "Expected: one of the listed values",
            _ => ""
        };

        public bool IsValid(string? value)
        {
            var v = (value ?? "").Trim();
            switch (Kind)
            {
                case HkParamKind.Bool:
                    return v.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || v.Equals("false", StringComparison.OrdinalIgnoreCase);
                case HkParamKind.Real:
                    return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                case HkParamKind.Int:
                    if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        return n >= Min && n <= Max;
                    return IsFlagCombo(v);
                case HkParamKind.Enum:
                    // HKX2 reads enums and flags through the same ReadFlag path, so
                    // pipe-joined member combos and numeric (incl. hex) remainders
                    // are legal ("FLAG_OUTPUT|FLAG_HIDDEN", "FLAG_RAGDOLL|0x4c0").
                    // Tokens check against the assembly-wide member index, not just
                    // EnumChoices — the choices list is a best guess when HKX2
                    // declares the member as a bare int (member names collide
                    // across enums), and a wrong guess must not fail validation.
                    return IsFlagCombo(v);
                default:
                    return true;
            }
        }

        private bool IsFlagCombo(string v)
        {
            if (v.Length == 0) return false;
            return v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .All(t => IsNumericToken(t)
                           || (EnumChoices?.Contains(t) ?? false)
                           || (KnownEnumMember?.Invoke(t) ?? false));
        }

        private static bool IsNumericToken(string t) =>
            long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _));
    }

    // Havok's XML writer wraps long ref arrays across lines, so a "states"-style value can
    // contain tokens like "#0137\n\t\t\t#0141". Anything splitting a ref list must use these
    // separators — splitting on ' ' alone silently drops the wrapped refs.
    public static class HkRefList
    {
        public static readonly char[] Separators = { ' ', '\t', '\n', '\r' };

        public static string[] Tokens(string? value) =>
            (value ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);
    }

    [XmlRoot("hkpackfile")]
    public class HkPackfile
    {
        [XmlAttribute("classversion")]
        public string ClassVersion { get; set; } = "";

        [XmlAttribute("contentsversion")]
        public string ContentsVersion { get; set; } = "";

        [XmlAttribute("toplevelobject")]
        public string TopLevelObject { get; set; } = "";

        [XmlElement("hksection")]
        public List<HkSection> Sections { get; set; } = new();
    }

    public class HkSection
    {
        [XmlAttribute("name")]
        public string Name { get; set; } = "";

        [XmlElement("hkobject")]
        public List<HkObject> Objects { get; set; } = new();
    }

    public class HkObject : NotifyBase
    {
        private string id = "";
        private string className = "";

        [XmlAttribute("name")]
        public string Id
        {
            get => id;
            set => SetField(ref id, value);
        }

        [XmlIgnore]
        public string DisplayName =>
            Params?.FirstOrDefault(p => p.Name == "name")?.Value ?? Id ?? "?";

        [XmlAttribute("class")]
        public string ClassName
        {
            get => className;
            set => SetField(ref className, value);
        }

        [XmlAttribute("signature")]
        public string Signature { get; set; } = "";

        [XmlElement("hkparam")]
        public List<HkParam> Params { get; set; } = new();

        /// <summary>Deep-clone as an inline (anonymous) element — see HkParam.Clone.</summary>
        public HkObject CloneAsInline() => new()
        {
            Id = "",
            ClassName = ClassName,
            Signature = Signature,
            Params = Params.Select(p => p.Clone()).ToList()
        };
    }

    public class HkParam : NotifyBase
    {
        private string name = "";
        private string _value = "";

        [XmlAttribute("name")]
        public string Name
        {
            get => name;
            set => SetField(ref name, value);
        }

        [XmlAttribute("numelements")]
        public string NumElements { get; set; } = "";

        [XmlText]
        public string Value
        {
            get
            {
                // Don't override value with children refs if we have hkcstring data
                if (Strings != null && Strings.Count > 0)
                    return _value;
                if (Children != null && Children.Count > 0 && !IsInlineAccounted)
                    return string.Join(" ", Children.Select(c => c.Id));
                return _value;
            }
            set
            {
                var trimmed = value?.Trim() ?? "";
                if (_value == trimmed) return;
                var old = _value;
                _value = trimmed;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValueTypeValid));
                ValueChanged?.Invoke(this, (old, trimmed));
            }
        }

        private HkParamTypeInfo? typeInfo;

        /// <summary>Declared Havok type, annotated at load by HavokTypeCatalog. Null = unknown.</summary>
        [XmlIgnore]
        public HkParamTypeInfo? TypeInfo
        {
            get => typeInfo;
            set { if (SetField(ref typeInfo, value)) OnPropertyChanged(nameof(IsValueTypeValid)); }
        }

        [XmlIgnore]
        public bool IsValueTypeValid => typeInfo?.IsValid(Value) ?? true;

        /// <summary>
        /// True for array params whose elements are inline (anonymous) hkobjects,
        /// e.g. hkbStateMachineEventPropertyArray.events. Sticky: set at load by
        /// HavokTypeCatalog.Annotate and maintained by the add/remove element
        /// handlers, so the "+ Add element" affordance survives deleting the last
        /// element (an empty array's element shape is otherwise indistinguishable
        /// from a ref array's).
        /// </summary>
        [XmlIgnore]
        public bool IsInlineStructArray { get; set; }

        /// <summary>
        /// Deep-clone this object as an inline (anonymous) element: no id, so it
        /// serializes nested inside its parent param. Inline grandchildren are
        /// cloned recursively; cached resolved refs stay shared — a cloned
        /// reference points at the same target, which is the correct semantic.
        /// </summary>
        public HkParam Clone()
        {
            var clone = new HkParam
            {
                Name = Name,
                NumElements = NumElements,
                Strings = new List<string>(Strings),
                Children = Children
                    .Select(c => string.IsNullOrEmpty(c.Id) ? c.CloneAsInline() : c)
                    .ToList(),
                TypeInfo = TypeInfo,
                IsInlineStructArray = IsInlineStructArray,
            };
            clone.Value = _value;   // raw text, not the getter's Children join
            return clone;
        }

        /// <summary>
        /// Recount NumElements from the value's tokens. Only meaningful for pure
        /// text-token arrays (ref lists): string arrays are counted by Strings,
        /// inline/cached arrays by Children, and scalars carry no numelements.
        /// Call from interactive edit paths — NOT from the Value setter, which
        /// also runs mid-deserialization before Strings/Children are populated
        /// and would clobber their counts. HKX2 treats numelements as
        /// authoritative on XML→HKX conversion, so a stale count silently
        /// truncates the array.
        /// </summary>
        public void ResyncNumElements()
        {
            if (string.IsNullOrWhiteSpace(NumElements)) return;
            if (Strings.Count > 0) return;
            // Inline structs are managed by dedicated editors; cached resolved
            // refs (non-inline children) count the same as their text tokens.
            if (Children.Any(c => string.IsNullOrEmpty(c.Id))) return;
            NumElements = Children.Count > 0
                ? Children.Count.ToString()
                : HkRefList.Tokens(_value).Length.ToString();
        }

        /// <summary>
        /// Rebuild the cached resolved-ref Children from the current text value.
        /// The Value getter prefers the Children join whenever resolved refs are
        /// cached there, so a text edit that only writes _value silently doesn't
        /// stick (display and save keep showing the stale ref). Call after
        /// interactive edits with the owning manager's resolver. If every token
        /// resolves, the cache is rebuilt — mutating a reference must update
        /// Children, not just Value. Otherwise (typo, "null", ref not created
        /// yet) the cache is cleared and the typed text becomes authoritative;
        /// the save-time broken-reference check reports any bad token.
        /// </summary>
        public void ReresolveChildren(Func<string, HkObject?> resolve)
        {
            if (Children.Count == 0) return;
            if (Children.Any(c => string.IsNullOrEmpty(c.Id))) return;  // inline structs

            var toks = HkRefList.Tokens(_value);
            var resolved = toks.All(t => t.StartsWith("#"))
                ? toks.Select(resolve).ToList()
                : null;

            Children.Clear();
            if (resolved != null && resolved.Count > 0 && resolved.All(o => o != null))
                foreach (var o in resolved) Children.Add(o!);
        }

        public event EventHandler<(string OldValue, string NewValue)>? ValueChanged;

        [XmlElement("hkcstring")]
        public List<string> Strings { get; set; } = new();

        // Hybrid logic: only serialize as XML elements if children are INLINE (no ID).
        [XmlElement("hkobject")]
        public List<HkObject> Children { get; set; } = new();

        [XmlIgnore]
        public HkObject? InnerObject
        {
            get => Children.FirstOrDefault();
            set
            {
                Children.Clear();
                if (value != null) Children.Add(value);
            }
        }

        // True when children are "inline" (objects without #IDs that must be nested).
        [XmlIgnore]
        private bool IsInlineAccounted => Children.Any(c => string.IsNullOrEmpty(c.Id));

        public bool ShouldSerializeChildren()
            => Children != null && Children.Count > 0 && IsInlineAccounted;

        public bool ShouldSerializeValue()
        {
            // If we are nesting objects inline, don't write the text Value
            if (IsInlineAccounted) return false;
            return !string.IsNullOrEmpty(Value);
        }

        public bool ShouldSerializeStrings() => Strings != null && Strings.Count > 0;
    }

    public enum NodeType { Root, StateMachine, State, Generator, Transition, Modifier }

    public class BehaviorNodeData
    {
        public string Name { get; set; } = "";
        public NodeType Type { get; set; }
        public HkObject? Object { get; set; }
        public List<BehaviorNodeData> Children { get; set; } = new();
        public bool IsVisible { get; set; } = true;
    }
}
