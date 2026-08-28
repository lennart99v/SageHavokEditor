using System;
using System.Windows;

namespace SageHavokEditor.UI.Dialogs
{
    /// <summary>
    /// Options for duplicating a state: the copy's name, and how far the copy
    /// reaches. Both checkboxes change how many objects get created, so the
    /// summary line re-counts through the caller's <c>countObjects</c> callback —
    /// it runs the real traversal, not an estimate, because "this creates 60
    /// objects" is the whole warning for a state whose generator is a nested
    /// machine.
    /// </summary>
    public partial class DuplicateStateDialog : Window
    {
        private readonly Func<bool, bool, int> _countObjects;

        public string NewName => TxtName.Text.Trim();
        public bool CopyGenerator => ChkGenerator.IsChecked == true;
        public bool CopyTransitions => ChkTransitions.IsChecked == true;

        public DuplicateStateDialog(
            string stateName, string machineName, string defaultName,
            string? generatorName, int transitionCount,
            Func<bool, bool, int> countObjects)
        {
            InitializeComponent();
            _countObjects = countObjects;

            HeaderText.Text = $"Duplicating '{stateName}' in state machine '{machineName}'.";
            TxtName.Text = defaultName;

            if (generatorName == null)
            {
                ChkGenerator.Content = "Duplicate the generator subtree";
                ChkGenerator.IsChecked = false;
                ChkGenerator.IsEnabled = false;
            }
            else
            {
                ChkGenerator.Content = $"Duplicate the generator subtree ('{generatorName}')";
            }

            if (transitionCount <= 0)
            {
                ChkTransitions.Content = "Copy the outgoing transitions";
                ChkTransitions.IsChecked = false;
                ChkTransitions.IsEnabled = false;
            }
            else
            {
                ChkTransitions.Content = transitionCount == 1
                    ? "Copy the 1 outgoing transition"
                    : $"Copy the {transitionCount} outgoing transitions";
            }

            UpdateSummary();

            TxtName.Focus();
            TxtName.SelectAll();
        }

        private void Option_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

        private void UpdateSummary()
        {
            int n = _countObjects(CopyGenerator, CopyTransitions);
            SummaryText.Text =
                $"Creates {n} new object{(n == 1 ? "" : "s")}. "
                + "Nothing transitions to the copy yet — transitions route by state id, "
                + "and the copy gets a fresh one.";
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NewName))
            {
                MessageBox.Show(this, "Give the copy a name.", "Duplicate State",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        { DialogResult = false; Close(); }
    }
}
