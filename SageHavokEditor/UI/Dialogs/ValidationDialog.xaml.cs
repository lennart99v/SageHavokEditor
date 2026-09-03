using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SageHavokEditor.Core.Validation;

namespace SageHavokEditor.UI.Dialogs
{
    /// <summary>What the dialog is being opened for.</summary>
    public enum ValidationDialogMode
    {
        /// <summary>🔎 Validate: a read-out, dismissed with Close.</summary>
        Report,
        /// <summary>Before a save that is allowed: Save anyway / Cancel save.</summary>
        PreSaveDecision,
        /// <summary>After a save was refused: the reasons, and no way to overrule them.</summary>
        SaveRefused,
    }

    public partial class ValidationDialog : Window
    {
        private readonly List<ValidationIssue> _allIssues;
        private readonly ObservableCollection<ValidationIssue> _filtered = new();
        public event Action<string>? ObjectSelected;

        public int ErrorCount => _allIssues.Count(i => i.IsError);
        public int WarningCount => _allIssues.Count(i => i.IsWarning);

        /// <param name="mode">Which of the dialog's three jobs this instance is doing.</param>
        /// <param name="fileName">The file being saved, for the two save-time modes.</param>
        /// <param name="headline">Overrides the report's own headline — the refusal says why.</param>
        public ValidationDialog(GraphDoctorReport report,
            ValidationDialogMode mode = ValidationDialogMode.Report,
            string? fileName = null, string? headline = null)
        {
            InitializeComponent();
            _allIssues = report.Issues;

            // Both badges turn green at zero through a DataTrigger on ErrorCount /
            // WarningCount, and neither could ever fire: nothing set a DataContext,
            // so the bindings resolved to nothing and the styles kept their setter
            // defaults. A clean file has been showing a purple "0 Errors" since the
            // dialog was written.
            DataContext = this;
            IssueList.ItemsSource = _filtered;

            ErrorCountText.Text = ErrorCount.ToString();
            WarningCountText.Text = WarningCount.ToString();
            HeadlineText.Text = headline ?? report.Headline;

            switch (mode)
            {
                case ValidationDialogMode.PreSaveDecision:
                    Title = $"Graph doctor — before saving {fileName}";
                    BtnCloseReport.Visibility = Visibility.Collapsed;
                    BtnSaveAnyway.Visibility = Visibility.Visible;
                    BtnCancelSave.Visibility = Visibility.Visible;
                    // Nothing in this mode refuses the save — the editor can't know
                    // whether an unreachable state is a mistake or a work in
                    // progress — so the safe-by-default button is the one that
                    // stops and lets you look.
                    BtnCancelSave.IsDefault = true;
                    SelectHint.Text = "Click an issue to select the object in the editor";
                    break;

                case ValidationDialogMode.SaveRefused:
                    // No decision to offer: these findings mean the graph
                    // contradicts itself, and the file is not written.
                    Title = $"Save refused — {fileName}";
                    SelectHint.Text = "Click an issue to select the object in the editor";
                    break;
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
