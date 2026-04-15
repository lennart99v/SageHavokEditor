using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Core.Patching;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Xml.Serialization;

namespace SkyrimHavokEditor.UI.Dialogs
{
    public partial class PatchPreviewDialog : Window
    {
        private readonly BehaviorPatch _patch;
        public ObservableCollection<PatchOpViewModel> Ops { get; } = new();

        private readonly HavokManager _manager;
        private readonly Dictionary<string, ObjectSnapshot> _snapshot;
        private readonly string _behaviorFilePath;

        public PatchPreviewDialog(BehaviorPatch patch, HavokManager manager = null,
    Dictionary<string, ObjectSnapshot> snapshot = null,
    string behaviorFilePath = "")
        {
            InitializeComponent();
            DataContext = this;
            _patch = patch;

            TxtAuthor.Text = patch.Author;
            TxtDescription.Text = patch.Description;

            foreach (var op in patch.Operations)
            {
                Ops.Add(new PatchOpViewModel
                {
                    IsIncluded = true,
                    TypeLabel = OpTypeLabel(op),
                    TypeColor = OpTypeColor(op),
                    Note = op.Note,
                    Operation = op
                });
            }
            _manager = manager;
            _snapshot = snapshot;
            _behaviorFilePath = behaviorFilePath;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var included = Ops.Where(o => o.IsIncluded).ToList();
            SummaryText.Text =
                $"+ {included.Count(o => o.Operation is AddObjectOp or AddEventOp or AddVariableOp)} added   " +
                $"− {included.Count(o => o.Operation is DeleteObjectOp)} removed   " +
                $"≠ {included.Count(o => o.Operation is ModifyParamOp or AppendParamOp or ModifyChildOp)} modified   " +
                $"✎ {included.Count(o => o.Operation is RenameEventOp or RenameVariableOp)} renamed";
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var op in Ops) op.IsIncluded = true;
            UpdateSummary();
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var op in Ops) op.IsIncluded = false;
            UpdateSummary();
        }

        private void CheckBox_Changed(object sender, RoutedEventArgs e)
            => UpdateSummary();

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Update patch metadata
            _patch.Author = TxtAuthor.Text;
            _patch.Description = TxtDescription.Text;

            // Keep only included operations
            _patch.Operations.Clear();
            foreach (var op in Ops.Where(o => o.IsIncluded))
                _patch.Operations.Add(op.Operation);

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Behavior Patch",
                Filter = "Sage Patch|*.sagepatch|XML|*.xml",
                FileName = string.IsNullOrEmpty(_patch.BaseFile)
    ? "my_patch"
    : _patch.BaseFile + "_patch"
            };

            if (sfd.ShowDialog() != true) return;

            try
            {
                var serializer = new XmlSerializer(typeof(BehaviorPatch));
                using var writer = new StreamWriter(sfd.FileName, false, System.Text.Encoding.UTF8);
                serializer.Serialize(writer, _patch);
                MessageBox.Show($"✓ Patch saved!\n\n{_patch.Operations.Count} operations\n{sfd.FileName}",
                    "Patch Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Save error: " + ex.Message);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private static string OpTypeLabel(PatchOperation op) => op switch
        {
            AddObjectOp => "ADD",
            DeleteObjectOp => "DEL",
            ModifyParamOp => "MOD",
            AppendParamOp => "APD",
            ModifyChildOp => "CHD",
            AddChildOp => "CHD+",
            AddEventOp => "EVT",
            RenameEventOp => "EVT",
            AddVariableOp => "VAR",
            RenameVariableOp => "VAR",
            _ => "???"
        };

        private static string OpTypeColor(PatchOperation op) => op switch
        {
            AddObjectOp or AddEventOp or AddVariableOp or AddChildOp => "#1A4A1A",
            DeleteObjectOp => "#4A1A1A",
            ModifyParamOp or AppendParamOp or ModifyChildOp => "#3A2A00",
            RenameEventOp or RenameVariableOp => "#1A2A4A",
            _ => "#333333"
        };

        public class PatchOpViewModel : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _isIncluded;
            public bool IsIncluded
            {
                get => _isIncluded;
                set { _isIncluded = value; OnPropertyChanged(); }
            }

            public string TypeLabel { get; set; }
            public string TypeColor { get; set; }
            public string Note { get; set; }
            public PatchOperation Operation { get; set; }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string n = null)
                => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
        }

        private void BtnExportNemesis_Click(object sender, RoutedEventArgs e)
    => ExportPatch(PatchEngineTarget.Nemesis);

        private void BtnExportPandora_Click(object sender, RoutedEventArgs e)
            => ExportPatch(PatchEngineTarget.Pandora);

        private void ExportPatch(PatchEngineTarget target)
        {
            if (_manager == null || _snapshot == null)
            { MessageBox.Show("Manager not available — open patch preview from the editor."); return; }

            var engineRoot = target == PatchEngineTarget.Nemesis ? "Nemesis_Engine" : "Pandora_Engine";

            // Single dialog — filename = mod code, folder = output root
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Export {target} Patch — type your mod code as the filename",
                FileName = "mymod",
                Filter = "Mod folder|*.modcode",
                OverwritePrompt = false,
                CheckFileExists = false,
                CheckPathExists = true
            };

            if (dlg.ShowDialog() != true) return;

            var modCode = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName).Trim();
            if (string.IsNullOrEmpty(modCode) || modCode.Contains(" "))
            { MessageBox.Show("Mod code cannot be empty or contain spaces."); return; }

            var outputFolder = System.IO.Path.GetDirectoryName(dlg.FileName);
            var behaviorName = System.IO.Path.GetFileNameWithoutExtension(_behaviorFilePath);

            var opts = new PatchExportOptions
            {
                ModCode = modCode,
                ModName = string.IsNullOrEmpty(_patch.Author) ? modCode : _patch.Author,
                Author = _patch.Author,
                OutputFolder = outputFolder,
                BehaviorFileName = behaviorName,
                ProjectName = DeriveProjectName(_behaviorFilePath),
                Target = target
            };

            try
            {
                var exporter = new HavokPatchExporter(_manager, _snapshot);
                var (count, errors) = exporter.Export(opts);

                string enginePath = System.IO.Path.Combine(outputFolder, engineRoot, "mod", modCode);
                string msg = $"✓ Exported {count} patch file(s).\n\nOutput: {enginePath}";
                if (errors.Count > 0)
                    msg += $"\n\nWarnings ({errors.Count}):\n" + string.Join("\n", errors.Take(5));

                MessageBox.Show(msg, $"{target} patch exported",
                    MessageBoxButton.OK,
                    errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export failed:\n" + ex.Message);
            }
        }

        private static string DeriveProjectName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var lower = path.ToLowerInvariant();
            if (lower.Contains("_1stperson")) return "_1stperson";
            if (lower.Contains("\\character\\") || lower.Contains("/character/")) return "defaultfemale";
            if (lower.Contains("horse")) return "horseproject";
            return "";
        }
    }
}