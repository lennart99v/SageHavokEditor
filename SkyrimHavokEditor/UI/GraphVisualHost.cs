using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SkyrimHavokEditor.Core;

namespace SkyrimHavokEditor.UI
{
    public enum GraphNodeType
    {
        StateMachine,   // hkbStateMachine          — purple header
        State,          // hkbStateMachineStateInfo  — blue header
        Clip,           // hkbClipGenerator          — green header
        Modifier,       // hkbModifier* etc          — orange header
        Blender,        // hkbBlender* etc           — teal header
        Unknown         // everything else           — grey header
    }

    public class GraphVisualHost : FrameworkElement
    {
        // ── Layer visuals ─────────────────────────────────────────────────────
        private readonly DrawingVisual _edgeLayer = new();
        private readonly DrawingVisual _labelLayer = new();
        private readonly DrawingVisual _draftLayer = new();
        private readonly DrawingVisual _overlayLayer = new();
        private readonly List<NodeVisual> _nodeVisuals = new();
        private readonly VisualCollection _visuals;
        private double _currentZoom = 1.0;  // updated by StateMachineGraphView via UpdateMapTransform

        // ── Graph state ───────────────────────────────────────────────────────
        private List<GraphNode> _nodes = new();
        private List<GraphEdge> _edges = new();
        private GraphNode? _selectedNode;
        public GraphNode? SelectedNode => _selectedNode;
        private HashSet<GraphNode> _selectedNodes = new();   // multi-select
        private GraphNode? _draggingNode;
        private GraphNode? _hoveredNode;
        private GraphEdge? _hoveredEdge;
        private GraphNode? _connectingFrom;
        private GraphNode? _connectHoverNode;
        private Point _connectingTo;
        private Point _dragOffset;
        private string _searchQuery = "";
        private double _dpi = 96;
        private string? _highlightId = null;  // search highlight
        private HashSet<string> _liveStateIds = new();
        private List<VariableValue> _liveVars = new();
        private double _pulsePhase = 0;
        private readonly System.Windows.Threading.DispatcherTimer _pulseTimer;
        private readonly Dictionary<GraphEdge, double> _firedEdges = new();
        private readonly DrawingVisual _guideLayer = new();
        private const double SnapBase = 8; // screen px at zoom 1.0
        private GraphEdge? _rewiringEdge;

        // ── Lasso selection ────────────────────────────────────────────────
        private bool _isLassoing;
        private Point _lassoStart;
        private Point _lassoEnd;

        // ── Comment interaction state ─────────────────────────────────────────────
        private enum ResizeHandleType { None, NW, N, NE, E, SE, S, SW, W }
        private const double HandleSize = 8;
        private const double MinCommentSize = 60;

        private GraphComment? _hoveredComment;
        private GraphComment? _draggingComment;
        private Point _commentDragOffset;
        private List<(GraphNode node, double ox, double oy)> _commentDraggedNodes = new();
        private GraphComment? _resizingComment;
        private ResizeHandleType _resizingHandle = ResizeHandleType.None;
        private Point _resizeStartMouse;
        private Rect _resizeStartRect;

        // ── Host-driven pan ───────────────────────────────────────────────
        private bool _isPanningFromHost;
        private Point _panStartPoint;

        // ── Comment boxes ─────────────────────────────────────────────────
        public List<GraphComment> Comments { get; } = new();

        // ── Events ────────────────────────────────────────────────────────────
        public event Action<GraphNode>? NodeSelected;
        public event Action<GraphNode>? NodeDoubleClicked;
        public event Action<GraphNode>? NodeMoved;
        public event Action<GraphNode, GraphNode>? ConnectionRequested;
        public event Action<GraphNode>? NodeContextMenuRequested;
        public event Action<GraphEdge>? EdgeContextMenuRequested;
        public event Action<GraphNode>? NodeRenameRequested;
        public event Action<Point>? CanvasContextMenuRequested;
        public event Action<HashSet<GraphNode>>? SelectionChanged;
        public event Action<GraphComment>? CommentAdded;
        public event Action<GraphComment>? CommentDoubleClicked;
        public event Action<GraphComment>? CommentContextMenuRequested;
        public event Action<double, double>? PanDelta;      // dx, dy from empty-space drag
        public event Action? MapTransformChanged;
        public event Action<GraphNode?, Point>? NodeHoverChanged;
        public event Action<GraphEdge?, Point>? EdgeHoverChanged;
        public event Action<GraphEdge, GraphNode>? EdgeRewireRequested;

        public Func<GraphNode, GraphNode, bool>? ConnectionValidator;
        public void RaiseCommentAdded(GraphComment c) => CommentAdded?.Invoke(c);

        // ── Node palette ──────────────────────────────────────────────────────
        private record NodePalette(Color Header, Color HeaderText, Color Body, Color Border);

        private static readonly Dictionary<GraphNodeType, NodePalette> _palette = new()
        {
            [GraphNodeType.StateMachine] = new(
        Color.FromRgb(0x5C, 0x3A, 0x99), Color.FromRgb(0xE8, 0xE8, 0xFF),
        Color.FromRgb(0x28, 0x1A, 0x42), Color.FromRgb(0xA0, 0x70, 0xFF)),
            [GraphNodeType.State] = new(
        Color.FromRgb(0x0D, 0x65, 0xBB), Color.FromRgb(0xE8, 0xF4, 0xFF),
        Color.FromRgb(0x0A, 0x32, 0x5A), Color.FromRgb(0x4F, 0xC3, 0xF7)),
            [GraphNodeType.Clip] = new(
        Color.FromRgb(0x1E, 0x7A, 0x38), Color.FromRgb(0xE8, 0xFF, 0xEC),
        Color.FromRgb(0x0E, 0x3D, 0x1C), Color.FromRgb(0x4C, 0xD9, 0x6E)),
            [GraphNodeType.Modifier] = new(
        Color.FromRgb(0xA0, 0x52, 0x0C), Color.FromRgb(0xFF, 0xF0, 0xE0),
        Color.FromRgb(0x4A, 0x26, 0x08), Color.FromRgb(0xFF, 0x99, 0x44)),
            [GraphNodeType.Blender] = new(
        Color.FromRgb(0x0C, 0x7A, 0x7A), Color.FromRgb(0xE0, 0xFF, 0xFF),
        Color.FromRgb(0x08, 0x3C, 0x3C), Color.FromRgb(0x22, 0xDD, 0xDD)),
            [GraphNodeType.Unknown] = new(
        Color.FromRgb(0x4A, 0x4A, 0x50), Color.FromRgb(0xCC, 0xCC, 0xCC),
        Color.FromRgb(0x26, 0x26, 0x2A), Color.FromRgb(0x88, 0x88, 0x99)),
        };

        // ── Shared frozen resources ───────────────────────────────────────────
        private static readonly Pen _selectedPen = FP(Color.FromRgb(0x4F, 0xC3, 0xF7), 2.5);
        private static readonly Pen _highlightPen = FP(Colors.Goldenrod, 2.5);
        private static readonly Pen _edgePen = FP(Color.FromRgb(0x54, 0x6E, 0x7A), 1.5);
        private static readonly Brush _textPrimary = FB(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)));
        private static readonly Brush _textSecondary = FB(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)));
        private static readonly Brush _textEvent = FB(new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)));
        private static readonly Brush _labelBg = FB(new SolidColorBrush(Color.FromArgb(220, 15, 15, 20)));
        private static readonly Pen _labelBorder = FP(Color.FromRgb(0x5F, 0x3F, 0x7F), 1);
        private static readonly Brush _arrowFill = FB(new SolidColorBrush(Color.FromRgb(0x54, 0x6E, 0x7A)));
        private static readonly Brush _arrowHover = FB(new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)));
        private static readonly Brush _portFill = FB(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)));
        private static readonly Pen _portPen = FP(Color.FromRgb(0x4F, 0xC3, 0xF7), 1);
        private static readonly Brush _startBadge = FB(new SolidColorBrush(Color.FromRgb(0x89, 0xD1, 0x85)));
        private static readonly Typeface _font = new("Segoe UI");

        private const double HeaderH = 22;
        private const double PortR = 5;

        public GraphVisualHost()
        {
            _visuals = new VisualCollection(this);
            _visuals.Add(_edgeLayer);
            _visuals.Add(_labelLayer);
            _visuals.Add(_commentLayer);
            _visuals.Add(_guideLayer);
            _visuals.Add(_draftLayer);
            _visuals.Add(_overlayLayer);

            MouseMove += OnMouseMove;
            MouseLeftButtonDown += OnMouseDown;
            MouseLeftButtonUp += OnMouseUp;
            MouseRightButtonDown += OnRightClick;
            MouseDown += OnAnyMouseDown;   // middle-button pan
            MouseUp += OnAnyMouseUp;
            MouseLeave += (_, __) =>
            {
                if (_hoveredNode != null) { var n = _hoveredNode; _hoveredNode = null; RedrawNode(n); }
                _hoveredEdge = null;
                NodeHoverChanged?.Invoke(null, default);
                EdgeHoverChanged?.Invoke(null, default);
            };
            _pulseTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            // Pulse timer tick — redraw all live nodes
            _pulseTimer.Tick += (_, __) =>
            {
                if (_liveStateIds.Count > 0)
                {
                    _pulsePhase = (_pulsePhase + 0.08) % (2 * Math.PI);
                    foreach (var id in _liveStateIds)
                        RedrawNode(_nodes.FirstOrDefault(n => n.Id == id));
                }
                if (_firedEdges.Count > 0)
                {
                    foreach (var e in _firedEdges.Keys.ToList())
                    { _firedEdges[e] -= 0.05; if (_firedEdges[e] <= 0) _firedEdges.Remove(e); }
                    DrawAllEdges();
                }
                if (_liveStateIds.Count == 0 && _firedEdges.Count == 0)
                    _pulseTimer.Stop();
            };
        }

        // ── Public API ─────────────────────────────────────────────────────────

        public void SetGraph(List<GraphNode> nodes, List<GraphEdge> edges)
        {
            _nodes = nodes;
            _edges = edges;
            _selectedNode = null;
            _selectedNodes.Clear();
            _hoveredEdge = null;
            _hoveredNode = null;
            _isLassoing = false;
            _firedEdges.Clear();
            _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RebuildNodeVisuals();
            DrawAllEdges();
            DrawComments();
            ClearOverlay();
        }

        public void HighlightNode(string nodeId)
        {
            _highlightId = nodeId;
            DrawAllNodes();
            DrawAllEdges();
        }

        public IReadOnlySet<GraphNode> GetSelectedNodes() => _selectedNodes;

        public void SetSearchQuery(string query)
        {
            _searchQuery = query ?? "";
            DrawAllNodes();
        }

        public void SelectNode(GraphNode node)
        {
            var prev = _selectedNode;
            _selectedNode = node;
            RedrawNode(prev);
            RedrawNode(node);
        }

        public void ShowOverlayText(string text, Color color)
        {
            using var dc = _overlayLayer.RenderOpen();
            dc.DrawText(MakeText(text, 16, new SolidColorBrush(color), FontWeights.Normal),
                new Point(40, 40));
        }

        public void ClearOverlay() { using var _ = _overlayLayer.RenderOpen(); }
        public void RedrawEdges() => DrawAllEdges();
        public void RedrawNodes() => DrawAllNodes();

        // ── FrameworkElement ───────────────────────────────────────────────────

        protected override int VisualChildrenCount => _visuals.Count + _nodeVisuals.Count;

        protected override Visual GetVisualChild(int index)
            => index < _visuals.Count ? _visuals[index] : _nodeVisuals[index - _visuals.Count];

        protected override HitTestResult HitTestCore(PointHitTestParameters p)
            => new PointHitTestResult(this, p.HitPoint);

        // ── Live Debugger ─────────────────────────────────────────────────────

        public void SetLiveStates(List<string> activeIds, List<VariableValue> vars)
        {
            var prev = _liveStateIds;
            var next = new HashSet<string>(activeIds ?? new List<string>());

            var exited = prev.Where(id => !next.Contains(id)).ToHashSet();
            var entered = next.Where(id => !prev.Contains(id)).ToHashSet();
            if (exited.Count > 0 && entered.Count > 0)
                foreach (var ed in _edges)
                    if (ed.From != null && ed.To != null &&
                        exited.Contains(ed.From.Id) && entered.Contains(ed.To.Id))
                        _firedEdges[ed] = 1.0;

            _liveStateIds = next;
            _liveVars = vars ?? new();

            foreach (var id in prev) RedrawNode(_nodes.FirstOrDefault(n => n.Id == id));
            foreach (var id in _liveStateIds) RedrawNode(_nodes.FirstOrDefault(n => n.Id == id));
            if (_firedEdges.Count > 0) DrawAllEdges();

            if ((_liveStateIds.Count > 0 || _firedEdges.Count > 0) && !_pulseTimer.IsEnabled)
                _pulseTimer.Start();
            else if (_liveStateIds.Count == 0 && _firedEdges.Count == 0)
                _pulseTimer.Stop();
        }

        // ── Node rendering ─────────────────────────────────────────────────────

        private void RebuildNodeVisuals()
        {
            foreach (var nv in _nodeVisuals) RemoveVisualChild(nv);
            _nodeVisuals.Clear();
            foreach (var node in _nodes)
            {
                var nv = new NodeVisual(node);
                _nodeVisuals.Add(nv);
                AddVisualChild(nv);
                DrawNodeVisual(nv);
            }
        }

        private void DrawAllNodes()
        {
            foreach (var nv in _nodeVisuals) DrawNodeVisual(nv);
        }

        private void RedrawNode(GraphNode? node)
        {
            if (node == null) return;
            var nv = _nodeVisuals.Find(v => v.Node == node);
            if (nv != null) DrawNodeVisual(nv);
        }

        private void DrawNodeVisual(NodeVisual nv)
        {
            var node = nv.Node;
            bool isSelected = node == _selectedNode || _selectedNodes.Contains(node);
            bool isHovered = node == _hoveredNode;
            bool isHighlighted = !string.IsNullOrEmpty(_searchQuery) &&
                node.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase);
            bool isSearchMatch = node.Id == _highlightId;  // go-to search result

            var pal = _palette[node.NodeType];

            using var dc = nv.RenderOpen();

            bool connecting = _connectingFrom != null;
            bool validTarget = connecting && IsValidConnectionTarget(node);
            bool dim = connecting && node != _connectingFrom && !validTarget;
            if (dim) dc.PushOpacity(0.28);

            var fullRect = new Rect(node.X, node.Y, node.Width, node.Height);

            // ── Border pen ────────────────────────────────────────────────────
            Pen borderPen = isSearchMatch ? new Pen(new SolidColorBrush(Colors.Gold), 3.5)
                          : isHighlighted ? _highlightPen
                          : isSelected ? _selectedPen
                          : new Pen(new SolidColorBrush(pal.Border),
                                    isHovered ? 1.8 : 1.0);

            // ── Drop shadow when selected/hovered ─────────────────────────────
            if (isSelected || isHovered)
            {
                var shadow = new SolidColorBrush(
                    Color.FromArgb(60, pal.Border.R, pal.Border.G, pal.Border.B));
                dc.DrawRoundedRectangle(shadow, null,
                    new Rect(node.X + 4, node.Y + 4, node.Width, node.Height), 7, 7);
            }

            // ── Live Glow ──────────────────────────────────────────────────────────

            bool isLive = _liveStateIds.Contains(node.Id);
            if (isLive)
            {
                // Animated green outer glow
                byte alpha = (byte)(80 + 120 * Math.Abs(Math.Sin(_pulsePhase)));
                var glowPen = new Pen(
                    new SolidColorBrush(Color.FromArgb(alpha, 0x00, 0xFF, 0x80)), 4);
                dc.DrawRoundedRectangle(null, glowPen,
                    new Rect(node.X - 4, node.Y - 4, node.Width + 8, node.Height + 8),
                    9, 9);

                // Subtle green tint on the body
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(18, 0x00, 0xFF, 0x80)),
                    null, fullRect, 6, 6);

                // LIVE badge bottom-right
                var liveBadge = MakeText("● LIVE", 8,
                    new SolidColorBrush(Color.FromArgb(alpha, 0x00, 0xFF, 0x80)),
                    FontWeights.Bold);
                dc.DrawText(liveBadge,
                    new Point(node.X + node.Width - liveBadge.Width - 5,
                              node.Y + node.Height - 13));
            }

            // ── Zoomed-out mode: float label above node ───────────────────────
            if (_currentZoom < 0.6)
            {
                // Draw minimal node body
                dc.DrawRoundedRectangle(new SolidColorBrush(pal.Header), borderPen, fullRect, 6, 6);

                if (isLive)
                {
                    byte alpha = (byte)(80 + 120 * Math.Abs(Math.Sin(_pulsePhase)));
                    dc.DrawRoundedRectangle(null,
                        new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0x00, 0xFF, 0x80)), 3),
                        new Rect(node.X - 3, node.Y - 3, node.Width + 6, node.Height + 6), 8, 8);
                }

                // Label floats above, unconstrained by node width
                double fontSize = Math.Max(9.0, 10.0 / Math.Max(_currentZoom, 0.1));
                var floatLabel = MakeText(node.Name, fontSize,
                    new SolidColorBrush(Colors.White), FontWeights.SemiBold);
                // Center above node
                double lx = node.X + node.Width / 2.0 - floatLabel.Width / 2.0;
                double ly = node.Y - floatLabel.Height - 4;
                // Small dark backing so text is readable over anything
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(180, 0x10, 0x10, 0x18)), null,
                    new Rect(lx - 4, ly - 2, floatLabel.Width + 8, floatLabel.Height + 4), 3, 3);
                dc.DrawText(floatLabel, new Point(lx, ly));
                if (dim) dc.Pop();
                return;
            }

            // ── Body ──────────────────────────────────────────────────────────
            dc.DrawRoundedRectangle(new SolidColorBrush(pal.Body), borderPen, fullRect, 6, 6);

            // ── Header (rounded top, square bottom) ───────────────────────────
            var headerGeo = new StreamGeometry();
            using (var sgc = headerGeo.Open())
            {
                sgc.BeginFigure(new Point(node.X + 6, node.Y), true, true);
                sgc.LineTo(new Point(node.X + node.Width - 6, node.Y), true, false);
                sgc.ArcTo(new Point(node.X + node.Width, node.Y + 6),
                          new Size(6, 6), 0, false, SweepDirection.Clockwise, true, false);
                sgc.LineTo(new Point(node.X + node.Width, node.Y + HeaderH), true, false);
                sgc.LineTo(new Point(node.X, node.Y + HeaderH), true, false);
                sgc.ArcTo(new Point(node.X, node.Y + 6),
                          new Size(6, 6), 0, false, SweepDirection.Clockwise, true, false);
            }
            headerGeo.Freeze();
            dc.PushClip(new RectangleGeometry(fullRect, 6, 6));
            dc.DrawGeometry(new SolidColorBrush(pal.Header), null, headerGeo);
            dc.Pop();


            // ── Header divider ────────────────────────────────────────────────
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1),
                new Point(node.X, node.Y + HeaderH),
                new Point(node.X + node.Width, node.Y + HeaderH));

            // ── Type icon ─────────────────────────────────────────────────────
            string icon = node.NodeType switch
            {
                GraphNodeType.StateMachine => "⬡",
                GraphNodeType.State => "◈",
                GraphNodeType.Clip => "▶",
                GraphNodeType.Modifier => "⚙",
                GraphNodeType.Blender => "⇌",
                _ => "○"
            };
            dc.DrawText(MakeText(icon, 10, new SolidColorBrush(pal.HeaderText), FontWeights.Normal),
                new Point(node.X + 5, node.Y + 4));

            // ── Header name ───────────────────────────────────────────────────
            double nameFontSize = Math.Max(8.5, 10.0 / Math.Max(_currentZoom, 0.35));
            var nameText = MakeText(node.Name, nameFontSize,
                new SolidColorBrush(pal.HeaderText), FontWeights.SemiBold);
            nameText.MaxTextWidth = node.Width - 30;
            nameText.MaxLineCount = 1;
            nameText.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(nameText, new Point(node.X + 19, node.Y + 4));

            // ── START badge ───────────────────────────────────────────────────
            if (node.IsStart)
            {
                var badge = MakeText("START", 7, _startBadge, FontWeights.Bold);
                dc.DrawText(badge,
                    new Point(node.X + node.Width - badge.Width - 5, node.Y + 6));
            }

            // ── Body: class name ──────────────────────────────────────────────
            double bodyY = node.Y + HeaderH + 6;
            var classText = MakeText(node.ClassName ?? "", 9, _textSecondary, FontWeights.Normal);
            classText.MaxTextWidth = node.Width - 12;
            classText.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(classText, new Point(node.X + 6, bodyY));

            // ── Body: stateId ─────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(node.StateId) && node.StateId != "0")
                dc.DrawText(MakeText($"stateId: {node.StateId}", 9,
                    _textSecondary, FontWeights.Normal),
                    new Point(node.X + 6, bodyY + 13));

            // ── Body: sub-label ───────────────────────────────────────────────
            if (!string.IsNullOrEmpty(node.SubLabel))
            {
                var sub = MakeText(node.SubLabel, 9, _textSecondary, FontWeights.Normal);
                sub.MaxTextWidth = node.Width - 12;
                sub.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(sub, new Point(node.X + 6, bodyY + 26));
            }

            // ── Drill-down indicator (⬇ bottom-center when CanDrillDown) ────────
            if (node.CanDrillDown)
            {
                var hint = MakeText("⬇", 9,
                    new SolidColorBrush(Color.FromArgb(isHovered ? (byte)220 : (byte)100,
                        0x4F, 0xC3, 0xF7)), FontWeights.Normal);
                dc.DrawText(hint, new Point(
                    node.X + node.Width / 2 - hint.Width / 2,
                    node.Y + node.Height - 14));
            }

            // ── Ports (visible on hover) ──────────────────────────────────────
            var portBrush = isHovered
                ? _portFill
                : new SolidColorBrush(Color.FromArgb(60, 0x4F, 0xC3, 0xF7));

            // Output (right)
            dc.DrawEllipse(portBrush, _portPen,
                new Point(node.X + node.Width, node.Y + node.Height / 2), PortR, PortR);
            // Input (left)
            dc.DrawEllipse(portBrush, _portPen,
                new Point(node.X, node.Y + node.Height / 2), PortR, PortR);

            // ── Valid connection target ring (outside the header clip) ──
            if (validTarget)
            {
                bool hot = node == _connectHoverNode;
                var ring = new Pen(new SolidColorBrush(
                    Color.FromArgb(hot ? (byte)0xFF : (byte)0xAA, 0x4C, 0xD9, 0x6E)), hot ? 3 : 2);
                dc.DrawRoundedRectangle(null, ring,
                    new Rect(node.X - 2, node.Y - 2, node.Width + 4, node.Height + 4), 8, 8);
            }
            if (dim) dc.Pop();
        }


        private bool IsValidConnectionTarget(GraphNode target)
        {
            if (_connectingFrom == null || target == null || target == _connectingFrom)
                return false;
            return ConnectionValidator?.Invoke(_connectingFrom, target)
                   ?? ((target.Machine ?? "") == (_connectingFrom.Machine ?? ""));
        }


        private void ClearGuides() { using var _ = _guideLayer.RenderOpen(); }

        private void DrawGuide(double? gx, double? gy,
            double minY, double maxY, double minX, double maxX)
        {
            using var dc = _guideLayer.RenderOpen();
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x3D, 0x7F)), 1)
            { DashStyle = DashStyles.Dash };
            if (gx.HasValue) dc.DrawLine(pen, new Point(gx.Value, minY), new Point(gx.Value, maxY));
            if (gy.HasValue) dc.DrawLine(pen, new Point(minX, gy.Value), new Point(maxX, gy.Value));
        }

        private void ApplySnap(GraphNode dragged, ref double tx, ref double ty)
        {
            double snap = SnapBase / Math.Max(_currentZoom, 0.1);
            double w = dragged.Width, h = dragged.Height;

            double bestX = snap, bestY = snap, dX = 0, dY = 0;
            double? guideX = null, guideY = null;
            GraphNode? matchX = null, matchY = null;

            var dxLines = new[] { tx, tx + w / 2, tx + w };
            var dyLines = new[] { ty, ty + h / 2, ty + h };

            foreach (var n in _nodes)
            {
                if (n == dragged || _selectedNodes.Contains(n)) continue;
                var txL = new[] { n.X, n.X + n.Width / 2, n.X + n.Width };
                var tyL = new[] { n.Y, n.Y + n.Height / 2, n.Y + n.Height };

                foreach (var dl in dxLines) foreach (var tl in txL)
                { double d = Math.Abs(dl - tl); if (d < bestX) { bestX = d; dX = tl - dl; guideX = tl; matchX = n; } }
                foreach (var dl in dyLines) foreach (var tl in tyL)
                { double d = Math.Abs(dl - tl); if (d < bestY) { bestY = d; dY = tl - dl; guideY = tl; matchY = n; } }
            }

            tx += dX; ty += dY;

            if (guideX.HasValue || guideY.HasValue)
            {
                double minY = ty, maxY = ty + h, minX = tx, maxX = tx + w;
                if (matchX != null) { minY = Math.Min(minY, matchX.Y); maxY = Math.Max(maxY, matchX.Y + matchX.Height); }
                if (matchY != null) { minX = Math.Min(minX, matchY.X); maxX = Math.Max(maxX, matchY.X + matchY.Width); }
                DrawGuide(guideX, guideY, minY - 20, maxY + 20, minX - 20, maxX + 20);
            }
            else ClearGuides();
        }

        // ── Edge rendering ─────────────────────────────────────────────────────

        private void DrawAllEdges()
        {
            using var edgeDc = _edgeLayer.RenderOpen();
            using var labelDc = _labelLayer.RenderOpen();
            foreach (var edge in _edges)
                DrawEdge(edge, edgeDc, labelDc);
        }

        private void DrawEdge(GraphEdge edge, DrawingContext edgeDc, DrawingContext labelDc)
        {
            if (edge.From == null || edge.To == null) return;
            if (edge == _rewiringEdge) return;

            _firedEdges.TryGetValue(edge, out double fire);
            bool isHovered = edge == _hoveredEdge;
            var pen = isHovered
                ? new Pen(new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)), 3)
                : _edgePen;
            var arrowFill = isHovered ? _arrowHover : _arrowFill;

            if (edge.From.Id == edge.To.Id)
            { DrawSelfLoop(edge, edgeDc, labelDc, pen, arrowFill, fire); return; }

            // Port-to-port: right side of From → left side of To
            double x1 = edge.From.X + edge.From.Width;
            double y1 = edge.From.Y + edge.From.Height / 2;
            double x2 = edge.To.X;
            double y2 = edge.To.Y + edge.To.Height / 2;

            bool goRight = edge.To.X >= edge.From.X;
            double cx1, cy1, cx2, cy2;

            if (goRight)
            {
                double dx = Math.Max((x2 - x1) * 0.5, 60);
                cx1 = x1 + dx; cy1 = y1;
                cx2 = x2 - dx; cy2 = y2;
            }
            else
            {
                double spread = Math.Max(Math.Abs(y2 - y1) * 0.5, 80);
                cx1 = x1 + 60; cy1 = y1 + spread;
                cx2 = x2 - 60; cy2 = y2 + spread;
            }

            edge.LastBezier = (new Point(x1, y1), new Point(cx1, cy1),
                               new Point(cx2, cy2), new Point(x2, y2));

            var geo = new StreamGeometry();
            using (var sgc = geo.Open())
            {
                sgc.BeginFigure(new Point(x1, y1), false, false);
                sgc.BezierTo(new Point(cx1, cy1), new Point(cx2, cy2),
                             new Point(x2, y2), true, false);
            }
            geo.Freeze();
            edgeDc.DrawGeometry(null, pen, geo);
            DrawArrow(edgeDc,
    fire > 0 ? new SolidColorBrush(Color.FromArgb((byte)(255 * fire), 0x00, 0xFF, 0x88)) : arrowFill,
    new Point(x2, y2), Math.Atan2(y2 - cy2, x2 - cx2));

            if (!string.IsNullOrEmpty(edge.EventName))
                DrawEdgeLabel(labelDc, edge.EventName, (cx1 + cx2) / 2, (cy1 + cy2) / 2 - 16,
                    isHovered || fire > 0, enlarge: isHovered);

            if (fire > 0)
            {
                byte a = (byte)(230 * fire);
                edgeDc.DrawGeometry(null,
                    new Pen(new SolidColorBrush(Color.FromArgb(a, 0x00, 0xFF, 0x88)), 2 + 4 * fire), geo);
            }
        }

        private void DrawSelfLoop(GraphEdge edge,
            DrawingContext edgeDc, DrawingContext labelDc, Pen pen, Brush arrowFill, double fire = 0)
        {
            double lx = edge.From.X + edge.From.Width / 2;
            double ty = edge.From.Y;
            var geo = new StreamGeometry();
            using (var sgc = geo.Open())
            {
                sgc.BeginFigure(new Point(lx, ty), false, false);
                sgc.BezierTo(new Point(lx + 50, ty - 50),
                             new Point(lx + 80, ty - 50),
                             new Point(lx + 60, ty), true, false);
            }
            geo.Freeze();
            edgeDc.DrawGeometry(null, pen, geo);
            if (!string.IsNullOrEmpty(edge.EventName))
                DrawEdgeLabel(labelDc, edge.EventName, lx + 30, ty - 45,
                    edge == _hoveredEdge, enlarge: edge == _hoveredEdge);
        }

        private static void DrawArrow(DrawingContext dc, Brush fill, Point tip, double angle)
        {
            const double S = 7;
            var geo = new StreamGeometry();
            using (var sgc = geo.Open())
            {
                sgc.BeginFigure(tip, true, true);
                sgc.LineTo(new Point(tip.X - S * Math.Cos(angle - 0.4),
                                     tip.Y - S * Math.Sin(angle - 0.4)), true, false);
                sgc.LineTo(new Point(tip.X - S * Math.Cos(angle + 0.4),
                                     tip.Y - S * Math.Sin(angle + 0.4)), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(fill, null, geo);
        }

        private void DrawEdgeLabel(DrawingContext dc, string text,
    double x, double y, bool highlighted = false, bool enlarge = false)
        {
            // Dim when not hovered — still visible but not intrusive
            var textBrush = highlighted
                ? _textEvent
                : FB(new SolidColorBrush(Color.FromArgb(0x88, 0xAA, 0xAA, 0xAA)));
            var bgBrush = highlighted
                ? _labelBg
                : FB(new SolidColorBrush(Color.FromArgb(0x55, 0x18, 0x18, 0x22)));
            var borderPen = highlighted ? _labelBorder : null;

            // Same zoom-compensation as node names, but only when enlarging (hover)
            double size = enlarge
                ? Math.Max(9.0, 10.0 / Math.Max(_currentZoom, 0.1))
                : 9.0;
            var ft = MakeText(text, size, textBrush, FontWeights.Normal);
            double w = ft.Width + 10, h = ft.Height + 4;
            dc.DrawRoundedRectangle(bgBrush, borderPen,
                new Rect(x - w / 2, y - h / 2, w, h), 3, 3);
            dc.DrawText(ft, new Point(x - ft.Width / 2, y - ft.Height / 2));
        }

        private bool HitTestEdgeEndpoint(Point p, out GraphEdge? edge)
        {
            edge = _hoveredEdge;
            if (edge == null || edge.LastBezier == default) return false;
            if (edge.Tag == null) return false;            // only real SM transitions, not generator edges
            var tip = edge.LastBezier.p3;                  // arrowhead = left port of To node
            double dx = p.X - tip.X, dy = p.Y - tip.Y;
            return Math.Sqrt(dx * dx + dy * dy) < PortR + 6;
        }

        // ── Draft connection ───────────────────────────────────────────────────

        private void DrawDraftConnection()
        {
            using var dc = _draftLayer.RenderOpen();
            if (_connectingFrom == null) return;
            double x1 = _connectingFrom.X + _connectingFrom.Width;
            double y1 = _connectingFrom.Y + _connectingFrom.Height / 2;
            double dx = Math.Max((_connectingTo.X - x1) * 0.5, 40);
            var geo = new StreamGeometry();
            using (var sgc = geo.Open())
            {
                sgc.BeginFigure(new Point(x1, y1), false, false);
                sgc.BezierTo(new Point(x1 + dx, y1),
                             new Point(_connectingTo.X - dx, _connectingTo.Y),
                             _connectingTo, true, false);
            }
            geo.Freeze();
            var draftPen = new Pen(
                new SolidColorBrush(Color.FromArgb(180, 0x4F, 0xC3, 0xF7)), 2)
            { DashStyle = DashStyles.Dash };
            dc.DrawGeometry(null, draftPen, geo);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(180, 0x4F, 0xC3, 0xF7)),
                null, _connectingTo, 5, 5);
        }

        private void ClearDraftConnection() { using var _ = _draftLayer.RenderOpen(); }

        // ── Hit testing ────────────────────────────────────────────────────────

        private GraphNode? HitTestNode(Point p)
        {
            for (int i = _nodes.Count - 1; i >= 0; i--)
            {
                var n = _nodes[i];
                if (p.X >= n.X && p.X <= n.X + n.Width &&
                    p.Y >= n.Y && p.Y <= n.Y + n.Height)
                    return n;
            }
            return null;
        }

        private bool HitTestOutputPort(Point p, out GraphNode? node)
        {
            foreach (var n in _nodes)
            {
                var port = new Point(n.X + n.Width, n.Y + n.Height / 2);
                double dx = p.X - port.X, dy = p.Y - port.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < PortR + 5)
                { node = n; return true; }
            }
            node = null; return false;
        }

        private GraphEdge? HitTestEdge(Point p)
        {
            foreach (var edge in _edges)
            {
                if (edge.LastBezier == default) continue;
                if (BezierDist(p, edge.LastBezier) < 12) return edge;
            }
            return null;
        }

        private static double BezierDist(Point p, (Point p0, Point p1, Point p2, Point p3) b)
        {
            double min = double.MaxValue;
            for (int i = 0; i <= 20; i++)
            {
                double t = i / 20.0, mt = 1 - t;
                double bx = mt * mt * mt * b.p0.X + 3 * mt * mt * t * b.p1.X
                          + 3 * mt * t * t * b.p2.X + t * t * t * b.p3.X;
                double by = mt * mt * mt * b.p0.Y + 3 * mt * mt * t * b.p1.Y
                          + 3 * mt * t * t * b.p2.Y + t * t * t * b.p3.Y;
                double d = Math.Sqrt((p.X - bx) * (p.X - bx) + (p.Y - by) * (p.Y - by));
                if (d < min) min = d;
                if (min < 8) return min;
            }
            return min;
        }

        // ── Mouse handlers ─────────────────────────────────────────────────────

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);

            // ── Double-click: comment rename or node drill ──────────────────────────
            if (e.ClickCount == 2)
            {
                var hdrComment = HitTestCommentHeader(p);
                if (hdrComment != null)
                { CommentDoubleClicked?.Invoke(hdrComment); e.Handled = true; return; }

                var dblNode = HitTestNode(p);
                if (dblNode != null)
                { NodeDoubleClicked?.Invoke(dblNode); e.Handled = true; return; }
            }

            // ── Resize handle ────────────────────────────────────────────────────────
            var (resizeC, resizeH) = HitTestCommentHandle(p);
            if (resizeC != null)
            {
                _resizingComment = resizeC;
                _resizingHandle = resizeH;
                _resizeStartMouse = p;
                _resizeStartRect = new Rect(resizeC.X, resizeC.Y, resizeC.Width, resizeC.Height);
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // ── Comment header drag ──────────────────────────────────────────────────
            var hdr = HitTestCommentHeader(p);
            if (hdr != null)
            {
                _draggingComment = hdr;
                _commentDragOffset = new Point(p.X - hdr.X, p.Y - hdr.Y);
                var bounds = new Rect(hdr.X, hdr.Y, hdr.Width, hdr.Height);
                _commentDraggedNodes = _nodes
                    .Where(n => bounds.Contains(new Point(n.X + n.Width / 2, n.Y + n.Height / 2)))
                    .Select(n => (n, n.X, n.Y))
                    .ToList();
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // ── Grab the END of a hovered edge → re-wire its destination ──────────────
            if (HitTestEdgeEndpoint(p, out var grabbed) && grabbed != null)
            {
                _rewiringEdge = grabbed;
                _connectingFrom = grabbed.From;   // draft + validity reuse the connect path
                _connectingTo = p;
                CaptureMouse();
                DrawAllEdges();        // hide the edge being re-wired
                DrawAllNodes();        // dim invalid targets
                DrawDraftConnection();
                e.Handled = true;
                return;
            }

            // ── Output port → start connection ──────────────────────────────────────
            if (HitTestOutputPort(p, out var portNode))
            {
                _connectingFrom = portNode;
                _connectingTo = p;
                CaptureMouse();
                DrawAllNodes();
                e.Handled = true;
                return;
            }

            // ── Node click ───────────────────────────────────────────────────────────
            var node = HitTestNode(p);
            if (node != null)
            {
                bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                if (ctrl)
                {
                    if (_selectedNodes.Contains(node)) _selectedNodes.Remove(node);
                    else _selectedNodes.Add(node);
                    DrawAllNodes();
                    SelectionChanged?.Invoke(_selectedNodes);
                    e.Handled = true;
                    return;
                }
                if (!_selectedNodes.Contains(node)) _selectedNodes.Clear();
                _selectedNodes.Add(node);
                _draggingNode = node;
                _dragOffset = new Point(p.X - node.X, p.Y - node.Y);
                CaptureMouse();
                SelectNode(node);
                NodeSelected?.Invoke(node);
                e.Handled = true;
                return;
            }

            // ── Empty-space lasso ────────────────────────────────────────────────────
            bool ctrlHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (!ctrlHeld) _selectedNodes.Clear();
            _isLassoing = true;
            _lassoStart = p;
            _lassoEnd = p;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);

            if (_connectingFrom != null)
            {
                var target = HitTestNode(p);
                if (_rewiringEdge != null)
                {
                    if (target != null && target != _rewiringEdge.To && IsValidConnectionTarget(target))
                        EdgeRewireRequested?.Invoke(_rewiringEdge, target);
                    _rewiringEdge = null;          // dropped on empty/same/invalid → cancel, edge reappears
                }
                else if (target != null && IsValidConnectionTarget(target))
                {
                    ConnectionRequested?.Invoke(_connectingFrom, target);
                }

                _connectingFrom = null;
                _connectHoverNode = null;
                ClearDraftConnection();
                DrawAllNodes();
                DrawAllEdges();                    // restore the hidden edge
            }

            if (_draggingNode != null) { NodeMoved?.Invoke(_draggingNode); _draggingNode = null; ClearGuides(); }
            if (_draggingComment != null) { _draggingComment = null; _commentDraggedNodes.Clear(); }
            if (_resizingComment != null) { _resizingComment = null; }
            if (_isPanningFromHost) _isPanningFromHost = false;
            if (_isLassoing)
            {
                _isLassoing = false;
                var lasso = MakeLassoRect(_lassoStart, _lassoEnd);
                foreach (var n in _nodes)
                    if (lasso.IntersectsWith(new Rect(n.X, n.Y, n.Width, n.Height)))
                        _selectedNodes.Add(n);
                DrawAllNodes(); ClearLasso();
                SelectionChanged?.Invoke(_selectedNodes);
            }

            ReleaseMouseCapture();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(this);

            // ── Draft connection ─────────────────────────────────────────────────────
            if (_connectingFrom != null)
            {
                _connectingTo = p;
                DrawDraftConnection();
                var t = HitTestNode(p);
                if (t != _connectHoverNode)
                { var prev = _connectHoverNode; _connectHoverNode = t; RedrawNode(prev); RedrawNode(t); }
                e.Handled = true; return;
            }

            // ── Comment drag ─────────────────────────────────────────────────────────
            if (_draggingComment != null)
            {
                double newX = p.X - _commentDragOffset.X;
                double newY = p.Y - _commentDragOffset.Y;
                double dx = newX - _draggingComment.X;
                double dy = newY - _draggingComment.Y;
                _draggingComment.X = newX;
                _draggingComment.Y = newY;
                foreach (var (n, _, _) in _commentDraggedNodes) { n.X += dx; n.Y += dy; }
                DrawComments(); DrawAllNodes(); DrawAllEdges();
                e.Handled = true; return;
            }

            // ── Comment resize ───────────────────────────────────────────────────────
            if (_resizingComment != null)
            {
                double dx = p.X - _resizeStartMouse.X;
                double dy = p.Y - _resizeStartMouse.Y;
                var rs = _resizeStartRect;
                var c = _resizingComment;
                switch (_resizingHandle)
                {
                    case ResizeHandleType.E:
                        c.Width = Math.Max(rs.Width + dx, MinCommentSize); break;
                    case ResizeHandleType.S:
                        c.Height = Math.Max(rs.Height + dy, MinCommentSize); break;
                    case ResizeHandleType.W:
                        c.X = Math.Min(rs.X + dx, rs.Right - MinCommentSize);
                        c.Width = Math.Max(rs.Width - dx, MinCommentSize); break;
                    case ResizeHandleType.N:
                        c.Y = Math.Min(rs.Y + dy, rs.Bottom - MinCommentSize);
                        c.Height = Math.Max(rs.Height - dy, MinCommentSize); break;
                    case ResizeHandleType.SE:
                        c.Width = Math.Max(rs.Width + dx, MinCommentSize);
                        c.Height = Math.Max(rs.Height + dy, MinCommentSize); break;
                    case ResizeHandleType.SW:
                        c.X = Math.Min(rs.X + dx, rs.Right - MinCommentSize);
                        c.Width = Math.Max(rs.Width - dx, MinCommentSize);
                        c.Height = Math.Max(rs.Height + dy, MinCommentSize); break;
                    case ResizeHandleType.NE:
                        c.Width = Math.Max(rs.Width + dx, MinCommentSize);
                        c.Y = Math.Min(rs.Y + dy, rs.Bottom - MinCommentSize);
                        c.Height = Math.Max(rs.Height - dy, MinCommentSize); break;
                    case ResizeHandleType.NW:
                        c.X = Math.Min(rs.X + dx, rs.Right - MinCommentSize);
                        c.Width = Math.Max(rs.Width - dx, MinCommentSize);
                        c.Y = Math.Min(rs.Y + dy, rs.Bottom - MinCommentSize);
                        c.Height = Math.Max(rs.Height - dy, MinCommentSize); break;
                }
                DrawComments();
                e.Handled = true; return;
            }

            // ── Node drag ────────────────────────────────────────────────────────────
            if (_draggingNode != null)
            {
                double tx = p.X - _dragOffset.X;
                double ty = p.Y - _dragOffset.Y;

                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                    ApplySnap(_draggingNode, ref tx, ref ty);   // hold Alt to disable snapping
                else
                    ClearGuides();

                double dx = tx - _draggingNode.X;
                double dy = ty - _draggingNode.Y;
                foreach (var n in _selectedNodes) { n.X += dx; n.Y += dy; }
                if (!_selectedNodes.Contains(_draggingNode)) { _draggingNode.X += dx; _draggingNode.Y += dy; }

                DrawAllNodes(); DrawAllEdges(); DrawMiniMap();
                e.Handled = true; return;
            }

            // ── Pan ──────────────────────────────────────────────────────────────────
            if (_isPanningFromHost)
            {
                var sp = e.GetPosition(null);
                PanDelta?.Invoke(sp.X - _panStartPoint.X, sp.Y - _panStartPoint.Y);
                _panStartPoint = sp;
                e.Handled = true; return;
            }

            // ── Lasso ────────────────────────────────────────────────────────────────
            if (_isLassoing)
            { _lassoEnd = p; DrawLasso(); e.Handled = true; return; }

            // ── Hover: node ──────────────────────────────────────────────────────────
            var hoverNode = HitTestNode(p);
            if (hoverNode != _hoveredNode)
            {
                var prev = _hoveredNode; _hoveredNode = hoverNode; RedrawNode(prev); RedrawNode(hoverNode);
                NodeHoverChanged?.Invoke(hoverNode, p);
            }

            var edge = HitTestEdge(p);
            if (edge != _hoveredEdge)
            {
                _hoveredEdge = edge; DrawAllEdges();
                EdgeHoverChanged?.Invoke(edge, p);
            }

            if (_connectingFrom == null && _draggingNode == null && !_isLassoing)
                Cursor = HitTestEdgeEndpoint(p, out _) ? Cursors.SizeAll : Cursors.Arrow;

            // ── Hover: comment ───────────────────────────────────────────────────────
            var commentHover = HitTestCommentAny(p);
            if (commentHover != _hoveredComment)
            { _hoveredComment = commentHover; DrawComments(); }
        }

        private void OnAnyMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            _panStartPoint = e.GetPosition(null);
            _isPanningFromHost = true;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnAnyMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            _isPanningFromHost = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);

            var comment = HitTestCommentAny(p);
            if (comment != null)
            { CommentContextMenuRequested?.Invoke(comment); e.Handled = true; return; }

            var node = HitTestNode(p);
            if (node != null)
            {
                SelectNode(node); NodeSelected?.Invoke(node);
                NodeContextMenuRequested?.Invoke(node);
                e.Handled = true; return;
            }
            var edge = HitTestEdge(p);
            if (edge != null)
            { EdgeContextMenuRequested?.Invoke(edge); e.Handled = true; return; }

            CanvasContextMenuRequested?.Invoke(p);
            e.Handled = true;
        }

        // Called externally (e.g. F2 key) to start renaming the selected node
        public void RequestRenameSelected()
        {
            if (_selectedNode != null)
                NodeRenameRequested?.Invoke(_selectedNode);
        }

        public void SelectAll()
        {
            // Redraw all nodes with current selection state
            DrawAllNodes();
        }

        // ── Lasso rendering ───────────────────────────────────────────────────

        private void DrawLasso()
        {
            using var oc = _overlayLayer.RenderOpen();
            if (!_isLassoing) return;   // empty open = clears overlay
            var r = MakeLassoRect(_lassoStart, _lassoEnd);
            var fill = new SolidColorBrush(Color.FromArgb(0x22, 0x4F, 0xC3, 0xF7));
            var border = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x4F, 0xC3, 0xF7)), 1);
            oc.DrawRectangle(fill, border, r);
        }

        private void ClearLasso()
        {
            using var _ = _overlayLayer.RenderOpen(); // open + immediately close = clear
        }

        private static Rect MakeLassoRect(Point a, Point b)
            => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                   Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        // ── Comment box rendering ──────────────────────────────────────────────

        private readonly DrawingVisual _commentLayer = new();

        public void DrawComments()
        {
            using var dc = _commentLayer.RenderOpen();
            foreach (var c in Comments)
            {
                bool active = c == _hoveredComment || c == _draggingComment || c == _resizingComment;
                var col = c.Color;

                // ── Body ────────────────────────────────────────────────────────────
                var bodyRect = new Rect(c.X, c.Y, c.Width, c.Height);
                var fillBrush = new SolidColorBrush(Color.FromArgb(0x28, col.R, col.G, col.B));
                var borderPen = new Pen(new SolidColorBrush(
                    Color.FromArgb(active ? (byte)0xDD : (byte)0x88, col.R, col.G, col.B)),
                    active ? 2 : 1.5);
                dc.DrawRoundedRectangle(fillBrush, borderPen, bodyRect, 6, 6);

                // ── Header strip ────────────────────────────────────────────────────
                var headerGeo = new StreamGeometry();
                using (var sgc = headerGeo.Open())
                {
                    sgc.BeginFigure(new Point(c.X + 6, c.Y), true, true);
                    sgc.LineTo(new Point(c.X + c.Width - 6, c.Y), true, false);
                    sgc.ArcTo(new Point(c.X + c.Width, c.Y + 6), new Size(6, 6), 0, false,
                              SweepDirection.Clockwise, true, false);
                    sgc.LineTo(new Point(c.X + c.Width, c.Y + 20), true, false);
                    sgc.LineTo(new Point(c.X, c.Y + 20), true, false);
                    sgc.ArcTo(new Point(c.X, c.Y + 6), new Size(6, 6), 0, false,
                              SweepDirection.Clockwise, true, false);
                }
                headerGeo.Freeze();
                dc.DrawGeometry(
                    new SolidColorBrush(Color.FromArgb(0x55, col.R, col.G, col.B)),
                    null, headerGeo);

                // ── Drag grip dots ───────────────────────────────────────────────────
                var dotBrush = new SolidColorBrush(Color.FromArgb(0x77, col.R, col.G, col.B));
                for (int d = 0; d < 3; d++)
                    dc.DrawEllipse(dotBrush, null,
                        new Point(c.X + 10 + d * 7, c.Y + 10), 2, 2);

                // ── Title ────────────────────────────────────────────────────────────
                if (!string.IsNullOrEmpty(c.Title))
                {
                    var bright = Color.FromArgb(0xFF,
    (byte)Math.Min(255, col.R * 2.2),
    (byte)Math.Min(255, col.G * 2.2),
    (byte)Math.Min(255, col.B * 2.2));
                    var ft = MakeText(c.Title, 11,
                        new SolidColorBrush(bright),
                        FontWeights.SemiBold);
                    ft.MaxTextWidth = c.Width - 36;
                    ft.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(ft, new Point(c.X + 28, c.Y + 3));
                }

                // ── Resize handles (only when active) ─────────────────────────────
                if (active)
                {
                    var hFill = new SolidColorBrush(Color.FromArgb(0xDD, col.R, col.G, col.B));
                    var hPen = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)), 1);
                    foreach (var hr in GetHandleRects(c))
                        dc.DrawRectangle(hFill, hPen, hr);
                }
            }
        }

        private Rect[] GetHandleRects(GraphComment c)
        {
            double mx = c.X + c.Width / 2, my = c.Y + c.Height / 2;
            double r = c.X + c.Width, b = c.Y + c.Height;
            double h = HandleSize / 2;
            return new[]
            {
        new Rect(c.X - h, c.Y - h, HandleSize, HandleSize),  // NW
        new Rect(mx - h,  c.Y - h, HandleSize, HandleSize),  // N
        new Rect(r  - h,  c.Y - h, HandleSize, HandleSize),  // NE
        new Rect(r  - h,  my - h,  HandleSize, HandleSize),  // E
        new Rect(r  - h,  b  - h,  HandleSize, HandleSize),  // SE
        new Rect(mx - h,  b  - h,  HandleSize, HandleSize),  // S
        new Rect(c.X - h, b  - h,  HandleSize, HandleSize),  // SW
        new Rect(c.X - h, my - h,  HandleSize, HandleSize),  // W
    };
        }

        private static readonly ResizeHandleType[] _handleTypes =
        {
    ResizeHandleType.NW, ResizeHandleType.N,  ResizeHandleType.NE,
    ResizeHandleType.E,  ResizeHandleType.SE, ResizeHandleType.S,
    ResizeHandleType.SW, ResizeHandleType.W
};

        private (GraphComment? c, ResizeHandleType handle) HitTestCommentHandle(Point p)
        {
            for (int i = Comments.Count - 1; i >= 0; i--)
            {
                var c = Comments[i];
                var rects = GetHandleRects(c);
                for (int j = 0; j < rects.Length; j++)
                {
                    var hr = rects[j]; hr.Inflate(3, 3);
                    if (hr.Contains(p)) return (c, _handleTypes[j]);
                }
            }
            return (null, ResizeHandleType.None);
        }

        private GraphComment? HitTestCommentHeader(Point p)
        {
            for (int i = Comments.Count - 1; i >= 0; i--)
            {
                var c = Comments[i];
                if (p.X >= c.X && p.X <= c.X + c.Width && p.Y >= c.Y && p.Y <= c.Y + 20)
                    return c;
            }
            return null;
        }

        private GraphComment? HitTestCommentAny(Point p)
        {
            for (int i = Comments.Count - 1; i >= 0; i--)
            {
                var c = Comments[i];
                if (p.X >= c.X && p.X <= c.X + c.Width && p.Y >= c.Y && p.Y <= c.Y + c.Height)
                    return c;
            }
            return null;
        }

        // ── Mini-map ──────────────────────────────────────────────────────────

        private const double MapW = 160, MapH = 100, MapPad = 8;

        public void DrawMiniMap()
        {
            // Minimap is now rendered by StateMachineGraphView as a WPF overlay
            MapTransformChanged?.Invoke();
        }


        // Called by StateMachineGraphView to pass transform state for minimap viewport
        private double _mapScale = 1, _mapTranslateX, _mapTranslateY;
        public void UpdateMapTransform(double scale, double tx, double ty)
        {
            _mapScale = scale; _mapTranslateX = tx; _mapTranslateY = ty;
            if (_currentZoom != scale)
            {
                _currentZoom = scale;
                DrawAllNodes();
                if (_hoveredEdge != null) DrawAllEdges();   // ← keep hovered label pinned while zooming
            }
            DrawMiniMap();
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private FormattedText MakeText(string text, double size, Brush brush, FontWeight weight)
            => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                   _font, size, brush, _dpi == 0 ? 1.0 : _dpi);

        private static Pen FP(Color c, double t) { var p = new Pen(new SolidColorBrush(c), t); p.Freeze(); return p; }
        private static Brush FB(SolidColorBrush b) { b.Freeze(); return b; }

        public class NodeVisual : DrawingVisual
        {
            public GraphNode Node { get; }
            public NodeVisual(GraphNode node) { Node = node; }
        }
    }

    // ── Data models ───────────────────────────────────────────────────────────

    public class GraphNode
    {
        public string Id { get; set; } = "";
        public string StateId { get; set; } = "";
        public string Name { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string Machine { get; set; } = "";
        public string SubLabel { get; set; } = "";
        public GraphNodeType NodeType { get; set; } = GraphNodeType.State;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 190;
        public double Height { get; set; } = 68;
        public bool IsStart { get; set; }
        public bool CanDrillDown { get; set; }  // show ⬇ indicator
        public object? Tag { get; set; }
    }

    public class GraphEdge
    {
        public GraphNode From { get; set; } = null!;
        public GraphNode To { get; set; } = null!;
        public string EventName { get; set; } = "";
        public string EventId { get; set; } = "";
        public string Flags { get; set; } = "";
        public (Point p0, Point p1, Point p2, Point p3) LastBezier { get; set; }
        /// <summary>Backing Havok objects: (transitionChild, transitionArray, ownerState)</summary>
        public object? Tag { get; set; }
        /// <summary>Optional reroute points along this edge.</summary>
        public List<GraphReroute> Reroutes { get; set; } = new();
    }

    /// <summary>Comment/annotation box on the graph canvas.</summary>
    public class GraphComment
    {
        public string Title { get; set; } = "Comment";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 240;
        public double Height { get; set; } = 120;
        public Color Color { get; set; } = Color.FromRgb(0x60, 0x60, 0x20);
    }

    /// <summary>A draggable dot mid-wire for routing edges around nodes.</summary>
    public class GraphReroute
    {
        public double X { get; set; }
        public double Y { get; set; }
        public const double Radius = 5;
    }
}
