using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimHavokEditor.Models.ViewModels
{
    public class FileStats : INotifyPropertyChanged
    {
        private string _fileName = "No file loaded";
        private int _objectCount, _variableCount, _eventCount, _clipCount, _transitionCount, _bindingCount, _stateMachineCount;
        private bool _hasFile;

        public string FileName { get => _fileName; set { _fileName = value; OnPropertyChanged(); } }
        public bool HasFile { get => _hasFile; set { _hasFile = value; OnPropertyChanged(); } }
        public int ObjectCount { get => _objectCount; set { _objectCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
        public int VariableCount { get => _variableCount; set { _variableCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
        public int EventCount { get => _eventCount; set { _eventCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
        public int ClipCount { get => _clipCount; set { _clipCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
        public int TransitionCount { get => _transitionCount; set { _transitionCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
        public int BindingCount { get => _bindingCount; set { _bindingCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
        public int StateMachineCount { get => _stateMachineCount; set { _stateMachineCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }

        public string Summary => HasFile
            ? $"Objects: {ObjectCount}  |  Variables: {VariableCount}  |  Events: {EventCount}  |  Clips: {ClipCount}  |  Transitions: {TransitionCount}  |  Bindings: {BindingCount}  |  State Machines: {StateMachineCount}"
            : "No file loaded";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
