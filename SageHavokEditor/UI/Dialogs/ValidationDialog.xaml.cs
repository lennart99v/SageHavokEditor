using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SageHavokEditor.Core.Validation;

namespace SageHavokEditor.UI.Dialogs
{
    public partial class ValidationDialog : Window
    {
        private readonly List<ValidationIssue> _allIssues;
        private readonly ObservableCollection<ValidationIssue> _filtered = new();
        public event Action<string>? ObjectSelected;

        public int ErrorCount => _allIssues.Count(i => i.IsError);
        public int WarningCount => _allIssues.Count(i => i.IsWarning);

        /// <param name="preSaveFileName">
        /// Set to run as the pre-save gate for that file: the dialog goes modal
        /// and its Close button becomes a choice, with <c>DialogResult</c> true
        /// meaning "save anyway". Null shows the same report as a plain read-out.
        /// </param>
        public ValidationDialog(GraphDoctorReport report, string? preSaveFileName = null)
        {
            InitializeComponent();
            _allIssues = report.Issues;
            IssueList.ItemsSource = _filtered;

            ErrorCountText.Text = ErrorCount.ToString();
            WarningCountText.Text = WarningCount.ToString();
            HeadlineText.Text = report.Headline;

            if (preSaveFileName != null)
            {
                Title = $"Graph doctor — before saving {preSaveFileName}";
                BtnCloseReport.Visibility = Visibility.Collapsed;
                BtnSaveAnyway.Visibility = Visibility.Visible;
                BtnCancelSave.Visibility = Visibility.Visible;
                // Nothing here refuses the save — the editor can't know whether an
                // unreachable state is a mistake or a work in progress — so the
                // safe-by-default button is the one that stops and lets you look.
                BtnCancelSave.IsDefault = true;
                SelectHint.Text = "Click an issue to select the object in the editor";
            }

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

        private void BtnSaveAnyway_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancelSave_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
