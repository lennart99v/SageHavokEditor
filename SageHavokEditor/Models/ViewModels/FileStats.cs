using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SageHavokEditor.Models.ViewModels
{
    public class FileStats : INotifyPropertyChanged
    {
        private string _fileName = "No file loaded";
        private int _objectCount, _variableCount, _eventCount, _clipCount, _transitionCount, _bindingCount, _stateMachineCount;
        private bool _hasFile;
        private string _platformLabel = "";
        private string _animationCacheLabel = "";

        public string FileName { get => _fileName; set { _fileName = value; OnPropertyChanged(); } }
        public bool HasFile { get => _hasFile; set { _hasFile = value; OnPropertyChanged(); } }

        /// <summary>Edition the loaded .hkx used ("Skyrim LE (32-bit)" /
        /// "Skyrim SE (64-bit)"), or empty when the source was Havok XML.</summary>
        public string PlatformLabel { get => _platformLabel; set { _platformLabel = value; OnPropertyChanged(); } }

        /// <summary>
        /// Which animationdatasinglefile.txt projects the clip-cache checks are
        /// running against, or why they are not running. Empty when no cache was
        /// found anywhere above the open file, which is the honest "we did not
        /// look at this" rather than a claim either way.
        ///
        /// It lives in the stats bar rather than the status line because the
        /// status line is overwritten by the next thing that happens, and a check
        /// being silently off is precisely what a clean report cannot tell you.
        /// </summary>
        public string AnimationCacheLabel
        {
            get => _animationCacheLabel;
            set { _animationCacheLabel = value; OnPropertyChanged(); }
        }
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
