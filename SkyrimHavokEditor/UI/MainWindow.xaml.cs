using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Serialization;
using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Core.Animation;
using SkyrimHavokEditor.Core.Patching;
using SkyrimHavokEditor.Core.Services;
using SkyrimHavokEditor.Core.Skeletons;
using SkyrimHavokEditor.Core.Validation;
using SkyrimHavokEditor.Models;
using SkyrimHavokEditor.Models.ViewModels;
using SkyrimHavokEditor.UI;
using SkyrimHavokEditor.UI.Converters;
using SkyrimHavokEditor.UI.Dialogs;

namespace SkyrimHavokEditor
{
    public partial class MainWindow : Window
    {
        private HavokManager manager = new HavokManager();

        // ObservableCollections notify the UI when items are added/cleared
        public ObservableCollection<IdNamePair> VariableList { get; set; } = new();
        public ObservableCollection<IdNamePair> EventList { get; set; } = new();
        public ObservableCollection<ClipInfo> ClipList { get; set; } = new();

        public FileStats Stats { get; } = new FileStats();

        private ContextMenu RecentFilesMenu = new ContextMenu();

        private string _clipFilter = "";
        public string ClipFilter
        {
            get => _clipFilter;
            set
            {
                _clipFilter = value;
                ClipsView.Refresh();
            }
        }

        private string _variableFilter = "";
        public string VariableFilter
        {
            get => _variableFilter;
            set
            {
                _variableFilter = value;
                VariablesView.Refresh();
            }
        }

        public ICollectionView ClipsView { get; private set; }
        public ICollectionView VariablesView { get; private set; }

        public ObservableCollection<TransitionInfo> TransitionList { get; set; } = new();
        public ICollectionView TransitionsView { get; private set; }

        private string _transitionFilter = "";
        public string TransitionFilter
        {
            get => _transitionFilter;
            set { _transitionFilter = value; TransitionsView.Refresh(); }
        }

        public ObservableCollection<VariableUsage> UsageList { get; set; } = new();
        public ICollectionView UsageView { get; private set; }

        private IdNamePair _selectedVariable;
        public IdNamePair SelectedVariable
        {
            get => _selectedVariable;
            set
            {
                _selectedVariable = value;
                if (value != null) RefreshVarUsages(value);
            }
        }

        public ObservableCollection<EventUsageEntry> EventUsageList { get; set; } = new();

        private IdNamePair _selectedEvent;
        public IdNamePair SelectedEvent
        {
            get => _selectedEvent;
            set
            {
                _selectedEvent = value;
                if (value != null) RefreshEventUsages(value);
            }
        }

        public ObservableCollection<ClipTrigger> TriggerList { get; set; } = new();

        private ClipInfo _selectedClip;
        public ClipInfo SelectedClip
        {
            get => _selectedClip;
            set
            {
                _selectedClip = value;
                if (value != null) RefreshTriggers(value);
            }
        }

        public ObservableCollection<BindingEntry> BindingList { get; set; } = new();
        public ICollectionView BindingsView { get; private set; }

        private string _bindingFilter = "";
        public string BindingFilter
        {
            get => _bindingFilter;
            set { _bindingFilter = value; BindingsView.Refresh(); }
        }

        private const int MaxRecentFiles = 8;
        private const string RecentFilesKey = "RecentFiles";

        private static readonly string SettingsPath = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SkyrimHavokEditor", "recent.txt");

        private List<string> LoadRecentFiles()
        {
            try
            {
                if (!System.IO.File.Exists(SettingsPath)) return new List<string>();
                return System.IO.File.ReadAllLines(SettingsPath)
                    .Where(f => !string.IsNullOrEmpty(f) && System.IO.File.Exists(f))
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        private void SaveRecentFiles(List<string> files)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(SettingsPath);
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllLines(SettingsPath, files.Take(MaxRecentFiles));
            }
            catch { }
        }

        private void AddRecentFile(string path)
        {
            var files = LoadRecentFiles();
            files.Remove(path);           // remove if already present
            files.Insert(0, path);        // push to top
            SaveRecentFiles(files);
            RefreshRecentFilesMenu();
        }

        private void RefreshRecentFilesMenu()
        {
            RecentFilesMenu.Items.Clear();
            var files = LoadRecentFiles();

            if (files.Count == 0)
            {
                RecentFilesMenu.Items.Add(new MenuItem
                {
                    Header = "(no recent files)",
                    IsEnabled = false
                });
                return;
            }

            foreach (var f in files)
            {
                var item = new MenuItem { Header = f, ToolTip = f };
                var capturedPath = f;
                item.Click += async (s, e) => await LoadFileAsync(capturedPath);
                RecentFilesMenu.Items.Add(item);
            }

            RecentFilesMenu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear Recent Files" };
            clear.Click += (s, e) =>
            {
                SaveRecentFiles(new List<string>());
                RefreshRecentFilesMenu();
                LoadBookmarks();
            };
            RecentFilesMenu.Items.Add(clear);
        }

        private readonly UndoRedoManager _undoRedo = new();
        private bool _suppressUndoRecord = false;

        private readonly HkxConversionService _hkxConv = new();
        private bool _sourceWasHkx = false;
        private string _originalHkxPath = null;

        private bool _debuggerRunning = false;
        private readonly YamlBehaviorImporter _yamlImporter = new();
        private string _creatureRoot;

        private ClipPreviewService _clipPreview;
        private ClipPreviewWindow _previewWindow;
        private HkObject _previewableClipObj;

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;
            WireDebugPanel();
            ClipsView = CollectionViewSource.GetDefaultView(ClipList);
            ClipsView.Filter = o => o is ClipInfo c &&
                (string.IsNullOrEmpty(_clipFilter) ||
                 c.Name.Contains(_clipFilter, StringComparison.OrdinalIgnoreCase) ||
                 c.AnimationPath.Contains(_clipFilter, StringComparison.OrdinalIgnoreCase));

            VariablesView = CollectionViewSource.GetDefaultView(VariableList);
            VariablesView.Filter = o => o is IdNamePair v &&
                (string.IsNullOrEmpty(_variableFilter) ||
                 v.Name.Contains(_variableFilter, StringComparison.OrdinalIgnoreCase));
            TransitionsView = CollectionViewSource.GetDefaultView(TransitionList);
            TransitionsView.Filter = o => o is TransitionInfo t &&
                (string.IsNullOrEmpty(_transitionFilter) ||
                 t.FromState.Contains(_transitionFilter, StringComparison.OrdinalIgnoreCase) ||
                 t.ToState.Contains(_transitionFilter, StringComparison.OrdinalIgnoreCase) ||
                 t.EventName.Contains(_transitionFilter, StringComparison.OrdinalIgnoreCase));
            UsageView = CollectionViewSource.GetDefaultView(UsageList);
            BindingsView = CollectionViewSource.GetDefaultView(BindingList);
            BindingsView.Filter = o => o is BindingEntry b &&
                (string.IsNullOrEmpty(_bindingFilter) ||
                 b.OwnerName.Contains(_bindingFilter, StringComparison.OrdinalIgnoreCase) ||
                 b.VariableName.Contains(_bindingFilter, StringComparison.OrdinalIgnoreCase) ||
                 b.MemberPath.Contains(_bindingFilter, StringComparison.OrdinalIgnoreCase));
            ApplyTheme(AppSettings.IsDarkMode);
            // Recent files + drag drop
            RefreshRecentFilesMenu();
            AllowDrop = true;
            DragOver += Window_DragOver;
            Drop += Window_Drop;
            UpdateCanvasBackground();
            EventList.CollectionChanged += (s, e) =>
            {
                var eventStringData = manager?.ObjectMap?.Values
                    .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
                if (eventStringData == null) return;

                var eParam = eventStringData.Params.FirstOrDefault(p => p.Name == "eventNames");
                if (eParam != null)
                    eParam.NumElements = EventList.Count.ToString();
            };
            EventsView = CollectionViewSource.GetDefaultView(EventList);
            EventsView.Filter = o => o is IdNamePair ev &&
                (string.IsNullOrEmpty(_eventFilter) ||
                 ev.Name.Contains(_eventFilter, StringComparison.OrdinalIgnoreCase) ||
                 ev.Id.Contains(_eventFilter, StringComparison.OrdinalIgnoreCase));
            GraphView.AddTransitionRequested += (fromObjectId, toStateId) =>
            {
                // Find the SM that owns this state
                var parentSM = FindParentSM(fromObjectId);
                if (parentSM == null) return;
                SelectedSM = parentSM;
                MainTabControl.SelectedIndex = 6; // SM Inspector tab
                                                  // Pre-select the from state and open dialog
                OpenTransitionDialog(isAdd: true,
                    preselectedFromState: manager.ObjectMap[fromObjectId]);
            };
            LoadBundledSkeletons();
            InitializeSkeletonRegistry();
            LoadBookmarks();
        }


        private void OpenDoc(string section)
        {
            // Switch to the Guide tab and scroll to the section
            foreach (TabItem tab in MainTabControl.Items)
                if (tab.Content is DocumentationView) { tab.IsSelected = true; break; }
            DocView.ScrollToSection(section);
        }

        private void BtnGraphHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_graph");
        private void BtnVariablesHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_variables");
        private void BtnEventsHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_events");
        private void BtnTransitionsHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_transitions");
        private void BtnClipsHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_clips");
        private void BtnSMInspectorHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_sm_inspector");
        private void BtnBindingsHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_bindings");
        private void BtnProjectHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_project");
        private void BtnCharacterHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_character");
        private void BtnDebuggerHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_debugger");
        private void BtnBookmarksHelp_Click(object sender, RoutedEventArgs e)
            => OpenDoc("tab_bookmarks");

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            else
                DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            BtnMaximize.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();


        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            source?.AddHook(WndProc);
        }

        private const int WM_GETMINMAXINFO = 0x0024;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private readonly Stack<string> _navigationHistory = new();

        private void NavigateToObject(string objectId)
        {
            if (!manager.ObjectMap.TryGetValue(objectId, out var obj)) return;

            // Push current object to history if we have one
            if (ParamsEditor.ItemsSource != null &&
                BtnBackNavigation.Tag is string currentId)
                _navigationHistory.Push(currentId);

            LoadObjectIntoEditor(obj);
            BtnBackNavigation.IsEnabled = _navigationHistory.Count > 0;
        }


        private void LoadObjectIntoEditor(HkObject obj)
        {
            SelectedClassName.Text = $"Class: {obj.ClassName}";
            ParamsEditor.ItemsSource = obj.Params;
            var name = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? obj.Id;
            ObjectNameLabel.Text = $"{obj.Id}  {name}  ·  {obj.ClassName}";
            BtnBackNavigation.Tag = obj.Id;

            // Prime the bookmark toggle for THIS object (every load path runs through here now)
            BtnBookmarkToggle.Tag = obj;
            bool isBookmarked = Bookmarks.Any(b => b.Id == obj.Id);
            BtnBookmarkToggle.Content = isBookmarked ? "★" : "🔖";
            BtnBookmarkToggle.Foreground = isBookmarked
                ? new SolidColorBrush(Colors.Goldenrod)
                : (SolidColorBrush)Application.Current.Resources["TextSecondaryBrush"];
            BtnBookmarkToggle.ToolTip = isBookmarked ? "Remove bookmark" : "Bookmark this object";

            // Show preview button only for clip generators
            if (obj.ClassName == "hkbClipGenerator")
            {
                _previewableClipObj = obj;
                BtnPreviewClipObj.Visibility = Visibility.Visible;
            }
            else
            {
                _previewableClipObj = null;
                BtnPreviewClipObj.Visibility = Visibility.Collapsed;
            }

            foreach (var param in obj.Params)
                SubscribeParamUndo(param, obj.ClassName, name);
        }

        private void BtnPreviewClipObj_Click(object sender, RoutedEventArgs e)
        {
            if (_previewableClipObj == null) return;
            // Find the matching ClipInfo so PreviewClip gets the right triggers/name.
            var clip = ClipList.FirstOrDefault(c => c.Id == _previewableClipObj.Id);
            if (clip != null) PreviewClip(clip);
            else
            {
                // Object isn't in ClipList (rare) — build a minimal ClipInfo on the fly
                var animName = _previewableClipObj.Params.FirstOrDefault(p => p.Name == "animationName")?.Value;
                var nm = _previewableClipObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? _previewableClipObj.Id;
                if (!string.IsNullOrEmpty(animName))
                    PreviewClip(new ClipInfo { Id = _previewableClipObj.Id, Name = nm, AnimationPath = animName });
            }
        }

        private void BtnBackNavigation_Click(object sender, RoutedEventArgs e)
        {
            if (_navigationHistory.Count == 0) return;
            var prevId = _navigationHistory.Pop();
            if (!manager.ObjectMap.TryGetValue(prevId, out var obj)) return;
            LoadObjectIntoEditor(obj);
            BtnBackNavigation.IsEnabled = _navigationHistory.Count > 0;
        }

        private void ParamValue_PreviewMouseLeftButtonDown(object sender,
    System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!System.Windows.Input.Keyboard.Modifiers
                .HasFlag(System.Windows.Input.ModifierKeys.Control)) return;
            if (sender is not TextBox tb) return;

            var val = tb.Text?.Trim();
            if (string.IsNullOrEmpty(val) || !val.StartsWith("#")) return;

            // Handle space-separated multi-refs — find which token was clicked
            var refs = val.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Where(r => r.StartsWith("#"))
                          .ToList();

            if (refs.Count == 1)
            {
                NavigateToObject(refs[0]);
            }
            else if (refs.Count > 1)
            {
                // Show a small context menu to pick which ref to jump to
                var menu = new ContextMenu();
                foreach (var refId in refs)
                {
                    manager.ObjectMap.TryGetValue(refId, out var refObj);
                    var label = refObj != null
                        ? $"{refId}  {refObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? refObj.ClassName}"
                        : refId;
                    var item = new MenuItem { Header = label };
                    var capturedId = refId;
                    item.Click += (s, ev) => NavigateToObject(capturedId);
                    menu.Items.Add(item);
                }
                menu.PlacementTarget = tb;
                menu.IsOpen = true;
            }
            e.Handled = true;
        }

        private void ParamValue_PreviewMouseMove(object sender,
    System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not TextBox tb) return;
            var val = tb.Text?.Trim() ?? "";
            bool isRef = val.StartsWith("#") ||
                         val.Split(' ').Any(t => t.StartsWith("#"));
            bool ctrl = System.Windows.Input.Keyboard.Modifiers
                .HasFlag(System.Windows.Input.ModifierKeys.Control);

            if (isRef && ctrl)
            {
                tb.Cursor = System.Windows.Input.Cursors.Hand;
                tb.Background = (SolidColorBrush)Application.Current.Resources["BgSelectedBrush"];
            }
            else
            {
                tb.Cursor = System.Windows.Input.Cursors.IBeam;
                tb.Background = (SolidColorBrush)Application.Current.Resources["BgInputBrush"];
            }
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, 0x00000002);
            if (monitor != IntPtr.Zero)
            {
                RECT workArea = new RECT();
                SystemParametersInfo(0x0030, 0, ref workArea, 0);
                mmi.ptMaxPosition.X = workArea.Left;
                mmi.ptMaxPosition.Y = workArea.Top;
                mmi.ptMaxSize.X = workArea.Right - workArea.Left;
                mmi.ptMaxSize.Y = workArea.Bottom - workArea.Top;
                mmi.ptMinTrackSize.X = 400;
                mmi.ptMinTrackSize.Y = 300;
            }
            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        }

        [System.Runtime.InteropServices.DllImport("user32")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [System.Runtime.InteropServices.DllImport("user32")]
        private static extern bool SystemParametersInfo(uint uAction, uint uParam, ref RECT lpvParam, uint fuWinIni);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private bool _isDarkMode = true;

        private void UpdateCanvasBackground()
        {
            var dotColor = ((SolidColorBrush)Application.Current.Resources["CanvasDotBrush"]).Color;
            GraphView.UpdateCanvasBackground(dotColor);
        }

        private async void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            // Offer choice: file or folder
            var menu = new ContextMenu();

            var openFile = new MenuItem { Header = "📄 Open .hkx / .xml file…" };
            openFile.Click += async (s, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Open Havok Behavior File",
                    Filter = "Havok files|*.hkx;*.xml|All files|*.*"
                };
                if (dlg.ShowDialog() == true)
                    await LoadFileAsync(dlg.FileName);
            };

            var openFolder = new MenuItem { Header = "📂 Open YAML behavior folder…" };
            openFolder.Click += async (s, _) =>
            {
                // Use a SaveFileDialog trick to select a folder
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Navigate to the YAML behavior folder, then click Save",
                    FileName = "navigate_to_folder_then_click_save",
                    Filter = "Any|*.*",
                    CheckFileExists = false,
                    CheckPathExists = false
                };
                if (dlg.ShowDialog() != true) return;

                var folder = Path.GetDirectoryName(dlg.FileName)!;
                if (IsYamlBehaviorFolder(folder))
                    await LoadFileAsync(folder);
                else
                    MessageBox.Show("That folder doesn't look like a YAML behavior folder.\n" +
                                    "It should contain a behavior.yaml or subdirectories like clips/, generators/, etc.");
            };

            var openCreature = new MenuItem { Header = "🐺 Open creature folder…" };
            openCreature.Click += async (s, _) =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Navigate into the actor folder (e.g. ...\\actors\\dragon), then click Save",
                    FileName = "navigate_to_actor_folder_then_click_save",
                    Filter = "Any|*.*",
                    CheckFileExists = false,
                    CheckPathExists = false
                };
                if (dlg.ShowDialog() != true) return;
                var folder = Path.GetDirectoryName(dlg.FileName)!;
                await LoadCreatureFolderAsync(folder);
            };
            menu.Items.Add(openCreature);

            menu.Items.Add(openFile);
            menu.Items.Add(openFolder);
            menu.PlacementTarget = BtnLoad;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }


        private async Task LoadCreatureFolderAsync(string actorRoot)
        {
            if (!Directory.Exists(actorRoot))
            {
                MessageBox.Show("Folder not found:\n" + actorRoot);
                return;
            }

            // Project files are named "<race>project.hkx" — try the root, then recurse.
            var project = Directory.GetFiles(actorRoot, "*project.hkx", SearchOption.TopDirectoryOnly)
                .FirstOrDefault()
                ?? Directory.GetFiles(actorRoot, "*project.hkx", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (project == null)
            {
                MessageBox.Show(
                    "No *project.hkx found under that folder.\n" +
                    "Pick the actor root (the folder that directly contains <race>project.hkx).");
                return;
            }

            _creatureRoot = actorRoot;
            await LoadFileAsync(project);   // → HkFileType.Project → fills Project + Character tabs
        }

        // ── New central load entry point ───────────────────────────────────────────────
        // Everything — button, drag-drop, recent files — calls this one method.

        private async Task LoadFileAsync(string path)
        {
            // ── Handle YAML behavior folders ─────────────────────────────────────────
            if (IsYamlBehaviorFolder(path))
            {
                await LoadYamlFolderAsync(path);
                return;
            }

            if (!File.Exists(path))
            {
                MessageBox.Show($"File not found:\n{path}");
                return;
            }

            StatusText.Text = "⏳ Opening…";
            BtnLoad.IsEnabled = false;

            try
            {
                Workspace ??= new HavokWorkspace(_hkxConv);
                var fileType = await Workspace.LoadAutoAsync(path);

                switch (fileType)
                {
                    case HkFileType.Project:
                        LoadProjectIntoUI();
                        if (Workspace.ActiveBehavior != null)
                            LoadBehaviorIntoApp(Workspace.BehaviorFile!);
                        StatusText.Text = "✓ Project loaded";
                        break;
                    case HkFileType.Character:
                        LoadCharacterIntoUI();
                        StatusText.Text = "✓ Character loaded";
                        break;
                    case HkFileType.Behavior:
                    default:
                        LoadBehaviorIntoApp(Workspace.BehaviorFile!);
                        StatusText.Text = "✓ Behavior loaded";
                        break;
                }

                AddRecentFile(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening file:\n{ex.Message}");
                StatusText.Text = "Error";
            }
            finally
            {
                BtnLoad.IsEnabled = true;
            }
        }
        private async Task LoadYamlFolderAsync(string folderPath)
        {
            StatusText.Text = "⏳ Loading YAML behavior…";
            BtnLoad.IsEnabled = false;

            try
            {
                string behaviorName = await Task.Run(() =>
                {
                    manager = new HavokManager();
                    return _yamlImporter.Import(folderPath, manager);
                });

                Stats.FileName = behaviorName;
                Stats.ObjectCount = manager.ObjectMap.Count;

                _originalSnapshot = TakeSnapshot();
                _snapshotEvents = new List<string>();
                _snapshotVars = new List<string>();

                _validator = new HavokValidator(manager);

                _subscribedParams.Clear();
                _navigationHistory.Clear();
                BtnBackNavigation.IsEnabled = false;
                BtnBackNavigation.Tag = null;
                _undoRedo.Clear();
                UpdateUndoRedoButtons();

                RefreshLookups();

                // ← This is the missing call
                GraphView.Load(manager, EventList.ToList(), VariableList.ToList());
                WireGraphEvents();

                _snapshotEvents = EventList.Select(ev => ev.Name).ToList();
                _snapshotVars = VariableList.Select(v => v.Name).ToList();

                Stats.VariableCount = VariableList.Count;
                Stats.EventCount = EventList.Count;
                Stats.ClipCount = ClipList.Count;
                Stats.TransitionCount = TransitionList.Count;

                var builder = new BehaviorTreeBuilder(manager);
                ObjectTree.ItemsSource = new List<BehaviorNodeData> { builder.BuildTree("") };

                _sourceWasHkx = false;
                _originalHkxPath = null;

                AddRecentFile(folderPath);

                StatusText.Text = $"✓ YAML loaded: {behaviorName}  " +
                                  $"({manager.ObjectMap.Count} objects, " +
                                  $"{VariableList.Count} vars, {EventList.Count} events)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading YAML folder:\n{ex.Message}");
                StatusText.Text = "YAML load failed";
            }
            finally
            {
                BtnLoad.IsEnabled = true;
            }
        }
        private void BtnRecentFiles_Click(object sender, RoutedEventArgs e)
        {
            RefreshRecentFilesMenu();
            RecentFilesMenu.PlacementTarget = sender as Button;
            RecentFilesMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            RecentFilesMenu.IsOpen = true;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);

            // Accept first .hkx or .xml — or any file (let DetectFormat decide)
            var file = files.FirstOrDefault(f =>
                f.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault();

            if (file != null)
                await LoadFileAsync(file);
        }

        private void UpdateUndoRedoButtons()
        {
            BtnUndo.IsEnabled = _undoRedo.CanUndo;
            BtnRedo.IsEnabled = _undoRedo.CanRedo;
            BtnUndo.ToolTip = _undoRedo.CanUndo ? $"Undo: {_undoRedo.UndoDescription}" : "Nothing to undo";
            BtnRedo.ToolTip = _undoRedo.CanRedo ? $"Redo: {_undoRedo.RedoDescription}" : "Nothing to redo";
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            _undoRedo.Undo();
            UpdateUndoRedoButtons();
        }

        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            _undoRedo.Redo();
            UpdateUndoRedoButtons();
        }

        private void BtnDebugger_Click(object sender, RoutedEventArgs e)
        {
            if (_debuggerRunning)
            {
                GraphView.StopDebugging();
                BtnDebugger.Content = "🎮 Live Debug";
                _debuggerRunning = false;
            }
            else
            {
                GraphView.StartDebugging();
                BtnDebugger.Content = "⏹ Stop Debug";
                _debuggerRunning = true;
            }
        }

        private void RefreshLookups()
        {
            if (manager == null || manager.ObjectMap.Count == 0) return;

            VariableList.Clear();
            EventList.Clear();

            // ------------------------
            // VARIABLES
            // ------------------------

            var valueSet = manager.ObjectMap.Values
     .FirstOrDefault(o => o.ClassName == "hkbVariableValueSet");

            var valStrings = new List<string>();

            var typeInfos = new List<string>();
            var graphData = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphData");

            if (graphData != null)
            {
                var varInfosParam = graphData.Params.FirstOrDefault(p => p.Name == "variableInfos");
                if (varInfosParam?.Children != null)
                {
                    foreach (var child in varInfosParam.Children)
                    {
                        // type is stored as a role/type param inside each child hkobject
                        var typeParam = child.Params.FirstOrDefault(p => p.Name == "type");
                        typeInfos.Add(typeParam?.Value ?? "VARIABLE_TYPE_REAL");
                    }
                }
            }

            if (valueSet != null)
            {
                var wordValuesParam = valueSet.Params.FirstOrDefault(p => p.Name == "wordVariableValues");
                if (wordValuesParam?.Children != null)
                {
                    foreach (var child in wordValuesParam.Children)
                    {
                        var vp = child.Params.FirstOrDefault(p => p.Name == "value");
                        valStrings.Add(vp?.Value ?? "0");
                    }
                }
            }

            var variableObjects = manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbVariableNamesData"
                         || o.ClassName == "hkbProjectStringData"
                         || o.ClassName == "hkbBehaviorGraphStringData")
                .ToList();

            foreach (var varObj in variableObjects)
            {
                var namesParam = varObj.Params.FirstOrDefault(p => p.Name == "variableNames" || p.Name == "wordVariableNames");
                if (namesParam == null) continue;

                List<string> namesList = namesParam.Strings.Count > 0
                    ? namesParam.Strings
                    : namesParam.Value?.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();

                if (namesList == null || namesList.Count == 0) continue;

                // NOW actually add to VariableList using the values we fetched
                for (int i = 0; i < namesList.Count; i++)
                {
                    string rawVal = i < valStrings.Count ? valStrings[i] : "0";
                    string typeStr = i < typeInfos.Count ? typeInfos[i] : "VARIABLE_TYPE_REAL";
                    var pair = new IdNamePair
                    {
                        Id = $"{varObj.Id}_{i}",
                        Name = namesList[i],
                        Index = i,
                        RawValue = rawVal,
                        Value = DecodeHavokValue(rawVal),
                        VariableType = typeStr
                    };

                    pair.ValueChanged += (s, args) =>
                    {
                        if (_suppressUndoRecord || s is not IdNamePair v) return;
                        var capturedOld = args.OldValue;
                        var capturedNew = args.NewValue;
                        var capturedVar = v;
                        _undoRedo.Record(new EditAction
                        {
                            Description = $"Change {v.Name}: {args.OldValue} → {args.NewValue}",
                            Undo = () =>
                            {
                                _suppressUndoRecord = true;
                                capturedVar.Value = capturedOld;
                                _suppressUndoRecord = false;
                                UpdateUndoRedoButtons();
                            },
                            Redo = () =>
                            {
                                _suppressUndoRecord = true;
                                capturedVar.Value = capturedNew;
                                _suppressUndoRecord = false;
                                UpdateUndoRedoButtons();
                            }
                        });
                        UpdateUndoRedoButtons();
                    };

                    VariableList.Add(pair);
                }
            }

            // ------------------------
            // EVENTS
            // ------------------------
            var eventObjects = manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbEventNamesData"
                         || o.ClassName == "hkbProjectStringData"
                         || o.ClassName == "hkbBehaviorGraphStringData")
                .ToList();

            foreach (var eObj in eventObjects)
            {
                var eParam = eObj.Params.FirstOrDefault(p => p.Name == "eventNames");
                if (eParam == null) continue;

                var eNames = eParam.Strings.Count > 0
                    ? eParam.Strings
                    : eParam.Value?
                        .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();

                if (eNames == null || eNames.Count == 0) continue;

                for (int i = 0; i < eNames.Count; i++)
                {
                    EventList.Add(new IdNamePair
                    {
                        Id = i.ToString(),
                        Name = eNames[i]
                    });
                }
            }

            // ------------------------
            // CLIPS
            // ------------------------
            ClipList.Clear();
            var clips = manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbClipGenerator")
                .OrderBy(o => o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id);

            foreach (var clip in clips)
            {
                string Get(string n) => clip.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";

                var clipEntry = new ClipInfo
                {
                    Id = clip.Id,
                    Name = Get("name"),
                    Mode = Get("mode"),
                    PlaybackSpeed = Get("playbackSpeed")
                };

                // Subscribe BEFORE setting AnimationPath
                clipEntry.PathChanged += (s, args) =>
                {
                    if (_suppressUndoRecord || s is not ClipInfo c) return;
                    if (args.OldValue == args.NewValue) return;
                    var capturedOld = args.OldValue;
                    var capturedNew = args.NewValue;
                    var capturedClip = c;
                    _undoRedo.Record(new EditAction
                    {
                        Description = $"Path {c.Name}: {args.OldValue} → {args.NewValue}",
                        Undo = () =>
                        {
                            _suppressUndoRecord = true;
                            capturedClip.AnimationPath = capturedOld;
                            _suppressUndoRecord = false;
                            UpdateUndoRedoButtons();
                        },
                        Redo = () =>
                        {
                            _suppressUndoRecord = true;
                            capturedClip.AnimationPath = capturedNew;
                            _suppressUndoRecord = false;
                            UpdateUndoRedoButtons();
                        }
                    });
                    UpdateUndoRedoButtons();
                };

                // Set AFTER subscribing
                clipEntry.AnimationPath = Get("animationName");
                ClipList.Add(clipEntry);
            }

            // ------------------------
            // TRANSITIONS
            // ------------------------
            TransitionList.Clear();

            var stateIdToName = new Dictionary<string, string>();
            foreach (var stateObj in manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachineStateInfo"))
            {
                var stateId = stateObj.Params.FirstOrDefault(p => p.Name == "stateId")?.Value ?? "";
                var stateName = stateObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? stateObj.Id;
                if (!string.IsNullOrEmpty(stateId) && !stateIdToName.ContainsKey(stateId))
                    stateIdToName[stateId] = stateName;
            }

            var eventNamesLookup = EventList
                .ToDictionary(e => e.Id, e => e.Name);

            foreach (var stateObj in manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachineStateInfo"))
            {
                var fromName = stateObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? stateObj.Id;
                var transRef = stateObj.Params.FirstOrDefault(p => p.Name == "transitions")?.Value;

                if (string.IsNullOrEmpty(transRef) || transRef == "null") continue;
                if (!manager.TryResolve(transRef, out var transArrayObj)) continue;

                // transArrayObj is hkbStateMachineTransitionInfoArray
                // its "transitions" hkparam holds inline <hkobject> children
                var transitionsParam = transArrayObj.Params.FirstOrDefault(p => p.Name == "transitions");
                if (transitionsParam?.Children == null || transitionsParam.Children.Count == 0) continue;

                foreach (var tr in transitionsParam.Children)
                {
                    string Get(string n) => tr.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";

                    var toStateId = Get("toStateId");
                    var eventId = Get("eventId");
                    var flags = Get("flags");
                    var effect = Get("transition");

                    // blend duration lives inside the transition effect object
                    var blendDuration = "";
                    if (!string.IsNullOrEmpty(effect) && effect != "null"
                        && manager.TryResolve(effect, out var effectObj))
                    {
                        blendDuration = effectObj.Params.FirstOrDefault(p => p.Name == "duration")?.Value ?? "";
                    }

                    stateIdToName.TryGetValue(toStateId, out var toName);
                    eventNamesLookup.TryGetValue(eventId, out var evName);

                    TransitionList.Add(new TransitionInfo
                    {
                        FromState = fromName,
                        ToState = toName ?? $"ID:{toStateId}",
                        EventId = eventId,
                        EventName = evName ?? $"Event {eventId}",
                        BlendDuration = blendDuration,
                        Flags = flags,
                        TransitionEffect = effect
                    });
                }
            }

            // ------------------------
            // BINDINGS
            // ------------------------
            BindingList.Clear();

            // Build variable index -> name lookup
            var varIndexToName = VariableList
                .Select((v, i) => (i, v.Name))
                .ToDictionary(x => x.i.ToString(), x => x.Name);

            foreach (var bindingSetObj in manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbVariableBindingSet"))
            {
                // Find the object that owns this binding set
                var owner = manager.ObjectMap.Values.FirstOrDefault(o =>
                    o.Params.Any(p => p.Value == bindingSetObj.Id));

                var ownerName = owner?.Params.FirstOrDefault(p => p.Name == "name")?.Value
                                 ?? owner?.Id ?? bindingSetObj.Id;
                var ownerClass = owner?.ClassName ?? "Unknown";
                var ownerId = owner?.Id ?? bindingSetObj.Id;

                var bindingsParam = bindingSetObj.Params.FirstOrDefault(p => p.Name == "bindings");
                if (bindingsParam?.Children == null) continue;

                foreach (var binding in bindingsParam.Children)
                {
                    string Get(string n) => binding.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";

                    var memberPath = Get("memberPath");
                    var variableIndex = Get("variableIndex");
                    var bindingType = Get("bindingType");

                    varIndexToName.TryGetValue(variableIndex, out var varName);

                    BindingList.Add(new BindingEntry
                    {
                        OwnerName = ownerName,
                        OwnerClass = ownerClass,
                        OwnerId = ownerId,
                        MemberPath = memberPath,
                        VariableIndex = variableIndex,
                        VariableName = varName ?? $"var[{variableIndex}]",
                        BindingType = bindingType
                    });
                }
            }
            RefreshSmList();
        }



        private void PreviewClip_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // don't bubble to the row → no graph jump
            if ((sender as System.Windows.Controls.Button)?.Tag is ClipInfo clip)
                PreviewClip(clip);
        }

        private async void PreviewClip(ClipInfo clip)
        {
            var animPath = clip?.AnimationPath;
            var skelPath = Workspace?.Character?.SkeletonPath;
            if (string.IsNullOrEmpty(animPath) || string.IsNullOrEmpty(skelPath))
            { StatusText.Text = "Need a loaded character (skeleton path) to preview."; return; }

            _clipPreview ??= new ClipPreviewService(_hkxConv);

            var charFile = Workspace?.Character?.File?.OriginalPath ?? "";
            var charDir = System.IO.Path.GetDirectoryName(charFile) ?? "";
            var actorRoot = System.IO.Path.GetDirectoryName(charDir) ?? charDir;
            var above = System.IO.Path.GetDirectoryName(actorRoot) ?? actorRoot;

            if (_previewWindow == null || !_previewWindow.IsLoaded)
            { _previewWindow = new ClipPreviewWindow { Owner = this }; _previewWindow.Show(); }
            _previewWindow.Title = $"Clip Preview — {clip.Name}";
            _previewWindow.Activate();

            var res = await _clipPreview.LoadClipAsync(animPath, skelPath, charDir, actorRoot, above, _creatureRoot ?? "");
            if (!res.Success) { _previewWindow.View.ShowMessage("Couldn't load: " + res.Error); return; }

            // Gather this clip's behavior-side triggers (same resolution as RefreshTriggers)
            var triggers = GatherClipTriggers(clip, res.Clip.Duration);

            // Let the preview know which bones the clip actually drives, and jump-to-graph
            _previewWindow.View.OnShowInGraph = () => { _previewWindow.Activate(); RevealClipInGraph(clip); };
            _previewWindow.View.Show(res.Clip, res.Skeleton, triggers);
        }

        private List<UI.PreviewTrigger> GatherClipTriggers(ClipInfo clip, float duration)
        {
            var list = new List<UI.PreviewTrigger>();
            if (clip == null || !manager.ObjectMap.TryGetValue(clip.Id, out var clipObj)) return list;

            var triggersRef = clipObj.Params.FirstOrDefault(p => p.Name == "triggers")?.Value;
            if (string.IsNullOrEmpty(triggersRef) || triggersRef == "null") return list;
            if (!manager.TryResolve(triggersRef, out var triggerArrayObj)) return list;

            var triggersParam = triggerArrayObj.Params.FirstOrDefault(p => p.Name == "triggers");
            if (triggersParam?.Children == null) return list;

            var eventNamesLookup = EventList.ToDictionary(ev => ev.Id, ev => ev.Name);

            foreach (var tr in triggersParam.Children)
            {
                string Get(string n) => tr.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";
                var relToEnd = Get("relativeToEndOfClip").ToLower() == "true";

                if (!float.TryParse(Get("localTime"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t))
                    continue;

                // relativeToEndOfClip means the time is measured backward from the end
                var time = relToEnd ? Math.Max(0, duration + t) : t;

                var eventParam = tr.Params.FirstOrDefault(p => p.Name == "event");
                var eventId = eventParam?.Children?.Count > 0
                    ? eventParam.Children[0].Params.FirstOrDefault(p => p.Name == "id")?.Value ?? ""
                    : "";
                eventNamesLookup.TryGetValue(eventId, out var evName);

                list.Add(new UI.PreviewTrigger
                {
                    Time = time,
                    EventName = evName ?? $"Event {eventId}",
                    RelativeToEnd = relToEnd
                });
            }
            return list;
        }

        private void RevealClipInGraph(ClipInfo clip)
        {
            if (clip == null || !manager.ObjectMap.TryGetValue(clip.Id, out var obj)) return;

            MainTabControl.SelectedIndex = 0;          // Graph tab
            LoadObjectIntoEditor(obj);                  // Object Data panel (right side)

            // Reveal on the canvas — selects SM, drills into owning state, highlights clip.
            GraphView.RevealClipNode(clip.Id);

            // Also reflect in the tree (left panel), deferred so containers exist.
            var root = ObjectTree.ItemsSource?.Cast<BehaviorNodeData>().FirstOrDefault();
            if (root != null)
                Dispatcher.InvokeAsync(() =>
                {
                    var target = FindNodeById(root, clip.Id);
                    if (target != null) SelectTreeNode(ObjectTree, target);
                }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static readonly string SettingsFilePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SkyrimHavokEditor", "settings.txt");
        private void InitializeSkeletonRegistry()
        {
            var meshes = AppSettings.MeshesPath;
            if (string.IsNullOrEmpty(meshes) || !Directory.Exists(meshes)) return;

            Task.Run(() =>
            {
                try { SkeletonRegistry.Instance.AutoLoad(meshes); }
                catch (Exception ex)
                { System.Diagnostics.Debug.WriteLine($"Skeleton load: {ex.Message}"); }
            });
        }

        private void LoadBundledSkeletons()
        {
            var skeletonDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                           "Data", "Skeletons");
            if (!Directory.Exists(skeletonDir)) return;

            foreach (var json in Directory.GetFiles(skeletonDir, "*.json"))
            {
                try { SkeletonRegistry.Instance.LoadFromJson(json); }
                catch { /* skip bad files */ }
            }
        }

        // ── Patch system ──────────────────────────────────────────────────────────
        private Dictionary<string, ObjectSnapshot> _originalSnapshot;
        private List<string> _snapshotEvents = new();
        private List<string> _snapshotVars = new();

        private Dictionary<string, ObjectSnapshot> TakeSnapshot()
    => PatchGenerator.TakeSnapshot(manager);


        private void BtnGeneratePatch_Click(object sender, RoutedEventArgs e)
        {
            if (_originalSnapshot == null || _originalSnapshot.Count == 0)
            {
                MessageBox.Show("No file loaded.");
                return;
            }

            var strData = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            if (strData != null)
            {
                var ep = strData.Params.FirstOrDefault(p => p.Name == "eventNames");
                if (ep != null)
                {
                    ep.Strings = EventList.Select(ev => ev.Name).ToList();
                    ep.Value = string.Join("\n", ep.Strings);
                }
            }

            var gen = new PatchGenerator(manager, _originalSnapshot, _snapshotEvents, _snapshotVars);
            var patch = gen.Generate(
                baseFileName: System.IO.Path.GetFileNameWithoutExtension(Stats.FileName));

            if (patch.Operations.Count == 0)
            {
                MessageBox.Show("No changes detected since the file was loaded.",
                    "Nothing to patch", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var preview = new PatchPreviewDialog(patch, manager, _originalSnapshot,
    Workspace?.BehaviorFile?.OriginalPath ?? "")
            { Owner = this };
            preview.ShowDialog();
        }

        private async void BtnPatchFromFiles_Click(object sender, RoutedEventArgs e)
        {
            // Pick "base" file (vanilla)
            var dlgA = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Base File (e.g. vanilla)",
                Filter = "Havok files|*.hkx;*.xml|All files|*.*"
            };
            if (dlgA.ShowDialog() != true) return;

            // Pick "modified" file (the mod)
            var dlgB = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Modified File (e.g. mod version)",
                Filter = "Havok files|*.hkx;*.xml|All files|*.*"
            };
            if (dlgB.ShowDialog() != true) return;

            StatusText.Text = "⏳ Comparing files…";

            try
            {
                // Load base file into a temporary manager and snapshot it
                var baseManager = new HavokManager();
                var basePath = dlgA.FileName;
                var modPath = dlgB.FileName;

                // Convert HKX to XML if needed
                string baseXml = basePath;
                string modXml = modPath;

                var baseResult = await _hkxConv.PrepareXmlAsync(basePath);
                if (!baseResult.Success) { MessageBox.Show($"Failed to load base file:\n{baseResult.Error}"); return; }
                baseXml = baseResult.XmlPath;

                var modResult = await _hkxConv.PrepareXmlAsync(modPath);
                if (!modResult.Success) { MessageBox.Show($"Failed to load mod file:\n{modResult.Error}"); return; }
                modXml = modResult.XmlPath;

                // Load base
                var baseSerializer = new System.Xml.Serialization.XmlSerializer(typeof(HkPackfile));
                using (var fs = File.OpenRead(baseXml))
                {
                    var packfile = (HkPackfile)baseSerializer.Deserialize(fs);
                    baseManager.BuildGraph(packfile);
                }
                var baseSnapshot = PatchGenerator.TakeSnapshot(baseManager);

                // Get base events and variables for comparison
                var baseEvents = baseManager.ObjectMap.Values
                    .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData")
                    ?.Params.FirstOrDefault(p => p.Name == "eventNames")
                    ?.Strings ?? new List<string>();

                var baseVars = baseManager.ObjectMap.Values
                    .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData")
                    ?.Params.FirstOrDefault(p => p.Name == "variableNames")
                    ?.Strings ?? new List<string>();

                // Load modified file into a fresh manager
                var modManager = new HavokManager();
                using (var fs = File.OpenRead(modXml))
                {
                    var packfile = (HkPackfile)baseSerializer.Deserialize(fs);
                    modManager.BuildGraph(packfile);
                }

                // Clean up temp files
                if (baseXml != basePath && File.Exists(baseXml)) File.Delete(baseXml);
                if (modXml != modPath && File.Exists(modXml)) File.Delete(modXml);

                // Generate patch: base snapshot + mod as "current"
                var gen = new PatchGenerator(modManager, baseSnapshot, baseEvents, baseVars);
                var patch = gen.Generate(
                    baseFileName: Path.GetFileNameWithoutExtension(basePath),
                    author: "",
                    description: $"Patch from {Path.GetFileName(modPath)} onto {Path.GetFileName(basePath)}");

                if (patch.Operations.Count == 0)
                {
                    MessageBox.Show("No differences found between the two files.",
                        "No Changes", MessageBoxButton.OK, MessageBoxImage.Information);
                    StatusText.Text = "No differences found";
                    return;
                }

                StatusText.Text = $"✓ Found {patch.Operations.Count} differences";

                var preview = new PatchPreviewDialog(patch, modManager, baseSnapshot,
                    modPath)
                { Owner = this };
                preview.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error comparing files:\n{ex.Message}");
                StatusText.Text = "Compare failed";
            }
        }

        /// <summary>
        /// Returns true if the given path is a Pandora/YAML behavior folder.
        /// A YAML folder has a behavior.yaml file OR contains YAML subdirectories.
        /// </summary>
        private static bool IsYamlBehaviorFolder(string path)
        {
            if (!Directory.Exists(path)) return false;
            if (File.Exists(Path.Combine(path, "behavior.yaml"))) return true;

            var yamlSubdirs = new[] { "clips", "generators", "states", "modifiers", "transitions" };
            return yamlSubdirs.Any(sub => Directory.Exists(Path.Combine(path, sub)));
        }

        private void BtnApplyPatch_Click(object sender, RoutedEventArgs e)
        {
            if (manager.ObjectMap == null || manager.ObjectMap.Count == 0)
            {
                MessageBox.Show("Load a behavior file first before applying a patch.");
                return;
            }

            var dialog = new ApplyPatchDialog(manager) { Owner = this };
            dialog.ShowDialog();

            if (dialog.PatchWasApplied)
            {
                RefreshLookups();

                var builder = new BehaviorTreeBuilder(manager);
                ObjectTree.ItemsSource = new List<BehaviorNodeData> { builder.BuildTree("") };

                // Force SM Inspector refresh
                var currentSM = _selectedSM;
                _selectedSM = null;
                SelectedSM = currentSM; // triggers RefreshSmInspector via setter

                _originalSnapshot = TakeSnapshot();
                _snapshotEvents = EventList.Select(ev => ev.Name).ToList();
                _snapshotVars = VariableList.Select(v => v.Name).ToList();

                Stats.ObjectCount = manager.ObjectMap.Count;
                Stats.VariableCount = VariableList.Count;
                Stats.EventCount = EventList.Count;
                Stats.ClipCount = ClipList.Count;
                Stats.TransitionCount = TransitionList.Count;

                StatusText.Text = $"✓ Patch applied — {dialog.LastResult.AppliedCount} ops";
            }
        }
        private void AddTransition(HkObject tr, string fromName,
    Dictionary<string, string> stateIdToName,
    Dictionary<string, string> eventNames)
        {
            string Get(string n) => tr.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";

            var toStateId = Get("toStateId");
            var eventId = Get("eventId");
            var blend = Get("duration");
            var flags = Get("flags");
            var effect = Get("transition");

            stateIdToName.TryGetValue(toStateId, out var toName);
            eventNames.TryGetValue(eventId, out var evName);

            TransitionList.Add(new TransitionInfo
            {
                FromState = fromName,
                ToState = toName ?? $"ID:{toStateId}",
                EventId = eventId,
                EventName = evName ?? $"Event {eventId}",
                BlendDuration = blend,
                Flags = flags,
                TransitionEffect = effect
            });
        }

        private void ObjectTree_SelectedItemChanged(object sender,
    RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not BehaviorNodeData node || node.Object == null) return;

            // Push current to history before navigating
            if (BtnBackNavigation.Tag is string currentId &&
                !string.IsNullOrEmpty(currentId))
                _navigationHistory.Push(currentId);

            LoadObjectIntoEditor(node.Object);
            BtnBackNavigation.IsEnabled = _navigationHistory.Count > 0;

            // Update bookmark button
            bool isBookmarked = Bookmarks.Any(b => b.Id == node.Object.Id);
            BtnBookmarkToggle.Content = isBookmarked ? "★" : "🔖";
            BtnBookmarkToggle.Foreground = isBookmarked
                ? new SolidColorBrush(Colors.Goldenrod)
                : (SolidColorBrush)Application.Current.Resources["TextSecondaryBrush"];
            BtnBookmarkToggle.ToolTip = isBookmarked ? "Remove bookmark" : "Bookmark this object";
            BtnBookmarkToggle.Tag = node.Object;
        }

        private readonly HashSet<HkParam> _subscribedParams = new();

        private void SubscribeParamUndo(HkParam param, string className, string nodeName)
        {
            if (_subscribedParams.Contains(param)) return;
            _subscribedParams.Add(param);

            param.ValueChanged += (s, args) =>
            {
                if (_suppressUndoRecord || s is not HkParam p) return;

                // Ignore if values are the same or both null/empty
                if (args.OldValue == args.NewValue) return;
                if (string.IsNullOrEmpty(args.OldValue) && string.IsNullOrEmpty(args.NewValue)) return;

                // Ignore if this is just whitespace normalization
                if ((args.OldValue?.Trim() ?? "") == (args.NewValue?.Trim() ?? "")) return;

                var capturedOld = args.OldValue;
                var capturedNew = args.NewValue;
                var capturedParam = p;

                _undoRedo.Record(new EditAction
                {
                    Description = $"{nodeName}.{p.Name}: {args.OldValue} → {args.NewValue}",
                    Undo = () =>
                    {
                        _suppressUndoRecord = true;
                        capturedParam.Value = capturedOld;
                        _suppressUndoRecord = false;
                        UpdateUndoRedoButtons();
                    },
                    Redo = () =>
                    {
                        _suppressUndoRecord = true;
                        capturedParam.Value = capturedNew;
                        _suppressUndoRecord = false;
                        UpdateUndoRedoButtons();
                    }
                });
                UpdateUndoRedoButtons();
            };
        }

        private void BtnBookmarkToggle_Click(object sender, RoutedEventArgs e)
        {
            if (BtnBookmarkToggle.Tag is not HkObject obj) return;

            var existing = Bookmarks.FirstOrDefault(b => b.Id == obj.Id);
            if (existing != null)
            {
                Bookmarks.Remove(existing);
                BtnBookmarkToggle.Content = "🔖";
                BtnBookmarkToggle.Foreground = (SolidColorBrush)Application.Current.Resources["TextSecondaryBrush"];
                BtnBookmarkToggle.ToolTip = "Bookmark this object";
            }
            else
            {
                var name = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? obj.Id;
                Bookmarks.Add(new BookmarkEntry
                {
                    Id = obj.Id,
                    Name = name,
                    ClassName = obj.ClassName,
                    Label = ""
                });
                BtnBookmarkToggle.Content = "★";
                BtnBookmarkToggle.Foreground = new SolidColorBrush(Colors.Goldenrod);
                BtnBookmarkToggle.ToolTip = "Remove bookmark";
            }
            SaveBookmarks();
        }

        private void BookmarksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BookmarksList.SelectedItem is not BookmarkEntry bookmark) return;

            if (!manager.ObjectMap.TryGetValue(bookmark.Id, out var obj))
            {
                MessageBox.Show($"Object {bookmark.Id} not found in current file.");
                BookmarksList.SelectedItem = null;
                return;
            }

            // Load params
            SelectedClassName.Text = $"Class: {obj.ClassName}";
            ParamsEditor.ItemsSource = obj.Params;
            MainTabControl.SelectedIndex = 0; // switch to Object Data

            // Update bookmark button state
            ObjectNameLabel.Text = $"{bookmark.Name}  ·  {obj.ClassName}";
            BtnBookmarkToggle.Content = "★";
            BtnBookmarkToggle.Foreground = new SolidColorBrush(Colors.Goldenrod);
            BtnBookmarkToggle.ToolTip = "Remove bookmark";
            BtnBookmarkToggle.Tag = obj;

            // Navigate tree
            var root = ObjectTree.ItemsSource?.Cast<BehaviorNodeData>().FirstOrDefault();
            if (root != null)
            {
                var target = FindNodeById(root, bookmark.Id);
                if (target != null) SelectTreeNode(ObjectTree, target);
            }

            BookmarksList.SelectedItem = null; // deselect so clicking same item works again
        }

        private ContextMenu BookmarksMenu = new ContextMenu();

        private void BtnBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (ObjectTree.SelectedItem is not BehaviorNodeData node || node.Object == null)
            { MessageBox.Show("Select an object first."); return; }
            var name = node.Object.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? node.Object.Id;
            _bookmarkService.Add(node.Object.Id, name, node.Object.ClassName);
            StatusText.Text = $"✓ Bookmarked: {name}";
        }

        private void BtnBookmarks_Click(object sender, RoutedEventArgs e)
        {
            BookmarksMenu.Items.Clear();
            if (!Bookmarks.Any())
                BookmarksMenu.Items.Add(new MenuItem { Header = "(no bookmarks)", IsEnabled = false });
            else
                foreach (var b in Bookmarks)
                {
                    var item = new MenuItem { Header = b.Display };
                    item.Click += (s, ev) => NavigateToBookmarkById(b.Id);
                    BookmarksMenu.Items.Add(item);
                }
            BookmarksMenu.PlacementTarget = sender as Button;
            BookmarksMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            BookmarksMenu.IsOpen = true;
        }

        private void NavigateToBookmarkById(string id)
        {
            if (!manager.ObjectMap.TryGetValue(id, out var obj)) return;
            SelectedClassName.Text = $"Class: {obj.ClassName}";
            ParamsEditor.ItemsSource = obj.Params;
            MainTabControl.SelectedIndex = 0;
            var root = ObjectTree.ItemsSource?.Cast<BehaviorNodeData>().FirstOrDefault();
            if (root != null) { var t = FindNodeById(root, id); if (t != null) SelectTreeNode(ObjectTree, t); }
        }

        private void BtnRemoveBookmark_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not BookmarkEntry b) return;
            Bookmarks.Remove(b);
            SaveBookmarks();

            // Update bookmark button if this was the current object
            if (BtnBookmarkToggle.Tag is HkObject obj && obj.Id == b.Id)
            {
                BtnBookmarkToggle.Content = "🔖";
                BtnBookmarkToggle.Foreground = (SolidColorBrush)Application.Current.Resources["TextSecondaryBrush"];
                BtnBookmarkToggle.ToolTip = "Bookmark this object";
            }
        }

        private readonly BookmarkService _bookmarkService = new();

        // Replace Bookmarks references:
        public ObservableCollection<BookmarkEntry> Bookmarks => _bookmarkService.Bookmarks;

        // Replace SaveBookmarks():
        private void SaveBookmarks() => _bookmarkService.Save();

        // Replace LoadBookmarks():
        private void LoadBookmarks() => _bookmarkService.Load();

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (manager == null) return;
            var searchText = TxtSearch.Text.Trim();

            if (searchText.StartsWith("#"))
            {
                var found = manager.ObjectMap.TryGetValue(searchText, out var directObj);
                StatusText.Text = $"Looking for '{searchText}' — found: {found} — total objects: {manager.ObjectMap.Count}";

                if (found)
                {
                    SelectedClassName.Text = $"Class: {directObj.ClassName}";
                    ParamsEditor.ItemsSource = directObj.Params;
                    ObjectNameLabel.Text = $"{directObj.Id}  {directObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? directObj.Id}  ·  {directObj.ClassName}";
                    MainTabControl.SelectedIndex = 0;

                    foreach (var param in directObj.Params)
                        SubscribeParamUndo(param, directObj.ClassName,
                            directObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? directObj.Id);

                    var root = ObjectTree.ItemsSource?.Cast<BehaviorNodeData>().FirstOrDefault();
                    if (root != null)
                    {
                        var target = FindNodeById(root, directObj.Id);
                        if (target != null) SelectTreeNode(ObjectTree, target);
                    }
                    StatusText.Text = $"✓ Jumped to {directObj.Id}";
                }
                return;
            }

            var builder = new BehaviorTreeBuilder(manager);
            var filteredTree = builder.BuildTree(searchText);
            ObjectTree.ItemsSource = new List<BehaviorNodeData> { filteredTree };
        }
        private void BrowseAnimation_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not ClipInfo clip) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Animation File",
                Filter = "Havok Animation|*.hkx;*.HKX|All files|*.*",
                FileName = System.IO.Path.GetFileName(clip.AnimationPath ?? "")
            };

            // Try to set initial directory from existing path, anchored on the
            // configured game meshes folder (AnimationPath is stored relative to it).
            var anchor = AppSettings.MeshesPath;
            if (!string.IsNullOrEmpty(anchor) && !string.IsNullOrEmpty(clip.AnimationPath))
            {
                var dir = System.IO.Path.GetDirectoryName(
                    System.IO.Path.Combine(anchor, clip.AnimationPath.TrimStart('\\', '/')));
                if (System.IO.Directory.Exists(dir))
                    dlg.InitialDirectory = dir;
            }

            if (dlg.ShowDialog() == true)
            {
                // Try to make path relative to "Animations\" folder
                var full = dlg.FileName;
                var animIdx = full.IndexOf("Animations\\", StringComparison.OrdinalIgnoreCase);
                clip.AnimationPath = animIdx >= 0 ? full.Substring(animIdx) : full;
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (manager?.ObjectMap == null || manager.ObjectMap.Count == 0) return;

            // Default to saving in the same format as the source
            string defaultFilter = _sourceWasHkx
                ? "Skyrim SE HKX (64-bit)|*.hkx|Havok XML|*.xml"
                : "Havok XML|*.xml|Skyrim SE HKX (64-bit)|*.hkx";

            string defaultName = _sourceWasHkx
    ? Path.GetFileNameWithoutExtension(_originalHkxPath)
    : Path.GetFileNameWithoutExtension(Stats.FileName ?? "behavior");

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Havok Behavior File",
                Filter = defaultFilter,
                FileName = defaultName,
                InitialDirectory = _sourceWasHkx
                    ? Path.GetDirectoryName(_originalHkxPath)
                    : null
            };
            if (sfd.ShowDialog() != true) return;

            bool saveAsHkx = sfd.FileName.EndsWith(".hkx",
                StringComparison.OrdinalIgnoreCase);

            StatusText.Text = saveAsHkx ? "⏳ Saving as SE HKX…" : "⏳ Saving XML…";

            try
            {
                if (saveAsHkx)
                {
                    // Step 1 — serialize current state to a temp XML
                    var tmpXml = sfd.FileName + ".tmp.xml";
                    SerializeToFile(tmpXml);

                    // Step 2 — convert temp XML → SE HKX binary
                    await _hkxConv.XmlToHkxAsync(tmpXml, sfd.FileName);
                    File.Delete(tmpXml);

                    StatusText.Text = "✓ Saved as Skyrim SE HKX";
                }
                else
                {
                    SerializeToFile(sfd.FileName);
                    StatusText.Text = "✓ Saved as XML";
                }

                // If user saved as HKX, update source tracking
                if (saveAsHkx)
                {
                    _sourceWasHkx = true;
                    _originalHkxPath = sfd.FileName;
                }

                StatusText.Text = $"✓ Saved: {Path.GetFileName(sfd.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Save failed:\n{ex.Message}\n\n" +
                    "If saving as HKX, make sure the XML is valid Havok XML " +
                    "(not a converted-from-LE file).",
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Save failed";
            }
        }

        private void SerializeToFile(string path)
        {
            // Write variable values back
            var valueSet = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbVariableValueSet");
            if (valueSet != null)
            {
                var wordValuesParam = valueSet.Params
                    .FirstOrDefault(p => p.Name == "wordVariableValues");
                if (wordValuesParam?.Children != null)
                    for (int i = 0; i < wordValuesParam.Children.Count && i < VariableList.Count; i++)
                    {
                        var vp = wordValuesParam.Children[i].Params
                            .FirstOrDefault(p => p.Name == "value");
                        if (vp != null) vp.Value = EncodeHavokValue(VariableList[i].Value);
                    }
            }

            // Write animation paths back
            foreach (var clip in ClipList)
            {
                if (!manager.ObjectMap.TryGetValue(clip.Id, out var clipObj)) continue;
                var animParam = clipObj.Params.FirstOrDefault(p => p.Name == "animationName");
                if (animParam != null) animParam.Value = clip.AnimationPath;
            }

            // Write event names back
            var eventStringData = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            if (eventStringData != null)
            {
                var eParam = eventStringData.Params.FirstOrDefault(p => p.Name == "eventNames");
                if (eParam != null)
                {
                    eParam.Strings = EventList.Select(ev => ev.Name).ToList();
                    eParam.Value = string.Join("\n", eParam.Strings);
                }
            }

            // Serialize
            var packfile = new HkPackfile
            {
                TopLevelObject = "#0050",
                Sections = new List<HkSection>
        {
            new HkSection
            {
                Name    = "__data__",
                Objects = manager.ObjectMap.Values.OrderBy(o => o.Id).ToList()
            }
        }
            };

            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(HkPackfile));
            var tempPath = path + ".tmp";
            using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
                serializer.Serialize(writer, packfile);

            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);
        }

        private async void RecentFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string path)
                await LoadFileAsync(path);
        }

        private void BtnGroupedSummary_Click(object sender, RoutedEventArgs e) { /* Placeholder for your summary logic */ }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (manager.ObjectMap == null || manager.ObjectMap.Count == 0)
            {
                MessageBox.Show("No file loaded.");
                return;
            }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Summary",
                Filter = "CSV files|*.csv|Text file|*.txt",
                FileName = "behavior_summary"
            };

            if (sfd.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();
                bool isCsv = sfd.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                string sep = isCsv ? "," : "\t";

                // Helper to escape CSV values
                string Esc(string s) => isCsv ? $"\"{(s ?? "").Replace("\"", "\"\"")}\"" : (s ?? "");

                // ---- HEADER ----
                sb.AppendLine("Behavior Summary");
                sb.AppendLine($"File:        {Stats.FileName}");
                sb.AppendLine($"States:      {manager.ObjectMap.Values.Count(o => o.ClassName == "hkbStateMachineStateInfo")}");
                sb.AppendLine($"Clips:       {ClipList.Count}");
                sb.AppendLine($"Variables:   {VariableList.Count}");
                sb.AppendLine($"Events:      {EventList.Count}");
                sb.AppendLine($"Transitions: {TransitionList.Count}");
                sb.AppendLine($"Bindings:    {BindingList.Count}");
                sb.AppendLine($"Exported:    {DateTime.Now:yyyy-MM-dd HH:mm}");
                sb.AppendLine();

                // ---- VARIABLES ----
                sb.AppendLine("=== VARIABLES ===");
                sb.AppendLine(string.Join(sep, "Index", "Name", "Type", "Value", "RawValue"));
                for (int i = 0; i < VariableList.Count; i++)
                {
                    var v = VariableList[i];
                    sb.AppendLine(string.Join(sep,
                        Esc(i.ToString()),
                        Esc(v.Name),
                        Esc(v.VariableType.Replace("VARIABLE_TYPE_", "")),
                        Esc(v.Value),
                        Esc(v.RawValue)));
                }

                sb.AppendLine();

                // ---- EVENTS ----
                sb.AppendLine("=== EVENTS ===");
                sb.AppendLine(string.Join(sep, "Index", "Name"));
                foreach (var ev in EventList)
                {
                    sb.AppendLine(string.Join(sep,
                        Esc(ev.Id),
                        Esc(ev.Name)));
                }

                sb.AppendLine();

                // ---- CLIPS ----
                sb.AppendLine("=== CLIPS ===");
                sb.AppendLine(string.Join(sep, "Id", "Name", "AnimationPath", "Mode", "PlaybackSpeed"));
                foreach (var clip in ClipList)
                {
                    sb.AppendLine(string.Join(sep,
                        Esc(clip.Id),
                        Esc(clip.Name),
                        Esc(clip.AnimationPath),
                        Esc(clip.Mode),
                        Esc(clip.PlaybackSpeed)));
                }

                sb.AppendLine();

                // ---- TRANSITIONS ----
                sb.AppendLine("=== TRANSITIONS ===");
                sb.AppendLine(string.Join(sep, "FromState", "ToState", "Event", "BlendDuration", "Flags"));
                foreach (var tr in TransitionList)
                {
                    sb.AppendLine(string.Join(sep,
                        Esc(tr.FromState),
                        Esc(tr.ToState),
                        Esc(tr.EventName),
                        Esc(tr.BlendDuration),
                        Esc(tr.Flags)));
                }

                sb.AppendLine();

                // ---- BINDINGS ----
                sb.AppendLine("=== BINDINGS ===");
                sb.AppendLine(string.Join(sep, "Owner", "OwnerClass", "Variable", "VariableIndex", "MemberPath", "BindingType"));
                foreach (var b in BindingList)
                {
                    sb.AppendLine(string.Join(sep,
                        Esc(b.OwnerName),
                        Esc(b.OwnerClass),
                        Esc(b.VariableName),
                        Esc(b.VariableIndex),
                        Esc(b.MemberPath),
                        Esc(b.BindingType)));
                }

                System.IO.File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show($"Exported successfully!\n{VariableList.Count} variables, {EventList.Count} events, {ClipList.Count} clips, {TransitionList.Count} transitions, {BindingList.Count} bindings.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export error: " + ex.Message);
            }
        }

        private void BtnCompare_Click(object sender, RoutedEventArgs e)
        {
            if (manager.ObjectMap == null || manager.ObjectMap.Count == 0)
            {
                MessageBox.Show("Load a file first to use as File A.");
                return;
            }

            var dialog = new CompareDialog(
                manager,
                VariableList.ToList(),
                EventList.ToList(),
                ClipList.ToList());

            dialog.Owner = this;
            dialog.ObjectSelected += (id) =>
            {
                if (!manager.ObjectMap.TryGetValue(id, out var obj)) return;
                SelectedClassName.Text = $"Class: {obj.ClassName}";
                ParamsEditor.ItemsSource = obj.Params;
                MainTabControl.SelectedIndex = 0;
            };
            dialog.Show();
        }


        private void ClipsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ClipsList.SelectedItem is not ClipInfo clip) return;

            SelectedClip = clip;

            var root = ObjectTree.ItemsSource?.Cast<BehaviorNodeData>().FirstOrDefault();
            if (root == null) return;

            var target = FindNodeById(root, clip.Id);

            if (manager.ObjectMap.TryGetValue(clip.Id, out var obj))
            {
                LoadObjectIntoEditor(obj);     // ← replaces SelectedClassName + ParamsEditor lines; shows ▶ Preview

                if (MainTabControl.SelectedIndex != 0)
                    MainTabControl.SelectedIndex = 0;
            }

            if (target != null)
                SelectTreeNode(ObjectTree, target);
        }

        private void BindingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as ListBox)?.SelectedItem is not BindingEntry binding) return;
            if (!manager.ObjectMap.TryGetValue(binding.OwnerId, out var obj)) return;

            SelectedClassName.Text = $"Class: {obj.ClassName}";
            ParamsEditor.ItemsSource = obj.Params;
            MainTabControl.SelectedIndex = 0;

            var root = ObjectTree.ItemsSource?.Cast<BehaviorNodeData>().FirstOrDefault();
            if (root == null) return;
            var target = FindNodeById(root, binding.OwnerId);
            if (target != null) SelectTreeNode(ObjectTree, target);
        }

        private BehaviorNodeData FindNodeById(BehaviorNodeData node, string id)
        {
            if (node.Object?.Id == id) return node;
            foreach (var child in node.Children)
            {
                var found = FindNodeById(child, id);
                if (found != null) return found;
            }
            return null;
        }

        private void RefreshTriggers(ClipInfo clip)
        {
            TriggerList.Clear();
            if (!manager.ObjectMap.TryGetValue(clip.Id, out var clipObj)) return;

            var triggersRef = clipObj.Params.FirstOrDefault(p => p.Name == "triggers")?.Value;
            if (string.IsNullOrEmpty(triggersRef) || triggersRef == "null") return;
            if (!manager.TryResolve(triggersRef, out var triggerArrayObj)) return;

            var triggersParam = triggerArrayObj.Params.FirstOrDefault(p => p.Name == "triggers");
            if (triggersParam?.Children == null) return;

            var eventNamesLookup = EventList.ToDictionary(e => e.Id, e => e.Name);

            foreach (var tr in triggersParam.Children)
            {
                string Get(string n) => tr.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";
                var localTime = Get("localTime");
                var relToEnd = Get("relativeToEndOfClip").ToLower() == "true";
                var acyclic = Get("acyclic").ToLower() == "true";

                var eventParam = tr.Params.FirstOrDefault(p => p.Name == "event");
                var eventId = "";
                if (eventParam?.Children?.Count > 0)
                    eventId = eventParam.Children[0].Params
                        .FirstOrDefault(p => p.Name == "id")?.Value ?? "";

                eventNamesLookup.TryGetValue(eventId, out var evName);

                TriggerList.Add(new ClipTrigger
                {
                    ClipName = clip.Name,
                    LocalTime = localTime,
                    EventId = eventId,
                    EventName = evName ?? $"Event {eventId}",
                    RelativeToEnd = relToEnd,
                    Acyclic = acyclic
                });
            }

            if (TriggerList.Count == 0)
                TriggerList.Add(new ClipTrigger { EventName = "No triggers on this clip", LocalTime = "" });
        }

        private void RefreshVarUsages(IdNamePair variable)
        {
            UsageList.Clear();

            // Extract the index from the Id (format: "#objId_index")
            var parts = variable.Id.Split('_');
            if (parts.Length < 2) return;
            var varIndex = parts.Last();

            // Scan all hkbVariableBindingSet objects
            foreach (var obj in manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbVariableBindingSet"))
            {
                var bindingsParam = obj.Params.FirstOrDefault(p => p.Name == "bindings");
                if (bindingsParam?.Children == null) continue;

                foreach (var binding in bindingsParam.Children)
                {
                    string Get(string n) => binding.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";
                    var bindingVarIndex = Get("variableIndex");
                    if (bindingVarIndex != varIndex) continue;

                    var memberPath = Get("memberPath");

                    // Find the object that owns this binding set
                    var owner = manager.ObjectMap.Values.FirstOrDefault(o =>
                        o.Params.Any(p => p.Value == obj.Id));

                    var ownerName = owner?.Params.FirstOrDefault(p => p.Name == "name")?.Value
                                    ?? owner?.Id ?? obj.Id;
                    var ownerClass = owner?.ClassName ?? "Unknown";

                    UsageList.Add(new VariableUsage
                    {
                        VariableName = variable.Name,
                        UsedBy = ownerName,
                        UsedById = owner?.Id ?? obj.Id,
                        ClassName = ownerClass,
                        Property = memberPath,
                        BindingType = "VariableBinding"
                    });
                }
            }

            // Also scan hkbExpressionCondition and hkbBoolVariableSequencedData for direct references
            foreach (var obj in manager.ObjectMap.Values)
            {
                foreach (var param in obj.Params)
                {
                    if (param.Name == "variableIndex" && param.Value == varIndex)
                    {
                        var ownerName = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? obj.Id;
                        UsageList.Add(new VariableUsage
                        {
                            VariableName = variable.Name,
                            UsedBy = ownerName,
                            UsedById = obj.Id,
                            ClassName = obj.ClassName,
                            Property = param.Name,
                            BindingType = "Direct"
                        });
                    }
                }
            }

            if (UsageList.Count == 0)
            {
                UsageList.Add(new VariableUsage
                {
                    VariableName = variable.Name,
                    UsedBy = "No usages found",
                    ClassName = "",
                    Property = "",
                    BindingType = ""
                });
            }
        }

        private void RefreshEventUsages(IdNamePair ev)
        {
            EventUsageList.Clear();
            if (ev == null) return;

            var eventIndex = ev.Id; // "0", "1", "18" etc.

            // ── Transitions ───────────────────────────────────────────────
            foreach (var stateObj in manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachineStateInfo"))
            {
                var fromName = stateObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? stateObj.Id;
                var transRef = stateObj.Params.FirstOrDefault(p => p.Name == "transitions")?.Value;
                if (string.IsNullOrEmpty(transRef) || transRef == "null") continue;
                if (!manager.TryResolve(transRef, out var transArray)) continue;

                var tp = transArray.Params.FirstOrDefault(p => p.Name == "transitions");
                if (tp?.Children == null) continue;

                foreach (var tr in tp.Children)
                {
                    var trEventId = tr.Params.FirstOrDefault(p => p.Name == "eventId")?.Value;
                    if (trEventId != eventIndex) continue;

                    var toStateId = tr.Params.FirstOrDefault(p => p.Name == "toStateId")?.Value ?? "";
                    // resolve toState name
                    var toStateObj = manager.ObjectMap.Values.FirstOrDefault(o =>
                        o.ClassName == "hkbStateMachineStateInfo" &&
                        o.Params.FirstOrDefault(p => p.Name == "stateId")?.Value == toStateId);
                    var toName = toStateObj?.Params.FirstOrDefault(p => p.Name == "name")?.Value
                                 ?? $"stateId:{toStateId}";

                    EventUsageList.Add(new EventUsageEntry
                    {
                        UsageType = "Transition",
                        Description = $"{fromName}  →  {toName}",
                        ObjectId = stateObj.Id,
                        ClassName = "hkbStateMachineStateInfo"
                    });
                }
            }

            // ── Wildcard transitions ──────────────────────────────────────
            foreach (var sm in manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine"))
            {
                var wcRef = sm.Params.FirstOrDefault(p => p.Name == "wildcardTransitions")?.Value;
                if (string.IsNullOrEmpty(wcRef) || wcRef == "null") continue;
                if (!manager.TryResolve(wcRef, out var wcArray)) continue;

                var wtp = wcArray.Params.FirstOrDefault(p => p.Name == "transitions");
                if (wtp?.Children == null) continue;

                var smName = sm.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? sm.Id;

                foreach (var tr in wtp.Children)
                {
                    var trEventId = tr.Params.FirstOrDefault(p => p.Name == "eventId")?.Value;
                    if (trEventId != eventIndex) continue;

                    var toStateId = tr.Params.FirstOrDefault(p => p.Name == "toStateId")?.Value ?? "";
                    var toStateObj = manager.ObjectMap.Values.FirstOrDefault(o =>
                        o.ClassName == "hkbStateMachineStateInfo" &&
                        o.Params.FirstOrDefault(p => p.Name == "stateId")?.Value == toStateId);
                    var toName = toStateObj?.Params.FirstOrDefault(p => p.Name == "name")?.Value
                                 ?? $"stateId:{toStateId}";

                    EventUsageList.Add(new EventUsageEntry
                    {
                        UsageType = "Wildcard",
                        Description = $"★ {smName}  →  {toName}",
                        ObjectId = sm.Id,
                        ClassName = "hkbStateMachine"
                    });
                }
            }

            // ── Clip triggers ─────────────────────────────────────────────
            foreach (var clipObj in manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbClipGenerator"))
            {
                var clipName = clipObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? clipObj.Id;
                var trigRef = clipObj.Params.FirstOrDefault(p => p.Name == "triggers")?.Value;
                if (string.IsNullOrEmpty(trigRef) || trigRef == "null") continue;
                if (!manager.TryResolve(trigRef, out var trigArray)) continue;

                var tp = trigArray.Params.FirstOrDefault(p => p.Name == "triggers");
                if (tp?.Children == null) continue;

                foreach (var tr in tp.Children)
                {
                    var eventParam = tr.Params.FirstOrDefault(p => p.Name == "event");
                    if (eventParam?.Children?.Count == 0) continue;
                    var triggerId = eventParam?.Children?[0].Params
                        .FirstOrDefault(p => p.Name == "id")?.Value;
                    if (triggerId != eventIndex) continue;

                    var localTime = tr.Params.FirstOrDefault(p => p.Name == "localTime")?.Value ?? "?";
                    EventUsageList.Add(new EventUsageEntry
                    {
                        UsageType = "Trigger",
                        Description = $"{clipName}  at t={localTime}",
                        ObjectId = clipObj.Id,
                        ClassName = "hkbClipGenerator"
                    });
                }
            }

            // ── Generic param scan (enterEventId, exitEventId, etc.) ──────
            var eventParamNames = new HashSet<string>
    {
        "enterEventId", "exitEventId", "returnToPreviousStateEvent",
        "randomTransitionEventId", "transitionToNextHigherStateEventId",
        "transitionToNextLowerStateEventId", "syncVariableIndex"
    };

            foreach (var obj in manager.ObjectMap.Values)
            {
                var objName = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? obj.Id;
                foreach (var param in obj.Params)
                {
                    if (!eventParamNames.Contains(param.Name)) continue;
                    if (param.Value != eventIndex) continue;

                    EventUsageList.Add(new EventUsageEntry
                    {
                        UsageType = "Property",
                        Description = $"{objName}  [{param.Name}]",
                        ObjectId = obj.Id,
                        ClassName = obj.ClassName
                    });
                }
            }

            if (EventUsageList.Count == 0)
                EventUsageList.Add(new EventUsageEntry
                {
                    UsageType = "",
                    Description = "No usages found",
                    ObjectId = null,
                    ClassName = ""
                });
        }

        private void EventUsagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as ListBox)?.SelectedItem is not EventUsageEntry usage) return;
            if (string.IsNullOrEmpty(usage.ObjectId)) return;
            if (!manager.ObjectMap.TryGetValue(usage.ObjectId, out var obj)) return;
            LoadObjectIntoEditor(obj);
            MainTabControl.SelectedIndex = 0;
            var root = ObjectTree.ItemsSource?.Cast<BehaviorNodeData>().FirstOrDefault();
            if (root != null)
            {
                var target = FindNodeById(root, usage.ObjectId);
                if (target != null) SelectTreeNode(ObjectTree, target);
            }
        }

        private void EventFindUsages_Click(object sender, RoutedEventArgs e)
        {
            if (EventsListBox.SelectedItem is IdNamePair ev)
                SelectedEvent = ev;
        }

        public ObservableCollection<TransitionDetail> TransitionDetailList { get; set; } = new();

        private TransitionInfo _selectedTransition;
        public TransitionInfo SelectedTransition
        {
            get => _selectedTransition;
            set
            {
                _selectedTransition = value;
                if (value != null) RefreshTransitionDetail(value);
            }
        }

        private void RefreshTransitionDetail(TransitionInfo tr)
        {
            TransitionDetailList.Clear();

            // ── Basic info ────────────────────────────────────────────────
            TransitionDetailList.Add(new TransitionDetail
            { Label = "From State", Value = tr.FromState });
            TransitionDetailList.Add(new TransitionDetail
            { Label = "To State", Value = tr.ToState });
            TransitionDetailList.Add(new TransitionDetail
            { Label = "Event", Value = $"{tr.EventName}  (id {tr.EventId})" });
            TransitionDetailList.Add(new TransitionDetail
            { Label = "Blend", Value = string.IsNullOrEmpty(tr.BlendDuration) ? "0" : tr.BlendDuration });
            TransitionDetailList.Add(new TransitionDetail
            { Label = "Flags", Value = tr.Flags });

            if (string.IsNullOrEmpty(tr.TransitionEffect) || tr.TransitionEffect == "null")
                return;

            if (!manager.TryResolve(tr.TransitionEffect, out var effectObj)) return;

            // ── Transition effect details ─────────────────────────────────
            string Get(string n) => effectObj.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";

            var duration = Get("duration");
            var blendCurve = Get("blendCurve");
            var syncPoint = Get("syncPoint");
            var toNestedSt = Get("toNestedStateId");
            var fromNestedSt = Get("fromNestedStateId");
            var priority = Get("priority");
            var initSyncPt = Get("initiateInterval");

            if (!string.IsNullOrEmpty(duration))
                TransitionDetailList.Add(new TransitionDetail
                { Label = "Blend Duration", Value = duration });
            if (!string.IsNullOrEmpty(blendCurve))
                TransitionDetailList.Add(new TransitionDetail
                { Label = "Blend Curve", Value = blendCurve });
            if (!string.IsNullOrEmpty(syncPoint))
                TransitionDetailList.Add(new TransitionDetail
                { Label = "Sync Point", Value = syncPoint });
            if (toNestedSt != "0" && !string.IsNullOrEmpty(toNestedSt))
                TransitionDetailList.Add(new TransitionDetail
                { Label = "To Nested State", Value = toNestedSt });
            if (fromNestedSt != "0" && !string.IsNullOrEmpty(fromNestedSt))
                TransitionDetailList.Add(new TransitionDetail
                { Label = "From Nested State", Value = fromNestedSt });
            if (priority != "0" && !string.IsNullOrEmpty(priority))
                TransitionDetailList.Add(new TransitionDetail
                { Label = "Priority", Value = priority });

            // ── Condition ─────────────────────────────────────────────────
            var condRef = tr.TransitionEffect != null
                ? effectObj.Params.FirstOrDefault(p => p.Name == "condition")?.Value
                : null;

            if (!string.IsNullOrEmpty(condRef) && condRef != "null"
                && manager.TryResolve(condRef, out var condObj))
            {
                var condClass = condObj.ClassName ?? "";
                TransitionDetailList.Add(new TransitionDetail
                { Label = "──", Value = "Condition" });

                // hkbExpressionCondition
                var expr = condObj.Params.FirstOrDefault(p => p.Name == "expression")?.Value;
                if (!string.IsNullOrEmpty(expr))
                {
                    // Substitute variable indices with names where possible
                    var resolved = System.Text.RegularExpressions.Regex.Replace(expr,
                        @"\bvar\[(\d+)\]", m =>
                        {
                            var idx = m.Groups[1].Value;
                            var vn = VariableList.FirstOrDefault(v => v.Index.ToString() == idx)?.Name;
                            return vn != null ? vn : m.Value;
                        });
                    TransitionDetailList.Add(new TransitionDetail
                    { Label = "Expression", Value = resolved, ObjectId = condObj.Id });
                }

                // hkbBoolVariableCondition / hkbVariableCondition
                var varIdx = condObj.Params.FirstOrDefault(p => p.Name == "variableIndex")?.Value;
                if (!string.IsNullOrEmpty(varIdx))
                {
                    var varName = VariableList.FirstOrDefault(v => v.Index.ToString() == varIdx)?.Name
                                  ?? $"var[{varIdx}]";
                    var compareVal = condObj.Params.FirstOrDefault(p =>
                        p.Name == "value" || p.Name == "compareValue")?.Value ?? "?";
                    var op = condObj.Params.FirstOrDefault(p => p.Name == "operation")?.Value ?? "==";
                    TransitionDetailList.Add(new TransitionDetail
                    {
                        Label = "Condition",
                        Value = $"{varName}  {op}  {compareVal}",
                        ObjectId = condObj.Id
                    });
                }

                TransitionDetailList.Add(new TransitionDetail
                { Label = "Cond. Class", Value = condClass, ObjectId = condObj.Id });
            }

            // ── Trigger / initiate interval ───────────────────────────────
            var trigIntervalRef = effectObj.Params
                .FirstOrDefault(p => p.Name == "triggerInterval")?.Children?.FirstOrDefault();
            if (trigIntervalRef != null)
            {
                string TGet(string n) =>
                    trigIntervalRef.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";
                var enter = TGet("enterEventId");
                var exit = TGet("exitEventId");
                var tEnter = TGet("enterTime");
                var tExit = TGet("exitTime");

                if (enter != "-1" || exit != "-1")
                {
                    TransitionDetailList.Add(new TransitionDetail
                    { Label = "──", Value = "Trigger Interval" });
                    if (enter != "-1")
                    {
                        var evName = EventList.FirstOrDefault(e => e.Id == enter)?.Name ?? $"event[{enter}]";
                        TransitionDetailList.Add(new TransitionDetail
                        { Label = "Enter Event", Value = $"{evName}  at t={tEnter}" });
                    }
                    if (exit != "-1")
                    {
                        var evName = EventList.FirstOrDefault(e => e.Id == exit)?.Name ?? $"event[{exit}]";
                        TransitionDetailList.Add(new TransitionDetail
                        { Label = "Exit Event", Value = $"{evName}  at t={tExit}" });
                    }
                }
            }
        }

        private void TransitionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as ListBox)?.SelectedItem is TransitionInfo tr)
                SelectedTransition = tr;
        }

        private void TransitionDetail_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as ListBox)?.SelectedItem is not TransitionDetail detail) return;
            if (string.IsNullOrEmpty(detail.ObjectId)) return;
            if (!manager.ObjectMap.TryGetValue(detail.ObjectId, out var obj)) return;
            LoadObjectIntoEditor(obj);
            MainTabControl.SelectedIndex = 0;
        }

        private bool SelectTreeNode(ItemsControl container, BehaviorNodeData target)
        {
            foreach (var item in container.Items)
            {
                if (item is not BehaviorNodeData node) continue;

                var tvi = container.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (tvi == null) continue;

                if (node == target)
                {
                    tvi.IsSelected = true;
                    tvi.BringIntoView();
                    return true;
                }

                tvi.IsExpanded = true;
                tvi.UpdateLayout(); // force child containers to generate

                if (SelectTreeNode(tvi, target))
                    return true;
            }
            return false;
        }

        private DebuggerWindow _debuggerWindow;

        private void WireDebugPanel()
        {
            DebugTabPanel.DataContext = GraphView.DebugVM;
        }

        private void BtnDetachDebugger_Click(object sender, RoutedEventArgs e)
        {
            if (_debuggerWindow == null)
            {
                _debuggerWindow = new DebuggerWindow(GraphView.DebugVM, this);
                _debuggerWindow.ReturnToDock = () =>
                {
                    DebuggerTab.Header = "🎮 Debugger";
                    BtnDetachDebugger.Content = "⧉ Pop Out";
                };
            }

            if (_debuggerWindow.IsVisible)
            {
                _debuggerWindow.Hide();
                DebuggerTab.Header = "🎮 Debugger";
                BtnDetachDebugger.Content = "⧉ Pop Out";
            }
            else
            {
                _debuggerWindow.Show();
                DebuggerTab.Header = "🎮 Debugger ⧉";
                BtnDetachDebugger.Content = "↩ Dock";
            }
        }

        private void BtnGlobalSearch_Click(object sender, RoutedEventArgs e)
        {
            if (manager.ObjectMap == null || manager.ObjectMap.Count == 0)
            {
                MessageBox.Show("No file loaded.");
                return;
            }

            var dialog = new GlobalSearchDialog(manager, EventList.ToList());
            dialog.Owner = this;
            dialog.ObjectSelected += (id) =>
            {
                if (id == null) return; // Events/Variables fire null to signal tab switch only
                if (!manager.ObjectMap.TryGetValue(id, out var obj)) return;
                LoadObjectIntoEditor(obj);
                MainTabControl.SelectedIndex = 0;
                var root = ObjectTree.ItemsSource?.Cast<BehaviorNodeData>().FirstOrDefault();
                if (root != null)
                {
                    var target = FindNodeById(root, id);
                    if (target != null) SelectTreeNode(ObjectTree, target);
                }
            };
            dialog.NavigateToEvent += (idxStr) =>
            {
                if (!idxStr.StartsWith("idx:")) return;
                if (!int.TryParse(idxStr.Substring(4), out int idx)) return;
                if (idx < 0 || idx >= EventList.Count) return;

                EventFilter = "";
                MainTabControl.SelectedIndex = 4;

                Dispatcher.InvokeAsync(() =>
                {
                    var target = EventList.FirstOrDefault(ev => ev.Id == idx.ToString());
                    if (target == null) return;
                    EventsListBox.SelectedItem = target;
                    EventsListBox.ScrollIntoView(target);
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            };

            dialog.NavigateToVariable += (name) =>
            {
                var match = VariableList.FirstOrDefault(v =>
                    v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match == null) return;

                VariableFilter = "";

                MainTabControl.SelectedIndex = 2;

                Dispatcher.InvokeAsync(() =>
                {
                    VariablesList.SelectedItem = match;
                    VariablesList.ScrollIntoView(match);
                    SelectedVariable = match;
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            };
            dialog.Show();
        }

        public ICollectionView EventsView { get; private set; }

        private string _eventFilter = "";
        public string EventFilter
        {
            get => _eventFilter;
            set { _eventFilter = value; EventsView?.Refresh(); }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SkyrimHavokEditor.UI.Dialogs.SettingsDialog(this);
            if (dlg.ShowDialog() != true) return;

            if (dlg.PathsChanged)
            {
                InitializeSkeletonRegistry();   // reload skeletons from the new path
                StatusText.Text = $"✓ Settings saved — game path: {AppSettings.GamePath}";
            }
            else StatusText.Text = "✓ Settings saved";

            if (dlg.ThemeChanged)
                ApplyTheme(AppSettings.IsDarkMode);   // see step 4
        }

        private void ApplyTheme(bool dark)
        {
            _isDarkMode = dark;
            var dict = new ResourceDictionary
            {
                Source = new Uri(
                    _isDarkMode ? "UI/Themes/DarkTheme.xaml" : "UI/Themes/LightTheme.xaml",
                    UriKind.Relative)
            };
            Application.Current.Resources.MergedDictionaries[0] = dict;
            Background = (SolidColorBrush)Application.Current.Resources["BgDarkBrush"];
            UpdateCanvasBackground();
        }

        private void BtnEditStates_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not HkParam statesParam) return;

            // Find the parent object that owns this param
            var parentObj = manager.ObjectMap.Values.FirstOrDefault(o =>
                o.Params.Contains(statesParam));
            if (parentObj == null) return;

            // Get all available states in the file
            var allStates = manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachineStateInfo")
                .OrderBy(o => o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id)
                .ToList();

            // Get current state IDs from the param value
            var currentIds = (statesParam.Value ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            var dialog = new SkyrimHavokEditor.UI.Dialogs.StatesEditorDialog(allStates, currentIds, manager);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true)
            {
                var oldValue = statesParam.Value;
                var newIds = dialog.ResultIds;
                var newValue = string.Join(" ", newIds);

                statesParam.Value = newValue;

                // Update numelements
                statesParam.NumElements = newIds.Count.ToString();

                _undoRedo.Record(new EditAction
                {
                    Description = $"Edit states of {parentObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? parentObj.Id}",
                    Undo = () =>
                    {
                        _suppressUndoRecord = true;
                        statesParam.Value = oldValue;
                        statesParam.NumElements = oldValue.Split(' ',
                            StringSplitOptions.RemoveEmptyEntries).Length.ToString();
                        _suppressUndoRecord = false;
                        UpdateUndoRedoButtons();
                    },
                    Redo = () =>
                    {
                        _suppressUndoRecord = true;
                        statesParam.Value = newValue;
                        statesParam.NumElements = newIds.Count.ToString();
                        _suppressUndoRecord = false;
                        UpdateUndoRedoButtons();
                    }
                });
                UpdateUndoRedoButtons();
                StatusText.Text = $"✓ States updated ({newIds.Count} states)";
            }
        }

        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            bool ctrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            bool shift = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);

            if (!ctrl) return;

            switch (e.Key)
            {
                case System.Windows.Input.Key.S:
                    BtnSave_Click(null, null);
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.O:
                    BtnLoad_Click(null, null);
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.Z:
                    if (shift)
                    {
                        // Ctrl+Shift+Z = Redo
                        if (_undoRedo.CanRedo)
                        {
                            _undoRedo.Redo();
                            UpdateUndoRedoButtons();
                        }
                    }
                    else
                    {
                        // Ctrl+Z = Undo
                        if (_undoRedo.CanUndo)
                        {
                            _undoRedo.Undo();
                            UpdateUndoRedoButtons();
                        }
                    }
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.Y:
                    // Ctrl+Y = Redo (alternative)
                    if (_undoRedo.CanRedo)
                    {
                        _undoRedo.Redo();
                        UpdateUndoRedoButtons();
                    }
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.F:
                    // Ctrl+F = focus the active search box
                    FocusActiveSearchBox();
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.E:
                    // Ctrl+E = Export CSV
                    BtnExport_Click(null, null);
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.G:
                    BtnGlobalSearch_Click(null, null);
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.B:
                    if (shift) BtnBookmarks_Click(null, null);
                    else BtnBookmark_Click(null, null);
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.F2:
                    if (MainTabControl.SelectedIndex == 0)
                        GraphView.RequestRenameSelected();
                    e.Handled = true;
                    break;

            }
        }

        private void FocusActiveSearchBox()
        {
            switch (MainTabControl.SelectedIndex)
            {
                case 1: VarSearchBox.Focus(); VarSearchBox.SelectAll(); break;
                case 2: ClipSearchBox.Focus(); ClipSearchBox.SelectAll(); break;
                default: TxtSearch.Focus(); TxtSearch.SelectAll(); break;
            }
        }

        private string DecodeHavokValue(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue)) return "0";

            // Try to parse the integer bit pattern (handles negative too)
            if (long.TryParse(rawValue, out long longVal))
            {
                int intVal = (int)longVal;

                // Heuristic: Values like 1, 2, 3 are indices/bools. 
                // Bit patterns for floats like 0.1, 1.0, etc., are huge (> 1 million).
                if (Math.Abs(intVal) > 1000000 || intVal < 0)
                {
                    float f = BitConverter.Int32BitsToSingle(intVal);

                    // Check if it's a valid float (not NaN or Infinity)
                    if (!float.IsNaN(f) && !float.IsInfinity(f))
                    {
                        return f.ToString("0.###", CultureInfo.InvariantCulture);
                    }
                }
                return intVal.ToString(); // It's a plain integer (0, 1, 2, etc.)
            }

            return rawValue; // Fallback
        }

        private HavokValidator _validator;


        private void BtnValidate_Click(object sender, RoutedEventArgs e)
        {
            var issues = _validator.RunValidation();
            var dialog = new ValidationDialog(issues);
            dialog.Owner = this;
            dialog.ObjectSelected += (id) =>
            {
                if (!manager.ObjectMap.TryGetValue(id, out var obj)) return;
                SelectedClassName.Text = $"Class: {obj.ClassName}";
                ParamsEditor.ItemsSource = obj.Params;
                MainTabControl.SelectedIndex = 0;

                var root = ObjectTree.ItemsSource?.Cast<BehaviorNodeData>().FirstOrDefault();
                if (root != null)
                {
                    var target = FindNodeById(root, id);
                    if (target != null) SelectTreeNode(ObjectTree, target);
                }
            };

            dialog.Show();
        }

        // ── EVENTS: selection → enable Delete button ──────────────────────────────
        private void EventsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var ev = EventsListBox.SelectedItem as IdNamePair;
            BtnDeleteEvent.IsEnabled = ev != null;
            if (ev != null) SelectedEvent = ev;
        }

        // ── EVENTS: inline name edit ─────────────────────────────────────────────────
        private void EventName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || tb.Tag is not IdNamePair ev) return;
            var newName = tb.Text?.Trim() ?? "";
            var oldName = ev.Name;
            if (oldName == newName) return;

            var capturedOld = oldName;
            var capturedNew = newName;
            var capturedItem = ev;
            ev.Name = newName;

            _undoRedo.Record(new EditAction
            {
                Description = $"Rename event '{capturedOld}' → '{capturedNew}'",
                Undo = () => { _suppressUndoRecord = true; capturedItem.Name = capturedOld; _suppressUndoRecord = false; UpdateUndoRedoButtons(); },
                Redo = () => { _suppressUndoRecord = true; capturedItem.Name = capturedNew; _suppressUndoRecord = false; UpdateUndoRedoButtons(); }
            });
            UpdateUndoRedoButtons();
            StatusText.Text = $"✓ Renamed '{capturedOld}' → '{capturedNew}'";
        }

        // ── EVENTS: inline delete button ─────────────────────────────────────────────
        private void BtnDeleteEventInline_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not IdNamePair target) return;
            EventsListBox.SelectedItem = target;
            BtnDeleteEvent_Click(null, null);
        }

        // ── EVENTS: Add ───────────────────────────────────────────────────────────
        private void BtnAddEvent_Click(object sender, RoutedEventArgs e)
        {
            var newId = EventList.Count.ToString();
            var newEvent = new IdNamePair { Id = newId, Name = $"NewEvent_{newId}" };

            EventList.Add(newEvent);

            _undoRedo.Record(new EditAction
            {
                Description = $"Add event '{newEvent.Name}'",
                Undo = () => { _suppressUndoRecord = true; EventList.Remove(newEvent); RenumberEvents(); _suppressUndoRecord = false; UpdateUndoRedoButtons(); },
                Redo = () => { _suppressUndoRecord = true; EventList.Add(newEvent); RenumberEvents(); _suppressUndoRecord = false; UpdateUndoRedoButtons(); }
            });
            UpdateUndoRedoButtons();

            EventsListBox.SelectedItem = newEvent;
            EventsListBox.ScrollIntoView(newEvent);

            // Focus the name TextBox inside the new item
            Dispatcher.InvokeAsync(() =>
            {
                var container = EventsListBox.ItemContainerGenerator
                    .ContainerFromItem(newEvent) as ListBoxItem;
                var tb = container?.FindVisualChild<TextBox>();
                tb?.Focus();
                tb?.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            StatusText.Text = $"✓ Event added (index {newId})";
        }

        // ── EVENTS: Delete ────────────────────────────────────────────────────────
        private void BtnDeleteEvent_Click(object sender, RoutedEventArgs e)
        {
            if (EventsListBox.SelectedItem is not IdNamePair target) return;

            var capturedIndex = EventList.IndexOf(target);
            var capturedEvent = target;

            if (MessageBox.Show(
                    $"Delete event '{target.Name}' (index {target.Id})?\n\nWarning: transitions referencing this event by ID will be affected.",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            EventList.Remove(target);
            RenumberEvents();

            _undoRedo.Record(new EditAction
            {
                Description = $"Delete event '{capturedEvent.Name}'",
                Undo = () => { _suppressUndoRecord = true; int at = Math.Min(capturedIndex, EventList.Count); EventList.Insert(at, capturedEvent); RenumberEvents(); _suppressUndoRecord = false; UpdateUndoRedoButtons(); },
                Redo = () => { _suppressUndoRecord = true; EventList.Remove(capturedEvent); RenumberEvents(); _suppressUndoRecord = false; UpdateUndoRedoButtons(); }
            });
            UpdateUndoRedoButtons();

            BtnDeleteEvent.IsEnabled = false;
            StatusText.Text = $"✓ Event '{capturedEvent.Name}' deleted";
        }

        // ── EVENTS: Inline edit committed → record undo ──────────────────────────
        private void EventsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // Only care about committed edits to the Name column
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.Column.Header?.ToString() != "Name") return;
            if (e.Row.Item is not IdNamePair edited) return;

            // The TextBox still holds the *new* value; the binding hasn't flushed yet
            var newName = (e.EditingElement as TextBox)?.Text ?? edited.Name;
            var oldName = edited.Name;

            if (oldName == newName) return;

            // Flush the value now so undo captures the right state
            var capturedOld = oldName;
            var capturedNew = newName;
            var capturedItem = edited;

            _undoRedo.Record(new EditAction
            {
                Description = $"Rename event '{capturedOld}' → '{capturedNew}'",
                Undo = () =>
                {
                    _suppressUndoRecord = true;
                    capturedItem.Name = capturedOld;
                    _suppressUndoRecord = false;
                    UpdateUndoRedoButtons();
                },
                Redo = () =>
                {
                    _suppressUndoRecord = true;
                    capturedItem.Name = capturedNew;
                    _suppressUndoRecord = false;
                    UpdateUndoRedoButtons();
                }
            });
            UpdateUndoRedoButtons();

            StatusText.Text = $"✓ Event renamed: '{capturedOld}' → '{capturedNew}'";
        }

        // ── Helper: keep Id values sequential after add/delete ───────────────────
        private void RenumberEvents()
        {
            for (int i = 0; i < EventList.Count; i++)
                EventList[i].Id = i.ToString();
        }



        // ── SM Inspector collections ──────────────────────────────────────────────
        public ObservableCollection<HkObject> SmList { get; set; } = new();
        public ObservableCollection<SmTransitionRow> SmTransitionRows { get; set; } = new();

        // States valid in the currently selected SM — used for ToState dropdown
        public ObservableCollection<IdNamePair> SmStateOptions { get; set; } = new();

        private HkObject _selectedSM;
        public HkObject SelectedSM
        {
            get => _selectedSM;
            set { _selectedSM = value; if (value != null) RefreshSmInspector(value); }
        }

        private SmTransitionRow _selectedSmRow;
        public SmTransitionRow SelectedSmRow
        {
            get => _selectedSmRow;
            set { _selectedSmRow = value; }
        }

        // ── Find the hkbStateMachine that directly owns a given state object ──────
        private HkObject FindParentSM(string stateObjectId)
        {
            return manager.ObjectMap.Values.FirstOrDefault(o =>
                o.ClassName == "hkbStateMachine" &&
                (o.Params.FirstOrDefault(p => p.Name == "states")
                    ?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(stateObjectId) ?? false));
        }

        // ── Build the SM list (call after RefreshLookups) ─────────────────────────
        private void RefreshSmList()
        {
            SmList.Clear();
            foreach (var sm in manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine")
                .OrderBy(o => o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id))
            {
                SmList.Add(sm);
            }
        }

        // ── Populate the SM Inspector for a chosen state machine ─────────────────
        private void RefreshSmInspector(HkObject sm)
        {
            SmTransitionRows.Clear();
            SmStateOptions.Clear();

            var smName = sm.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? sm.Id;
            var eventLookup = EventList.ToDictionary(e => e.Id, e => e.Name);

            var stateIdToName = new Dictionary<string, string>();
            var statesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
            if (statesParam == null) return;

            // Build stateId → name map and dropdown options
            foreach (var stateRef in statesParam.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!manager.TryResolve(stateRef, out var stateObj)) continue;
                var sid = stateObj.Params.FirstOrDefault(p => p.Name == "stateId")?.Value ?? "";
                var name = stateObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? stateObj.Id;
                stateIdToName[sid] = name;
                SmStateOptions.Add(new IdNamePair { Id = sid, Name = $"{name}  (id {sid})" });
            }

            // ── Walk every state's own transition array ───────────────────────────
            foreach (var stateRef in statesParam.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!manager.TryResolve(stateRef, out var stateObj)) continue;
                var fromName = stateObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? stateObj.Id;
                var transRef = stateObj.Params.FirstOrDefault(p => p.Name == "transitions")?.Value;
                if (string.IsNullOrEmpty(transRef) || transRef == "null") continue;
                if (!manager.TryResolve(transRef, out var transArray)) continue;

                var transitionsParam = transArray.Params.FirstOrDefault(p => p.Name == "transitions");
                if (transitionsParam?.Children == null) continue;

                foreach (var tr in transitionsParam.Children)
                {
                    string Get(string n) => tr.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";
                    var toStateId = Get("toStateId");
                    var eventId = Get("eventId");
                    var flags = Get("flags");
                    var effect = Get("transition");

                    var blendDuration = "";
                    HkObject resolvedEffect = null;
                    if (!string.IsNullOrEmpty(effect) && effect != "null"
                        && manager.TryResolve(effect, out var effectObj))
                    {
                        blendDuration = effectObj.Params
                            .FirstOrDefault(p => p.Name == "duration")?.Value ?? "";
                        resolvedEffect = effectObj;
                    }

                    stateIdToName.TryGetValue(toStateId, out var toName);
                    eventLookup.TryGetValue(eventId, out var evName);

                    SmTransitionRows.Add(new SmTransitionRow
                    {
                        OwnerState = stateObj,
                        TransitionArray = transArray,
                        TransitionChild = tr,
                        ParentSM = sm,
                        ParentSMName = smName,
                        FromState = fromName,
                        ToState = toName ?? $"ID:{toStateId}",
                        ToStateId = toStateId,
                        EventId = eventId,
                        EventName = evName ?? $"Event {eventId}",
                        BlendDuration = blendDuration,
                        TransitionEffectObj = resolvedEffect,
                        Flags = flags
                    });
                }
            }

            // ── Wildcard transitions (added ONCE, outside the state loop) ─────────
            var wildcardRef = sm.Params.FirstOrDefault(p => p.Name == "wildcardTransitions")?.Value;
            if (!string.IsNullOrEmpty(wildcardRef) && wildcardRef != "null"
                && manager.TryResolve(wildcardRef, out var wildcardArrayObj))
            {
                var wtp = wildcardArrayObj.Params.FirstOrDefault(p => p.Name == "transitions");
                if (wtp?.Children != null)
                {
                    foreach (var tr in wtp.Children)
                    {
                        string Get(string n) => tr.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";
                        var toStateId = Get("toStateId");
                        var eventId = Get("eventId");
                        var flags = Get("flags");
                        var effect = Get("transition");

                        var blendDuration = "";
                        if (!string.IsNullOrEmpty(effect) && effect != "null"
                            && manager.TryResolve(effect, out var effectObj))
                            blendDuration = effectObj.Params
                                .FirstOrDefault(p => p.Name == "duration")?.Value ?? "";

                        stateIdToName.TryGetValue(toStateId, out var toName);
                        eventLookup.TryGetValue(eventId, out var evName);

                        SmTransitionRows.Add(new SmTransitionRow
                        {
                            OwnerState = null,
                            TransitionArray = wildcardArrayObj,
                            TransitionChild = tr,
                            ParentSM = sm,
                            ParentSMName = smName,
                            FromState = "★ WILDCARD",
                            ToState = toName ?? $"ID:{toStateId}",
                            ToStateId = toStateId,
                            EventId = eventId,
                            EventName = evName ?? $"Event {eventId}",
                            BlendDuration = blendDuration,
                            Flags = flags
                        });
                    }
                }
            }
        }

        private void SmTransitionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedSmRow = SmTransitionsGrid.SelectedItem as SmTransitionRow;
            SelectedSmRow = _selectedSmRow;

            bool hasRow = _selectedSmRow != null;
            BtnDeleteSmTransition.IsEnabled = hasRow;
            BtnEditSmTransition.IsEnabled = hasRow;
        }

        // ── Edit button ───────────────────────────────────────────────────────────────
        private void BtnEditSmTransition_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSmRow == null) return;
            OpenTransitionDialog(isAdd: false, preselectedFromState: null);
        }

        // ── Add button ────────────────────────────────────────────────────────────────
        private void BtnAddSmTransition_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSM == null) { MessageBox.Show("Select a state machine first."); return; }

            var statesParam = _selectedSM.Params.FirstOrDefault(p => p.Name == "states");
            if (statesParam == null || string.IsNullOrWhiteSpace(statesParam.Value))
            { MessageBox.Show("This state machine has no states."); return; }

            // Go straight to the shared popup — no InputDialog prompt
            OpenTransitionDialog(isAdd: true, preselectedFromState: null);
        }
        // ── Shared Add / Edit popup ───────────────────────────────────────────────────
        private void OpenTransitionDialog(bool isAdd, HkObject preselectedFromState)
        {
            var statesParam = _selectedSM?.Params.FirstOrDefault(p => p.Name == "states");
            var stateOptions = new List<IdNamePair>();
            if (statesParam != null)
            {
                foreach (var sr in statesParam.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!manager.TryResolve(sr, out var so)) continue;
                    var sid = so.Params.FirstOrDefault(p => p.Name == "stateId")?.Value ?? "";
                    var name = so.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? so.Id;
                    stateOptions.Add(new IdNamePair { Id = sid, Name = $"{name}  (id {sid})" });
                }
            }

            // For the From State combo — use object IDs (not stateIds) as keys
            var fromOptions = statesParam?.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => manager.TryResolve(r, out var so)
                    ? new IdNamePair { Id = so.Id, Name = so.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? so.Id }
                    : null)
                .Where(x => x != null)
                .ToList() ?? new List<IdNamePair>();

            string title, initFromId, initEventId, initToStateId, initFlags;

            if (isAdd)
            {
                title = "Add Transition";
                initFromId = fromOptions.Count > 0 ? fromOptions[0].Id : null;
                initEventId = EventList.Count > 0 ? EventList[0].Id : null;
                initToStateId = stateOptions.Count > 0 ? stateOptions[0].Id : null;
                initFlags = "FLAG_DISABLE_CONDITION";
            }
            else
            {
                title = "Edit Transition";
                initFromId = preselectedFromState?.Id ?? _selectedSmRow.OwnerState?.Id;
                initEventId = _selectedSmRow.EventId;
                initToStateId = _selectedSmRow.ToStateId;
                initFlags = _selectedSmRow.Flags;
            }

            var popup = new SkyrimHavokEditor.UI.Dialogs.SmTransitionDialog(
                title, fromOptions, EventList, stateOptions,
                initFromId, initEventId, initToStateId, initFlags)
            { Owner = this };

            if (popup.ShowDialog() != true) return;

            // Resolve the chosen from-state HkObject by its ID
            var fromStateObj = isAdd
                ? manager.ObjectMap.TryGetValue(popup.ResultFromStateId ?? "", out var fso) ? fso : null
                : preselectedFromState ?? _selectedSmRow.OwnerState;

            if (isAdd && fromStateObj == null) { MessageBox.Show("Could not resolve selected from-state."); return; }

            var resultEventId = popup.ResultEventId;
            var resultToStateId = popup.ResultToStateId;
            var resultFlags = popup.ResultFlags;

            if (isAdd)
            {
                // ── Commit: ADD ──────────────────────────────────────────────────────
                // Get or create transition array
                var transRef = fromStateObj.Params.FirstOrDefault(p => p.Name == "transitions")?.Value;
                HkObject transArray;

                if (string.IsNullOrEmpty(transRef) || transRef == "null" ||
                    !manager.TryResolve(transRef, out transArray))
                {
                    var newId = GenerateNewObjectId();
                    transArray = new HkObject
                    {
                        Id = newId,
                        ClassName = "hkbStateMachineTransitionInfoArray",
                        Signature = "0xe397b11e",
                        Params = new List<HkParam>
                {
                    new HkParam { Name = "transitions", NumElements = "0",
                                  Children = new List<HkObject>() }
                }
                    };
                    manager.ObjectMap[newId] = transArray;
                    var tp2 = fromStateObj.Params.FirstOrDefault(p => p.Name == "transitions");
                    if (tp2 != null) tp2.Value = newId;
                }

                var newTr = new HkObject
                {
                    Params = new List<HkParam>
            {
                new HkParam { Name = "triggerInterval",  Children = new List<HkObject>
                {
                    new HkObject { Params = new List<HkParam> {
                        new HkParam { Name = "enterEventId", Value = "-1" },
                        new HkParam { Name = "exitEventId",  Value = "-1" },
                        new HkParam { Name = "enterTime",    Value = "0.000000" },
                        new HkParam { Name = "exitTime",     Value = "0.000000" }
                    }}
                }},
                new HkParam { Name = "initiateInterval", Children = new List<HkObject>
                {
                    new HkObject { Params = new List<HkParam> {
                        new HkParam { Name = "enterEventId", Value = "-1" },
                        new HkParam { Name = "exitEventId",  Value = "-1" },
                        new HkParam { Name = "enterTime",    Value = "0.000000" },
                        new HkParam { Name = "exitTime",     Value = "0.000000" }
                    }}
                }},
                new HkParam { Name = "transition",           Value = "#0141"  },
                new HkParam { Name = "condition",            Value = "null"   },
                new HkParam { Name = "eventId",              Value = resultEventId   ?? "0" },
                new HkParam { Name = "toStateId",            Value = resultToStateId ?? "0" },
                new HkParam { Name = "fromNestedStateId",    Value = "0" },
                new HkParam { Name = "toNestedStateId",      Value = "0" },
                new HkParam { Name = "priority",             Value = "0" },
                new HkParam { Name = "flags",                Value = resultFlags ?? "FLAG_DISABLE_CONDITION" }
            }
                };

                var tParam = transArray.Params.FirstOrDefault(p => p.Name == "transitions");
                tParam?.Children.Add(newTr);
                if (tParam != null) tParam.NumElements = tParam.Children.Count.ToString();

                var capturedFromName = fromStateObj.Params.FirstOrDefault(p => p.Name == "name")?.Value
                                       ?? fromStateObj.Id;

                _undoRedo.Record(new EditAction
                {
                    Description = $"Add transition from {capturedFromName}",
                    Undo = () =>
                    {
                        tParam?.Children.Remove(newTr);
                        if (tParam != null) tParam.NumElements = tParam.Children.Count.ToString();
                        RefreshSmInspector(_selectedSM); UpdateUndoRedoButtons();
                    },
                    Redo = () =>
                    {
                        tParam?.Children.Add(newTr);
                        if (tParam != null) tParam.NumElements = tParam.Children.Count.ToString();
                        RefreshSmInspector(_selectedSM); UpdateUndoRedoButtons();
                    }
                });
                UpdateUndoRedoButtons();

                RefreshSmInspector(_selectedSM);
                StatusText.Text = $"✓ Transition added from {capturedFromName}";
            }
            else
            {
                // ── Commit: EDIT ─────────────────────────────────────────────────────
                var row = _selectedSmRow;
                var oldEventId = row.EventId;
                var oldToStateId = row.ToStateId;
                var oldFlags = row.Flags;

                // Write new values to the backing HkObject params
                var eparam = row.TransitionChild?.Params.FirstOrDefault(x => x.Name == "eventId");
                var tparam = row.TransitionChild?.Params.FirstOrDefault(x => x.Name == "toStateId");
                var fparam = row.TransitionChild?.Params.FirstOrDefault(x => x.Name == "flags");
                if (eparam != null) eparam.Value = resultEventId;
                if (tparam != null) tparam.Value = resultToStateId;
                if (fparam != null) fparam.Value = resultFlags;

                // Resolve display names
                var eventLookup = EventList.ToDictionary(e => e.Id, e => e.Name);
                eventLookup.TryGetValue(resultEventId ?? "", out var newEvName);

                var stateIdToName = new Dictionary<string, string>();
                foreach (var s in stateOptions)
                    stateIdToName[s.Id] = s.Name.Split('(')[0].Trim();
                stateIdToName.TryGetValue(resultToStateId ?? "", out var newToName);

                row.EventId = resultEventId;
                row.EventName = newEvName ?? $"Event {resultEventId}";
                row.ToStateId = resultToStateId;
                row.ToState = newToName ?? $"ID:{resultToStateId}";
                row.Flags = resultFlags;

                _undoRedo.Record(new EditAction
                {
                    Description = $"Edit transition {row.FromState} → {row.ToState}",
                    Undo = () =>
                    {
                        _suppressUndoRecord = true;
                        if (eparam != null) eparam.Value = oldEventId;
                        if (tparam != null) tparam.Value = oldToStateId;
                        if (fparam != null) fparam.Value = oldFlags;
                        row.EventId = oldEventId;
                        row.ToStateId = oldToStateId;
                        row.Flags = oldFlags;
                        RefreshSmInspector(_selectedSM);
                        _suppressUndoRecord = false;
                        UpdateUndoRedoButtons();
                    },
                    Redo = () =>
                    {
                        _suppressUndoRecord = true;
                        if (eparam != null) eparam.Value = resultEventId;
                        if (tparam != null) tparam.Value = resultToStateId;
                        if (fparam != null) fparam.Value = resultFlags;
                        row.EventId = resultEventId;
                        row.ToStateId = resultToStateId;
                        row.Flags = resultFlags;
                        RefreshSmInspector(_selectedSM);
                        _suppressUndoRecord = false;
                        UpdateUndoRedoButtons();
                    }
                });
                UpdateUndoRedoButtons();
                StatusText.Text = $"✓ Transition updated";
            }
        }

        // ── Delete (unchanged logic, button enable already handled above) ─────────────
        private void BtnDeleteSmTransition_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSmRow == null) return;
            var row = _selectedSmRow;
            var tParam = row.TransitionArray.Params.FirstOrDefault(p => p.Name == "transitions");
            if (tParam == null) return;

            if (MessageBox.Show(
                    $"Delete transition:\n{row.FromState}  ➔  {row.ToState}  [{row.EventName}]?",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            tParam.Children.Remove(row.TransitionChild);
            tParam.NumElements = tParam.Children.Count.ToString();

            _undoRedo.Record(new EditAction
            {
                Description = $"Delete transition {row.FromState} → {row.ToState}",
                Undo = () =>
                {
                    tParam.Children.Add(row.TransitionChild);
                    tParam.NumElements = tParam.Children.Count.ToString();
                    RefreshSmInspector(_selectedSM); UpdateUndoRedoButtons();
                },
                Redo = () =>
                {
                    tParam.Children.Remove(row.TransitionChild);
                    tParam.NumElements = tParam.Children.Count.ToString();
                    RefreshSmInspector(_selectedSM); UpdateUndoRedoButtons();
                }
            });
            UpdateUndoRedoButtons();
            RefreshSmInspector(_selectedSM);
            StatusText.Text = "✓ Transition deleted";
        }

        // ── Generate a safe new object ID not already in ObjectMap ────────────────
        private string GenerateNewObjectId()
        {
            var existing = manager.ObjectMap.Keys
                .Where(k => k.StartsWith("#"))
                .Select(k => int.TryParse(k.Substring(1), out int n) ? n : 0)
                .ToHashSet();
            int next = 1;
            while (existing.Contains(next)) next++;
            return $"#{next:D4}";
        }


        private string EncodeHavokValue(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "0";

            // If it looks like a float, encode as IEEE 754 bit pattern
            if (input.Contains(".") && float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float fVal))
            {
                byte[] bytes = BitConverter.GetBytes(fVal);
                return BitConverter.ToUInt32(bytes, 0).ToString();
            }

            // Otherwise pass integers through as-is
            return input;
        }

        private void VariablesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VariablesList.SelectedItem is IdNamePair pair)
            {
                SelectedVariable = pair;
                BtnDeleteVariable.IsEnabled = true;
            }
            else
            {
                BtnDeleteVariable.IsEnabled = false;
            }
        }

        private void BtnAddVariable_Click(object sender, RoutedEventArgs e)
        {
            var strData = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            var graphData = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphData");
            var valueSet = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbVariableValueSet");

            if (strData == null) { MessageBox.Show("No hkbBehaviorGraphStringData found."); return; }

            // Pick type
            var typeDialog = new InputDialog("Variable type:", "VARIABLE_TYPE_FLOAT") { Owner = this };
            if (typeDialog.ShowDialog() != true) return;
            var varType = typeDialog.InputText?.Trim();
            if (string.IsNullOrEmpty(varType)) return;

            var newIndex = VariableList.Count;
            var newName = $"NewVariable_{newIndex}";

            // 1 — Add to variableNames string list
            var namesParam = strData.Params.FirstOrDefault(p => p.Name == "variableNames");
            if (namesParam != null)
            {
                if (namesParam.Strings == null || namesParam.Strings.Count == 0)
                    namesParam.Strings = (namesParam.Value ?? "")
                        .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .ToList();
                namesParam.Strings.Add(newName);
                namesParam.Value = string.Join("\n", namesParam.Strings);
                namesParam.NumElements = namesParam.Strings.Count.ToString();
            }

            // 2 — Add to variableInfos in hkbBehaviorGraphData
            if (graphData != null)
            {
                var infosParam = graphData.Params.FirstOrDefault(p => p.Name == "variableInfos");
                if (infosParam != null)
                {
                    infosParam.Children ??= new List<HkObject>();
                    infosParam.Children.Add(new HkObject
                    {
                        Params = new List<HkParam>
                {
                    new HkParam { Name = "role", Value = "{ 0 0 0 }" },
                    new HkParam { Name = "type", Value = varType }
                }
                    });
                    infosParam.NumElements = infosParam.Children.Count.ToString();
                }
            }

            // 3 — Add default value to hkbVariableValueSet
            if (valueSet != null)
            {
                var wordValsParam = valueSet.Params
                    .FirstOrDefault(p => p.Name == "wordVariableValues");
                if (wordValsParam != null)
                {
                    wordValsParam.Children ??= new List<HkObject>();
                    wordValsParam.Children.Add(new HkObject
                    {
                        Params = new List<HkParam>
                {
                    new HkParam { Name = "value", Value = "0" }
                }
                    });
                    wordValsParam.NumElements = wordValsParam.Children.Count.ToString();
                }
            }

            // 4 — Add to UI list
            var newVar = new IdNamePair
            {
                Id = $"{strData.Id}_{newIndex}",
                Name = newName,
                Index = newIndex,
                RawValue = "0",
                Value = "0",
                VariableType = varType
            };
            VariableList.Add(newVar);

            _undoRedo.Record(new EditAction
            {
                Description = $"Add variable '{newName}'",
                Undo = () => { _suppressUndoRecord = true; DeleteVariableAt(newIndex); _suppressUndoRecord = false; },
                Redo = () => { /* re-add is complex — just refresh */ RefreshLookups(); }
            });
            UpdateUndoRedoButtons();

            // Select and rename immediately
            VariablesList.SelectedItem = newVar;
            VariablesList.ScrollIntoView(newVar);
            StatusText.Text = $"✓ Variable added (index {newIndex})";
        }

        private void BtnDeleteVariable_Click(object sender, RoutedEventArgs e)
        {
            if (VariablesList.SelectedItem is not IdNamePair variable) return;

            // Check usages first
            var usages = GetVariableUsageList(variable.Index);
            if (usages.Count > 0)
            {
                var usageText = string.Join("\n", usages.Take(8));
                if (usages.Count > 8) usageText += $"\n...and {usages.Count - 8} more";
                var answer = MessageBox.Show(
                    $"'{variable.Name}' is used in {usages.Count} place(s):\n\n{usageText}\n\n" +
                    "Deleting will leave those references broken.\nDelete anyway?",
                    "Variable In Use",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }
            else
            {
                if (MessageBox.Show(
                    $"Delete variable '{variable.Name}' (index {variable.Index})?",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question)
                    != MessageBoxResult.Yes) return;
            }

            DeleteVariableAt(variable.Index);
            StatusText.Text = $"✓ Variable '{variable.Name}' deleted";
        }

        private void DeleteVariableAt(int index)
        {
            var strData = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData");
            var graphData = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphData");
            var valueSet = manager.ObjectMap.Values
                .FirstOrDefault(o => o.ClassName == "hkbVariableValueSet");

            // Remove from variableNames
            if (strData != null)
            {
                var np = strData.Params.FirstOrDefault(p => p.Name == "variableNames");
                if (np?.Strings != null && index < np.Strings.Count)
                {
                    np.Strings.RemoveAt(index);
                    np.Value = string.Join("\n", np.Strings);
                    np.NumElements = np.Strings.Count.ToString();
                }
            }

            // Remove from variableInfos
            if (graphData != null)
            {
                var ip = graphData.Params.FirstOrDefault(p => p.Name == "variableInfos");
                if (ip?.Children != null && index < ip.Children.Count)
                {
                    ip.Children.RemoveAt(index);
                    ip.NumElements = ip.Children.Count.ToString();
                }
            }

            // Remove from wordVariableValues
            if (valueSet != null)
            {
                var wp = valueSet.Params.FirstOrDefault(p => p.Name == "wordVariableValues");
                if (wp?.Children != null && index < wp.Children.Count)
                {
                    wp.Children.RemoveAt(index);
                    wp.NumElements = wp.Children.Count.ToString();
                }
            }

            // Refresh UI — renumbers everything correctly
            RefreshLookups();
            BtnDeleteVariable.IsEnabled = false;
            UpdateUndoRedoButtons();
        }

        private List<string> GetVariableUsageList(int varIndex)
        {
            var usages = new List<string>();
            var idx = varIndex.ToString();

            // Check variableBindingSet bindings
            foreach (var obj in manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbVariableBindingSet"))
            {
                var bp = obj.Params.FirstOrDefault(p => p.Name == "bindings");
                if (bp?.Children == null) continue;
                foreach (var binding in bp.Children)
                {
                    var vi = binding.Params.FirstOrDefault(p => p.Name == "variableIndex")?.Value;
                    if (vi != idx) continue;
                    var owner = manager.ObjectMap.Values.FirstOrDefault(o =>
                        o.Params.Any(p => p.Value == obj.Id));
                    var ownerName = owner?.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? obj.Id;
                    var memberPath = binding.Params.FirstOrDefault(p => p.Name == "memberPath")?.Value ?? "";
                    usages.Add($"Binding: {ownerName}.{memberPath}");
                }
            }

            // Check direct variableIndex params
            foreach (var obj in manager.ObjectMap.Values)
            {
                var name = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? obj.Id;
                foreach (var param in obj.Params
                    .Where(p => p.Name == "variableIndex" && p.Value == idx))
                    usages.Add($"Direct: {name} [{obj.ClassName}]");
            }

            return usages;
        }
    }

    public static class VisualHelper
    {
        public static T FindVisualChild<T>(this DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}

