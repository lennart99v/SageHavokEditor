using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class SmTransitionRow : INotifyPropertyChanged
    {
        // Source info — needed to write back to XML
        public HkObject OwnerState { get; set; }  // hkbStateMachineStateInfo
        public HkObject TransitionArray { get; set; }  // hkbStateMachineTransitionInfoArray
        public HkObject TransitionChild { get; set; }  // the inline hkobject inside transitions
        public HkObject ParentSM { get; set; }  // hkbStateMachine that owns OwnerState
        public string ParentSMName { get; set; }

        // Display
        public string FromState { get; set; }

        private string _toState;
        public string ToState
        {
            get => _toState;
            set { if (_toState != value) { _toState = value; OnPropertyChanged(); } }
        }

        private string _eventName;
        public string EventName
        {
            get => _eventName;
            set { if (_eventName != value) { _eventName = value; OnPropertyChanged(); } }
        }
        public string Flags { get; set; }
        // Add this property to SmTransitionRow
        public HkObject TransitionEffectObj { get; set; }  // hkbBlendingTransitionEffect

        // Replace the BlendDuration property:
        private string _blendDuration = "";
        public string BlendDuration
        {
            get => _blendDuration;
            set
            {
                if (_blendDuration == value) return;
                _blendDuration = value;
                OnPropertyChanged();

                // Write through to the actual effect object's duration param
                if (TransitionEffectObj != null)
                {
                    var durParam = TransitionEffectObj.Params
                        .FirstOrDefault(p => p.Name == "duration");
                    if (durParam != null)
                        durParam.Value = value;
                }
            }
        }



        // Editable backing fields — write through to the HkObject on change
        private string _eventId;
        public string EventId
        {
            get => _eventId;
            set
            {
                if (_eventId != value)
                {
                    _eventId = value; OnPropertyChanged();
                    WriteBack("eventId", value);
                }
            }
        }

        private string _toStateId;
        public string ToStateId
        {
            get => _toStateId;
            set
            {
                if (_toStateId != value)
                {
                    _toStateId = value; OnPropertyChanged();
                    WriteBack("toStateId", value);
                }
            }
        }

        private void WriteBack(string paramName, string val)
        {
            var p = TransitionChild?.Params.FirstOrDefault(x => x.Name == paramName);
            if (p != null) p.Value = val;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
