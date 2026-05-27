using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Core.Patching;
using SkyrimHavokEditor.Models;

namespace SkyrimHavokEditor.UI.Dialogs
{
    public partial class ApplyPatchDialog : Window
    {
        private readonly HavokManager _manager;
        private BehaviorPatch _patch;

        public bool PatchWasApplied { get; private set; } = false;
        public ApplyResult LastResult { get; private set; }

        private readonly ObservableCollection<LogEntry> _log = new();

        public ApplyPatchDialog(HavokManager manager)
        {
            InitializeComponent();
            _manager = manager;
            ResultsList.ItemsSource = _log;
            ResultsList.KeyDown += ResultsList_KeyDown;
        }

        private void BtnBrowsePatch_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open Behavior Patch",
                Filter = "Sage Patch|*.sagepatch|XML Patch|*.xml|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var serializer = new XmlSerializer(typeof(BehaviorPatch));
                using var fs = new FileStream(dlg.FileName, FileMode.Open);
                _patch = (BehaviorPatch)serializer.Deserialize(fs);

                PatchFileLabel.Text = Path.GetFileName(dlg.FileName);
                ShowPatchInfo();
                PreviewOps();
                BtnPreview.IsEnabled = true;
                BtnApply.IsEnabled = true;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Failed to load patch: " + ex.Message);
            }
        }

        private void ShowPatchInfo()
        {
            PatchInfoPanel.Visibility = Visibility.Visible;

            TxtPatchAuthor.Text = string.IsNullOrEmpty(_patch.Author)
                ? "Author: (unknown)"
                : $"Author: {_patch.Author}";

            TxtPatchDescription.Text = string.IsNullOrEmpty(_patch.Description)
                ? ""
                : _patch.Description;

            TxtPatchCreated.Text = string.IsNullOrEmpty(_patch.Created)
                ? ""
                : $"Created: {_patch.Created}";

            TxtPatchStats.Text =
                $"Operations: {_patch.Operations.Count}\n" +
                $"  + Add:    {_patch.AddCount}\n" +
                $"  − Delete: {_patch.DeleteCount}\n" +
                $"  ≠ Modify: {_patch.ModifyCount}\n" +
                $"  ⚡ Events: {_patch.EventCount}\n" +
                $"  ⚙ Vars:   {_patch.VarCount}";
        }

        private void PreviewOps()
        {
            _log.Clear();
            ResultHeader.Text = "Operations to apply:";

            foreach (var op in _patch.Operations)
            {
                _log.Add(new LogEntry
                {
                    Icon = OpIcon(op),
                    Message = op.Note,
                    Color = OpColor(op)
                });
            }

            SummaryText.Text = $"{_patch.Operations.Count} operations ready to apply";
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_patch == null) return;

            // Deep-clone the manager state via snapshot, apply to clone, show results
            _log.Clear();
            ResultHeader.Text = "Dry-run preview (nothing modified):";

            // We can't easily clone HavokManager, so instead we just validate
            // each operation against the current state and report what would happen
            int wouldApply = 0;
            int wouldWarn = 0;
            int wouldFail = 0;

            foreach (var op in _patch.Operations)
            {
                var (icon, msg, color) = ValidateOp(op);
                _log.Add(new LogEntry { Icon = icon, Message = msg, Color = color });
                if (color == "#89D185" || color == "#4FC3F7") wouldApply++;
                else if (color == "Orange") wouldWarn++;
                else wouldFail++;
            }

            SummaryText.Text = $"Preview: {wouldApply} ok   {wouldWarn} warnings   {wouldFail} conflicts";
        }

        private void BtnBrowseNemesisFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Navigate to the behavior patch folder containing #XXXX.txt files, " +
                        "then click Save (the folder itself is selected, not a file)",
                FileName = "navigate_to_folder_then_click_save",
                Filter = "Any file|*.*",
                CheckFileExists = false,
                CheckPathExists = false
            };
            if (dlg.ShowDialog() != true) return;

            var folder = System.IO.Path.GetDirectoryName(dlg.FileName);

            // Verify it actually contains patch files
            var patchFiles = System.IO.Directory.GetFiles(folder, "#*.txt");
            if (patchFiles.Length == 0)
            {
                MessageBox.Show(
                    $"No patch files (#XXXX.txt) found in:\n{folder}\n\n" +
                    "Navigate inside the mod folder to the behavior subfolder, e.g.:\n" +
                    @"Nemesis_Engine\mod\mymod\0_master\",
                    "Wrong folder");
                return;
            }

            try
            {
                _patch = NemesisPatchReader.ReadFolder(folder);
                PatchFileLabel.Text = $"[Nemesis/Pandora] {System.IO.Path.GetFileName(folder)}" +
                                      $"  ({patchFiles.Length} files, {_patch.Operations.Count} ops)";
                ShowPatchInfo();
                PreviewOps();
                BtnPreview.IsEnabled = true;
                BtnApply.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to read patch folder:\n" + ex.Message);
            }
        }

        private (string icon, string msg, string color) ValidateOp(PatchOperation op)
        {
            switch (op)
            {
                case AddObjectOp add:
                    return ("+", $"Will add new {add.ClassName} [{add.Note}]", "#89D185");

                case DeleteObjectOp del:
                    {
                        var delObj = _manager.ObjectMap.Values.FirstOrDefault(o =>
                            del.Anchor.StartsWith("name:")
                                ? o.Params.Any(p => p.Name == "name" && p.Value == del.Anchor.Substring(5))
                                : o.Id == del.Anchor.Substring(3));
                        return delObj != null
                            ? ("−", $"Will delete {delObj.ClassName} {delObj.Id} [{del.Note}]", "#FF6B6B")
                            : ("⚠", $"Object not found for delete: '{del.Anchor}'", "Orange");
                    }

                case ModifyParamOp modOp:
                    {
                        // Warn on raw ID anchors — not portable
                        if (modOp.Anchor.StartsWith("id:"))
                            return ("⚠", $"Raw ID anchor '{modOp.Anchor}' — may not be portable. {modOp.Note}", "Orange");

                        var modObj = _manager.ObjectMap.Values.FirstOrDefault(o =>
                            modOp.Anchor.StartsWith("name:")
                                ? o.Params.Any(p => p.Name == "name" && p.Value == modOp.Anchor.Substring(5))
                                : o.Id == modOp.Anchor.Substring(3));

                        if (modObj == null)
                            return ("✕", $"Object not found: '{modOp.Anchor}'", "#FF6B6B");

                        var modParam = modObj.Params.FirstOrDefault(p => p.Name == modOp.ParamName);
                        if (modParam == null)
                            return ("⚠", $"Param '{modOp.ParamName}' not found on {modObj.Id}", "Orange");

                        if (modParam.Value != modOp.OldValue)
                            return ("⚠", $"Value mismatch on {modObj.Id}.{modOp.ParamName} " +
                                $"(expected '{Truncate(modOp.OldValue)}', found '{Truncate(modParam.Value)}') — will apply anyway",
                                "Orange");

                        return ("≠", $"Will modify {modObj.Id}.{modOp.ParamName} [{modOp.Note}]", "#89D185");
                    }

                case AppendParamOp apd:
                    return ("≠", $"Will append to {apd.Anchor}.{apd.ParamName} [{apd.Note}]", "#89D185");

                case AddChildOp ach:
                    return ("+", $"Will add child to {ach.Anchor}.{ach.ParamName} [{ach.Note}]", "#89D185");

                case ModifyChildOp chd:
                    {
                        if (chd.Anchor.Contains("stringData") ||
                            chd.ParamName == "eventNames" ||
                            chd.ParamName == "stringData")
                            return ("⚠", $"Skipping string data child op (noise) [{chd.Note}]", "Orange");

                        return ("≠", $"Will modify child {chd.Anchor}.{chd.ParamName}[{chd.ChildIndex}].{chd.ChildParam}", "#89D185");
                    }

                case RenameEventOp rev:
                    return ("✎", $"Will rename event[{rev.Index}] '{rev.OldName}' → '{rev.NewName}'", "#4FC3F7");

                case RenameVariableOp rvr:
                    return ("✎", $"Will rename variable[{rvr.Index}] '{rvr.OldName}' → '{rvr.NewName}'", "#4FC3F7");

                case AddEventOp aev:
                    return ("+", $"Will add event '{aev.Name}'", "#89D185");

                case AddVariableOp avr:
                    return ("+", $"Will add variable '{avr.Name}'", "#89D185");

                default:
                    return ("?", op.Note, "#9D9D9D");
            }
        }

        private void ResultsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.C &&
                System.Windows.Input.Keyboard.Modifiers.HasFlag(
                    System.Windows.Input.ModifierKeys.Control))
            {
                var lines = ResultsList.SelectedItems
                    .OfType<LogEntry>()
                    .Select(l => $"{l.Icon} {l.Message}");

                var text = string.Join("\n", lines);
                if (!string.IsNullOrEmpty(text))
                    Clipboard.SetText(text);

                e.Handled = true;
            }
        }

        private static string Truncate(string s, int max = 30)
            => s?.Length > max ? s.Substring(0, max) + "…" : s ?? "";
        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (_patch == null) return;

            var confirm = MessageBox.Show(
                $"Apply {_patch.Operations.Count} operations to the current behavior?\n\n" +
                "This will modify the in-memory state. Save afterwards to persist changes.",
                "Confirm Apply",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            var applier = new PatchApplier(_manager);
            var result = applier.Apply(_patch);
            LastResult = result;
            PatchWasApplied = true;

            // Show results
            _log.Clear();
            ResultHeader.Text = "Apply results:";

            foreach (var msg in result.Applied)
                _log.Add(new LogEntry { Icon = "✓", Message = msg, Color = "#89D185" });

            foreach (var msg in result.Warnings)
                _log.Add(new LogEntry { Icon = "⚠", Message = msg, Color = "Orange" });

            foreach (var msg in result.Errors)
                _log.Add(new LogEntry { Icon = "✕", Message = msg, Color = "#FF6B6B" });

            var summary = $"✓ {result.AppliedCount} applied";
            if (result.WarningCount > 0) summary += $"   ⚠ {result.WarningCount} warnings";
            if (result.ErrorCount > 0) summary += $"   ✕ {result.ErrorCount} errors";
            SummaryText.Text = summary;

            BtnApply.IsEnabled = false; // prevent double-apply

            if (result.Success)
                MessageBox.Show(
                    $"Patch applied successfully!\n\n" +
                    $"{result.AppliedCount} operations applied\n" +
                    $"{result.WarningCount} warnings\n\n" +
                    "Remember to save the file to persist changes.",
                    "Patch Applied",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            else
                MessageBox.Show(
                    $"Patch applied with errors.\n\n" +
                    $"{result.AppliedCount} succeeded\n" +
                    $"{result.ErrorCount} failed\n" +
                    $"{result.WarningCount} warnings\n\n" +
                    "Check the log for details.",
                    "Patch Applied With Errors",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private static string OpIcon(PatchOperation op) => op switch
        {
            AddObjectOp or AddEventOp or AddVariableOp or AddChildOp => "+",
            DeleteObjectOp => "−",
            ModifyParamOp or AppendParamOp or ModifyChildOp => "≠",
            RenameEventOp or RenameVariableOp => "✎",
            _ => "?"
        };

        private static string OpColor(PatchOperation op) => op switch
        {
            AddObjectOp or AddEventOp or AddVariableOp or AddChildOp => "#89D185",
            DeleteObjectOp => "#FF6B6B",
            ModifyParamOp or AppendParamOp or ModifyChildOp => "Orange",
            RenameEventOp or RenameVariableOp => "#4FC3F7",
            _ => "#9D9D9D"
        };
    }

    public class LogEntry
    {
        public string Icon { get; set; }
        public string Message { get; set; }
        public string Color { get; set; }
    }
}
