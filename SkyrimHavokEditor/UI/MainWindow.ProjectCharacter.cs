// ── Add this to MainWindow.cs ─────────────────────────────────────────────────
// This is a partial class addition — put it in MainWindow.ProjectCharacter.cs
// or append to the bottom of MainWindow.cs inside the class body.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Core.Services;
using SkyrimHavokEditor.Models;
using SkyrimHavokEditor.Models.ViewModels;
using SkyrimHavokEditor.UI;

namespace SkyrimHavokEditor
{
    public partial class MainWindow
    {
        // ── Workspace ─────────────────────────────────────────────────────────
        public HavokWorkspace Workspace { get; private set; }

        private bool _suppressCharacterFieldSync = false;


        // ── Load behavior into app (replaces old LoadFile for behaviors) ──────
        private void LoadBehaviorIntoApp(HkLoadedFile file)
        {
            // Swap the manager the app uses
            if (file.Manager == null) return;
            manager = file.Manager;

            _sourceWasHkx = file.WasHkx;
            _originalHkxPath = file.HkxPath;

            // Run the full existing pipeline
            _validator = new SkyrimHavokEditor.Core.Validation.HavokValidator(manager);

            var builder = new BehaviorTreeBuilder(manager);
            ObjectTree.ItemsSource = new List<BehaviorNodeData>
                { builder.BuildTree("") };

            _subscribedParams.Clear();
            _navigationHistory.Clear();
            BtnBackNavigation.IsEnabled = false;
            BtnBackNavigation.Tag = null;
            _undoRedo.Clear();
            UpdateUndoRedoButtons();

            // RefreshLookups FIRST so EventList is populated before the graph builds
            RefreshLookups();

            GraphView.Load(manager, EventList.ToList(), VariableList.ToList());
            GraphView.StateSelected += (id) =>
            {
                if (!manager.ObjectMap.TryGetValue(id, out var obj)) return;
                LoadObjectIntoEditor(obj);   // ← was the two direct lines
            };
            _originalSnapshot = TakeSnapshot();
            _snapshotEvents = EventList.Select(e => e.Name).ToList();
            _snapshotVars = VariableList.Select(v => v.Name).ToList();

            Stats.FileName = Path.GetFileName(file.OriginalPath);
            Stats.HasFile = true;
            Stats.ObjectCount = manager.ObjectMap.Count;
            Stats.VariableCount = VariableList.Count;
            Stats.EventCount = EventList.Count;
            Stats.ClipCount = ClipList.Count;
            Stats.TransitionCount = TransitionList.Count;
            Stats.BindingCount = BindingList.Count;
            Stats.StateMachineCount = manager.ObjectMap.Values
                .Count(o => o.ClassName == "hkbStateMachine");

            Title = file.WasHkx
                ? $"Skyrim Havok Editor — {Path.GetFileName(file.HkxPath)} [SE HKX]"
                : $"Skyrim Havok Editor — {Path.GetFileName(file.XmlPath)}";

            // Wire graph events (delete transition, inline rename) for undo support
            WireGraphEvents();
        }

        // ── Project UI ────────────────────────────────────────────────────────

        private void LoadProjectIntoUI()
        {
            var pvm = Workspace?.Project;
            if (pvm == null) return;

            ProjectFilePath.Text = pvm.File?.OriginalPath ?? "";
            TxtWorldUpWS.Text = pvm.WorldUpWS ?? "(0.000000 0.000000 1.000000 0.000000)";
            TxtDefaultEventMode.Text = pvm.DefaultEventMode ?? "";
            ProjectCharactersList.ItemsSource = Workspace?.Project?.Characters;

            // Refresh binding — Characters list is bound to Workspace.Project.Characters
            OnPropertyChanged(nameof(Workspace));

            // If a character was also loaded, populate that tab too
            if (Workspace?.Character != null) LoadCharacterIntoUI();

            // Switch to Project tab
            MainTabControl.SelectedIndex = GetTabIndex("📦 Project");
        }

        private void BtnOpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open Project File",
                Filter = "Havok Project|*.hkx;*.xml|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
                _ = LoadFileAsync(dlg.FileName);
        }

        private void BtnSaveProject_Click(object sender, RoutedEventArgs e)
        {
            if (Workspace?.Project == null) { MessageBox.Show("No project loaded."); return; }

            // Flush UI values back to model
            FlushProjectUIToModel();

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Havok XML|*.xml|Skyrim HKX|*.hkx",
                FileName = Path.GetFileName(
                    Workspace.Project.File?.OriginalPath ?? "project")
            };
            if (sfd.ShowDialog() != true) return;

            try
            {
                Workspace.SaveProjectXml(sfd.FileName);
                StatusText.Text = "✓ Project saved";
            }
            catch (Exception ex) { MessageBox.Show("Save error: " + ex.Message); }
        }

        private void BtnNewProject_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("New project wizard coming soon — " +
                "this will generate project.hkx, character.hkx, and behavior.hkx " +
                "from a skeleton file.", "Coming Soon");
        }

        private void BtnAddCharacterRef_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Character HKX",
                Filter = "Havok Character|*.hkx;*.xml"
            };
            if (dlg.ShowDialog() != true) return;

            Workspace?.Project?.Characters.Add(new CharacterViewModel
            {
                File = new HkLoadedFile { OriginalPath = dlg.FileName },
                Name = Path.GetFileNameWithoutExtension(dlg.FileName)
            });
            OnPropertyChanged(nameof(Workspace));
        }

        private void BtnRemoveCharacterRef_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.Tag
                is CharacterViewModel vm)
            {
                Workspace?.Project?.Characters.Remove(vm);
                OnPropertyChanged(nameof(Workspace));
            }
        }

        private async void BtnOpenCharacterFromProject_Click(object sender,
            RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.Tag
                is CharacterViewModel vm
                && !string.IsNullOrEmpty(vm.File?.OriginalPath))
            {
                await LoadFileAsync(vm.File.OriginalPath);
            }
        }

        private void ProjectCharactersList_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Preview the selected character's name in the status bar
            if (ProjectCharactersList.SelectedItem is CharacterViewModel vm)
                StatusText.Text = $"Character: {vm.Name}  ({vm.File?.OriginalPath})";
        }

        private void FlushProjectUIToModel()
        {
            var pvm = Workspace?.Project;
            if (pvm == null) return;

            if (pvm.ProjectDataObj != null)
            {
                SetParam(pvm.ProjectDataObj, "worldUpWS", TxtWorldUpWS.Text);
                SetParam(pvm.ProjectDataObj, "defaultEventMode", TxtDefaultEventMode.Text);
            }
        }

        // ── Character UI ──────────────────────────────────────────────────────

        private void LoadCharacterIntoUI()
        {
            var cvm = Workspace?.Character;
            if (cvm == null) return;

            _suppressCharacterFieldSync = true;

            CharacterFilePath.Text = cvm.File?.OriginalPath ?? "";
            TxtCharacterName.Text = cvm.Name ?? "";
            TxtCapsuleHeight.Text = cvm.CapsuleHeight.ToString("0.000000",
                CultureInfo.InvariantCulture);
            TxtCapsuleRadius.Text = cvm.CapsuleRadius.ToString("0.000000",
                CultureInfo.InvariantCulture);
            TxtSkeletonPath.Text = cvm.SkeletonPath ?? "";
            TxtRagdollPath.Text = cvm.RagdollPath ?? "";
            TxtBehaviorPath.Text = cvm.BehaviorPath ?? "";
            AnimationNamesList.ItemsSource = Workspace?.Character?.AnimationNames;

            // Animation names list refreshed via binding
            OnPropertyChanged(nameof(Workspace));

            _suppressCharacterFieldSync = false;

            // Switch to Character tab
            MainTabControl.SelectedIndex = GetTabIndex("🧑 Character");
        }

        private void BtnOpenCharacter_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open Character File",
                Filter = "Havok Character|*.hkx;*.xml|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
                _ = LoadFileAsync(dlg.FileName);
        }

        private void BtnSaveCharacter_Click(object sender, RoutedEventArgs e)
        {
            if (Workspace?.Character == null)
            { MessageBox.Show("No character loaded."); return; }

            FlushCharacterUIToModel();

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Havok XML|*.xml|Skyrim HKX|*.hkx",
                FileName = Path.GetFileName(
                    Workspace.Character.File?.OriginalPath ?? "character")
            };
            if (sfd.ShowDialog() != true) return;

            try
            {
                Workspace.SaveCharacterXml(sfd.FileName);
                StatusText.Text = "✓ Character saved";
            }
            catch (Exception ex) { MessageBox.Show("Save error: " + ex.Message); }
        }

        private void CharacterField_Changed(object sender,
            System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_suppressCharacterFieldSync) return;
            FlushCharacterUIToModel();
        }

        private void FlushCharacterUIToModel()
        {
            var cvm = Workspace?.Character;
            if (cvm == null) return;

            cvm.Name = TxtCharacterName.Text;
            cvm.SkeletonPath = TxtSkeletonPath.Text;
            cvm.RagdollPath = TxtRagdollPath.Text;
            cvm.BehaviorPath = TxtBehaviorPath.Text;

            if (float.TryParse(TxtCapsuleHeight.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out float h)) cvm.CapsuleHeight = h;
            if (float.TryParse(TxtCapsuleRadius.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out float r)) cvm.CapsuleRadius = r;

            // Write back to HkObject params
            if (cvm.CharacterStringDataObj != null)
            {
                SetParam(cvm.CharacterStringDataObj, "name", cvm.Name);
                SetParam(cvm.CharacterStringDataObj, "rigName", cvm.SkeletonPath);
                SetParam(cvm.CharacterStringDataObj, "ragdollName", cvm.RagdollPath);
                SetParam(cvm.CharacterStringDataObj, "behaviorFilename", cvm.BehaviorPath);
            }

            if (cvm.CharacterDataObj != null)
            {
                var ccInfo = cvm.CharacterDataObj.Params
                    .FirstOrDefault(p => p.Name == "characterControllerInfo");
                if (ccInfo?.Children?.Count > 0)
                {
                    SetParam(ccInfo.Children[0], "capsuleHeight",
                        cvm.CapsuleHeight.ToString("0.000000", CultureInfo.InvariantCulture));
                    SetParam(ccInfo.Children[0], "capsuleRadius",
                        cvm.CapsuleRadius.ToString("0.000000", CultureInfo.InvariantCulture));
                }
            }
        }

        private void BtnBrowseSkeleton_Click(object sender, RoutedEventArgs e)
            => BrowsePath(TxtSkeletonPath, "Skeleton HKX|*.hkx;*.xml");

        private void BtnBrowseRagdoll_Click(object sender, RoutedEventArgs e)
            => BrowsePath(TxtRagdollPath, "Ragdoll HKX|*.hkx;*.xml");

        private void BtnBrowseBehavior_Click(object sender, RoutedEventArgs e)
            => BrowsePath(TxtBehaviorPath, "Behavior HKX|*.hkx;*.xml");

        private async void BtnOpenLinkedBehavior_Click(object sender, RoutedEventArgs e)
        {
            var path = TxtBehaviorPath.Text.Trim();
            if (string.IsNullOrEmpty(path)) return;

            // Resolve relative to character file location
            var charDir = Path.GetDirectoryName(
                Workspace?.Character?.File?.OriginalPath ?? "");
            var full = string.IsNullOrEmpty(charDir)
                ? path
                : Path.GetFullPath(Path.Combine(charDir, path));

            if (File.Exists(full))
                await LoadFileAsync(full);
            else
                MessageBox.Show($"Behavior file not found:\n{full}");
        }

        private void BtnAddAnimationName_Click(object sender, RoutedEventArgs e)
        {
            var cvm = Workspace?.Character;
            if (cvm == null) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Animation File",
                Filter = "Havok Animation|*.hkx;*.xml|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            // Make relative path if possible
            var charDir = Path.GetDirectoryName(
                cvm.File?.OriginalPath ?? "") ?? "";
            var rel = MakeRelative(charDir, dlg.FileName);

            cvm.AnimationNames.Add(rel);
            SyncAnimNamesToModel(cvm);
            OnPropertyChanged(nameof(Workspace));
        }

        private void BtnBrowseAnimName_Click(object sender, RoutedEventArgs e)
        {
            var cvm = Workspace?.Character;
            if (cvm == null) return;
            if ((sender as System.Windows.Controls.Button)?.Tag is not string oldAnim) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Animation File",
                Filter = "Havok Animation|*.hkx;*.xml|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            var charDir = Path.GetDirectoryName(cvm.File?.OriginalPath ?? "") ?? "";
            var rel = MakeRelative(charDir, dlg.FileName);

            int idx = cvm.AnimationNames.IndexOf(oldAnim);
            if (idx >= 0) cvm.AnimationNames[idx] = rel;   // replace in place by index
            SyncAnimNamesToModel(cvm);
        }

        private void BtnPreviewAnimName_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.Tag is not string anim) return;
            if (string.IsNullOrEmpty(anim)) return;
            // Build a throwaway ClipInfo so we reuse the existing preview path.
            var name = System.IO.Path.GetFileNameWithoutExtension(anim);
            PreviewClip(new ClipInfo { Id = "(character-anim)", Name = name, AnimationPath = anim });
        }

        private void BtnRemoveAnimName_Click(object sender, RoutedEventArgs e)
        {
            var cvm = Workspace?.Character;
            if (cvm == null) return;
            if ((sender as System.Windows.Controls.Button)?.Tag is string anim)
            {
                cvm.AnimationNames.Remove(anim);
                SyncAnimNamesToModel(cvm);
                OnPropertyChanged(nameof(Workspace));
            }
        }

        private void SyncAnimNamesToModel(CharacterViewModel cvm)
        {
            if (cvm.CharacterStringDataObj == null) return;
            var param = cvm.CharacterStringDataObj.Params
                .FirstOrDefault(p => p.Name == "animationNames");
            if (param == null) return;
            param.Strings = cvm.AnimationNames.ToList();
            param.NumElements = cvm.AnimationNames.Count.ToString();
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        private static void SetParam(HkObject obj, string name, string value)
        {
            var p = obj.Params.FirstOrDefault(x => x.Name == name);
            if (p != null) p.Value = value;
        }

        private static void BrowsePath(System.Windows.Controls.TextBox target,
            string filter)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select File",
                Filter = filter + "|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
                target.Text = dlg.FileName;
        }

        private static string MakeRelative(string baseDir, string fullPath)
        {
            if (string.IsNullOrEmpty(baseDir)) return fullPath;
            try
            {
                var baseUri = new Uri(baseDir.TrimEnd('\\', '/') + "\\");
                var fileUri = new Uri(fullPath);
                return Uri.UnescapeDataString(
                    baseUri.MakeRelativeUri(fileUri).ToString()
                    .Replace('/', '\\'));
            }
            catch { return fullPath; }
        }

        private int GetTabIndex(string header)
        {
            for (int i = 0; i < MainTabControl.Items.Count; i++)
                if (MainTabControl.Items[i] is System.Windows.Controls.TabItem ti
                    && ti.Header?.ToString() == header)
                    return i;
            return 0;
        }

        // INotifyPropertyChanged (add if not already present)
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(name));

        // ── Graph event wiring (call from LoadBehaviorIntoApp) ────────────────

        private void WireGraphEvents()
        {
            // Avoid double-subscribing
            GraphView.TransitionDeletedFromGraph -= OnTransitionDeletedFromGraph;
            GraphView.NodeRenamedOnGraph -= OnNodeRenamedOnGraph;
            GraphView.NodeAddedToGraph -= OnNodeAddedToGraph;
            GraphView.NodeDeletedFromGraph -= OnNodeDeletedFromGraph;
            GraphView.StatusText_ -= OnGraphStatus;
            GraphView.TransitionDeletedFromGraph += OnTransitionDeletedFromGraph;
            GraphView.NodeRenamedOnGraph += OnNodeRenamedOnGraph;
            GraphView.NodeAddedToGraph += OnNodeAddedToGraph;
            GraphView.NodeDeletedFromGraph += OnNodeDeletedFromGraph;
            GraphView.StatusText_ += OnGraphStatus;
        }

        private void OnGraphStatus(string msg)
            => StatusText.Text = msg;

        private void OnNodeDeletedFromGraph(
            SkyrimHavokEditor.Models.HkObject deletedObj,
            SkyrimHavokEditor.Models.HkObject parentSM,
            string oldStatesValue)
        {
            var capturedId = deletedObj.Id;
            var capturedObj = deletedObj;
            var capturedSM = parentSM;
            var capturedOldStates = oldStatesValue;

            _undoRedo.Record(new EditAction
            {
                Description = $"Delete '{deletedObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? deletedObj.Id}'",
                Undo = () =>
                {
                    manager.ObjectMap[capturedId] = capturedObj;
                    if (capturedSM != null)
                    {
                        var sp = capturedSM.Params.FirstOrDefault(p => p.Name == "states");
                        if (sp != null)
                        {
                            sp.Value = capturedOldStates;
                            sp.NumElements = (capturedOldStates ?? "")
                                .Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                                .Length.ToString();
                        }
                    }
                    RefreshLookups();
                    GraphView.Load(manager, EventList.ToList(), VariableList.ToList());
                    UpdateUndoRedoButtons();
                },
                Redo = () =>
                {
                    manager.ObjectMap.Remove(capturedId);
                    if (capturedSM != null)
                    {
                        var sp = capturedSM.Params.FirstOrDefault(p => p.Name == "states");
                        if (sp != null)
                        {
                            var ids = (sp.Value ?? "")
                                .Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                                .Where(id => id != capturedId).ToList();
                            sp.Value = string.Join(" ", ids);
                            sp.NumElements = ids.Count.ToString();
                        }
                    }
                    RefreshLookups();
                    GraphView.Load(manager, EventList.ToList(), VariableList.ToList());
                    UpdateUndoRedoButtons();
                }
            });
            UpdateUndoRedoButtons();
            RefreshLookups();
        }

        private void OnNodeAddedToGraph(
            SkyrimHavokEditor.Models.HkObject newObj,
            SkyrimHavokEditor.Models.HkObject parentSM)
        {
            // Record undo — remove from ObjectMap and from SM states list
            var capturedId = newObj.Id;
            var capturedSM = parentSM;

            _undoRedo.Record(new EditAction
            {
                Description = $"Add '{newObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? newObj.Id}'",
                Undo = () =>
                {
                    manager.ObjectMap.Remove(capturedId);
                    if (capturedSM != null)
                    {
                        var sp = capturedSM.Params.FirstOrDefault(p => p.Name == "states");
                        if (sp != null)
                        {
                            var ids = sp.Value.Split(' ',
                                System.StringSplitOptions.RemoveEmptyEntries)
                                .Where(id => id != capturedId).ToList();
                            sp.Value = string.Join(" ", ids);
                            sp.NumElements = ids.Count.ToString();
                        }
                    }
                    RefreshLookups();
                    UpdateUndoRedoButtons();
                },
                Redo = () =>
                {
                    manager.ObjectMap[capturedId] = newObj;
                    if (capturedSM != null)
                    {
                        var sp = capturedSM.Params.FirstOrDefault(p => p.Name == "states");
                        if (sp != null)
                        {
                            var current = sp.Value?.Trim() ?? "";
                            sp.Value = string.IsNullOrEmpty(current)
                                ? capturedId : current + " " + capturedId;
                            sp.NumElements = sp.Value
                                .Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                                .Length.ToString();
                        }
                    }
                    RefreshLookups();
                    UpdateUndoRedoButtons();
                }
            });
            UpdateUndoRedoButtons();
            RefreshLookups();
        }

        private void OnTransitionDeletedFromGraph(
            SkyrimHavokEditor.Models.HkObject trChild,
            SkyrimHavokEditor.Models.HkObject transArray,
            string fromName, string toName)
        {
            var tParam = transArray.Params
                .FirstOrDefault(p => p.Name == "transitions");
            if (tParam == null) return;

            _undoRedo.Record(new EditAction
            {
                Description = $"Delete transition {fromName} → {toName} (graph)",
                Undo = () =>
                {
                    tParam.Children.Add(trChild);
                    tParam.NumElements = tParam.Children.Count.ToString();
                    UpdateUndoRedoButtons();
                },
                Redo = () =>
                {
                    tParam.Children.Remove(trChild);
                    tParam.NumElements = tParam.Children.Count.ToString();
                    UpdateUndoRedoButtons();
                }
            });
            UpdateUndoRedoButtons();
            StatusText.Text = $"✓ Transition deleted: {fromName} → {toName}";

            // Refresh transitions tab
            RefreshLookups();
        }

        private void OnNodeRenamedOnGraph(string objectId, string oldName, string newName)
        {
            if (!manager.ObjectMap.TryGetValue(objectId, out var obj)) return;
            var nameParam = obj.Params.FirstOrDefault(p => p.Name == "name");
            if (nameParam == null) return;

            _undoRedo.Record(new EditAction
            {
                Description = $"Rename '{oldName}' → '{newName}' (graph)",
                Undo = () =>
                {
                    _suppressUndoRecord = true;
                    nameParam.Value = oldName;
                    _suppressUndoRecord = false;
                    // Rebuild graph to show old name
                    GraphView.Load(manager, EventList.ToList(), VariableList.ToList());
                    UpdateUndoRedoButtons();
                },
                Redo = () =>
                {
                    _suppressUndoRecord = true;
                    nameParam.Value = newName;
                    _suppressUndoRecord = false;
                    GraphView.Load(manager, EventList.ToList(), VariableList.ToList());
                    UpdateUndoRedoButtons();
                }
            });
            UpdateUndoRedoButtons();
            StatusText.Text = $"✓ Renamed: '{oldName}' → '{newName}'";
        }
    }
}
