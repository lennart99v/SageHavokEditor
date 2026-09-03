using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SageHavokEditor.Core;
using SageHavokEditor.Core.Services;

namespace SageHavokEditor.UI.Dialogs
{
    /// <summary>One event name, and what each side of the reference does with it.</summary>
    public sealed class ReferenceEventRow
    {
        public string Name { get; init; } = "";
        public string Here { get; init; } = "";
        public string There { get; init; } = "";
        public string Note { get; init; } = "";
        /// <summary>Used on one side, absent from the other — it can't cross.</summary>
        public bool CannotCross { get; init; }
    }

    /// <summary>
    /// The two event tables either side of an <c>hkbBehaviorReferenceGenerator</c>,
    /// laid next to each other.
    ///
    /// This started life as a graph-doctor warning — "the referenced graph uses N
    /// events this file hasn't got" — and measurement killed it. Against vanilla
    /// SSE <c>0_master.hkx</c> that fires on 10 of its 13 references, at 1, 1, 1,
    /// 2, 3, 3, 3, 32, 135 and 418 events, on a file the game runs perfectly. A
    /// child behaviour's internal events are simply its own business, so the
    /// condition is normal rather than a defect and a warning about it is one
    /// nobody would read twice.
    ///
    /// The same numbers are worth having when you go looking for them, which is
    /// what this is: asked for from the reference node, phrased as a comparison,
    /// with the events that can't cross filterable rather than shouted about.
    /// </summary>
    public partial class BehaviorReferenceEventsDialog : Window
    {
        private readonly List<ReferenceEventRow> _all = new();
        private readonly ObservableCollection<ReferenceEventRow> _shown = new();
        private readonly string _report;

        public BehaviorReferenceEventsDialog(
            HavokManager here, string hereName, ReferencedBehavior there)
        {
            InitializeComponent();
            EventsList.ItemsSource = _shown;

            var thereName = Path.GetFileName(there.Path) ?? there.BehaviorName;
            ThereColumn.Header = thereName;

            var hereNames = here.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData")
                ?.Params.FirstOrDefault(p => p.Name == "eventNames")?.Strings
                ?? new List<string>();
            // "Used" has to mean the same thing on both sides or the comparison is
            // meaningless, so it comes from the same scan the index runs.
            var hereUsed = BehaviorReferenceIndex.UsedEvents(here, hereNames);

            var hereSet = new HashSet<string>(hereNames, StringComparer.OrdinalIgnoreCase);
            var thereSet = new HashSet<string>(there.EventNames, StringComparer.OrdinalIgnoreCase);
            var thereUsed = new HashSet<string>(there.UsedEventNames, StringComparer.OrdinalIgnoreCase);

            foreach (var name in hereSet.Union(thereSet, StringComparer.OrdinalIgnoreCase)
                                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                bool inHere = hereSet.Contains(name), inThere = thereSet.Contains(name);
                bool usedHere = hereUsed.Contains(name), usedThere = thereUsed.Contains(name);

                // Only a *used* event can be disappointed by the other side's
                // silence. One merely declared and never referenced crosses or
                // doesn't without anything noticing.
                bool cannotCross = (usedHere && !inThere) || (usedThere && !inHere);

                _all.Add(new ReferenceEventRow
                {
                    Name = name,
                    Here = Side(inHere, usedHere),
                    There = Side(inThere, usedThere),
                    Note = !cannotCross
                        ? (inHere && inThere ? "shared" : "")
                        : usedThere && !inHere
                            ? "used there, no entry here"
                            : "used here, no entry there",
                    CannotCross = cannotCross,
                });
            }

            int shared = _all.Count(r => r.Here != "—" && r.There != "—");
            int blocked = _all.Count(r => r.CannotCross);

            HeadlineText.Text =
                $"{hereName} ↔ {thereName}. The two graphs link by event name, and each keeps its own " +
                $"table: {shared} name{(shared == 1 ? " appears" : "s appear")} in both, " +
                $"{blocked} {(blocked == 1 ? "is" : "are")} used on one side with no entry on the other.";

            StatusText.Text =
                $"{_all.Count} distinct names · {shared} shared · " +
                $"{_all.Count(r => r.There == "—")} only here · " +
                $"{_all.Count(r => r.Here == "—")} only there. " +
                "An event a referenced graph only uses internally needs no entry here — vanilla " +
                "0_master has hundreds — so treat the list as leads, not faults.";

            _report = BuildReport(hereName, thereName, there.Path ?? there.BehaviorName);
            ApplyFilter();
        }

        private static string Side(bool declared, bool used) =>
            !declared ? "—" : used ? "declared, used" : "declared";

        private string BuildReport(string hereName, string thereName, string therePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Events across the behavior reference");
            sb.AppendLine($"  this file : {hereName}");
            sb.AppendLine($"  referenced: {thereName}  ({therePath})");
            sb.AppendLine();
            sb.AppendLine($"{"event",-52}{"this file",-18}{thereName}");
            foreach (var r in _all)
                sb.AppendLine($"{r.Name,-52}{r.Here,-18}{r.There}"
                              + (r.Note.Length > 0 ? $"   [{r.Note}]" : ""));
            return sb.ToString();
        }

        private void ApplyFilter()
        {
            var text = (FilterBox.Text ?? "").Trim();
            bool blockedOnly = UnsharedOnlyCheck.IsChecked == true;

            _shown.Clear();
            foreach (var r in _all)
            {
                if (blockedOnly && !r.CannotCross) continue;
                if (text.Length > 0 &&
                    r.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _shown.Add(r);
            }
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
        private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_report); } catch { /* clipboard busy */ }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
