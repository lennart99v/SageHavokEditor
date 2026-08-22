using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Services;
using SageHavokEditor.Models.ViewModels;

namespace SageHavokEditor.UI.Dialogs
{
    /// <summary>
    /// Whole-file event cross-reference: for every event in the table, what listens
    /// to it and what sends it. The counts are computed up front so unused events —
    /// the ones a hand-edited or tool-extended event table tends to accumulate —
    /// are visible without clicking through each one.
    /// </summary>
    public partial class EventCrossReferenceDialog : Window
    {
        private readonly HavokManager _manager;
        private readonly List<EventXrefSummary> _all = new();
        private readonly Dictionary<string, List<EventUsageEntry>> _usages = new();

        public ObservableCollection<EventXrefSummary> Events { get; } = new();
        public ObservableCollection<EventUsageEntry> Usages { get; } = new();

        /// <summary>Raised with an object id when the user double-clicks a usage.</summary>
        public event Action<string> ObjectSelected;

        public EventCrossReferenceDialog(HavokManager manager, IEnumerable<IdNamePair> events)
        {
            InitializeComponent();
            _manager = manager;

            EventsList.ItemsSource = Events;
            UsagesList.ItemsSource = Usages;

            foreach (var ev in events ?? Enumerable.Empty<IdNamePair>())
            {
                if (string.IsNullOrEmpty(ev.Id)) continue;

                var found = EventCrossReference.Find(_manager, ev.Id);
                _usages[ev.Id] = found;
                _all.Add(new EventXrefSummary
                {
                    Id = ev.Id,
                    Name = string.IsNullOrEmpty(ev.Name) ? $"‹unnamed #{ev.Id}›" : ev.Name,
                    Listens = found.Count(u => u.Direction == EventCrossReference.Listens),
                    Sends = found.Count(u => u.Direction == EventCrossReference.Sends),
                });
            }

            ApplyFilter();

            var unreferenced = _all.Count(e => e.IsOrphan);
            StatusText.Text = $"{_all.Count} events · {_all.Sum(e => e.Total)} references · " +
                              $"{unreferenced} with no reference in this file";
        }

        private void ApplyFilter()
        {
            var text = FilterBox.Text?.Trim() ?? "";
            var unusedOnly = UnusedOnlyCheck.IsChecked == true;

            var filtered = _all.AsEnumerable();

            if (unusedOnly)
                filtered = filtered.Where(e => e.IsOrphan);

            if (text.Length > 0)
                filtered = filtered.Where(e =>
                    e.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Id.Equals(text, StringComparison.OrdinalIgnoreCase));

            Events.Clear();
            foreach (var e in filtered.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                Events.Add(e);
        }

        private void ShowUsages(EventXrefSummary summary)
        {
            Usages.Clear();
            if (summary == null)
            {
                UsageHeader.Text = "Select an event";
                return;
            }

            UsageHeader.Text = summary.Total == 0
                ? $"{summary.Name}  (#{summary.Id})  —  nothing in this file references it " +
                  "(may still be fired by an animation annotation or another behaviour)"
                : $"{summary.Name}  (#{summary.Id})  —  {summary.Listens} listen, {summary.Sends} send";

            if (!_usages.TryGetValue(summary.Id, out var list)) return;
            foreach (var u in list) Usages.Add(u);
        }

        private void EventsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ShowUsages(EventsList.SelectedItem as EventXrefSummary);

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void UnusedOnlyCheck_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

        private void UsagesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (UsagesList.SelectedItem is not EventUsageEntry usage) return;
            if (string.IsNullOrEmpty(usage.ObjectId)) return;
            ObjectSelected?.Invoke(usage.ObjectId);
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (EventsList.SelectedItem is not EventXrefSummary summary)
            {
                MessageBox.Show(this, "Select an event first.", "Copy report",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{summary.Name}  (id {summary.Id})");
            sb.AppendLine(new string('-', 60));

            if (!_usages.TryGetValue(summary.Id, out var list) || list.Count == 0)
            {
                sb.AppendLine("no references");
            }
            else
            {
                foreach (var u in list)
                    sb.AppendLine($"[{u.Direction,-7}] {u.UsageType,-10} {u.Description}   ({u.SubText})");
            }

            try
            {
                Clipboard.SetText(sb.ToString());
                StatusText.Text = $"Copied {(list?.Count ?? 0)} references for {summary.Name}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not access the clipboard:\n{ex.Message}", "Copy report",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
