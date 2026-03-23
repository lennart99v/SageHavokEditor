using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;

namespace SkyrimHavokEditor.Models
{
    // Base class to provide property change notification
    public class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    [XmlRoot("hkpackfile")]
    public class HkPackfile
    {
        [XmlAttribute("classversion")]
        public string ClassVersion { get; set; }

        [XmlAttribute("contentsversion")]
        public string ContentsVersion { get; set; }

        [XmlAttribute("toplevelobject")]
        public string TopLevelObject { get; set; }

        [XmlElement("hksection")]
        public List<HkSection> Sections { get; set; } = new();
    }

    public class HkSection
    {
        [XmlAttribute("name")]
        public string Name { get; set; }

        [XmlElement("hkobject")]
        public List<HkObject> Objects { get; set; } = new();
    }

    public class HkObject : NotifyBase
    {
        private string id;
        private string className;

        // In HkObject — change the Id setter to remove the bad OnPropertyChanged
        [XmlAttribute("name")]
        public string Id
        {
            get => id;
            set => SetField(ref id, value);   // remove the OnPropertyChanged(nameof(Name)) line
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
        public string Signature { get; set; }

        [XmlElement("hkparam")]
        public List<HkParam> Params { get; set; } = new();
    }

    public class HkParam : NotifyBase
    {
        private string name;
        private string _value;

        [XmlAttribute("name")]
        public string Name
        {
            get => name;
            set => SetField(ref name, value);
        }

        [XmlAttribute("numelements")]
        public string NumElements { get; set; }

        [XmlText]
        public string Value
        {
            get
            {
                // If we have children with IDs (references like #0052), return them as text
                if (Children != null && Children.Count > 0 && !IsInlineAccounted)
                {
                    return string.Join(" ", Children.Select(c => c.Id));
                }
                return _value;
            }
            set
            {
                var trimmed = value?.Trim();
                if (_value == trimmed) return;
                var old = _value;
                _value = trimmed;
                OnPropertyChanged();
                ValueChanged?.Invoke(this, (old, trimmed));
            }
        }

        public event EventHandler<(string OldValue, string NewValue)> ValueChanged;

        // Re-added: Needed for event names/strings logic
        [XmlElement("hkcstring")]
        public List<string> Strings { get; set; } = new();

        // Hybrid logic: Only serialize as XML Elements if they are INLINE (no ID)
        [XmlElement("hkobject")]
        public List<HkObject> Children { get; set; } = new();

        // Re-added: Needed for logic that accesses the first child directly
        [XmlIgnore]
        public HkObject InnerObject
        {
            get => Children.FirstOrDefault();
            set
            {
                Children.Clear();
                if (value != null) Children.Add(value);
            }
        }

        // Logic to determine if children are "inline" (objects without #IDs that must be nested)
        [XmlIgnore]
        private bool IsInlineAccounted => Children.Any(c => string.IsNullOrEmpty(c.Id));

        public bool ShouldSerializeChildren()
        {
            // Only write <hkobject> tags if they are inline objects
            return Children != null && Children.Count > 0 && IsInlineAccounted;
        }

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
        public string Name { get; set; }
        public NodeType Type { get; set; }
        public HkObject Object { get; set; }
        public List<BehaviorNodeData> Children { get; set; } = new();
        public bool IsVisible { get; set; } = true;
    }
}