using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Models;
using SkyrimHavokEditor.Models.ViewModels;

namespace SkyrimHavokEditor.UI.Dialogs
{
    public partial class StatesEditorDialog : Window
    {
        private HavokManager _manager;
        private List<HkObject> _allStates;
        public List<string> ResultIds { get; private set; }

        public ObservableCollection<HkObject> CurrentStates { get; set; } = new();
        public ObservableCollection<HkObject> AvailableStates { get; set; } = new();
        private string _filter = "";

        public StatesEditorDialog(List<HkObject> allStates, List<string> currentIds, HavokManager manager)
        {
            InitializeComponent();
            _manager = manager;
            _allStates = allStates;
            DataContext = this;

            foreach (var id in currentIds)
            {
                if (manager.ObjectMap.TryGetValue(id, out var obj))
                    CurrentStates.Add(obj);
            }

            RefreshAvailable();
        }

        private void RefreshAvailable()
        {
            AvailableStates.Clear();
            foreach (var s in _allStates)
            {
                var name = s.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? s.Id;
                if (string.IsNullOrEmpty(_filter) ||
                    name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                    s.Id.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                    AvailableStates.Add(s);
            }
        }

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _filter = TxtFilter.Text;
            RefreshAvailable();
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (AvailableList.SelectedItem is HkObject obj && !CurrentStates.Contains(obj))
                CurrentStates.Add(obj);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentList.SelectedItem is HkObject obj)
                CurrentStates.Remove(obj);
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            ResultIds = CurrentStates.Select(o => o.Id).ToList();
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}
