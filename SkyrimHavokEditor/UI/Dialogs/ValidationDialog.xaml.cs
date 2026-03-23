using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SkyrimHavokEditor.Core.Validation;

namespace SkyrimHavokEditor.UI.Dialogs
{
    public partial class ValidationDialog : Window
    {
        private readonly List<ValidationIssue> _allIssues;
        private readonly ObservableCollection<ValidationIssue> _filtered = new();
        public event Action<string> ObjectSelected;

        public int ErrorCount => _allIssues.Count(i => i.IsError);
        public int WarningCount => _allIssues.Count(i => i.IsWarning);

        public ValidationDialog(List<ValidationIssue> issues)
        {
            InitializeComponent();
            _allIssues = issues;
            IssueList.ItemsSource = _filtered;

            ErrorCountText.Text = ErrorCount.ToString();
            WarningCountText.Text = WarningCount.ToString();

            // Don't call ApplyFilter here - use Loaded event instead
            Loaded += (s, e) => ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (_allIssues == null) return;

            _filtered.Clear();

            var source = FilterErrors?.IsChecked == true
                ? _allIssues.Where(i => i.IsError)
                : FilterWarnings?.IsChecked == true
                    ? _allIssues.Where(i => i.IsWarning)
                    : _allIssues.AsEnumerable();

            foreach (var issue in source)
                _filtered.Add(issue);
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
            => ApplyFilter();

        private void IssueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IssueList.SelectedItem is ValidationIssue issue && !string.IsNullOrEmpty(issue.ObjectId))
                ObjectSelected?.Invoke(issue.ObjectId);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();
    }
}