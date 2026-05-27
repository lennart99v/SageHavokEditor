using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SageHavokEditor.Core;
using SageHavokEditor.Models;

namespace SageHavokEditor.UI
{
    public class DebuggerViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnProp([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        // ── Collections (shared, both panels bind to these) ───────────────────
        public ObservableCollection<HistoryEntry> HistoryEntries { get; } = new();
        public ObservableCollection<LiveVariable> LiveVariables { get; } = new();
        public ObservableCollection<ActiveStateInfo> ActiveStates { get; } = new();
        public ObservableCollection<VariableValue> DragonVars { get; } = new();

        // ── Actor header ──────────────────────────────────────────────────────

        private Brush _dotFill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        public Brush DotFill { get => _dotFill; set { _dotFill = value; OnProp(); } }

        private string _actorIcon = "❓";
        public string ActorIcon { get => _actorIcon; set { _actorIcon = value; OnProp(); } }

        private string _actorLabel = "Live Debugger";
        public string ActorLabel { get => _actorLabel; set { _actorLabel = value; OnProp(); } }

        // ── Behavior mode badge ───────────────────────────────────────────────
        private Brush _accentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xAA, 0x55));
        public Brush AccentBrush { get => _accentBrush; set { _accentBrush = value; OnProp(); } }

        // ── Dragon section ────────────────────────────────────────────────────
        private Visibility _dragonVis = Visibility.Collapsed;
        public Visibility DragonVis { get => _dragonVis; set { _dragonVis = value; OnProp(); } }

        private string _dragonLabel = "🐉 MOUNT";
        public string DragonLabel { get => _dragonLabel; set { _dragonLabel = value; OnProp(); } }

        private Brush _dragonAccent = new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xBB));
        public Brush DragonAccent { get => _dragonAccent; set { _dragonAccent = value; OnProp(); } }

        private Brush _dragonBg = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x1A));
        public Brush DragonBg { get => _dragonBg; set { _dragonBg = value; OnProp(); } }

        // ── Button state ──────────────────────────────────────────────────────
        private string _pauseContent = "⏸";
        public string PauseContent { get => _pauseContent; set { _pauseContent = value; OnProp(); } }

        private string _recordContent = "⏺";
        public string RecordContent { get => _recordContent; set { _recordContent = value; OnProp(); } }

        private Brush _recordFg = new SolidColorBrush(Color.FromRgb(0xCC, 0x44, 0x44));
        public Brush RecordFg { get => _recordFg; set { _recordFg = value; OnProp(); } }

        private double _panToOpacity = 0.5;
        public double PanToOpacity { get => _panToOpacity; set { _panToOpacity = value; OnProp(); } }

        // ── Commands wired by StateMachineGraphView ───────────────────────────
        public Action? OnPauseToggle { get; set; }
        public Action? OnRecordToggle { get; set; }
        public Action? OnExportRecording { get; set; }
        public Action? OnPanToActiveToggle { get; set; }
    }
}
