using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Models;
using SkyrimHavokEditor.Models.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SkyrimHavokEditor.UI.Dialogs
{
    public partial class GlobalSearchDialog : Window, INotifyPropertyChanged
    {
        private readonly HavokManager _manager;
        private readonly List<IdNamePair> _eventList;
        private readonly ObservableCollection<SearchResultItem> _allResults = new();
        private string _activeFilter = "all";
        private bool _replaceVisible = false;

        public event Action<string> ObjectSelected;
        public event Action<string> NavigateToEvent;
        public event Action<string> NavigateToVariable;
        public event PropertyChangedEventHandler PropertyChanged;
        public List<ReplaceChange> Changes { get; } = new();

        public Action<string, Action, Action> RecordUndo { get; set; }

        private bool _showPlaceholder = true;
        public Visibility PlaceholderVisibility =>
            _showPlaceholder ? Visibility.Visible : Visibility.Collapsed;

        public GlobalSearchDialog(HavokManager manager, List<IdNamePair> eventList)
        {
            InitializeComponent();
            DataContext = this;
            _manager = manager;
            _eventList = eventList;
            ResultsList.ItemsSource = _allResults;
            Loaded += (s, e) => TxtSearch.Focus();
        }

        // ── Search ────────────────────────────────────────────────────────────────

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _showPlaceholder = string.IsNullOrEmpty(TxtSearch.Text);
            OnPropertyChanged(nameof(PlaceholderVisibility));
            RunSearch(TxtSearch.Text);
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ResultsList.Items.Count > 0)
            {
                ResultsList.SelectedIndex = 0;
                NavigateToSelected();
            }
            else if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Down)
            {
                ResultsList.Focus();
                if (ResultsList.Items.Count > 0) ResultsList.SelectedIndex = 0;
            }
        }

        private void RunSearch(string raw)
        {
            _allResults.Clear();
            if (string.IsNullOrWhiteSpace(raw) || raw.Length < 2)
            {
                ResultCount.Text = "";
                return;
            }

            string prefix = null;
            string query = raw.Trim();

            var prefixMap = new Dictionary<string, string>
            {
                { "event:", "event" }, { "ev:", "event" },
                { "clip:", "clip" },
                { "state:", "state" }, { "st:", "state" },
                { "var:", "var" }, { "variable:", "var" },
                { "obj:", "obj" }, { "object:", "obj" },
                { "trans:", "transition" }, { "transition:", "transition" }
            };

            foreach (var kv in prefixMap)
            {
                if (query.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    prefix = kv.Value;
                    query = query.Substring(kv.Key.Length).Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(query)) return;
            var effectiveFilter = prefix ?? _activeFilter;

            bool caseSensitive = ChkCaseSensitive.IsChecked == true;
            bool useRegex = ChkRegex.IsChecked == true;

            // Events
            if (effectiveFilter == "all" || effectiveFilter == "event")
            {
                foreach (var ev in _eventList)
                {
                    if (!Matches(ev.Name, query, caseSensitive, useRegex) &&
                        !Matches(ev.Id, query, caseSensitive, useRegex)) continue;
                    _allResults.Add(new SearchResultItem
                    {
                        Category = "Event",
                        CategoryBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x3C)),
                        CategoryTextBrush = new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)),
                        Name = ev.Name,
                        Id = $"idx:{ev.Id}",
                        Details = "Behavior event",
                        ObjectId = null,
                        IsNavigable = false,
                        RawValue = ev.Name,
                        IsEditable = false
                    });
                }
            }

            // HkObjects
            foreach (var obj in _manager.ObjectMap.Values)
            {
                var name = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? "";
                var cls = obj.ClassName ?? "";
                bool matchesName = Matches(name, query, caseSensitive, useRegex)
                                || Matches(obj.Id, query, caseSensitive, useRegex)
                                || Matches(cls, query, caseSensitive, useRegex);

                HkParam matchedParam = null;
                if (!matchesName)
                {
                    matchedParam = obj.Params.FirstOrDefault(p =>
                        !string.IsNullOrEmpty(p.Value) &&
                        p.Value.Length < 500 &&  
                        p.Name != "eventNames" &&
                        p.Name != "variableNames" &&
                        p.Name != "wordVariableNames" &&
                        p.Name != "animationNames" &&
                        Matches(p.Value, query, caseSensitive, useRegex));
                    if (matchedParam == null) continue;
                }

                string category; SolidColorBrush catBrush, catTextBrush; string details;
                bool isEditable = false;

                if (cls == "hkbClipGenerator")
                {
                    if (effectiveFilter != "all" && effectiveFilter != "clip") continue;
                    category = "Clip"; isEditable = true;
                    catBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x3C, 0x1A));
                    catTextBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xD1, 0x85));
                    var anim = obj.Params.FirstOrDefault(p => p.Name == "animationName")?.Value ?? "";
                    details = string.IsNullOrEmpty(anim) ? cls : anim;
                }
                else if (cls == "hkbStateMachineStateInfo")
                {
                    if (effectiveFilter != "all" && effectiveFilter != "state") continue;
                    category = "State";
                    catBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x4C));
                    catTextBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
                    var sid = obj.Params.FirstOrDefault(p => p.Name == "stateId")?.Value ?? "";
                    details = string.IsNullOrEmpty(sid) ? cls : $"stateId: {sid}";
                }
                else if (cls == "hkbStateMachine")
                {
                    if (effectiveFilter != "all" && effectiveFilter != "state") continue;
                    category = "SM";
                    catBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x30, 0x10));
                    catTextBrush = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
                    var cnt = obj.Params.FirstOrDefault(p => p.Name == "states")?.Value
                        ?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
                    details = $"{cnt} states";
                }
                else if (cls.Contains("Variable") || cls.Contains("variable"))
                {
                    // Skip binding sets — these are containers, not variables
                    if (cls == "hkbVariableBindingSet")
                    {
                        if (effectiveFilter != "all" && effectiveFilter != "obj") continue;
                        category = "Object";
                        catBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                        catTextBrush = new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));
                        details = cls;
                    }
                    else
                    {
                        if (effectiveFilter != "all" && effectiveFilter != "var") continue;
                        category = "Variable";
                        catBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x5C));
                        catTextBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
                        details = cls;
                    }
                }
                else
                {
                    if (effectiveFilter != "all" && effectiveFilter != "obj") continue;
                    category = "Object"; isEditable = matchedParam != null;
                    catBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                    catTextBrush = new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));
                    details = matchedParam != null
                        ? $"{matchedParam.Name} = {matchedParam.Value}"
                        : cls;
                }

                _allResults.Add(new SearchResultItem
                {
                    Category = category,
                    CategoryBrush = catBrush,
                    CategoryTextBrush = catTextBrush,
                    Name = string.IsNullOrEmpty(name) ? obj.Id : name,
                    Id = obj.Id,
                    Details = matchedParam != null && !matchesName ? $"[param] {details}" : details,
                    ObjectId = obj.Id,
                    IsNavigable = true,
                    RawValue = matchedParam?.Value ?? name,
                    IsEditable = isEditable,
                    MatchedParamName = matchedParam?.Name
                });
            }

            // Named variables
            if (effectiveFilter == "all" || effectiveFilter == "var")
            {
                foreach (var varObj in _manager.ObjectMap.Values
                    .Where(o => o.ClassName == "hkbBehaviorGraphStringData" ||
                                o.ClassName == "hkbVariableNamesData"))
                {
                    var np = varObj.Params.FirstOrDefault(p =>
                        p.Name == "variableNames" || p.Name == "wordVariableNames");
                    if (np == null) continue;

                    var names = np.Strings.Count > 0 ? np.Strings
                        : np.Value?.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                              .ToList() ?? new List<string>();

                    for (int i = 0; i < names.Count; i++)
                    {
                        if (!Matches(names[i], query, caseSensitive, useRegex)) continue;
                        if (_allResults.Any(r => r.Category == "Var" && r.Name == names[i])) continue;
                        _allResults.Add(new SearchResultItem
                        {
                            Category = "Var",
                            CategoryBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x5C)),
                            CategoryTextBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                            Name = names[i],
                            Id = $"idx:{i}",
                            Details = "Behavior variable",
                            ObjectId = varObj.Id,
                            IsNavigable = true,
                            RawValue = names[i],
                            IsEditable = false
                        });
                    }
                }
            }

            var sorted = _allResults
                .OrderByDescending(r => r.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => CategoryOrder(r.Category))
                .ThenBy(r => r.Name)
                .ToList();

            _allResults.Clear();
            foreach (var item in sorted) _allResults.Add(item);
            ResultCount.Text = $"{_allResults.Count} result{(_allResults.Count != 1 ? "s" : "")}";
        }

        // ── Replace ───────────────────────────────────────────────────────────────

        private void BtnToggleReplace_Click(object sender, RoutedEventArgs e)
        {
            _replaceVisible = !_replaceVisible;
            ReplaceRow.Visibility = _replaceVisible ? Visibility.Visible : Visibility.Collapsed;
            if (_replaceVisible) TxtReplace.Focus();
        }

        private void BtnReplaceSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = ResultsList.SelectedItems.Cast<SearchResultItem>()
                .Where(r => r.IsEditable).ToList();
            ApplyReplace(selected);
        }

        private void BtnReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            var editable = _allResults.Where(r => r.IsEditable).ToList();
            ApplyReplace(editable);
        }

        private void ApplyReplace(List<SearchResultItem> items)
        {
            if (items.Count == 0) { MessageBox.Show("No editable items selected."); return; }

            var replaceWith = TxtReplace.Text ?? "";
            var find = TxtSearch.Text?.Trim() ?? "";
            // Strip prefix if present
            foreach (var kv in new[] { "event:", "ev:", "clip:", "state:", "st:", "var:",
                                        "variable:", "obj:", "object:", "trans:", "transition:" })
                if (find.StartsWith(kv, StringComparison.OrdinalIgnoreCase))
                { find = find.Substring(kv.Length).Trim(); break; }

            if (string.IsNullOrEmpty(find)) return;

            bool caseSensitive = ChkCaseSensitive.IsChecked == true;
            bool useRegex = ChkRegex.IsChecked == true;
            int count = 0;

            foreach (var item in items)
            {
                if (!_manager.ObjectMap.TryGetValue(item.ObjectId ?? "", out var obj)) continue;

                var animParam = obj.Params.FirstOrDefault(p => p.Name == "animationName");
                if (animParam == null) continue;

                string oldVal = animParam.Value ?? "";
                var newVal = ReplaceIn(oldVal, find, replaceWith, caseSensitive, useRegex);

                if (newVal != oldVal)
                {
                    // RECORD THE CHANGE FOR UNDO
                    Changes.Add(new ReplaceChange
                    {
                        ClipId = item.ObjectId,
                        ClipName = item.Name,
                        OldPath = oldVal,
                        NewPath = newVal
                    });

                    animParam.Value = newVal;
                    item.Details = newVal;
                    count++;
                }
            }

            RecordUndo?.Invoke(
                $"Replace {count} value(s)",
                () => { /* undo: restore old values */ },
                () => { /* redo: reapply new values */ }
            );
            HintText.Text = $"✓ Replaced {count} value(s)";
        }

        private static string ReplaceIn(string input, string find, string replacement,
            bool caseSensitive, bool useRegex)
        {
            if (string.IsNullOrEmpty(input)) return input;
            try
            {
                if (useRegex)
                {
                    var opts = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return Regex.Replace(input, find, replacement, opts);
                }
                var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var sb = new System.Text.StringBuilder();
                int pos = 0;
                while (true)
                {
                    int idx = input.IndexOf(find, pos, comparison);
                    if (idx < 0) { sb.Append(input, pos, input.Length - pos); break; }
                    sb.Append(input, pos, idx - pos);
                    sb.Append(replacement);
                    pos = idx + find.Length;
                }
                return sb.ToString();
            }
            catch { return input; }
        }

        // ── Navigation ────────────────────────────────────────────────────────────

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton btn) return;
            _activeFilter = btn.Tag?.ToString() ?? "all";
            foreach (var tb in new[] { FilterAll, FilterEvents, FilterClips, FilterStates, FilterVariables, FilterObjects })
                tb.IsChecked = tb == btn;
            RunSearch(TxtSearch.Text);
        }

        private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsList.SelectedItem is not SearchResultItem item) return;

            var action = item.Category == "Event" ? "Jump to event"
                       : item.Category is "Var" or "Variable" ? "Jump to variable"
                       : item.IsNavigable ? "Jump to"
                       : "Select";

            HintText.Text = $"↵ {action}: {item.Name}   •   Double-click to navigate";
        }

        private void ResultsList_DoubleClick(object sender, MouseButtonEventArgs e)
            => NavigateToSelected();

        private void NavigateToSelected()
        {
            if (ResultsList.SelectedItem is not SearchResultItem item) return;

            if (item.Category == "Event")
            {
                NavigateToEvent?.Invoke(item.Id);
                return;
            }

            if (item.Category == "Var" || item.Category == "Variable")
            {
                NavigateToVariable?.Invoke(item.Name);
                return;
            }

            if (!item.IsNavigable || string.IsNullOrEmpty(item.ObjectId)) return;
            ObjectSelected?.Invoke(item.ObjectId);
        }
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static bool Matches(string value, string query, bool caseSensitive, bool useRegex)
        {
            if (string.IsNullOrEmpty(value)) return false;
            try
            {
                if (useRegex)
                {
                    var opts = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return Regex.IsMatch(value, query, opts);
                }
                var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                return value.Contains(query, comparison);
            }
            catch { return false; }
        }

        private void Window_Activated(object sender, EventArgs e)
        {
            Opacity = 1.0;
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            Opacity = 0.75;
        }

        private void TitleBar_MouseDown(object sender,
    System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private static int CategoryOrder(string cat) => cat switch
        {
            "Event" => 0,
            "State" => 1,
            "SM" => 2,
            "Clip" => 3,
            "Var" => 4,
            "Variable" => 4,
            _ => 5
        };

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SearchResultItem : INotifyPropertyChanged
    {
        public string Category { get; set; }
        public SolidColorBrush CategoryBrush { get; set; }
        public SolidColorBrush CategoryTextBrush { get; set; }

        private string _name;
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Id { get; set; }

        private string _details;
        public string Details
        {
            get => _details;
            set { _details = value; OnPropertyChanged(); }
        }

        public string ObjectId { get; set; }
        public bool IsNavigable { get; set; }
        public bool IsEditable { get; set; }
        public string RawValue { get; set; }
        public string MatchedParamName { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class ReplaceChange
    {
        public string ClipId { get; set; }
        public string ClipName { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
    }
}