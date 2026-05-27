using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SageHavokEditor.Models.ViewModels
{
    public class IdNamePair : INotifyPropertyChanged
    {
        public string Id { get; set; } = "";

        private string _name = "";

        public int Index { get; set; }
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }
        public string VariableType { get; set; } = "VARIABLE_TYPE_REAL";

        public bool IsBool => VariableType == "VARIABLE_TYPE_BOOL";
        public bool IsInt => VariableType == "VARIABLE_TYPE_INT32";
        public bool IsReal => !IsBool && !IsInt;

        public string RawValue { get; set; } = "";

        private bool _boolValue;
        public bool BoolValue
        {
            get => _boolValue;
            set
            {
                if (_boolValue != value)
                {
                    _boolValue = value;
                    OnPropertyChanged();
                    // Update Value without re-triggering BoolValue sync
                    var newVal = value ? "1" : "0";
                    if (_value != newVal)
                    {
                        var old = _value;
                        _value = newVal;
                        OnPropertyChanged(nameof(Value));
                        ValueChanged?.Invoke(this, (old, newVal));
                    }
                }
            }
        }

        private string _value = "";
        public string Value
        {
            get => _value;
            set
            {
                var trimmed = value?.Trim() ?? "";
                if (_value != trimmed)
                {
                    var old = _value;
                    _value = trimmed;
                    _boolValue = trimmed == "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BoolValue));
                    OnPropertyChanged(nameof(BoolDisplayValue));
                    ValueChanged?.Invoke(this, (old, trimmed));
                }
            }
        }

        public string BoolDisplayValue
        {
            get => (_value == "1" || _value.Equals("true", StringComparison.OrdinalIgnoreCase)) ? "true" : "false";
            set
            {
                Value = value == "true" ? "1" : "0";
                OnPropertyChanged();
            }
        }

        public string TypeShort => VariableType switch
        {
            "VARIABLE_TYPE_BOOL" => "BOOL",
            "VARIABLE_TYPE_INT8" => "INT8",
            "VARIABLE_TYPE_INT16" => "INT16",
            "VARIABLE_TYPE_INT32" => "INT",
            "VARIABLE_TYPE_REAL" => "FLOAT",
            "VARIABLE_TYPE_FLOAT" => "FLOAT",
            "VARIABLE_TYPE_POINTER" => "PTR",
            _ => "?"
        };

        public string TypeColor => VariableType switch
        {
            "VARIABLE_TYPE_BOOL" => "#2E6DA4",
            "VARIABLE_TYPE_INT8"
                or "VARIABLE_TYPE_INT16"
                or "VARIABLE_TYPE_INT32" => "#2E7D32",
            "VARIABLE_TYPE_REAL"
                or "VARIABLE_TYPE_FLOAT" => "#7B5800",
            "VARIABLE_TYPE_POINTER" => "#6A1E6A",
            _ => "#555555"
        };


        public event EventHandler<(string OldValue, string NewValue)>? ValueChanged;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
