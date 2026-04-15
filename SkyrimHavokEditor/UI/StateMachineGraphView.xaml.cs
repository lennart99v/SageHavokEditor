using SkyrimHavokEditor.Core;
using SkyrimHavokEditor.Models;
using SkyrimHavokEditor.Models.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SkyrimHavokEditor.UI
{
    // ── Navigation level — what the graph is currently showing ───────────────
    public enum GraphViewLevel { StateMachine, GeneratorHierarchy }

    public class GraphBreadcrumb
    {
        public GraphViewLevel Level { get; set; }
        public string Label { get; set; }  // display in breadcrumb bar
        public string MachineFilter { get; set; }
        public string RootObjectId { get; set; }  // state that was drilled into
    }

    // ── Debug UI models ───────────────────────────────────────────────────────────
    public class HistoryEntry
    {
        public string Time { get; set; }
        public string Label { get; set; }
    }

    public class LiveVariable : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public string Name { get; set; }

        private float _value;
        public float Value
        {
            get => _value;
            set { _value = value; OnChanged(nameof(Value)); OnChanged(nameof(DisplayValue)); }
        }

        public string DisplayValue => Value == 0f ? "0" : Value.ToString("F2");

        private System.Windows.Media.Brush _valueBrush = System.Windows.Media.Brushes.CornflowerBlue;
        public System.Windows.Media.Brush ValueBrush
        {
            get => _valueBrush;
            set { _valueBrush = value; OnChanged(nameof(ValueBrush)); }
        }

        private System.Windows.Media.Brush _nameBrush
            = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x9D, 0x9D, 0x9D));
        public System.Windows.Media.Brush NameBrush
        {
            get => _nameBrush;
            set { _nameBrush = value; OnChanged(nameof(NameBrush)); }
        }


        private bool _isAlternate;
        public bool IsAlternate
        {
            get => _isAlternate;
            set { _isAlternate = value; OnChanged(nameof(IsAlternate)); }
        }

        private void OnChanged(string p)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
    }

    public partial class StateMachineGraphView : UserControl
    {
        // ── Data ──────────────────────────────────────────────────────────────
        private HavokManager _manager;
        private List<IdNamePair> _events = new();

        // ── Graph state ───────────────────────────────────────────────────────
        private List<GraphNode> _nodes = new();
        private List<GraphEdge> _edges = new();


        // ── Debug state ───────────────────────────────────────────────────────────────
        public DebuggerViewModel DebugVM { get; } = new();
        private HashSet<string> _lastActiveStateKeys = new();
        private bool _debugPaused;
        private bool _debugRecording;
        private BehaviorDebuggerClient _debugger;
        private Dictionary<(string smName, int stateId), string> _stateLookup = new();
        public string LoadedFileName { get; set; } = "";
        private GraphVisualHost _visualHost;
        private bool _panToActive = false;
        private string _pendingPanToNodeId = null;

        // ── Actor type detection ──────────────────────────────────────────────────────
        public enum DebugActorType { Player, HumanoidNPC, Dragon, Horse, Creature, Unknown }

        private static readonly Dictionary<DebugActorType, (string icon, Color accent, Color bg)> _actorTheme = new()
        {
            [DebugActorType.Player] = ("👤", Color.FromRgb(0x00, 0xAA, 0xFF), Color.FromRgb(0x0A, 0x14, 0x1E)),
            [DebugActorType.HumanoidNPC] = ("🧍", Color.FromRgb(0x00, 0xCC, 0x88), Color.FromRgb(0x0A, 0x1A, 0x14)),
            [DebugActorType.Dragon] = ("🐉", Color.FromRgb(0x8A, 0x2B, 0xE2), Color.FromRgb(0x0D, 0x06, 0x1A)),
            [DebugActorType.Horse] = ("🐴", Color.FromRgb(0xCC, 0x88, 0x22), Color.FromRgb(0x1A, 0x12, 0x06)),
            [DebugActorType.Creature] = ("🐺", Color.FromRgb(0xCC, 0x44, 0x22), Color.FromRgb(0x1A, 0x08, 0x06)),
            [DebugActorType.Unknown] = ("❓", Color.FromRgb(0x66, 0x66, 0x66), Color.FromRgb(0x10, 0x10, 0x10)),

        };
        private List<IdNamePair> _loadedVariables = new();
        private static readonly Dictionary<DebugActorType, HashSet<string>> _relevantVars = new()
        {
            [DebugActorType.Player] = new()
    {
        "Speed","Direction","TurnDelta","Pitch","VelocityZ","weaponSpeedMult",
        "iRightHandType","iLeftHandType","iCombatStance",
        "IsSneaking","IsBlocking","bInJumpState","IsAttacking","IsSprinting",
        "IsStaggering","IsRecoiling","IsEquipping","IsBleedingOut","IsBashing",
        "IsCastingRight","IsCastingLeft","IsShouting","bBowDrawn","bIsRiding","IsDismounting"
    },
            [DebugActorType.HumanoidNPC] = new()
    {
        "Speed","Direction","TurnDelta","Pitch",
        "IsSneaking","IsBlocking","IsAttacking","IsSprinting",
        "IsStaggering","IsBleedingOut","iCombatStance",
        "iRightHandType","iLeftHandType"
    },
            [DebugActorType.Dragon] = new()
    {
        "Speed","TurnDelta","Pitch","Direction",
        "IsOnGround","IsMoving","IsIdle","iCombat",
        "IsShouting","IsTurningLeft","IsTurningRight",
        "IsMovingForward","IsMovingBackward","iInjured"
    },
            [DebugActorType.Horse] = new()
    {
        "Speed","Direction","TurnDelta","Pitch","VelocityZ",
        "IsSprinting","bInJumpState","IsMoving"
    },
            [DebugActorType.Creature] = new()
    {
        "Speed","Direction","TurnDelta","Pitch",
        "IsAttacking","IsStaggering","IsBleedingOut",
        "iCombatStance","IsMoving","IsIdle"
    },
        };


        // ── Navigation stack ──────────────────────────────────────────────────
        private readonly Stack<GraphBreadcrumb> _navStack = new();
        private GraphBreadcrumb _currentLevel;

        // ── Pan / zoom ────────────────────────────────────────────────────────
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _translate = new(0, 0);
        private Point _panStart;
        private bool _isPanning;
        private double _zoom = 1.0;
        private bool _fitPending = false;

        // ── Graph bookmarks (Ctrl+1-9 = save, 1-9 = jump) ────────────────────
        private readonly (double scale, double tx, double ty, string label)?[] _graphBookmarks
            = new (double, double, double, string)?[10];

        // ── Events ────────────────────────────────────────────────────────────
        public event Action<string> StateSelected;
        public event Action<string, string> AddTransitionRequested;
        public event Action<HkObject, HkObject, string, string> TransitionDeletedFromGraph;
        public event Action<string, string, string> NodeRenamedOnGraph;
        public event Action<HkObject, HkObject> NodeAddedToGraph;
        public event Action<HkObject, HkObject, string> NodeDeletedFromGraph;
        public event Action<string> StatusText_;

        // ── Graphviz ──────────────────────────────────────────────────────────
        private static readonly string GraphvizDotPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Graphviz", "bin", "dot.exe");

        // ── Node type classifier ──────────────────────────────────────────────
        private static GraphNodeType ClassifyNode(string cls) => cls switch
        {
            "hkbStateMachine" => GraphNodeType.StateMachine,
            "hkbStateMachineStateInfo" => GraphNodeType.State,
            "hkbClipGenerator" => GraphNodeType.Clip,
            _ when cls?.Contains("Modifier") == true => GraphNodeType.Modifier,
            _ when cls?.Contains("Blender") == true => GraphNodeType.Blender,
            _ when cls?.Contains("Generator") == true => GraphNodeType.Blender,
            _ => GraphNodeType.Unknown
        };

        public StateMachineGraphView()
        {
            InitializeComponent();
            DebugVM.OnPauseToggle = () =>
            {
                if (_debugger == null) return;
                _debugPaused = !_debugPaused;
                if (_debugPaused) { _debugger.Pause(); DebugVM.PauseContent = "▶"; }
                else { _debugger.Resume(); DebugVM.PauseContent = "⏸"; }
                StatusText_?.Invoke(_debugPaused ? "⏸ Paused" : "▶ Resumed");
            };

            DebugVM.OnRecordToggle = () =>
            {
                if (_debugger == null) return;
                _debugRecording = !_debugRecording;
                if (_debugRecording)
                {
                    _debugger.StartRecording();
                    DebugVM.RecordContent = "⏹";
                    DebugVM.RecordFg = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44));
                }
                else
                {
                    var frames = _debugger.StopRecording();
                    DebugVM.RecordContent = "⏺";
                    DebugVM.RecordFg = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xCC, 0x44, 0x44));
                    StatusText_?.Invoke($"⏹ {frames.Count} frames captured");
                }
            };

            DebugVM.OnExportRecording = () =>
            {
                if (_debugger == null) return;
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JSON|*.json",
                    FileName = $"session_{DateTime.Now:yyyyMMdd_HHmmss}.json"
                };
                if (sfd.ShowDialog() != true) return;
                try { _debugger.ExportRecording(sfd.FileName); StatusText_?.Invoke("💾 Exported"); }
                catch (Exception ex) { MessageBox.Show("Export failed: " + ex.Message); }
            };

            DebugVM.OnPanToActiveToggle = () =>
            {
                _panToActive = !_panToActive;
                DebugVM.PanToOpacity = _panToActive ? 1.0 : 0.5;
                StatusText_?.Invoke(_panToActive ? "🎯 Pan-to-active ON" : "🎯 Pan-to-active OFF");
            };
            _visualHost = new GraphVisualHost();
            _visualHost.Width = 50000;
            _visualHost.Height = 50000;

            // Single click → inspect in Object Data
            _visualHost.NodeSelected += node => StateSelected?.Invoke(node.Id);

            // Double-click → drill into node if it has children
            _visualHost.NodeDoubleClicked += node => DrillInto(node);

            // Drag-to-connect
            _visualHost.ConnectionRequested += (from, to) =>
            {
                if (_manager.ObjectMap.TryGetValue(from.Id, out _))
                {
                    StateSelected?.Invoke(from.Id);
                    AddTransitionRequested?.Invoke(from.Id, to.StateId);
                }
            };

            _visualHost.NodeContextMenuRequested += ShowNodeContextMenu;
            _visualHost.EdgeContextMenuRequested += ShowEdgeContextMenu;
            _visualHost.NodeRenameRequested += StartInlineRename;
            _visualHost.CanvasContextMenuRequested += p => ShowCanvasContextMenu(p);
            // Empty-space drag: deltas come in graph-space, divide by zoom for screen-space
            _visualHost.PanDelta += (dx, dy) =>
            {
                // dx/dy are in GraphVisualHost local (graph) space.
                // GraphCanvas is scaled by _zoom, so 1 graph-px = _zoom screen-px.
                _translate.X += dx;
                _translate.Y += dy;
                SyncMinimap();
                RedrawMinimapOverlay();
            };
            _visualHost.MapTransformChanged += () => RedrawMinimapOverlay();

            GraphCanvas.Children.Clear();
            GraphCanvas.Children.Add(_visualHost);

            // Key handling for graph shortcuts
            Focusable = true;
            KeyDown += OnGraphKeyDown;

            var tg = new TransformGroup();
            tg.Children.Add(_scale);
            tg.Children.Add(_translate);
            GraphCanvas.RenderTransform = tg;
            // Fit to view as soon as the canvas becomes visible (e.g. user switches to Graph tab)
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue && _fitPending && _nodes.Count > 0)
                {
                    _fitPending = false;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                        new Action(() => { FitToView(); RedrawMinimapOverlay(); }));
                }
            };
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Load(HavokManager manager, List<IdNamePair> events, List<IdNamePair> variables = null)
        {
            _manager = manager;
            _events = events;
            _loadedVariables = variables ?? new();
            var machines = manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine")
                .Select(o => o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id)
                .OrderBy(n => n).ToList();

            MachineSelector.Items.Clear();
            MachineSelector.Items.Add("-- All Machines --");
            foreach (var m in machines) MachineSelector.Items.Add(m);
            MachineSelector.SelectedIndex = machines.Count > 0 ? 1 : 0;
            RebuildStateLookup();
            // Send config to plugin if debugger is already running
            if (_debugger != null)
                _debugger.SendConfig(BuildDebugConfigFromLoadedFile());
        }

        public void UpdateCanvasBackground(Color dotColor)
        {
            GraphCanvas.Background = new DrawingBrush
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 24, 24),
                ViewportUnits = BrushMappingMode.Absolute,
                Drawing = new GeometryDrawing
                {
                    Brush = new SolidColorBrush(dotColor),
                    Geometry = new EllipseGeometry(new Point(12, 12), 0.8, 0.8)
                }
            };
        }

        // ── Drill-down navigation ─────────────────────────────────────────────

        private void DrillInto(GraphNode node)
        {
            if (_manager == null) return;
            if (!_manager.ObjectMap.TryGetValue(node.Id, out var obj)) return;

            // What can we drill into?
            // State node → show its generator hierarchy
            // SM node    → show its states (already the default view, zoom in)
            if (obj.ClassName == "hkbStateMachineStateInfo")
            {
                var genRef = obj.Params.FirstOrDefault(p => p.Name == "generator")?.Value;
                if (string.IsNullOrEmpty(genRef) || genRef == "null")
                {
                    _visualHost.ShowOverlayText("This state has no generator",
                        Color.FromRgb(0x9D, 0x9D, 0x9D));
                    return;
                }

                // Push current level onto nav stack
                _navStack.Push(_currentLevel ?? new GraphBreadcrumb
                {
                    Level = GraphViewLevel.StateMachine,
                    Label = MachineSelector.SelectedItem as string ?? "Graph",
                    MachineFilter = MachineSelector.SelectedItem as string ?? ""
                });

                _currentLevel = new GraphBreadcrumb
                {
                    Level = GraphViewLevel.GeneratorHierarchy,
                    Label = node.Name,
                    RootObjectId = node.Id
                };

                UpdateBreadcrumb();
                BuildGeneratorGraph(genRef, node.Name);
            }
            else if (obj.ClassName == "hkbStateMachine")
            {
                // Already showing states — just select it
                StateSelected?.Invoke(node.Id);
            }
        }

        private void NavigateBack()
        {
            if (_navStack.Count == 0) return;

            var prev = _navStack.Pop();
            _currentLevel = _navStack.Count > 0 ? _navStack.Peek() : null;

            UpdateBreadcrumb();

            if (prev.Level == GraphViewLevel.StateMachine)
            {
                // Restore the machine selector and rebuild state graph
                if (!string.IsNullOrEmpty(prev.MachineFilter))
                {
                    // Re-select in dropdown without triggering SelectionChanged recursion
                    MachineSelector.SelectionChanged -= MachineSelector_SelectionChanged;
                    MachineSelector.SelectedItem = prev.MachineFilter;
                    MachineSelector.SelectionChanged += MachineSelector_SelectionChanged;
                }
                BuildStateMachineGraph(prev.MachineFilter);
            }
        }

        private void UpdateBreadcrumb()
        {
            BreadcrumbPanel.Children.Clear();

            // Root crumb
            var root = MakecrumbButton("⚙ Graph", () =>
            {
                _navStack.Clear();
                _currentLevel = null;
                UpdateBreadcrumb();
                if (MachineSelector.SelectedItem is string m)
                    BuildStateMachineGraph(m);
            });
            BreadcrumbPanel.Children.Add(root);

            // Intermediate crumbs from stack (bottom to top)
            var crumbs = _navStack.Reverse().ToList();
            foreach (var crumb in crumbs)
            {
                BreadcrumbPanel.Children.Add(MakeSeparator());
                var c = crumb; // capture
                BreadcrumbPanel.Children.Add(MakecrumbButton(crumb.Label, () =>
                {
                    // Pop back to this crumb
                    while (_navStack.Count > 0 && _navStack.Peek() != c)
                        _navStack.Pop();
                    NavigateBack();
                }));
            }

            // Current level (not clickable — it's where we are)
            if (_currentLevel != null)
            {
                BreadcrumbPanel.Children.Add(MakeSeparator());
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = _currentLevel.Label,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                });
            }

            // Back button visibility
            BtnBack.IsEnabled = _navStack.Count > 0;
        }

        private static Button MakecrumbButton(string label, Action onClick)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Click += (_, __) => onClick();
            return btn;
        }

        private static TextBlock MakeSeparator() => new TextBlock
        {
            Text = " › ",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            VerticalAlignment = VerticalAlignment.Center
        };

        // ── Context menu ──────────────────────────────────────────────────────

        private void ShowNodeContextMenu(GraphNode node)
        {
            var menu = new ContextMenu();

            var inspect = new MenuItem { Header = "🔍 Inspect in Object Data" };
            inspect.Click += (_, __) => StateSelected?.Invoke(node.Id);

            var drill = new MenuItem { Header = "⬇ Drill into generator" };
            drill.Click += (_, __) => DrillInto(node);

            var addTrans = new MenuItem { Header = "➕ Add Transition from this state" };
            addTrans.Click += (_, __) =>
            {
                StateSelected?.Invoke(node.Id);
                AddTransitionRequested?.Invoke(node.Id, null);
            };

            var copyId = new MenuItem { Header = $"📋 Copy ID ({node.Id})" };
            copyId.Click += (_, __) => Clipboard.SetText(node.Id);

            var copyName = new MenuItem { Header = $"📋 Copy Name ({node.Name})" };
            copyName.Click += (_, __) => Clipboard.SetText(node.Name);

            // Only show drill option for state nodes
            if (_manager.ObjectMap.TryGetValue(node.Id, out var obj)
                && obj.ClassName == "hkbStateMachineStateInfo")
            {
                var genRef = obj.Params.FirstOrDefault(p => p.Name == "generator")?.Value;
                if (!string.IsNullOrEmpty(genRef) && genRef != "null")
                    menu.Items.Add(drill);
            }

            menu.Items.Add(inspect);
            menu.Items.Add(addTrans);
            var rename = new MenuItem { Header = "✏ Rename (F2)" };
            rename.Click += (_, __) => StartInlineRename(node);

            var delete = new MenuItem { Header = "🗑 Delete Node" };
            delete.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            delete.Click += (_, __) => DeleteNode(node);

            menu.Items.Add(new Separator
            {
                Style = (Style)TryFindResource("MenuSeparatorStyle")
            });
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            menu.Items.Add(new Separator
            {
                Style = (Style)TryFindResource("MenuSeparatorStyle")
            });
            menu.Items.Add(copyId);
            menu.Items.Add(copyName);
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private void ShowCommentContextMenu(GraphComment comment)
        {
            var menu = new ContextMenu();

            var rename = new MenuItem { Header = "✏ Rename" };
            rename.Click += (_, __) => StartInlineCommentRename(comment);
            menu.Items.Add(rename);

            // ── Recolor submenu ───────────────────────────────────────────────────
            var recolorItem = new MenuItem { Header = "🎨 Recolor" };
            var presets = new (string label, Color col)[]
            {
        ("🟡 Gold",   Color.FromRgb(0x80, 0x70, 0x00)),
        ("🟢 Green",  Color.FromRgb(0x20, 0x60, 0x30)),
        ("🟣 Purple", Color.FromRgb(0x50, 0x20, 0x70)),
        ("🔴 Red",    Color.FromRgb(0x70, 0x18, 0x18)),
        ("🔵 Blue",   Color.FromRgb(0x18, 0x38, 0x70)),
        ("🩵 Teal",   Color.FromRgb(0x18, 0x58, 0x58)),
        ("🟤 Brown",  Color.FromRgb(0x60, 0x38, 0x10)),
            };
            foreach (var (label, col) in presets)
            {
                var item = new MenuItem { Header = label };
                var c = col;
                item.Click += (_, __) => { comment.Color = c; _visualHost.DrawComments(); };
                recolorItem.Items.Add(item);
            }
            menu.Items.Add(recolorItem);

            menu.Items.Add(new Separator
            {
                Style = (Style)TryFindResource("MenuSeparatorStyle")
            });

            var delete = new MenuItem { Header = "🗑 Delete Comment" };
            delete.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            delete.Click += (_, __) =>
            {
                _visualHost.Comments.Remove(comment);
                _visualHost.DrawComments();
                StatusText_?.Invoke("✓ Comment deleted");
            };
            menu.Items.Add(delete);

            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        // ── Canvas context menu (empty space right-click) ───────────────────

        private void ShowCanvasContextMenu(Point canvasPoint)
        {
            var menu = new ContextMenu();

            // ── Add State ──────────────────────────────────────────────────
            var addState = new MenuItem { Header = "➕ Add State" };
            addState.Click += (_, __) => AddNewState(canvasPoint);
            menu.Items.Add(addState);

            // ── Add State Machine ──────────────────────────────────────────
            var addSM = new MenuItem { Header = "⚙ Add State Machine" };
            addSM.Click += (_, __) => AddNewStateMachine(canvasPoint);
            menu.Items.Add(addSM);

            menu.Items.Add(new Separator
            {
                Style = (Style)TryFindResource("MenuSeparatorStyle")
            });

            // ── Layout ────────────────────────────────────────────────────
            var fitView = new MenuItem { Header = "🔍 Fit to View" };
            fitView.Click += (_, __) => FitToView();
            menu.Items.Add(fitView);

            var resetZoom = new MenuItem { Header = "⊞ Reset Zoom (1:1)" };
            resetZoom.Click += (_, __) =>
            {
                _scale.ScaleX = _scale.ScaleY = 1.0;
                _translate.X = _translate.Y = 0;
                _zoom = 1.0;
            };
            menu.Items.Add(resetZoom);

            var autoLayout = new MenuItem { Header = "⟳ Re-layout Graph" };
            autoLayout.Click += (_, __) =>
            {
                var filter = MachineSelector.SelectedItem as string ?? "-- All Machines --";
                BuildStateMachineGraph(filter);
            };
            menu.Items.Add(autoLayout);

            menu.Items.Add(new Separator
            {
                Style = (Style)TryFindResource("MenuSeparatorStyle")
            });

            // ── Selection ─────────────────────────────────────────────────
            var selectAll = new MenuItem { Header = "☐ Select All Nodes" };
            selectAll.Click += (_, __) => _visualHost.SelectAll();
            menu.Items.Add(selectAll);

            // ── Export ────────────────────────────────────────────────────
            var exportImg = new MenuItem { Header = "📷 Export Graph as PNG" };
            exportImg.Click += (_, __) => ExportGraphAsPng();
            menu.Items.Add(exportImg);

            menu.Items.Add(new Separator
            {
                Style = (Style)TryFindResource("MenuSeparatorStyle")
            });

            // ── Stats ─────────────────────────────────────────────────────
            var stats = new MenuItem
            {
                Header = $"ℹ {_nodes.Count} nodes  ·  {_edges.Count} transitions",
                IsEnabled = false
            };
            menu.Items.Add(stats);

            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private void AddNewState(Point canvasPoint)
        {
            if (_manager == null) return;

            // Find the current SM
            var currentFilter = MachineSelector.SelectedItem as string;
            var sm = _manager.ObjectMap.Values.FirstOrDefault(o =>
                o.ClassName == "hkbStateMachine" &&
                (o.Params.FirstOrDefault(p => p.Name == "name")?.Value == currentFilter
                 || currentFilter == "-- All Machines --"));

            if (sm == null) { MessageBox.Show("Select a specific state machine first."); return; }

            // Generate new IDs
            var stateId = GenerateNewObjectId();
            var maxStateId = _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachineStateInfo")
                .Select(o => int.TryParse(
                    o.Params.FirstOrDefault(p => p.Name == "stateId")?.Value, out int n) ? n : 0)
                .DefaultIfEmpty(0).Max() + 1;

            var newState = new HkObject
            {
                Id = stateId,
                ClassName = "hkbStateMachineStateInfo",
                Signature = "0xed7f9d0",
                Params = new List<HkParam>
                {
                    new HkParam { Name = "listeners",   Value = "",    NumElements = "0" },
                    new HkParam { Name = "generator",   Value = "null" },
                    new HkParam { Name = "name",        Value = $"NewState_{maxStateId}" },
                    new HkParam { Name = "stateId",     Value = maxStateId.ToString() },
                    new HkParam { Name = "probability", Value = "1.000000" },
                    new HkParam { Name = "enable",      Value = "true" },
                    new HkParam { Name = "transitions", Value = "null" },
                }
            };
            _manager.ObjectMap[stateId] = newState;

            // Add to SM's states list
            var statesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
            if (statesParam != null)
            {
                var current = statesParam.Value?.Trim() ?? "";
                statesParam.Value = string.IsNullOrEmpty(current)
                    ? stateId : current + " " + stateId;
                statesParam.NumElements = (statesParam.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length).ToString();
            }

            // Rebuild graph and select new node
            NodeAddedToGraph?.Invoke(newState, sm);
            BuildStateMachineGraph(currentFilter);
            StatusText_?.Invoke($"✓ Added state '{newState.Params.First(p => p.Name == "name").Value}'");
        }

        private void AddNewStateMachine(Point canvasPoint)
        {
            if (_manager == null) return;

            var smId = GenerateNewObjectId();
            var smName = "NewStateMachine";

            var newSM = new HkObject
            {
                Id = smId,
                ClassName = "hkbStateMachine",
                Signature = "0x816c1dcb",
                Params = new List<HkParam>
                {
                    new HkParam { Name = "userData",                          Value = "0" },
                    new HkParam { Name = "name",                              Value = smName },
                    new HkParam { Name = "startStateChooser",                 Value = "null" },
                    new HkParam { Name = "startStateId",                      Value = "0" },
                    new HkParam { Name = "returnToPreviousStateEventId",      Value = "-1" },
                    new HkParam { Name = "randomTransitionEventId",           Value = "-1" },
                    new HkParam { Name = "transitionToNextHigherStateEventId",Value = "-1" },
                    new HkParam { Name = "transitionToNextLowerStateEventId", Value = "-1" },
                    new HkParam { Name = "syncVariableIndex",                 Value = "-1" },
                    new HkParam { Name = "wrapAroundStateId",                 Value = "-1" },
                    new HkParam { Name = "maxSimultaneousTransitions",        Value = "32" },
                    new HkParam { Name = "startStateMode",                    Value = "START_STATE_MODE_DEFAULT" },
                    new HkParam { Name = "selfTransitionMode",                Value = "SELF_TRANSITION_MODE_NO_TRANSITION" },
                    new HkParam { Name = "states",    Value = "", NumElements = "0" },
                    new HkParam { Name = "wildcardTransitions", Value = "null" },
                }
            };
            _manager.ObjectMap[smId] = newSM;

            // Add to SM selector
            MachineSelector.Items.Add(smName);
            MachineSelector.SelectedItem = smName;

            NodeAddedToGraph?.Invoke(newSM, null);
            StatusText_?.Invoke($"✓ Added state machine '{smName}'");
        }

        private void ExportGraphAsPng()
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Graph as PNG",
                Filter = "PNG Image|*.png",
                FileName = $"graph_{MachineSelector.SelectedItem ?? "export"}.png"
            };
            if (sfd.ShowDialog() != true) return;

            try
            {
                // Render the graph canvas to a bitmap
                var size = new Size(GraphCanvas.ActualWidth, GraphCanvas.ActualHeight);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)size.Width, (int)size.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(GraphCanvas);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(
                    System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

                using var fs = new FileStream(sfd.FileName, FileMode.Create);
                encoder.Save(fs);

                StatusText_?.Invoke($"✓ Graph exported: {Path.GetFileName(sfd.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export failed: " + ex.Message);
            }
        }

        // ── Debugger ────────────────────────────

        public void StartDebugging()
        {
            if (_debugger != null) return;

            DebugVM.HistoryEntries.Clear();
            DebugVM.LiveVariables.Clear();
            DebugVM.ActiveStates.Clear();
            _lastActiveStateKeys.Clear();

            _debugger = new BehaviorDebuggerClient();

            _debugger.ConnectionChanged += connected => Dispatcher.Invoke(() =>
            {
                var accent = connected
                    ? Color.FromRgb(0x00, 0xFF, 0x80)
                    : Color.FromRgb(0x88, 0x00, 0x00);
                DebugVM.DotFill = new SolidColorBrush(accent);

                // ← Re-send config every time we (re)connect
                if (connected && _manager != null)
                    _debugger.SendConfig(BuildDebugConfigFromLoadedFile());

                StatusText_?.Invoke(connected
                    ? "🟢 Live debugger connected"
                    : "🔴 Live debugger disconnected — retrying...");
            });

            _debugger.SnapshotReceived += snap => Dispatcher.Invoke(() =>
            {
                // ── Step 1: resolve all state names first ─────────────────────────
                foreach (var state in snap.ActiveStates) 
                {
                    if (!string.IsNullOrEmpty(state.StateName)) continue;
                    var key = (state.SMName?.ToLowerInvariant() ?? "", state.StateId);
                    if (_stateLookup.TryGetValue(key, out var nodeId))
                    {
                        var node = _nodes.FirstOrDefault(n => n.Id == nodeId);
                        if (node != null)
                            state.StateName = node.Name;
                        else if (_manager?.ObjectMap.TryGetValue(nodeId, out var stateObj) == true)
                            state.StateName = stateObj.Params
                                .FirstOrDefault(p => p.Name == "name")?.Value
                                ?? $"state {state.StateId}";
                        else
                            state.StateName = $"state {state.StateId}";
                    }
                    else
                    {
                        state.StateName = $"state {state.StateId}";
                    }
                }

                // ── Step 2: build active IDs for graph highlighting ───────────────
                var activeIds = new List<string>();
                foreach (var state in snap.ActiveStates)
                {
                    var key = (state.SMName?.ToLowerInvariant() ?? "", state.StateId);
                    if (_stateLookup.TryGetValue(key, out var nodeId))
                        activeIds.Add(nodeId);
                }
                _visualHost.SetLiveStates(activeIds.Distinct().ToList(), snap.Variables);

                if (_panToActive && activeIds.Count > 0)
                    _pendingPanToNodeId = activeIds[0];

                // ── Step 3: history + auto-follow for newly entered states ────────
                var newKeys = new HashSet<string>(
                    snap.ActiveStates
                        .Select(s => $"{s.SMName}:{s.StateId}"));

                foreach (var state in snap.ActiveStates)
                {
                    var stateKey = $"{state.SMName}:{state.StateId}";
                    if (_lastActiveStateKeys.Contains(stateKey)) continue;
                    if (string.IsNullOrEmpty(state.StateName)) continue;

                    DebugVM.HistoryEntries.Insert(0, new HistoryEntry
                    {
                        Time = snap.Timestamp.ToString("HH:mm:ss"),
                        Label = $"{state.SMName} → {state.StateName}"
                    });
                    if (DebugVM.HistoryEntries.Count > 50)
                        DebugVM.HistoryEntries.RemoveAt(DebugVM.HistoryEntries.Count - 1);

                    // Auto-follow: only switch if this SM is in the dropdown
                    if (!string.IsNullOrEmpty(state.SMName) &&
                        MachineSelector.Items.Contains(state.SMName) &&
                        (string)MachineSelector.SelectedItem != state.SMName)
                    {
                        MachineSelector.SelectionChanged -= MachineSelector_SelectionChanged;
                        MachineSelector.SelectedItem = state.SMName;
                        MachineSelector.SelectionChanged += MachineSelector_SelectionChanged;
                        BuildStateMachineGraph(state.SMName);
                    }
                }
                _lastActiveStateKeys = newKeys;

                // ── Step 4: update active states panel ────────────────────────────
                DebugVM.ActiveStates.Clear();
                foreach (var s in snap.ActiveStates)
                    DebugVM.ActiveStates.Add(s);

                // ── Step 5: variables + theme ─────────────────────────────────────
                var actorType = DetectActorType(snap.BehaviorFile);
                ApplyActorTheme(actorType, snap.ActorName);
                UpdateVariableList(snap.Variables, actorType);

                // ── Step 6: dragon ────────────────────────────────────────────────
                if (snap.Dragon != null)
                {
                    DebugVM.DragonVis = Visibility.Visible;
                    DebugVM.DragonVars.Clear();
                    foreach (var v in snap.Dragon.Variables) DebugVM.DragonVars.Add(v);
                    var dt = DetectActorType(snap.Dragon.BehaviorFile);
                    if (_actorTheme.TryGetValue(dt, out var dth))
                    {
                        DebugVM.DragonAccent = new SolidColorBrush(dth.accent);
                        DebugVM.DragonBg = new SolidColorBrush(
                            Color.FromArgb(0xCC, dth.bg.R, dth.bg.G, dth.bg.B));
                        DebugVM.DragonLabel = $"{dth.icon} {snap.Dragon.BehaviorFile}";
                    }
                }
                else DebugVM.DragonVis = Visibility.Collapsed;
            });

            _debugger.Start();
            if (_manager != null)
                _debugger.SendConfig(BuildDebugConfigFromLoadedFile());
            StatusText_?.Invoke("⏳ Live debugger started — launch Skyrim with SKSE");
        }

        public void StopDebugging()
        {
            if (_debugger == null) return;
            _debugger.Stop();
            _debugger = null;

            _visualHost.SetLiveStates(new List<string>(), new List<VariableValue>());
            DebugVM.ActiveStates.Clear();
            DebugVM.LiveVariables.Clear();
            DebugVM.HistoryEntries.Clear();
            DebugVM.DragonVis = Visibility.Collapsed;

            StatusText_?.Invoke("⏹ Live debugger stopped");
        }

        public DebugConfig BuildDebugConfigFromLoadedFile()
        {
            var config = new DebugConfig();
            if (_manager == null) return config;

            foreach (var v in _loadedVariables)
            {
                bool isFloat = v.VariableType?.Contains("REAL") == true
                            || v.VariableType?.Contains("VECTOR") == true
                            || v.VariableType?.Contains("QUATERNION") == true;
                config.Variables.Add(new DebugVarEntry { Name = v.Name, Type = isFloat ? "float" : "int" });
            }

            foreach (var sm in _manager.ObjectMap.Values.Where(o => o.ClassName == "hkbStateMachine"))
            {
                var smName = sm.Params.FirstOrDefault(p => p.Name == "name")?.Value;
                var syncIdx = sm.Params.FirstOrDefault(p => p.Name == "syncVariableIndex")?.Value;
                if (string.IsNullOrEmpty(smName) || !int.TryParse(syncIdx, out int idx) || idx < 0) continue;
                if (idx >= _loadedVariables.Count) continue;
                config.StateMachines.Add(new DebugSMEntry { VariableName = _loadedVariables[idx].Name, SmName = smName });
            }

            // ── DIAGNOSTIC ───────────────────────────────────────────────────────
            StatusText_?.Invoke($"Config: {config.Variables.Count} vars, {config.StateMachines.Count} SMs");
            System.Diagnostics.Debug.WriteLine($"[Config] vars={config.Variables.Count}, SMs={config.StateMachines.Count}");
            // ─────────────────────────────────────────────────────────────────────

            return config;
        }

        private async void FlashVariable(LiveVariable lv)
        {
            var changed = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0x80));
            var normal = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4F, 0xC3, 0xF7));
            var changedN = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xD4, 0xD4, 0xD4));
            var normalN = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x9D, 0x9D, 0x9D));

            lv.ValueBrush = changed;
            lv.NameBrush = changedN;
            await System.Threading.Tasks.Task.Delay(400);
            lv.ValueBrush = normal;
            lv.NameBrush = normalN;
        }
        

        private void PanToNodeIfPending()
        {
            if (!_panToActive || _pendingPanToNodeId == null) return;
            var firstNode = _nodes.FirstOrDefault(n => n.Id == _pendingPanToNodeId);
            _pendingPanToNodeId = null;
            if (firstNode == null) return;

            double cW = CanvasBorder.ActualWidth > 0 ? CanvasBorder.ActualWidth : 800;
            double cH = CanvasBorder.ActualHeight > 0 ? CanvasBorder.ActualHeight : 500;
            double s = _zoom;
            double tx = cW / 2 - (firstNode.X + firstNode.Width / 2) * s;
            double ty = cH / 2 - (firstNode.Y + firstNode.Height / 2) * s;

            var animX = new System.Windows.Media.Animation.DoubleAnimation(
                _translate.X, tx, TimeSpan.FromMilliseconds(300));
            var animY = new System.Windows.Media.Animation.DoubleAnimation(
                _translate.Y, ty, TimeSpan.FromMilliseconds(300));
            animX.Completed += (_, __) => { _translate.BeginAnimation(TranslateTransform.XProperty, null); _translate.X = tx; };
            animY.Completed += (_, __) => { _translate.BeginAnimation(TranslateTransform.YProperty, null); _translate.Y = ty; };
            _translate.BeginAnimation(TranslateTransform.XProperty, animX);
            _translate.BeginAnimation(TranslateTransform.YProperty, animY);
            SyncMinimap();
        }

        private DebugActorType DetectActorType(string behaviorFile)
        {
            if (string.IsNullOrEmpty(behaviorFile)) return DebugActorType.Unknown;
            var f = behaviorFile.ToLowerInvariant();

            if (f.Contains("dragon")) return DebugActorType.Dragon;
            if (f.Contains("horse")) return DebugActorType.Horse;
            if (f.Contains("0_master") || f.Contains("defaultmale") ||
                f.Contains("defaultfemale") || f.Contains("mt_behavior")) return DebugActorType.Player;

            // Match against loaded file — if you're editing a creature behavior,
            // the snapshot behaviorFile will match and show as HumanoidNPC/Creature
            if (!string.IsNullOrEmpty(LoadedFileName))
            {
                var loaded = Path.GetFileNameWithoutExtension(LoadedFileName).ToLowerInvariant();
                if (!string.IsNullOrEmpty(loaded) && (f.Contains(loaded) || loaded.Contains(f)))
                {
                    // Heuristic: if the file has combat/attack vars it's a humanoid NPC,
                    // otherwise generic creature
                    bool hasHumanoidVars = _loadedVariables.Any(v =>
                        v.Name is "iRightHandType" or "iLeftHandType" or "IsSneaking");
                    return hasHumanoidVars ? DebugActorType.HumanoidNPC : DebugActorType.Creature;
                }
            }

            // Fallback: if variables suggest a humanoid
            bool looksHumanoid = _loadedVariables.Any(v =>
                v.Name is "iRightHandType" or "iCombatStance" or "IsSneaking");
            return looksHumanoid ? DebugActorType.HumanoidNPC : DebugActorType.Creature;
        }

        private void ApplyActorTheme(DebugActorType type, string actorName = null)
        {
            if (!_actorTheme.TryGetValue(type, out var theme)) return;
            DebugVM.DotFill = new SolidColorBrush(theme.accent);
            DebugVM.AccentBrush = new SolidColorBrush(theme.accent);
            DebugVM.ActorIcon = theme.icon;
            DebugVM.ActorLabel = actorName ?? type.ToString();
        }

        private void UpdateVariableList(List<VariableValue> vars, DebugActorType type)
        {
            var relevant = _relevantVars.TryGetValue(type, out var set) ? set : null;
            foreach (var v in vars)
            {
                var existing = DebugVM.LiveVariables.FirstOrDefault(lv => lv.Name == v.Name);
                if (existing == null)
                {
                    var lv = new LiveVariable
                    {
                        Name = v.Name,
                        Value = v.Value,
                        IsAlternate = DebugVM.LiveVariables.Count % 2 == 1,
                        NameBrush = relevant == null || relevant.Contains(v.Name)
                            ? new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4))
                            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
                    };
                    DebugVM.LiveVariables.Add(lv);
                }
                else if (Math.Abs(existing.Value - v.Value) > 0.001f)
                {
                    existing.Value = v.Value;
                    if (relevant == null || relevant.Contains(v.Name))
                        FlashVariable(existing);
                }
            }
        }


        // ── Edge context menu (delete transition) ────────────────────────────

        private void ShowEdgeContextMenu(GraphEdge edge)
        {
            var menu = new ContextMenu();

            // Header label — non-clickable
            var header = new MenuItem
            {
                Header = $"⟶  {edge.From?.Name}  →  {edge.To?.Name}",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold
            };
            menu.Items.Add(header);
            menu.Items.Add(new Separator
            {
                Style = (Style)TryFindResource("MenuSeparatorStyle")
            });

            var eventItem = new MenuItem
            {
                Header = $"Event: {edge.EventName}",
                IsEnabled = false
            };
            menu.Items.Add(eventItem);
            menu.Items.Add(new Separator
            {
                Style = (Style)TryFindResource("MenuSeparatorStyle")
            });

            var deleteItem = new MenuItem { Header = "🗑 Delete Transition" };
            deleteItem.Click += (_, __) => DeleteEdgeTransition(edge);
            menu.Items.Add(deleteItem);

            menu.IsOpen = true;
        }

        private void DeleteEdgeTransition(GraphEdge edge)
        {
            if (edge.Tag is not (HkObject trChild, HkObject transArray, HkObject ownerState))
            {
                MessageBox.Show("Cannot resolve transition backing data.");
                return;
            }

            var fromName = edge.From?.Name ?? "?";
            var toName = edge.To?.Name ?? "?";

            if (MessageBox.Show(
                    $"Delete transition:\n{fromName}  ->  {toName}  [{edge.EventName}]?",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            var tParam = transArray.Params.FirstOrDefault(p => p.Name == "transitions");
            if (tParam == null) return;

            tParam.Children.Remove(trChild);
            tParam.NumElements = tParam.Children.Count.ToString();

            // Fire event so MainWindow can record undo
            TransitionDeletedFromGraph?.Invoke(trChild, transArray, fromName, toName);

            // Rebuild graph to reflect deletion
            var currentFilter = MachineSelector.SelectedItem as string ?? "-- All Machines --";
            BuildStateMachineGraph(currentFilter);
        }

        // ── Inline node rename ────────────────────────────────────────────────

        private System.Windows.Controls.TextBox _renameBox;
        private GraphNode _renamingNode;

        private void StartInlineRename(GraphNode node)
        {
            if (_manager == null) return;
            if (!_manager.ObjectMap.TryGetValue(node.Id, out var obj)) return;

            _renamingNode = node;

            // Build the TextBox overlay
            _renameBox = new System.Windows.Controls.TextBox
            {
                Text = node.Name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x22)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(4, 2, 4, 2),
                MinWidth = node.Width,
                MaxWidth = node.Width + 40
            };

            // Position over the node header in canvas space
            // The canvas has a RenderTransform (scale + translate) applied to GraphCanvas,
            // so we need to map node coords through the transform
            var transform = GraphCanvas.RenderTransform as TransformGroup;
            var scale = (transform?.Children[0] as ScaleTransform)?.ScaleX ?? 1.0;
            var translateX = (transform?.Children[1] as TranslateTransform)?.X ?? 0.0;
            var translateY = (transform?.Children[1] as TranslateTransform)?.Y ?? 0.0;

            var screenX = node.X * scale + translateX;
            var screenY = node.Y * scale + translateY + 4; // +4 = top padding into header

            System.Windows.Controls.Canvas.SetLeft(_renameBox, screenX + 28); // after icon
            System.Windows.Controls.Canvas.SetTop(_renameBox, screenY);

            // Commit on Enter, cancel on Escape
            _renameBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter) { CommitRename(); ke.Handled = true; }
                if (ke.Key == Key.Escape) { CancelRename(); ke.Handled = true; }
            };
            _renameBox.LostFocus += (_, __) => CommitRename();

            GraphCanvas.Children.Add(_renameBox);
            _renameBox.Focus();
            _renameBox.SelectAll();
        }

        private void CommitRename()
        {
            if (_renameBox == null || _renamingNode == null) return;

            var newName = _renameBox.Text?.Trim();
            var node = _renamingNode;
            CleanupRenameBox();

            if (string.IsNullOrEmpty(newName) || newName == node.Name) return;
            if (!_manager.ObjectMap.TryGetValue(node.Id, out var obj)) return;

            var oldName = node.Name;
            var nameParam = obj.Params.FirstOrDefault(p => p.Name == "name");
            if (nameParam == null) return;

            nameParam.Value = newName;
            node.Name = newName;

            // Fire event so MainWindow can record undo
            NodeRenamedOnGraph?.Invoke(node.Id, oldName, newName);

            // Redraw nodes to show new name
            _visualHost.SetGraph(_nodes, _edges);
        }

        private void CancelRename() => CleanupRenameBox();

        private void CleanupRenameBox()
        {
            if (_renameBox != null)
            {
                GraphCanvas.Children.Remove(_renameBox);
                _renameBox = null;
                _renamingNode = null;
            }
            Keyboard.Focus(this);
        }

        // ── State machine graph (top level) ───────────────────────────────────

        private async void BuildStateMachineGraph(string machineFilter)
        {
            _nodes.Clear();
            _edges.Clear();
            if (_manager == null) return;

            _visualHost.ShowOverlayText("Building graph…", Color.FromRgb(0x9D, 0x9D, 0x9D));

            var eventNames = _events.ToDictionary(e => e.Id, e => e.Name);
            var allObjects = _manager.ObjectMap.Values.ToList();
            bool useGv = File.Exists(GraphvizDotPath);

            List<GraphNode> localNodes = new();
            List<GraphEdge> localEdges = new();
            List<XDotNode>? xdotNodes = null;
            List<XDotEdge>? xdotEdges = null;

            await System.Threading.Tasks.Task.Run(() =>
            {
                var stateIdToNode = new Dictionary<string, GraphNode>();

                foreach (var sm in allObjects.Where(o => o.ClassName == "hkbStateMachine"))
                {
                    var smName = sm.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? sm.Id;
                    if (machineFilter != "-- All Machines --" && smName != machineFilter) continue;

                    var startStateId = sm.Params
                        .FirstOrDefault(p => p.Name == "startStateId")?.Value ?? "-1";
                    var statesParam = sm.Params.FirstOrDefault(p => p.Name == "states");
                    if (statesParam == null) continue;

                    foreach (var stateRef in statesParam.Value
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!_manager.TryResolve(stateRef, out var stateObj)) continue;

                        var name = stateObj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? stateRef;
                        var stateId = stateObj.Params.FirstOrDefault(p => p.Name == "stateId")?.Value ?? "0";

                        var genRef = stateObj.Params.FirstOrDefault(p => p.Name == "generator")?.Value;
                        var genClass = "";
                        if (!string.IsNullOrEmpty(genRef) && genRef != "null"
                            && _manager.TryResolve(genRef, out var genObj))
                            genClass = genObj.ClassName ?? "";

                        var node = new GraphNode
                        {
                            Id = stateObj.Id,
                            StateId = stateId,
                            Name = name,
                            ClassName = stateObj.ClassName,
                            Machine = smName,
                            SubLabel = genClass,
                            NodeType = GraphNodeType.State,
                            IsStart = stateId == startStateId,
                            // Double-click hint — show drill indicator if has generator
                            CanDrillDown = !string.IsNullOrEmpty(genRef) && genRef != "null",
                            Tag = stateObj,
                            Width = Math.Clamp(name.Length * 7.2 + 36, 160, 240),
                            Height = 68
                        };
                        localNodes.Add(node);
                        stateIdToNode[stateId] = node;
                    }
                }

                // Transition edges
                foreach (var stateObj in allObjects
                    .Where(o => o.ClassName == "hkbStateMachineStateInfo"))
                {
                    var fromNode = localNodes.FirstOrDefault(n => n.Id == stateObj.Id);
                    if (fromNode == null) continue;

                    var transRef = stateObj.Params.FirstOrDefault(p => p.Name == "transitions")?.Value;
                    if (string.IsNullOrEmpty(transRef) || transRef == "null") continue;
                    if (!_manager.TryResolve(transRef, out var transArray)) continue;

                    var transParam = transArray.Params.FirstOrDefault(p => p.Name == "transitions");
                    if (transParam?.Children == null) continue;

                    foreach (var tr in transParam.Children)
                    {
                        string Get(string n) =>
                            tr.Params.FirstOrDefault(p => p.Name == n)?.Value ?? "";
                        var toStateId = Get("toStateId");
                        var eventId = Get("eventId");
                        if (!stateIdToNode.TryGetValue(toStateId, out var toNode)) continue;
                        eventNames.TryGetValue(eventId, out var evName);

                        localEdges.Add(new GraphEdge
                        {
                            From = fromNode,
                            To = toNode,
                            EventName = evName ?? $"Event {eventId}",
                            EventId = eventId,
                            Flags = Get("flags"),
                            // Store backing objects so edge context menu can delete
                            Tag = (tr, transArray, stateObj)
                        });
                    }
                }

                if (useGv) RunGraphvizLayout(localNodes, localEdges, ref xdotNodes, ref xdotEdges);
                else ApplyLayout(localNodes, localEdges);
            });

            _nodes = localNodes;
            _edges = localEdges;

            if (machineFilter == "-- All Machines --" && _edges.Count > 500)
            {
                if (MessageBox.Show($"{_edges.Count} transitions — show anyway?",
                        "Large Graph", MessageBoxButton.YesNo) == MessageBoxResult.No)
                { _visualHost.ClearOverlay(); return; }
            }

            _visualHost.SetGraph(_nodes, _edges);
            RebuildStateLookup();
            FitToView();
            RedrawMinimapOverlay();
            PanToNodeIfPending();
        }

        private void RebuildStateLookup()
        {
            _stateLookup.Clear();
            if (_manager == null) return;

            foreach (var sm in _manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine"))
            {
                var smName = sm.Params
                    .FirstOrDefault(p => p.Name == "name")?.Value ?? "";
                var statesParam = sm.Params
                    .FirstOrDefault(p => p.Name == "states");
                if (statesParam == null) continue;

                foreach (var stateRef in (statesParam.Value ?? "")
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!_manager.TryResolve(stateRef, out var stateObj)) continue;
                    var sidStr = stateObj.Params
                        .FirstOrDefault(p => p.Name == "stateId")?.Value;
                    if (!int.TryParse(sidStr, out int sid)) continue;

                    var key = (smName.ToLowerInvariant(), sid);
                    if (!_stateLookup.ContainsKey(key))
                        _stateLookup[key] = stateObj.Id;
                }
            }

            StatusText_?.Invoke($"Lookup built: {_stateLookup.Count} entries");
        }
        // ── Generator hierarchy graph (drill-down level) ──────────────────────

        private async void BuildGeneratorGraph(string rootRef, string stateName)
        {
            _nodes.Clear();
            _edges.Clear();
            _visualHost.ShowOverlayText($"Loading {stateName}…",
                Color.FromRgb(0x9D, 0x9D, 0x9D));

            List<GraphNode> localNodes = new();
            List<GraphEdge> localEdges = new();
            bool useGv = File.Exists(GraphvizDotPath);

            await System.Threading.Tasks.Task.Run(() =>
            {
                // Walk the generator graph depth-first
                var visited = new HashSet<string>();
                WalkGenerator(rootRef, null, localNodes, localEdges, visited, depth: 0);

                if (useGv)
                {
                    List<XDotNode>? xn = null; List<XDotEdge>? xe = null;
                    RunGraphvizLayout(localNodes, localEdges, ref xn, ref xe);
                }
                else ApplyTreeLayout(localNodes, localEdges);
            });

            _nodes = localNodes;
            _edges = localEdges;
            _visualHost.SetGraph(_nodes, _edges);
            FitToView();
            RedrawMinimapOverlay();
        }

        private void OnFirstResize(object sender, SizeChangedEventArgs e)
        {
            CanvasBorder.SizeChanged -= OnFirstResize;
            FitToView();
            RedrawMinimapOverlay();
        }

        private void WalkGenerator(string objRef, GraphNode parentNode,
            List<GraphNode> nodes, List<GraphEdge> edges,
            HashSet<string> visited, int depth)
        {
            if (string.IsNullOrEmpty(objRef) || objRef == "null") return;
            if (visited.Contains(objRef)) return;  // break cycles
            if (depth > 12) return;                 // safety limit
            if (!_manager.TryResolve(objRef, out var obj)) return;

            visited.Add(objRef);

            var name = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value
                    ?? obj.ClassName ?? obj.Id;

            var node = new GraphNode
            {
                Id = obj.Id,
                Name = name,
                ClassName = obj.ClassName,
                NodeType = ClassifyNode(obj.ClassName),
                Tag = obj
            };
            nodes.Add(node);

            if (parentNode != null)
                edges.Add(new GraphEdge { From = parentNode, To = node, EventName = "" });

            // ── Recurse into children depending on class ──────────────────────

            // hkbStateMachineStateInfo → generator
            var genRef = obj.Params.FirstOrDefault(p => p.Name == "generator")?.Value;
            if (!string.IsNullOrEmpty(genRef) && genRef != "null")
                WalkGenerator(genRef, node, nodes, edges, visited, depth + 1);

            // hkbBlenderGenerator → children[]
            var childrenParam = obj.Params.FirstOrDefault(p => p.Name == "children");
            if (childrenParam != null)
            {
                foreach (var childRef in (childrenParam.Value ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    WalkGenerator(childRef, node, nodes, edges, visited, depth + 1);
            }

            // hkbModifierGenerator → modifier + generator
            var modRef = obj.Params.FirstOrDefault(p => p.Name == "modifier")?.Value;
            if (!string.IsNullOrEmpty(modRef) && modRef != "null")
                WalkGenerator(modRef, node, nodes, edges, visited, depth + 1);

            // hkbBehaviorReferenceGenerator → behaviorName (leaf — no resolve)
            var behavRef = obj.Params.FirstOrDefault(p => p.Name == "behaviorName")?.Value;
            if (!string.IsNullOrEmpty(behavRef))
                node.SubLabel = $"→ {behavRef}";

            // hkbManualSelectorGenerator / hkbPoseMatchingGenerator → generators[]
            var gensParam = obj.Params.FirstOrDefault(p => p.Name == "generators");
            if (gensParam != null)
            {
                foreach (var gRef in (gensParam.Value ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    WalkGenerator(gRef, node, nodes, edges, visited, depth + 1);
            }
        }

        // ── Tree layout for generator hierarchy ───────────────────────────────
        // Top-down layout: root at top, children below

        private static void ApplyTreeLayout(List<GraphNode> nodes, List<GraphEdge> edges)
        {
            if (nodes.Count == 0) return;

            // BFS from root (node with no incoming edges)
            var hasIncoming = edges.Select(e => e.To.Id).ToHashSet();
            var root = nodes.FirstOrDefault(n => !hasIncoming.Contains(n.Id)) ?? nodes[0];

            var depths = new Dictionary<string, int> { [root.Id] = 0 };
            var bfsQueue = new Queue<GraphNode>();
            bfsQueue.Enqueue(root);

            while (bfsQueue.Count > 0)
            {
                var cur = bfsQueue.Dequeue();
                foreach (var edge in edges.Where(e => e.From.Id == cur.Id))
                {
                    if (!depths.ContainsKey(edge.To.Id))
                    {
                        depths[edge.To.Id] = depths[cur.Id] + 1;
                        bfsQueue.Enqueue(edge.To);
                    }
                }
            }

            // Any disconnected nodes go at max depth + 1
            int maxDepth = depths.Values.DefaultIfEmpty(0).Max();
            foreach (var n in nodes.Where(n => !depths.ContainsKey(n.Id)))
                depths[n.Id] = maxDepth + 1;

            // Group by depth row
            var rows = nodes.GroupBy(n => depths[n.Id])
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.ToList());

            double rowH = 100, colW = 220;

            foreach (var row in rows)
            {
                var items = row.Value;
                double totalW = items.Count * colW;
                double startX = -totalW / 2 + colW / 2;

                for (int i = 0; i < items.Count; i++)
                {
                    items[i].X = startX + i * colW;
                    items[i].Y = row.Key * rowH + 60;
                }
            }
        }

        // ── Graphviz layout helper ────────────────────────────────────────────

        private void RunGraphvizLayout(List<GraphNode> nodes, List<GraphEdge> edges,
            ref List<XDotNode>? xdotNodes, ref List<XDotEdge>? xdotEdges)
        {
            try
            {
                var dot = GenerateDot(nodes, edges);
                var tmpDot = Path.Combine(Path.GetTempPath(), "havok_graph.dot");
                File.WriteAllText(tmpDot, dot);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = GraphvizDotPath,
                    Arguments = $"-Txdot \"{tmpDot}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = System.Diagnostics.Process.Start(psi)!;
                string xdo = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                if (!string.IsNullOrEmpty(xdo))
                {
                    var (pn, pe, _) = ParseXDot(xdo);
                    xdotNodes = pn; xdotEdges = pe;

                    foreach (var xn in pn)
                    {
                        var sn = nodes.FirstOrDefault(n =>
                            $"n{n.Id.Replace("#", "")}" == xn.DotId);
                        if (sn == null) continue;
                        sn.X = xn.X; sn.Y = xn.Y;
                        sn.Width = Math.Max(xn.W, 180);
                        sn.Height = Math.Max(xn.H, 68);
                    }
                }
            }
            catch { ApplyLayout(nodes, edges); }
        }

        // ── Layout (state machine) ────────────────────────────────────────────

        private void ApplyLayout(List<GraphNode> nodes, List<GraphEdge> edges)
        {
            var machines = nodes.Select(n => n.Machine ?? "").Distinct().ToList();
            double offsetY = 80;
            foreach (var machine in machines)
            {
                var mn = nodes.Where(n => (n.Machine ?? "") == machine).ToList();
                if (mn.Count == 0) continue;
                int maxOut = mn.Max(n => edges.Count(e => e.From.Id == n.Id));
                bool hub = mn.Count > 3 && maxOut > mn.Count * 0.4;
                if (hub) RadialLayout(mn, edges, 480, offsetY + 300);
                else LayerLayout(mn, edges, 80, offsetY);
                offsetY = mn.Max(n => n.Y + n.Height) + 120;
            }
        }

        private void RadialLayout(List<GraphNode> nodes, List<GraphEdge> edges,
            double cx, double cy)
        {
            if (nodes.Count == 0) return;
            var hub = nodes.OrderByDescending(n =>
                edges.Count(e => e.From.Id == n.Id)).First();
            hub.X = cx - hub.Width / 2;
            hub.Y = cy - hub.Height / 2;

            var outgoing = edges.Where(e => e.From.Id == hub.Id)
                .Select(e => e.To).Distinct().Where(n => n.Id != hub.Id).ToList();
            var incoming = edges.Where(e => e.To.Id == hub.Id)
                .Select(e => e.From).Distinct()
                .Where(n => n.Id != hub.Id && !outgoing.Contains(n)).ToList();
            var isolated = nodes.Where(n => n.Id != hub.Id
                && !outgoing.Contains(n) && !incoming.Contains(n)).ToList();

            PlaceInArc(outgoing, cx, cy, 340, -75, 75);
            PlaceInArc(incoming, cx, cy, 300, 105, 255);
            for (int i = 0; i < isolated.Count; i++)
            { isolated[i].X = cx - 200 + i * 220; isolated[i].Y = cy + 300; }
        }

        private static void PlaceInArc(List<GraphNode> nodes,
            double cx, double cy, double r, double startDeg, double endDeg)
        {
            if (nodes.Count == 0) return;
            double step = nodes.Count == 1 ? 0 : (endDeg - startDeg) / (nodes.Count - 1);
            for (int i = 0; i < nodes.Count; i++)
            {
                double a = (startDeg + i * step) * Math.PI / 180;
                nodes[i].X = cx + r * Math.Cos(a) - nodes[i].Width / 2;
                nodes[i].Y = cy + r * Math.Sin(a) - nodes[i].Height / 2;
            }
        }

        private void LayerLayout(List<GraphNode> nodes, List<GraphEdge> edges,
            double startX, double startY)
        {
            if (nodes.Count == 0) return;
            var layers = new Dictionary<string, int>();
            var inDegree = nodes.ToDictionary(n => n.Id, _ => 0);
            foreach (var e in edges.Where(e => nodes.Contains(e.From) && nodes.Contains(e.To)))
                if (inDegree.ContainsKey(e.To.Id)) inDegree[e.To.Id]++;

            foreach (var n in nodes) layers[n.Id] = -1;
            var start = nodes.FirstOrDefault(n => n.IsStart)
                     ?? nodes.FirstOrDefault(n => inDegree[n.Id] == 0)
                     ?? nodes[0];
            layers[start.Id] = 0;

            var queue = new Queue<GraphNode>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var next in edges
                    .Where(e => e.From.Id == cur.Id && nodes.Contains(e.To))
                    .Select(e => e.To).Distinct())
                {
                    int nl = layers[cur.Id] + 1;
                    if (layers[next.Id] < nl) { layers[next.Id] = nl; queue.Enqueue(next); }
                }
            }

            int maxL = layers.Values.Where(v => v >= 0).DefaultIfEmpty(0).Max();
            foreach (var n in nodes.Where(n => layers[n.Id] < 0)) layers[n.Id] = ++maxL;

            var groups = nodes.GroupBy(n => layers[n.Id]).OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Name).ToList());

            for (int pass = 0; pass < 3; pass++)
                foreach (var lkv in groups.OrderBy(k => k.Key))
                {
                    if (!groups.TryGetValue(lkv.Key - 1, out var prev)) continue;
                    var pos = prev.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i);
                    groups[lkv.Key] = lkv.Value.OrderBy(n =>
                    {
                        var inc = edges.Where(e => e.To.Id == n.Id && pos.ContainsKey(e.From.Id))
                            .Select(e => (double)pos[e.From.Id]).ToList();
                        return inc.Count > 0 ? inc.Average() : double.MaxValue;
                    }).ToList();
                }

            double colW = 250, rowH = 90;
            int maxN = groups.Values.Max(g => g.Count);
            foreach (var lkv in groups)
            {
                var layer = lkv.Value;
                double yOff = startY + (maxN * rowH - layer.Count * rowH) / 2.0;
                for (int i = 0; i < layer.Count; i++)
                {
                    layer[i].X = startX + lkv.Key * colW;
                    layer[i].Y = yOff + i * rowH;
                }
            }
        }

        // ── Dot / XDot ────────────────────────────────────────────────────────

        // ── Delete node ───────────────────────────────────────────────────────

        private void DeleteNode(GraphNode node)
        {
            if (_manager == null || !_manager.ObjectMap.TryGetValue(node.Id, out var obj)) return;

            var className = obj.ClassName;
            var nodeName = obj.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? node.Id;

            // Find the parent SM that owns this state
            HkObject parentSM = null;
            HkParam statesParam = null;
            if (className == "hkbStateMachineStateInfo")
            {
                parentSM = _manager.ObjectMap.Values.FirstOrDefault(o =>
                    o.ClassName == "hkbStateMachine" &&
                    (o.Params.FirstOrDefault(p => p.Name == "states")
                        ?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Contains(node.Id) ?? false));
                statesParam = parentSM?.Params.FirstOrDefault(p => p.Name == "states");
            }

            if (MessageBox.Show(
                    $"Delete '{nodeName}' ({className})? 'This will remove the node from the model.Transitions referencing it will become broken.",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            // Remove from SM states list
            string oldStatesValue = statesParam?.Value;
            if (statesParam != null)
            {
                var ids = (statesParam.Value ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(id => id != node.Id).ToList();
                statesParam.Value = string.Join(" ", ids);
                statesParam.NumElements = ids.Count.ToString();
            }

            // Remove from ObjectMap
            _manager.ObjectMap.Remove(node.Id);

            // Fire for MainWindow undo recording
            NodeDeletedFromGraph?.Invoke(obj, parentSM, oldStatesValue);
            StatusText_?.Invoke($"✓ Deleted '{nodeName}'");

            // Rebuild
            var filter = MachineSelector.SelectedItem as string ?? "-- All Machines --";
            BuildStateMachineGraph(filter);
        }

        // ── Search / Go-To ────────────────────────────────────────────────────

        public void SearchAndGoTo(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { _visualHost.HighlightNode(null); return; }

            var match = _nodes.FirstOrDefault(n =>
                n.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                StatusText_?.Invoke($"No node matching '{query}'");
                return;
            }

            _visualHost.HighlightNode(match.Id);

            // Use CanvasBorder as viewport — it's the visible area
            double cW = CanvasBorder.ActualWidth > 0 ? CanvasBorder.ActualWidth : 800;
            double cH = CanvasBorder.ActualHeight > 0 ? CanvasBorder.ActualHeight : 500;

            // Stop ongoing animations so we start from the real current position
            _translate.BeginAnimation(TranslateTransform.XProperty, null);
            _translate.BeginAnimation(TranslateTransform.YProperty, null);

            double s = _zoom;
            double tx = cW / 2 - (match.X + match.Width / 2) * s;
            double ty = cH / 2 - (match.Y + match.Height / 2) * s;

            var animX = new System.Windows.Media.Animation.DoubleAnimation(
                _translate.X, tx, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            };
            var animY = new System.Windows.Media.Animation.DoubleAnimation(
                _translate.Y, ty, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            };
            // Commit final values after animation so pan works immediately after
            animX.Completed += (_, __) => { _translate.BeginAnimation(TranslateTransform.XProperty, null); _translate.X = tx; };
            animY.Completed += (_, __) => { _translate.BeginAnimation(TranslateTransform.YProperty, null); _translate.Y = ty; };
            _translate.BeginAnimation(TranslateTransform.XProperty, animX);
            _translate.BeginAnimation(TranslateTransform.YProperty, animY);

            _visualHost.UpdateMapTransform(s, tx, ty);
            StatusText_?.Invoke($"📍 '{match.Name}'");
        }


        // ── Graph bookmarks (Ctrl+1-9 save, 1-9 jump) ────────────────────────

        public void SaveGraphBookmark(int slot)
        {
            if (slot < 1 || slot > 9) return;
            var transform = GraphCanvas.RenderTransform as TransformGroup;
            var s = (transform?.Children[0] as ScaleTransform)?.ScaleX ?? 1;
            var tx = (transform?.Children[1] as TranslateTransform)?.X ?? 0;
            var ty = (transform?.Children[1] as TranslateTransform)?.Y ?? 0;
            var label = MachineSelector.SelectedItem as string ?? "Graph";
            _graphBookmarks[slot] = (s, tx, ty, label);
            StatusText_?.Invoke($"📌 Bookmark {slot} saved — {label}");
        }

        public void JumpToGraphBookmark(int slot)
        {
            if (slot < 1 || slot > 9 || _graphBookmarks[slot] == null) return;
            var (s, tx, ty, label) = _graphBookmarks[slot]!.Value;
            var transform = GraphCanvas.RenderTransform as TransformGroup;
            var scale = transform?.Children[0] as ScaleTransform;
            var translate = transform?.Children[1] as TranslateTransform;
            if (scale == null || translate == null) return;

            scale.ScaleX = scale.ScaleY = s;
            var animX = new System.Windows.Media.Animation.DoubleAnimation(
                translate.X, tx, TimeSpan.FromMilliseconds(300));
            var animY = new System.Windows.Media.Animation.DoubleAnimation(
                translate.Y, ty, TimeSpan.FromMilliseconds(300));
            translate.BeginAnimation(TranslateTransform.XProperty, animX);
            translate.BeginAnimation(TranslateTransform.YProperty, animY);
            _zoom = s;
            _visualHost.UpdateMapTransform(s, tx, ty);
            StatusText_?.Invoke($"📌 Jumped to bookmark {slot} — {label}");
        }

        // ── Alignment (selected nodes) ────────────────────────────────────────

        public void AlignSelectedNodes(bool horizontal)
        {
            var sel = _visualHost.GetSelectedNodes().ToList();
            if (sel.Count < 2) { StatusText_?.Invoke("Select 2+ nodes to align"); return; }

            if (horizontal)
            {
                // Q = row: align all to same Y (topmost), sort by X and space evenly
                double minY = sel.Min(n => n.Y);
                const double gap = 20;
                var sorted = sel.OrderBy(n => n.X).ToList();
                double x = sorted[0].X;
                foreach (var n in sorted)
                {
                    n.Y = minY;
                    n.X = x;
                    x += n.Width + gap;
                }
            }
            else
            {
                // W = column: same X left-edge, then sort by current Y and
                // space them evenly so they never overlap
                double minX = sel.Min(n => n.X);
                const double gap = 20;
                var sorted = sel.OrderBy(n => n.Y).ToList();
                double y = sorted[0].Y;
                foreach (var n in sorted)
                {
                    n.X = minX;
                    n.Y = y;
                    y += n.Height + gap;
                }
            }
            _visualHost.SetGraph(_nodes, _edges);
            StatusText_?.Invoke($"✓ Aligned {sel.Count} nodes ({(horizontal ? "row" : "column")})");
        }

        public void DistributeSelectedNodes(bool horizontal)
        {
            var sel = _visualHost.GetSelectedNodes().OrderBy(n => horizontal ? n.X : n.Y).ToList();
            if (sel.Count < 3) { StatusText_?.Invoke("Select 3+ nodes to distribute"); return; }

            const double minGap = 20;

            if (horizontal)
            {
                // Total span = from first node's left to last node's right
                double totalSpan = (sel.Last().X + sel.Last().Width) - sel.First().X;
                double totalWidth = sel.Sum(n => n.Width);
                double totalGap = Math.Max(totalSpan - totalWidth, minGap * (sel.Count - 1));
                double gap = totalGap / (sel.Count - 1);
                double x = sel.First().X;
                foreach (var n in sel) { n.X = x; x += n.Width + gap; }
            }
            else
            {
                double totalSpan = (sel.Last().Y + sel.Last().Height) - sel.First().Y;
                double totalHeight = sel.Sum(n => n.Height);
                double totalGap = Math.Max(totalSpan - totalHeight, minGap * (sel.Count - 1));
                double gap = totalGap / (sel.Count - 1);
                double y = sel.First().Y;
                foreach (var n in sel) { n.Y = y; y += n.Height + gap; }
            }
            _visualHost.SetGraph(_nodes, _edges);
            StatusText_?.Invoke($"✓ Distributed {sel.Count} nodes");
        }

        // ── Comment boxes ─────────────────────────────────────────────────────

        public void AddCommentAroundSelection()
        {
            var sel = _visualHost.GetSelectedNodes().ToList();
            if (sel.Count == 0) { StatusText_?.Invoke("Select nodes first, then press C"); return; }

            const double pad = 24;
            double minX = sel.Min(n => n.X) - pad;
            double minY = sel.Min(n => n.Y) - pad - 18; // header room
            double maxX = sel.Max(n => n.X + n.Width) + pad;
            double maxY = sel.Max(n => n.Y + n.Height) + pad;

            var colors = new[]
            {
                Color.FromRgb(0x60, 0x60, 0x20),
                Color.FromRgb(0x20, 0x50, 0x30),
                Color.FromRgb(0x40, 0x20, 0x60),
                Color.FromRgb(0x50, 0x30, 0x10),
            };
            var comment = new GraphComment
            {
                Title = "Comment",
                X = minX,
                Y = minY,
                Width = maxX - minX,
                Height = maxY - minY,
                Color = colors[_visualHost.Comments.Count % colors.Length]
            };
            _visualHost.Comments.Add(comment);
            _visualHost.RaiseCommentAdded(comment);
            _visualHost.DrawComments();          // exposed below
            _visualHost.CommentContextMenuRequested += ShowCommentContextMenu;
            StatusText_?.Invoke("✓ Comment box added — double-click to rename");
        }

        // ── Update FitToView and zoom to sync minimap ─────────────────────────

        private void SyncMinimap()
        {
            _visualHost.UpdateMapTransform(_zoom, _translate.X, _translate.Y);
            RedrawMinimapOverlay();
        }


        // ── WPF Minimap overlay (sits outside transform, top-right of panel) ───

        private System.Windows.Controls.Canvas _minimapCanvas;

        private System.Windows.Controls.Canvas _minimapOverlayHost;

        private void EnsureMinimapCanvas()
        {
            if (_minimapCanvas != null) return;

            // Swap CanvasBorder.Child: Border → Grid → [GraphCanvas, minimap overlay]
            // This keeps the minimap OUTSIDE GraphCanvas's RenderTransform.
            var existingChild = CanvasBorder.Child;   // this is GraphCanvas
            var grid = new System.Windows.Controls.Grid();
            CanvasBorder.Child = grid;

            if (existingChild != null)
                grid.Children.Add(existingChild);     // GraphCanvas back in, row 0

            // Overlay canvas for the minimap — sits on top, not transformed
            _minimapOverlayHost = new System.Windows.Controls.Canvas
            {
                IsHitTestVisible = false,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch
            };
            grid.Children.Add(_minimapOverlayHost);   // same row, z-order above

            _minimapCanvas = new System.Windows.Controls.Canvas
            {
                Width = 160,
                Height = 100,
                IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x12, 0x1A))
            };
            var border = new System.Windows.Controls.Border
            {
                Width = 162,
                Height = 102,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0x44, 0x44, 0x55)),
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(4),
                IsHitTestVisible = false,
                Child = _minimapCanvas
            };
            _minimapOverlayHost.Children.Add(border);

            // Reposition whenever the host resizes
            _minimapOverlayHost.SizeChanged += (_, __) => PositionMinimap(border);
            PositionMinimap(border);
        }

        private void PositionMinimap(System.Windows.Controls.Border border)
        {
            double pad = 8;
            double host = _minimapOverlayHost?.ActualWidth ?? CanvasBorder.ActualWidth;
            double left = host > 0 ? host - border.Width - pad : 0;
            System.Windows.Controls.Canvas.SetLeft(border, left);
            System.Windows.Controls.Canvas.SetTop(border, pad);
        }

        private void RedrawMinimapOverlay()
        {
            Dispatcher.InvokeAsync(() =>
            {
                EnsureMinimapCanvas();
                _minimapCanvas.Children.Clear();
                if (_nodes.Count == 0) return;

                double minX = _nodes.Min(n => n.X), maxX = _nodes.Max(n => n.X + n.Width);
                double minY = _nodes.Min(n => n.Y), maxY = _nodes.Max(n => n.Y + n.Height);
                double gW = Math.Max(maxX - minX, 1), gH = Math.Max(maxY - minY, 1);
                double ms = Math.Min(144.0 / gW, 84.0 / gH); // fit into 144x84 with 8px padding

                // Node dots
                foreach (var n in _nodes)
                {
                    double nx = 8 + (n.X - minX) * ms;
                    double ny = 8 + (n.Y - minY) * ms;
                    double nw = Math.Max(n.Width * ms, 4);
                    double nh = Math.Max(n.Height * ms, 3);

                    var colors = new Dictionary<GraphNodeType, Color>
                    {
                        [GraphNodeType.StateMachine] = Color.FromRgb(0x7C, 0x4D, 0xBB),
                        [GraphNodeType.State] = Color.FromRgb(0x1A, 0x7A, 0xCC),
                        [GraphNodeType.Clip] = Color.FromRgb(0x2E, 0xA0, 0x4A),
                        [GraphNodeType.Modifier] = Color.FromRgb(0xCC, 0x6E, 0x1A),
                        [GraphNodeType.Blender] = Color.FromRgb(0x1A, 0xAA, 0xAA),
                        [GraphNodeType.Unknown] = Color.FromRgb(0x5A, 0x5A, 0x60),
                    };
                    colors.TryGetValue(n.NodeType, out var col);

                    var rect = new System.Windows.Shapes.Rectangle
                    {
                        Width = nw,
                        Height = nh,
                        Fill = new SolidColorBrush(col)
                    };
                    System.Windows.Controls.Canvas.SetLeft(rect, nx);
                    System.Windows.Controls.Canvas.SetTop(rect, ny);
                    _minimapCanvas.Children.Add(rect);
                }

                // Viewport rect
                double cW = CanvasBorder.ActualWidth;
                double cH = CanvasBorder.ActualHeight;
                if (_zoom > 0 && cW > 0)
                {
                    double vx = 8 + (-_translate.X / _zoom - minX) * ms;
                    double vy = 8 + (-_translate.Y / _zoom - minY) * ms;
                    double vw = (cW / _zoom) * ms;
                    double vh = (cH / _zoom) * ms;

                    var vRect = new System.Windows.Shapes.Rectangle
                    {
                        Width = Math.Min(vw, 144),
                        Height = Math.Min(vh, 84),
                        Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                        StrokeThickness = 1,
                        Fill = new SolidColorBrush(Color.FromArgb(0x11, 0xFF, 0xFF, 0xFF))
                    };
                    System.Windows.Controls.Canvas.SetLeft(vRect, Math.Max(8, Math.Min(vx, 152 - vw)));
                    System.Windows.Controls.Canvas.SetTop(vRect, Math.Max(8, Math.Min(vy, 92 - vh)));
                    _minimapCanvas.Children.Add(vRect);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        // ── Helper: generate safe new object ID ──────────────────────────────
        private string GenerateNewObjectId()
        {
            var existing = _manager.ObjectMap.Keys
                .Where(k => k.StartsWith("#"))
                .Select(k => int.TryParse(k.Substring(1), out int n) ? n : 0)
                .ToHashSet();
            int next = 1;
            while (existing.Contains(next)) next++;
            return $"#{next:D4}";
        }

        // ── Inline comment rename ────────────────────────────────────────────────

        private void StartInlineCommentRename(GraphComment comment)
        {
            var tb = new System.Windows.Controls.TextBox
            {
                Text = comment.Title,
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x22)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x20)),
                BorderThickness = new System.Windows.Thickness(1.5),
                Padding = new System.Windows.Thickness(4, 2, 4, 2),
                MinWidth = 160
            };

            // Position over the comment title area
            double s = _zoom;
            double ox = comment.X * s + _translate.X;
            double oy = comment.Y * s + _translate.Y;
            System.Windows.Controls.Canvas.SetLeft(tb, ox + 8);
            System.Windows.Controls.Canvas.SetTop(tb, oy + 4);

            void Commit()
            {
                var newTitle = tb.Text?.Trim();
                GraphCanvas.Children.Remove(tb);
                if (!string.IsNullOrEmpty(newTitle))
                {
                    comment.Title = newTitle;
                    _visualHost.DrawComments();
                }
                Keyboard.Focus(this);
            }

            tb.KeyDown += (_, ke) =>
            {
                if (ke.Key == System.Windows.Input.Key.Enter) { Commit(); ke.Handled = true; }
                if (ke.Key == System.Windows.Input.Key.Escape)
                {
                    GraphCanvas.Children.Remove(tb);
                    Keyboard.Focus(this); // ← add this
                    ke.Handled = true;
                }
            };
            tb.LostFocus += (_, __) => Commit();

            GraphCanvas.Children.Add(tb);
            tb.Focus();
            tb.SelectAll();
        }

        // ── Passthrough: start rename on selected node ─────────────────────────
        public void RequestRenameSelected()
        {
            if (_visualHost.SelectedNode != null)
                StartInlineRename(_visualHost.SelectedNode);
        }

        private string GenerateDot(List<GraphNode> nodes, List<GraphEdge> edges)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("digraph G {");
            sb.AppendLine("  rankdir=LR; splines=curved; nodesep=0.6; ranksep=1.4;");
            sb.AppendLine("  node [shape=rect, width=2.2, height=0.8];");
            foreach (var n in nodes)
                sb.AppendLine($"  \"n{n.Id.Replace("#", "")}\" [label=\"{EscDot(n.Name)}\"];");
            foreach (var e in edges)
                sb.AppendLine($"  \"n{e.From.Id.Replace("#", "")}\" -> " +
                              $"\"n{e.To.Id.Replace("#", "")}\" " +
                              $"[label=\"{EscDot(e.EventName)}\"];");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string EscDot(string s) => (s ?? "").Replace("\"", "'").Replace("\n", " ");

        private class XDotNode { public string DotId; public double X, Y, W, H; }
        private class XDotEdge { public List<Point> BezierPoints = new(); public Point ArrowHead; public string Label; }

        private (List<XDotNode>, List<XDotEdge>, double) ParseXDot(string xdot)
        {
            var nodes = new List<XDotNode>();
            var edges = new List<XDotEdge>();
            double graphH = 800, scale = 1.5;

            var bbM = System.Text.RegularExpressions.Regex.Match(xdot,
                @"bb=""[\d.]+,[\d.]+,([\d.]+),([\d.]+)""");
            if (bbM.Success)
                graphH = double.Parse(bbM.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture);

            double Dbl(string s) =>
                double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

            var nrx = new System.Text.RegularExpressions.Regex(
                @"""?(n\w+)""?\s*\[([^\]]+)\]",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            foreach (System.Text.RegularExpressions.Match m in nrx.Matches(xdot))
            {
                var attrs = m.Groups[2].Value;
                var posM = System.Text.RegularExpressions.Regex.Match(attrs, @"pos=""([\d.]+),([\d.]+)""");
                var wM = System.Text.RegularExpressions.Regex.Match(attrs, @"width=""([\d.]+)""");
                var hM = System.Text.RegularExpressions.Regex.Match(attrs, @"height=""([\d.]+)""");
                if (!posM.Success) continue;
                double x = Dbl(posM.Groups[1].Value) * scale;
                double y = (graphH - Dbl(posM.Groups[2].Value)) * scale;
                double w = wM.Success ? Dbl(wM.Groups[1].Value) * 72 * scale : 180;
                double h = hM.Success ? Dbl(hM.Groups[1].Value) * 72 * scale : 68;
                nodes.Add(new XDotNode
                { DotId = m.Groups[1].Value, X = x - w / 2, Y = y - h / 2, W = w, H = h });
            }

            var erx = new System.Text.RegularExpressions.Regex(
                @"""?(n\w+)""?\s*->\s*""?(n\w+)""?\s*\[([^\]]+)\]",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            foreach (System.Text.RegularExpressions.Match m in erx.Matches(xdot))
            {
                var attrs = m.Groups[3].Value;
                var posM = System.Text.RegularExpressions.Regex.Match(attrs, @"pos=""e,([\d.,\s]+)""");
                var lblM = System.Text.RegularExpressions.Regex.Match(attrs, @"label=""([^""]+)""");
                var edge = new XDotEdge { Label = lblM.Success ? lblM.Groups[1].Value : "" };
                if (posM.Success)
                {
                    var coords = posM.Groups[1].Value.Split(
                        new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (coords.Length >= 2)
                        edge.ArrowHead = new Point(Dbl(coords[0]) * scale,
                            (graphH - Dbl(coords[1])) * scale);
                    for (int i = 2; i + 1 < coords.Length; i += 2)
                        edge.BezierPoints.Add(new Point(Dbl(coords[i]) * scale,
                            (graphH - Dbl(coords[i + 1])) * scale));
                }
                edges.Add(edge);
            }
            return (nodes, edges, graphH * scale);
        }

        // ── Pan / zoom / fit ──────────────────────────────────────────────────

        private void BtnPanToActive_Click(object sender, RoutedEventArgs e)
        {
            _panToActive = !_panToActive;
            BtnPanToActive.Opacity = _panToActive ? 1.0 : 0.5;
            BtnPanToActive.ToolTip = _panToActive
                ? "Pan to active node (on)" : "Pan to active node (off)";
            StatusText_?.Invoke(_panToActive
                ? "🎯 Pan-to-active ON" : "🎯 Pan-to-active OFF");
        }

        // ── Graph keyboard shortcuts ──────────────────────────────────────────

        private void OnGraphKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox tb
        && GraphCanvas.Children.Contains(tb))
                return;

            bool ctrl = System.Windows.Input.Keyboard.Modifiers
                .HasFlag(System.Windows.Input.ModifierKeys.Control);

            // Ctrl+1-9 = save bookmark, 1-9 = jump
            int digit = e.Key switch
            {
                System.Windows.Input.Key.D1 or System.Windows.Input.Key.NumPad1 => 1,
                System.Windows.Input.Key.D2 or System.Windows.Input.Key.NumPad2 => 2,
                System.Windows.Input.Key.D3 or System.Windows.Input.Key.NumPad3 => 3,
                System.Windows.Input.Key.D4 or System.Windows.Input.Key.NumPad4 => 4,
                System.Windows.Input.Key.D5 or System.Windows.Input.Key.NumPad5 => 5,
                System.Windows.Input.Key.D6 or System.Windows.Input.Key.NumPad6 => 6,
                System.Windows.Input.Key.D7 or System.Windows.Input.Key.NumPad7 => 7,
                System.Windows.Input.Key.D8 or System.Windows.Input.Key.NumPad8 => 8,
                System.Windows.Input.Key.D9 or System.Windows.Input.Key.NumPad9 => 9,
                _ => 0
            };
            if (digit > 0)
            {
                if (ctrl) SaveGraphBookmark(digit);
                else JumpToGraphBookmark(digit);
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                // C = wrap selection in comment box
                case System.Windows.Input.Key.C when !ctrl:
                    AddCommentAroundSelection();
                    e.Handled = true;
                    break;

                // Q = align selected nodes to row (horizontal)
                case System.Windows.Input.Key.Q when !ctrl:
                    AlignSelectedNodes(horizontal: true);
                    e.Handled = true;
                    break;

                // W = align selected nodes to column (vertical)  
                case System.Windows.Input.Key.W when !ctrl:
                    AlignSelectedNodes(horizontal: false);
                    e.Handled = true;
                    break;

                // E = distribute evenly horizontal
                case System.Windows.Input.Key.E when !ctrl:
                    DistributeSelectedNodes(horizontal: true);
                    e.Handled = true;
                    break;

                // Delete / Backspace = delete selected node
                case System.Windows.Input.Key.Delete:
                case System.Windows.Input.Key.Back:
                    var sel = _visualHost.SelectedNode;
                    if (sel != null) DeleteNode(sel);
                    e.Handled = true;
                    break;

                // F = fit to view
                case System.Windows.Input.Key.F when !ctrl:
                    FitToView();
                    e.Handled = true;
                    break;

                // Escape = clear selection + highlight
                case System.Windows.Input.Key.Escape:
                    _visualHost.HighlightNode(null);
                    e.Handled = true;
                    break;
            }
        }

        private void FitToView()
        {
            if (_nodes.Count == 0) return;

            double cW = CanvasBorder.ActualWidth;
            double cH = CanvasBorder.ActualHeight;

            if (cW <= 0 || cH <= 0)
            {
                _fitPending = true; 
                return;
            }

            _fitPending = false;

            double minX = _nodes.Min(n => n.X);
            double minY = _nodes.Min(n => n.Y);
            double maxX = _nodes.Max(n => n.X + n.Width);
            double maxY = _nodes.Max(n => n.Y + n.Height);

            double graphW = maxX - minX;
            double graphH = maxY - minY;

            double scaleX = (cW - 80) / Math.Max(graphW, 1);
            double scaleY = (cH - 80) / Math.Max(graphH, 1);
            _zoom = Math.Clamp(Math.Min(scaleX, scaleY), 0.05, 2.0);

            _translate.X = (cW - graphW * _zoom) / 2.0 - minX * _zoom;
            _translate.Y = (cH - graphH * _zoom) / 2.0 - minY * _zoom;

            ApplyTransform();
            SyncMinimap();
        }

        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            double newZoom = Math.Clamp(_zoom * factor, 0.05, 4.0);

            var pos = e.GetPosition(CanvasBorder);
            double mouseX = (pos.X - _translate.X) / _zoom;
            double mouseY = (pos.Y - _translate.Y) / _zoom;

            _zoom = newZoom;
            _translate.X = pos.X - mouseX * _zoom;
            _translate.Y = pos.Y - mouseY * _zoom;

            ApplyTransform();
            SyncMinimap();
            e.Handled = true;
        }

        private void ApplyTransform()
        {
            _scale.ScaleX = _zoom;
            _scale.ScaleY = _zoom;
            // _translate is already bound to the TransformGroup, no extra set needed
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Pan now handled by middle mouse in GraphVisualHost; left-click is lasso.
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isPanning = false;
            if (CanvasBorder.IsMouseCaptured)
                CanvasBorder.ReleaseMouseCapture();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Only pan if CanvasBorder itself captured (not _visualHost)
            if (!_isPanning || !CanvasBorder.IsMouseCaptured) return;
            var pos = e.GetPosition(CanvasBorder);
            _translate.X += pos.X - _panStart.X;
            _translate.Y += pos.Y - _panStart.Y;
            _panStart = pos;
            SyncMinimap();
        }

        // ── Search box handlers ───────────────────────────────────────────────

        private void GraphSearchBox_KeyDown(object sender,
            System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                SearchAndGoTo((sender as System.Windows.Controls.TextBox)?.Text ?? "");
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (sender is System.Windows.Controls.TextBox tb) tb.Text = "";
                _visualHost.HighlightNode(null);
                e.Handled = true;
            }
        }

        private void GraphSearchBox_TextChanged(object sender,
            System.Windows.Controls.TextChangedEventArgs e)
        {
            var q = (sender as System.Windows.Controls.TextBox)?.Text ?? "";
            // Live filter: highlight as you type (no pan until Enter)
            if (string.IsNullOrWhiteSpace(q)) { _visualHost.HighlightNode(null); return; }
            var match = _nodes.FirstOrDefault(n =>
                n.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            _visualHost.HighlightNode(match?.Id);
        }

        private void GraphSearchClear_Click(object sender,
            System.Windows.RoutedEventArgs e)
        {
            if (GraphSearchBox != null) GraphSearchBox.Text = "";
            _visualHost.HighlightNode(null);
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        private void MachineSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MachineSelector.SelectedItem is string m)
            {
                _navStack.Clear();
                _currentLevel = null;
                UpdateBreadcrumb();
                BuildStateMachineGraph(m);
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) => NavigateBack();

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
            => _visualHost.SetSearchQuery(SearchBox.Text);

        private void BtnLayout_Click(object sender, RoutedEventArgs e)
        {
            if (_currentLevel?.Level == GraphViewLevel.GeneratorHierarchy)
                BuildGeneratorGraph(_currentLevel.RootObjectId,
                    _currentLevel.Label);
            else if (MachineSelector.SelectedItem is string m)
                BuildStateMachineGraph(m);
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Min(_zoom * 1.2, 5.0);
            _scale.ScaleX = _zoom; _scale.ScaleY = _zoom;
        }

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Max(_zoom * 0.8, 0.1);
            _scale.ScaleX = _zoom; _scale.ScaleY = _zoom;
        }

        private void BtnFit_Click(object sender, RoutedEventArgs e) => FitToView();

        private void BtnExportPng_Click(object sender, RoutedEventArgs e)
        {
            if (_nodes.Count == 0) return;
            var sfd = new Microsoft.Win32.SaveFileDialog
            { Filter = "PNG Image|*.png", FileName = "graph" };
            if (sfd.ShowDialog() != true) return;

            var bounds = new Rect(
                _nodes.Min(n => n.X) - 20, _nodes.Min(n => n.Y) - 20,
                _nodes.Max(n => n.X + n.Width) + 40,
                _nodes.Max(n => n.Y + n.Height) + 40);

            var oldT = GraphCanvas.RenderTransform;
            GraphCanvas.RenderTransform = Transform.Identity;
            var rtb = new RenderTargetBitmap(
                (int)bounds.Width, (int)bounds.Height, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
                    null, bounds);
            rtb.Render(dv);
            rtb.Render(GraphCanvas);
            GraphCanvas.RenderTransform = oldT;

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var stream = File.OpenWrite(sfd.FileName);
            enc.Save(stream);
            MessageBox.Show($"Exported to {sfd.FileName}");
        }

        // ── BuildGraph (called from MachineSelector) ──────────────────────────
        // Keeping old name so existing wiring still works
        private void BuildGraph(string machineFilter) => BuildStateMachineGraph(machineFilter);
    }
}