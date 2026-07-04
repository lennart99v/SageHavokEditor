using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SageHavokEditor.UI.Dialogs
{
    /// <summary>
    /// Searchable picker for a modifier class to create. Fed by ModifierCatalog.ClassNames.
    /// </summary>
    public partial class ModifierPickerDialog : Window
    {
        private readonly List<string> _all;

        /// <summary>The chosen class name, or null if cancelled.</summary>
        public string? SelectedClass { get; private set; }

        public ModifierPickerDialog(IEnumerable<string> classNames, string? prompt = null)
        {
            InitializeComponent();
            if (!string.IsNullOrEmpty(prompt)) PromptText.Text = prompt;
            _all = classNames.ToList();
            List.ItemsSource = _all;
            if (_all.Count > 0) List.SelectedIndex = 0;
            Loaded += (_, __) => FilterBox.Focus();
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var q = FilterBox.Text?.Trim() ?? "";
            List.ItemsSource = string.IsNullOrEmpty(q)
                ? _all
                : _all.Where(c => c.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (List.Items.Count > 0) List.SelectedIndex = 0;
        }

        private void Commit()
        {
            if (List.SelectedItem is string s)
            {
                SelectedClass = s;
                DialogResult = true;
                Close();
            }
        }

        private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => Commit();

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Commit();

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
