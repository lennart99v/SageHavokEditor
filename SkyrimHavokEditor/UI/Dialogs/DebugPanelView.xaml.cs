// UI/DebugPanelView.xaml.cs
using System.Windows;
using System.Windows.Controls;

namespace SkyrimHavokEditor.UI.Dialogs
{
    public partial class DebugPanelView : UserControl
    {
        public DebugPanelView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => WireCollections();
        }

        private void WireCollections()
        {
            if (DataContext is not DebuggerViewModel vm) return;
            HistoryList.ItemsSource = vm.HistoryEntries;
            LiveVarsList.ItemsSource = vm.LiveVariables;
            ActiveStatesList.ItemsSource = vm.ActiveStates;
            DragonVarsList.ItemsSource = vm.DragonVars;
        }

        private DebuggerViewModel VM => DataContext as DebuggerViewModel;

        private void BtnPause_Click(object sender, RoutedEventArgs e)
            => VM?.OnPauseToggle?.Invoke();
        private void BtnRecord_Click(object sender, RoutedEventArgs e)
            => VM?.OnRecordToggle?.Invoke();
        private void BtnExport_Click(object sender, RoutedEventArgs e)
            => VM?.OnExportRecording?.Invoke();
        private void BtnPanToActive_Click(object sender, RoutedEventArgs e)
            => VM?.OnPanToActiveToggle?.Invoke();
    }
}
