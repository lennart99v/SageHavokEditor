using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SageHavokEditor.UI.Dialogs
{
    /// <summary>
    /// Searchable picker for a modifier class to create. Fed by ModifierCatalog.ClassNames;
    /// an optional curated shortlist is shown as a "Common" group above the full list.
    /// </summary>
    public partial class ModifierPickerDialog : Window
    {
        public sealed class PickerItem
        {
            public string Name { get; init; } = "";
            public string Group { get; init; } = "";
        }

        private readonly List<string> _all;
        private readonly List<PickerItem> _grouped;
        private readonly bool _hasGroups;

        /// <summary>The chosen class name, or null if cancelled.</summary>
        public string? SelectedClass { get; private set; }

        public ModifierPickerDialog(IEnumerable<string> classNames, string? prompt = null,
                                    IEnumerable<string>? commonClassNames = null)
        {
            InitializeComponent();
            if (!string.IsNullOrEmpty(prompt)) PromptText.Text = prompt;
            _all = classNames.ToList();

            var common = (commonClassNames ?? Enumerable.Empty<string>())
                .Where(_all.Contains).ToList();
            _hasGroups = common.Count > 0;

            // Common entries also stay in the full list below — the shortlist is a
            // shortcut, not a partition.
            _grouped = common
                .Select(c => new PickerItem { Name = c, Group = "Common" })
                .Concat(_all.Select(c => new PickerItem
                { Name = c, Group = _hasGroups ? "All modifiers" : "" }))
                .ToList();

            ApplyFilter("");
            Loaded += (_, __) => FilterBox.Focus();
        }

        private void ApplyFilter(string q)
        {
            // Groups only make sense on the unfiltered list; a search shows flat matches.
            List<PickerItem> src = string.IsNullOrEmpty(q)
                ? _grouped
                : _all.Where(c => c.Contains(q, StringComparison.OrdinalIgnoreCase))
                      .Select(c => new PickerItem { Name = c }).ToList();

            var view = new ListCollectionView(src);
            if (string.IsNullOrEmpty(q) && _hasGroups)
                view.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(PickerItem.Group)));

            List.ItemsSource = view;
            if (List.Items.Count > 0) List.SelectedIndex = 0;
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
            => ApplyFilter(FilterBox.Text?.Trim() ?? "");

        private void Commit()
        {
            if (List.SelectedItem is PickerItem s)
            {
                SelectedClass = s.Name;
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
