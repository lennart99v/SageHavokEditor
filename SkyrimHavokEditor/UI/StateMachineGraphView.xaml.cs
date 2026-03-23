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

    public partial class StateMachineGraphView : UserControl
    {
        // ── Data ──────────────────────────────────────────────────────────────
        private HavokManager _manager;
        private List<IdNamePair> _events = new();

        // ── Graph state ───────────────────────────────────────────────────────
        private List<GraphNode> _nodes = new();
        private List<GraphEdge> _edges = new();
        private GraphVisualHost _visualHost;

        // ── Navigation stack ──────────────────────────────────────────────────
        private readonly Stack<GraphBreadcrumb> _navStack = new();
        private GraphBreadcrumb _currentLevel;

        // ── Pan / zoom ────────────────────────────────────────────────────────
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _translate = new(0, 0);
        private Point _panStart;
        private bool _isPanning;
        private double _zoom = 1.0;

        // ── Events ────────────────────────────────────────────────────────────
        public event Action<string> StateSelected;
        public event Action<string, string> AddTransitionRequested;

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

            _visualHost = new GraphVisualHost();

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

            GraphCanvas.Children.Clear();
            GraphCanvas.Children.Add(_visualHost);

            var tg = new TransformGroup();
            tg.Children.Add(_scale);
            tg.Children.Add(_translate);
            GraphCanvas.RenderTransform = tg;
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Load(HavokManager manager, List<IdNamePair> events)
        {
            _manager = manager;
            _events = events;

            var machines = manager.ObjectMap.Values
                .Where(o => o.ClassName == "hkbStateMachine")
                .Select(o => o.Params.FirstOrDefault(p => p.Name == "name")?.Value ?? o.Id)
                .OrderBy(n => n).ToList();

            MachineSelector.Items.Clear();
            MachineSelector.Items.Add("-- All Machines --");
            foreach (var m in machines) MachineSelector.Items.Add(m);
            MachineSelector.SelectedIndex = machines.Count > 0 ? 1 : 0;
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
            menu.Items.Add(new Separator());
            menu.Items.Add(copyId);
            menu.Items.Add(copyName);
            menu.IsOpen = true;
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
                            Tag = stateObj
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
                            Flags = Get("flags")
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
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(FitToView));
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

                List<XDotNode>? xn = null;
                List<XDotEdge>? xe = null;
                if (useGv) RunGraphvizLayout(localNodes, localEdges, ref xn, ref xe);
                else ApplyTreeLayout(localNodes, localEdges);
            });

            _nodes = localNodes;
            _edges = localEdges;
            _visualHost.SetGraph(_nodes, _edges);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(FitToView));
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

        private void FitToView()
        {
            if (_nodes.Count == 0) return;
            double minX = _nodes.Min(n => n.X), minY = _nodes.Min(n => n.Y);
            double maxX = _nodes.Max(n => n.X + n.Width), maxY = _nodes.Max(n => n.Y + n.Height);
            double gW = maxX - minX + 80, gH = maxY - minY + 80;
            double cW = CanvasBorder.ActualWidth > 0 ? CanvasBorder.ActualWidth : 800;
            double cH = CanvasBorder.ActualHeight > 0 ? CanvasBorder.ActualHeight : 500;
            _zoom = Math.Clamp(Math.Min(cW / gW, cH / gH) * 0.9, 0.1, 2.0);
            _scale.ScaleX = _zoom; _scale.ScaleY = _zoom;
            _translate.X = (cW - gW * _zoom) / 2 - minX * _zoom;
            _translate.Y = (cH - gH * _zoom) / 2 - minY * _zoom;
        }

        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 0.9), 0.1, 5.0);
            _scale.ScaleX = _zoom; _scale.ScaleY = _zoom;
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Canvas ||
                e.OriginalSource == _visualHost)
            {
                _isPanning = true;
                _panStart = e.GetPosition(CanvasBorder);
                CanvasBorder.CaptureMouse();
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isPanning = false;
            CanvasBorder.ReleaseMouseCapture();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var pos = e.GetPosition(CanvasBorder);
            _translate.X += pos.X - _panStart.X;
            _translate.Y += pos.Y - _panStart.Y;
            _panStart = pos;
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