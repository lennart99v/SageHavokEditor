using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Serialization;
using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Models;
using SkyrimHavokEditor.Models.ViewModels;

namespace SkyrimHavokEditor.UI.Dialogs
{
    public partial class CompareDialog : Window
    {
        private readonly HavokManager _managerA;
        private HavokManager? _managerB;
        private readonly List<IdNamePair> _variablesA;
        private readonly List<IdNamePair> _eventsA;
        private readonly List<ClipInfo> _clipsA;

        private readonly List<CompareResult> _allResults = new();
        private readonly ObservableCollection<CompareResult> _filtered = new();

        public event Action<string>? ObjectSelected;

        public CompareDialog(HavokManager managerA,
    List<IdNamePair> variables, List<IdNamePair> events, List<ClipInfo> clips)
        {
            InitializeComponent();
            _managerA = managerA;
            _variablesA = variables;
            _eventsA = events;
            _clipsA = clips;

            ResultsList.ItemsSource = _filtered;
        }

        private void BtnBrowseB_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "XML files|*.xml" };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var serializer = new XmlSerializer(typeof(HkPackfile));
                using var fs = new FileStream(dlg.FileName, FileMode.Open);
                var packfile = (HkPackfile?)serializer.Deserialize(fs)
                    ?? throw new InvalidDataException("Could not parse XML.");

                _managerB = new HavokManager();
                _managerB.BuildGraph(packfile);

                FileBLabel.Text = Path.GetFileName(dlg.FileName);
                RunComparison();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading File B: " + ex.Message);
            }
        }

        private void RunComparison()
        {
            if (_managerB == null) return;
            _allResults.Clear();

            // Decode IEEE 754 bit patterns same as main window
            string Decode(string rawValue)
            {
                if (string.IsNullOrWhiteSpace(rawValue)) return "0";
                if (long.TryParse(rawValue, out long longVal))
                {
                    int intVal = (int)longVal;
                    if (Math.Abs(intVal) > 1000000 || intVal < 0)
                    {
                        float f = BitConverter.Int32BitsToSingle(intVal);
                        if (!float.IsNaN(f) && !float.IsInfinity(f))
                            return f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    return intVal.ToString();
                }
                return rawValue;
            }

            // --- VARIABLES ---
            var varNamesB = new List<string>();
            var varValuesB = new List<string>();
            var strDataB = _managerB.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            var valueSetB = _managerB.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbVariableValueSet");

            if (strDataB != null)
            {
                var np = strDataB.Params.FirstOrDefault(p => p.Name == "variableNames");
                varNamesB = np?.Strings ?? new List<string>();
            }
            if (valueSetB != null)
            {
                var vp = valueSetB.Params.FirstOrDefault(p => p.Name == "wordVariableValues");
                if (vp?.Children != null)
                    varValuesB = vp.Children
                        .Select(c => Decode(c.Params.FirstOrDefault(p => p.Name == "value")?.Value ?? "0"))
                        .ToList();
            }

            int maxVars = Math.Max(_variablesA.Count, varNamesB.Count);
            for (int i = 0; i < maxVars; i++)
            {
                var nameA = i < _variablesA.Count ? _variablesA[i].Name : null;
                var valueA = i < _variablesA.Count ? _variablesA[i].Value : null;
                var nameB = i < varNamesB.Count ? varNamesB[i] : null;
                var valueB = i < varValuesB.Count ? varValuesB[i] : null;

                string diffType;
                if (nameA == null) diffType = "Added";
                else if (nameB == null) diffType = "Removed";
                else if (nameA != nameB || valueA != valueB) diffType = "Modified";
                else diffType = "Same";

                _allResults.Add(new CompareResult
                {
                    Category = "Variable",
                    Name = nameA ?? nameB ?? $"var[{i}]",
                    Id = i.ToString(),
                    ValueA = nameA != null ? $"{nameA} = {valueA}" : "(not present)",
                    ValueB = nameB != null ? $"{nameB} = {valueB}" : "(not present)",
                    DiffType = diffType
                });
            }

            // --- EVENTS ---
            var eventNamesB = new List<string>();
            var evStrDataB = _managerB.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            if (evStrDataB != null)
            {
                var ep = evStrDataB.Params.FirstOrDefault(p => p.Name == "eventNames");
                eventNamesB = ep?.Strings ?? new List<string>();
            }

            int maxEvents = Math.Max(_eventsA.Count, eventNamesB.Count);
            for (int i = 0; i < maxEvents; i++)
            {
                var nameA = i < _eventsA.Count ? _eventsA[i].Name : null;
                var nameB = i < eventNamesB.Count ? eventNamesB[i] : null;

                string diffType;
                if (nameA == null) diffType = "Added";
                else if (nameB == null) diffType = "Removed";
                else if (nameA != nameB) diffType = "Modified";
                else diffType = "Same";

                _allResults.Add(new CompareResult
                {
                    Category = "Event",
                    Name = nameA ?? nameB ?? $"event[{i}]",
                    Id = i.ToString(),
                    ValueA = nameA ?? "(not present)",
                    ValueB = nameB ?? "(not present)",
                    DiffType = diffType
                });
            }

            // --- CLIPS ---
            var clipsB = _managerB.ObjectMap.Values
                .Where(o => o.ClassName == "hkbClipGenerator")
                .ToDictionary(
                    o => o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id,
                    o => o.Params.FirstOrDefault(p => p.Name == "animationName")?.Value ?? "");

            var clipsADict = _clipsA.ToDictionary(c => c.Name, c => c.AnimationPath ?? "");

            var allClipNames = clipsADict.Keys.Union(clipsB.Keys).OrderBy(n => n);
            foreach (var name in allClipNames)
            {
                bool inA = clipsADict.TryGetValue(name, out var pathA);
                bool inB = clipsB.TryGetValue(name, out var pathB);

                string diffType;
                if (!inA) diffType = "Added";
                else if (!inB) diffType = "Removed";
                else if (pathA != pathB) diffType = "Modified";
                else diffType = "Same";

                _allResults.Add(new CompareResult
                {
                    Category = "Clip",
                    Name = name,
                    ValueA = inA ? (pathA ?? "") : "(not present)",
                    ValueB = inB ? (pathB ?? "") : "(not present)",
                    DiffType = diffType
                });
            }

            // --- OBJECTS (class-level) ---
            var classesA = _managerA.ObjectMap.Values
                .GroupBy(o => o.ClassName)
                .ToDictionary(g => g.Key, g => g.Count());
            var classesB = _managerB.ObjectMap.Values
                .GroupBy(o => o.ClassName)
                .ToDictionary(g => g.Key, g => g.Count());

            var allClasses = classesA.Keys.Union(classesB.Keys).OrderBy(c => c);
            foreach (var cls in allClasses)
            {
                classesA.TryGetValue(cls, out int countA);
                classesB.TryGetValue(cls, out int countB);

                string diffType;
                if (countA == 0) diffType = "Added";
                else if (countB == 0) diffType = "Removed";
                else if (countA != countB) diffType = "Modified";
                else diffType = "Same";

                _allResults.Add(new CompareResult
                {
                    Category = "Object",
                    Name = cls,
                    ValueA = countA > 0 ? $"{countA} instance(s)" : "(not present)",
                    ValueB = countB > 0 ? $"{countB} instance(s)" : "(not present)",
                    DiffType = diffType
                });
            }

            UpdateSummary();
            ApplyFilter();
        }

        private void UpdateSummary()
        {
            int added = _allResults.Count(r => r.IsAdded);
            int removed = _allResults.Count(r => r.IsRemoved);
            int modified = _allResults.Count(r => r.IsModified);
            SummaryText.Text = $"+ {added} added   − {removed} removed   ≠ {modified} modified";
        }

        private void ApplyFilter()
        {
            if (_filtered == null) return;
            if (ChkHideSame == null) return;

            _filtered.Clear();
            bool hideSame = ChkHideSame.IsChecked == true;

            foreach (var r in _allResults)
            {
                if (hideSame && r.IsSame) continue;
                if (r.Category == "Variable" && ChkVariables?.IsChecked != true) continue;
                if (r.Category == "Event" && ChkEvents?.IsChecked != true) continue;
                if (r.Category == "Clip" && ChkClips?.IsChecked != true) continue;
                if (r.Category == "Object" && ChkObjects?.IsChecked != true) continue;
                _filtered.Add(r);
            }
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
            => ApplyFilter();

        private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsList.SelectedItem is CompareResult r && !string.IsNullOrEmpty(r.Id))
                ObjectSelected?.Invoke(r.Id);
        }

        private void BtnExportDiff_Click(object sender, RoutedEventArgs e)
        {
            if (_allResults.Count == 0) { MessageBox.Show("No comparison data."); return; }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV files|*.csv|Text file|*.txt",
                FileName = "behavior_diff"
            };
            if (sfd.ShowDialog() != true) return;

            var sb = new StringBuilder();
            bool csv = sfd.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            string sep = csv ? "," : "\t";
            string Q(string s) => csv ? $"\"{(s ?? "").Replace("\"", "\"\"")}\"" : (s ?? "");

            sb.AppendLine(string.Join(sep, "Category", "DiffType", "Name", "FileA", "FileB"));
            foreach (var r in _filtered)
                sb.AppendLine(string.Join(sep, Q(r.Category), Q(r.DiffType), Q(r.Name), Q(r.ValueA), Q(r.ValueB)));

            File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"Exported {_filtered.Count} diff entries.");
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();
    }
}
