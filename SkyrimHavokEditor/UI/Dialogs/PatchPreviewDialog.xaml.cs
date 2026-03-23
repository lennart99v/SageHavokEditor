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

        public PatchPreviewDialog(BehaviorPatch patch)
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
                Filter = "Behavior Patch|*.behaviorpatch|XML|*.xml",
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
    }
}