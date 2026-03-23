using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class IdNamePair : INotifyPropertyChanged
    {
        public string Id { get; set; }

        private string _name;

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

        public string RawValue { get; set; }

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

        private string _value;
        public string Value
        {
            get => _value;
            set
            {
                var trimmed = value?.Trim();
                if (_value != trimmed)
                {
                    var old = _value;
                    _value = trimmed;
                    _boolValue = trimmed == "1" || trimmed?.ToLower() == "true";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BoolValue));
                    if (old != null || trimmed != null)
                        ValueChanged?.Invoke(this, (old, trimmed));
                }
            }
        }



        public event EventHandler<(string OldValue, string NewValue)> ValueChanged;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
