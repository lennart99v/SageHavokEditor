using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

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

        // ── Graph state ───────────────────────────────────────────────────────
        private List<GraphNode> _nodes = new();
        private List<GraphEdge> _edges = new();
        private GraphNode _selectedNode;
        private GraphNode _draggingNode;
        private GraphNode _hoveredNode;
        private GraphEdge _hoveredEdge;
        private GraphNode _connectingFrom;
        private Point _connectingTo;
        private Point _dragOffset;
        private string _searchQuery = "";
        private double _dpi = 96;

        // ── Events ────────────────────────────────────────────────────────────
        public event Action<GraphNode> NodeSelected;
        public event Action<GraphNode> NodeDoubleClicked;
        public event Action<GraphNode> NodeMoved;
        public event Action<GraphNode, GraphNode> ConnectionRequested;
        public event Action<GraphNode> NodeContextMenuRequested;

        // ── Node palette ──────────────────────────────────────────────────────
        private record NodePalette(Color Header, Color HeaderText, Color Body, Color Border);

        private static readonly Dictionary<GraphNodeType, NodePalette> _palette = new()
        {
            [GraphNodeType.StateMachine] = new(
                Color.FromRgb(0x4A, 0x2F, 0x7A), Color.FromRgb(0xD4, 0xD4, 0xD4),
                Color.FromRgb(0x1E, 0x14, 0x2E), Color.FromRgb(0x7C, 0x4D, 0xBB)),
            [GraphNodeType.State] = new(
                Color.FromRgb(0x0D, 0x47, 0x85), Color.FromRgb(0xD4, 0xD4, 0xD4),
                Color.FromRgb(0x0B, 0x28, 0x42), Color.FromRgb(0x1A, 0x7A, 0xCC)),
            [GraphNodeType.Clip] = new(
                Color.FromRgb(0x1A, 0x5C, 0x2A), Color.FromRgb(0xD4, 0xD4, 0xD4),
                Color.FromRgb(0x0D, 0x2E, 0x14), Color.FromRgb(0x2E, 0xA0, 0x4A)),
            [GraphNodeType.Modifier] = new(
                Color.FromRgb(0x7A, 0x3D, 0x0A), Color.FromRgb(0xD4, 0xD4, 0xD4),
                Color.FromRgb(0x3A, 0x1E, 0x06), Color.FromRgb(0xCC, 0x6E, 0x1A)),
            [GraphNodeType.Blender] = new(
                Color.FromRgb(0x0A, 0x5A, 0x5A), Color.FromRgb(0xD4, 0xD4, 0xD4),
                Color.FromRgb(0x06, 0x2E, 0x2E), Color.FromRgb(0x1A, 0xAA, 0xAA)),
            [GraphNodeType.Unknown] = new(
                Color.FromRgb(0x3A, 0x3A, 0x3E), Color.FromRgb(0xBB, 0xBB, 0xBB),
                Color.FromRgb(0x1E, 0x1E, 0x21), Color.FromRgb(0x5A, 0x5A, 0x60)),
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
            _visuals.Add(_draftLayer);
            _visuals.Add(_overlayLayer);

            MouseMove += OnMouseMove;
            MouseLeftButtonDown += OnMouseDown;
            MouseLeftButtonUp += OnMouseUp;
            MouseRightButtonDown += OnRightClick;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        public void SetGraph(List<GraphNode> nodes, List<GraphEdge> edges)
        {
            _nodes = nodes;
            _edges = edges;
            _selectedNode = null;
            _hoveredEdge = null;
            _hoveredNode = null;
            _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RebuildNodeVisuals();
            DrawAllEdges();
            ClearOverlay();
        }

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

        private void RedrawNode(GraphNode node)
        {
            if (node == null) return;
            var nv = _nodeVisuals.Find(v => v.Node == node);
            if (nv != null) DrawNodeVisual(nv);
        }

        private void DrawNodeVisual(NodeVisual nv)
        {
            var node = nv.Node;
            bool isSelected = node == _selectedNode;
            bool isHovered = node == _hoveredNode;
            bool isHighlighted = !string.IsNullOrEmpty(_searchQuery) &&
                node.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase);

            var pal = _palette[node.NodeType];

            using var dc = nv.RenderOpen();

            var fullRect = new Rect(node.X, node.Y, node.Width, node.Height);

            // ── Border pen ────────────────────────────────────────────────────
            Pen borderPen = isHighlighted ? _highlightPen
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
            dc.DrawGeometry(new SolidColorBrush(pal.Header), null, headerGeo);

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
            var nameText = MakeText(node.Name, 10,
                new SolidColorBrush(pal.HeaderText), FontWeights.SemiBold);
            nameText.MaxTextWidth = node.Width - 30;
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

            bool isHovered = edge == _hoveredEdge;
            var pen = isHovered
                ? new Pen(new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)), 3)
                : _edgePen;
            var arrowFill = isHovered ? _arrowHover : _arrowFill;

            if (edge.From.Id == edge.To.Id)
            { DrawSelfLoop(edge, edgeDc, labelDc, pen, arrowFill); return; }

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
            DrawArrow(edgeDc, arrowFill, new Point(x2, y2),
                Math.Atan2(y2 - cy2, x2 - cx2));

            if (isHovered && !string.IsNullOrEmpty(edge.EventName))
                DrawEdgeLabel(labelDc, edge.EventName,
                    (cx1 + cx2) / 2, (cy1 + cy2) / 2 - 16);
        }

        private void DrawSelfLoop(GraphEdge edge,
            DrawingContext edgeDc, DrawingContext labelDc, Pen pen, Brush arrowFill)
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
            if (edge == _hoveredEdge && !string.IsNullOrEmpty(edge.EventName))
                DrawEdgeLabel(labelDc, edge.EventName, lx + 30, ty - 45);
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

        private void DrawEdgeLabel(DrawingContext dc, string text, double x, double y)
        {
            var ft = MakeText(text, 9, _textEvent, FontWeights.Normal);
            double w = ft.Width + 10, h = ft.Height + 4;
            dc.DrawRoundedRectangle(_labelBg, _labelBorder,
                new Rect(x - w / 2, y - h / 2, w, h), 3, 3);
            dc.DrawText(ft, new Point(x - ft.Width / 2, y - ft.Height / 2));
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

        private GraphNode HitTestNode(Point p)
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

        private bool HitTestOutputPort(Point p, out GraphNode node)
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

        private GraphEdge HitTestEdge(Point p)
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

            if (HitTestOutputPort(p, out var portNode))
            {
                _connectingFrom = portNode;
                _connectingTo = p;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            var node = HitTestNode(p);
            if (node != null)
            {
                if (e.ClickCount == 2) { NodeDoubleClicked?.Invoke(node); e.Handled = true; return; }
                _draggingNode = node;
                _dragOffset = new Point(p.X - node.X, p.Y - node.Y);
                CaptureMouse();
                SelectNode(node);
                NodeSelected?.Invoke(node);
                e.Handled = true;
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);

            if (_connectingFrom != null)
            {
                var target = HitTestNode(p);
                if (target != null && target != _connectingFrom)
                    ConnectionRequested?.Invoke(_connectingFrom, target);
                _connectingFrom = null;
                ClearDraftConnection();
            }

            if (_draggingNode != null)
            {
                NodeMoved?.Invoke(_draggingNode);
                _draggingNode = null;
            }

            ReleaseMouseCapture();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(this);

            if (_connectingFrom != null && IsMouseCaptured)
            {
                _connectingTo = p;
                DrawDraftConnection();
                e.Handled = true;
                return;
            }

            if (_draggingNode != null && IsMouseCaptured)
            {
                _draggingNode.X = p.X - _dragOffset.X;
                _draggingNode.Y = p.Y - _dragOffset.Y;
                RedrawNode(_draggingNode);
                DrawAllEdges();
                e.Handled = true;
                return;
            }

            // Node hover
            var hoverNode = HitTestNode(p);
            if (hoverNode != _hoveredNode)
            {
                var prev = _hoveredNode;
                _hoveredNode = hoverNode;
                RedrawNode(prev);
                RedrawNode(hoverNode);
            }

            // Edge hover
            var edge = HitTestEdge(p);
            if (edge != _hoveredEdge) { _hoveredEdge = edge; DrawAllEdges(); }
        }

        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            var node = HitTestNode(p);
            if (node == null) return;
            SelectNode(node);
            NodeSelected?.Invoke(node);
            NodeContextMenuRequested?.Invoke(node);
            e.Handled = true;
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
        public string Id { get; set; }
        public string StateId { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
        public string Machine { get; set; }
        public string SubLabel { get; set; }
        public GraphNodeType NodeType { get; set; } = GraphNodeType.State;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 190;
        public double Height { get; set; } = 68;
        public bool IsStart { get; set; }
        public bool CanDrillDown { get; set; }  // show ⬇ indicator
        public object Tag { get; set; }
    }

    public class GraphEdge
    {
        public GraphNode From { get; set; }
        public GraphNode To { get; set; }
        public string EventName { get; set; }
        public string EventId { get; set; }
        public string Flags { get; set; }
        public (Point p0, Point p1, Point p2, Point p3) LastBezier { get; set; }
    }
}