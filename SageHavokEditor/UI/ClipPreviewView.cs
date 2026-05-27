using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SageHavokEditor.Core.Animation;

namespace SageHavokEditor.UI
{
    public sealed class PreviewTrigger
    {
        public float Time;
        public string EventName = "";
        public bool RelativeToEnd;
    }

    public class ClipPreviewView : UserControl
    {
        private enum ViewAxis { Side, Front, Top }

        private readonly SkeletonElement _skel = new();
        private readonly Slider _scrub = new() { Minimum = 0, Maximum = 1000, Margin = new Thickness(6, 0, 6, 0) };
        private readonly Button _play = new() { Content = "▶", Width = 30, Margin = new Thickness(0, 0, 6, 0) };
        private readonly Button _viewBtn = new() { Content = "Side", Width = 52, Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _graphBtn = new() { Content = "Show in graph", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 90, FontSize = 11, Foreground = Brushes.Gainsboro };
        private readonly TextBlock _status = new() { Foreground = Brushes.Gainsboro, Margin = new Thickness(6), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly Canvas _tickOverlay = new() { IsHitTestVisible = true, Height = 24 };
        private readonly Grid _scrubArea = new();
        private readonly TextBlock _legend = new() { Foreground = Brushes.Gray, Margin = new Thickness(6, 0, 6, 4), FontSize = 11 };

        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _watch = new();
        private bool _suppressScrub;
        private AnimationClip? _clip;
        private List<PreviewTrigger> _triggers = new();
        private ViewAxis _view = ViewAxis.Side;

        /// <summary>Set by the host so "Show in graph" can jump back to the clip's node.</summary>
        public Action? OnShowInGraph;

        public ClipPreviewView()
        {
            Background = Brushes.Transparent;
            // Slider + tick overlay in the same grid cell (ticks sit ON the track)
            _scrub.VerticalAlignment = VerticalAlignment.Center;
            _scrubArea.Children.Add(_scrub);
            _scrubArea.Children.Add(_tickOverlay);   // on top, same coordinate space
            _tickOverlay.HorizontalAlignment = HorizontalAlignment.Stretch;
            var controls = new DockPanel { Margin = new Thickness(6) };
            DockPanel.SetDock(_play, Dock.Left);
            DockPanel.SetDock(_time, Dock.Left);
            DockPanel.SetDock(_graphBtn, Dock.Right);
            DockPanel.SetDock(_viewBtn, Dock.Right);
            controls.Children.Add(_play);
            controls.Children.Add(_time);
            controls.Children.Add(_graphBtn);
            controls.Children.Add(_viewBtn);
            controls.Children.Add(_scrubArea);

            var bottom = new StackPanel();
            bottom.Children.Add(_status);
            bottom.Children.Add(_legend);
            bottom.Children.Add(controls);

            var root = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(_skel);

            Content = root;

            _play.Click += (_, __) => TogglePlay();
            _viewBtn.Click += (_, __) => CycleView();
            _graphBtn.Click += (_, __) => OnShowInGraph?.Invoke();
            _scrub.ValueChanged += OnScrub;
            _tickOverlay.SizeChanged += (_, __) => DrawTicks();
            _skel.HorizontalAlignment = HorizontalAlignment.Stretch;
            _skel.VerticalAlignment = VerticalAlignment.Stretch;

            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTick;

            _view = AppSettings.PreviewDefaultAxis switch
            {
                "Front" => ViewAxis.Front,
                "Top" => ViewAxis.Top,
                _ => ViewAxis.Side
            };
            _viewBtn.Content = _view.ToString();

            ShowMessage("Select a clip to preview.");
        }

        public void ShowMessage(string msg)
        {
            Stop();
            _clip = null;
            _skel.Clear();
            _status.Text = msg;
            _time.Text = "";
            _tickOverlay.Children.Clear();
            _graphBtn.IsEnabled = false;
        }

        public void Show(AnimationClip clip, Skeleton skeleton, List<PreviewTrigger>? triggers = null)
        {
            Stop();
            _clip = clip;
            _triggers = triggers ?? new List<PreviewTrigger>();

            var world = new HkTransform[clip.NumFrames][];
            for (int f = 0; f < clip.NumFrames; f++)
                world[f] = HkTransform.ComputeWorld(clip.Frames[f], skeleton.ParentIndices);

            // Which bones the clip actually drives: a bone is "animated" if any frame
            // differs from the reference pose translation (cheap, robust enough for coloring).
            var animated = new bool[skeleton.ReferencePose.Length];
            for (int b = 0; b < animated.Length; b++)
            {
                var refT = skeleton.ReferencePose[b].Translation;
                for (int f = 0; f < clip.NumFrames; f++)
                    if ((clip.Frames[f][b].Translation - refT).LengthSquared() > 1e-6f)
                    { animated[b] = true; break; }
            }

            _skel.SetData(world, skeleton.ParentIndices, animated);
            ApplyView();
            _skel.SetFrame(0);

            int animCount = animated.Count(a => a);
            _status.Text = $"{clip.NumFrames} frames · {clip.Duration:F2}s · {clip.NumTracks} tracks · "
    + $"{animCount}/{animated.Length} bones animated"
    + (clip.TrackCountExceedsBones ? "  ⚠ more tracks than bones (wrong skeleton?)" : "");
            _legend.Text = "purple = animation annotations   ·   orange = clip triggers   ·   Ctrl+click a tick to jump";
            _graphBtn.IsEnabled = OnShowInGraph != null;
            UpdateTimeLabel(0);
            DrawTicks();
            if (AppSettings.PreviewAutoplay) TogglePlay();
        }

        private void TogglePlay()
        {
            if (_clip == null) return;
            if (_timer.IsEnabled) Stop();
            else { _watch.Restart(); _timer.Start(); _play.Content = "⏸"; }
        }

        private void Stop()
        {
            _timer.Stop(); _watch.Stop(); _play.Content = "▶";
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_clip == null) return;
            double t = _watch.Elapsed.TotalSeconds;
            _skel.SetFrame(_clip.FrameAt(t));

            double frac = _clip.Duration > 0 ? (t % _clip.Duration) / _clip.Duration : 0;
            _suppressScrub = true; _scrub.Value = frac * 1000; _suppressScrub = false;

            double tc = t % Math.Max(_clip.Duration, 0.0001);
            UpdateTimeLabel(tc);
            HighlightTicks(tc);
        }

        private void OnScrub(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressScrub || _clip == null) return;
            Stop();
            double t = (_scrub.Value / 1000.0) * _clip.Duration;
            _skel.SetFrame(_clip.FrameAt(t));
            UpdateTimeLabel(t);
            HighlightTicks(t);
        }

        private void UpdateTimeLabel(double t)
            => _time.Text = _clip == null ? "" : $"{t:F2} / {_clip.Duration:F2}s";

        private void CycleView()
        {
            _view = _view switch { ViewAxis.Side => ViewAxis.Front, ViewAxis.Front => ViewAxis.Top, _ => ViewAxis.Side };
            _viewBtn.Content = _view.ToString();
            ApplyView();
        }

        private void ApplyView()
        {
            (int h, int v) = _view switch
            {
                ViewAxis.Side => (1, 2),
                ViewAxis.Front => (0, 2),
                _ => (0, 1),
            };
            _skel.SetAxes(h, v, flipV: _view != ViewAxis.Top);
        }

        // ── timeline ticks: annotations (purple, lower) + triggers (orange, upper) ──
        private void DrawTicks()
        {
            _tickOverlay.Children.Clear();
            if (_clip == null || _clip.Duration <= 0 || _tickOverlay.ActualWidth < 4) return;

            // Slider thumb has padding; the usable track is inset slightly. ~8px each side
            // is the default WPF slider thumb half-width — tweak if your slider style differs.
            double inset = 8;
            double usable = Math.Max(_tickOverlay.ActualWidth - inset * 2, 1);

            double H = _tickOverlay.ActualHeight;
            double mid = H / 2;

            // annotations — purple, below centerline; triggers — orange, above
            foreach (var a in _clip.Annotations)
                AddTick(a.Time, a.Text, Brushes.MediumPurple, inset, usable, mid, mid + 8, "anim");
            foreach (var t in _triggers)
                AddTick(t.Time, t.EventName, Brushes.Orange, inset, usable, mid - 8, mid, "trig");
        }

        private void AddTick(float time, string text, Brush stroke,
            double inset, double usable, double y1, double y2, string kind)
        {
            double x = inset + (time / _clip.Duration) * usable;


            var line = new System.Windows.Shapes.Line
            {
                X1 = x,
                X2 = x,
                Y1 = y1,
                Y2 = y2,
                Stroke = stroke,
                StrokeThickness = 2,
                Tag = $"{kind}|{time.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                ToolTip = new ToolTip
                {
                    Content = $"{text}  @ {time:F3}s",
                    Background = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2C)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(stroke is SolidColorBrush sb ? sb.Color : Colors.Gray),
                    BorderThickness = new Thickness(1),
                    FontSize = 12,
                    Padding = new Thickness(8, 4, 8, 4)
                },
                Cursor = System.Windows.Input.Cursors.Hand,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            // click a tick → seek there
            line.MouseLeftButtonDown += (_, e) =>
            {
                if (_clip == null) return;
                // Only seek on Ctrl+click — a plain click falls through to the slider for scrubbing.
                if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
                    return;   // leave e.Handled = false → slider handles it
                Stop();
                _scrub.Value = (time / _clip.Duration) * 1000;   // fires OnScrub → seeks + redraws
                e.Handled = true;   // consumed → slider doesn't also move
            };
            _tickOverlay.Children.Add(line);
        }

        private void HighlightTicks(double t)
        {
            foreach (var child in _tickOverlay.Children)
                if (child is System.Windows.Shapes.Line ln && ln.Tag is string tag)
                {
                    var parts = tag.Split('|');
                    bool near = parts.Length == 2 &&
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var at)
                        && Math.Abs(at - t) < 0.05;
                    var baseColor = parts[0] == "trig" ? Brushes.Orange : Brushes.MediumPurple;
                    ln.Stroke = near ? Brushes.Lime : baseColor;
                    ln.StrokeThickness = near ? 3.5 : 2;
                }
        }

        // ── renderer ──────────────────────────────────────────────────────────
        private sealed class SkeletonElement : FrameworkElement
        {
            private HkTransform[][] _world;
            private int[] _parents;
            private bool[] _animated;
            private int _frame;
            private int _hAxis = 1, _vAxis = 2;
            private bool _flipV = true;
            private double _hMin, _hMax, _vMin, _vMax;

            private readonly Pen _bonePenLive = new(new SolidColorBrush(Color.FromRgb(0x6F, 0xB7, 0xFF)), 2);
            private readonly Pen _bonePenHeld = new(new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x58)), 1.2);
            private readonly Brush _joint = Brushes.White;
            private readonly Brush _heldJoint = new SolidColorBrush(Color.FromRgb(0x66, 0x6C, 0x7A));
            private readonly Brush _rootJoint = Brushes.LimeGreen;

            public void Clear() { _world = null; InvalidateVisual(); }

            public void SetData(HkTransform[][] world, int[] parents, bool[] animated)
            {
                _world = world; _parents = parents; _animated = animated; _frame = 0;
                _bonePenLive.Freeze(); _bonePenHeld.Freeze();
                RecomputeBounds();
            }

            public void SetAxes(int h, int v, bool flipV)
            {
                _hAxis = h; _vAxis = v; _flipV = flipV;
                RecomputeBounds(); InvalidateVisual();
            }

            public void SetFrame(int f)
            {
                if (_world == null) return;
                _frame = Math.Clamp(f, 0, _world.Length - 1);
                InvalidateVisual();
            }

            private static float Comp(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

            private void RecomputeBounds()
            {
                if (_world == null) return;
                _hMin = _vMin = double.MaxValue; _hMax = _vMax = double.MinValue;
                foreach (var frame in _world)
                    foreach (var t in frame)
                    {
                        double h = Comp(t.Translation, _hAxis), v = Comp(t.Translation, _vAxis);
                        if (Math.Abs(h) > 1e5 || Math.Abs(v) > 1e5) continue;
                        if (h < _hMin) _hMin = h; if (h > _hMax) _hMax = h;
                        if (v < _vMin) _vMin = v; if (v > _vMax) _vMax = v;
                    }
            }

            protected override void OnRender(DrawingContext dc)
            {
                double W = ActualWidth, H = ActualHeight;
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1C)), null, new Rect(0, 0, W, H));
                if (_world == null) return;

                double pad = 24;
                double spanH = _hMax - _hMin, spanV = _vMax - _vMin;
                if (spanH <= 0 || spanV <= 0) return;
                double scale = Math.Min((W - 2 * pad) / spanH, (H - 2 * pad) / spanV);
                double offX = (W - spanH * scale) / 2;
                double offY = (H - spanV * scale) / 2;

                Point Project(Vector3 p)
                {
                    double h = Comp(p, _hAxis), v = Comp(p, _vAxis);
                    double x = offX + (h - _hMin) * scale;
                    double y = _flipV ? H - (offY + (v - _vMin) * scale) : offY + (v - _vMin) * scale;
                    return new Point(x, y);
                }

                var frame = _world[_frame];
                // held bones first (so animated draw on top)
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < frame.Length; i++)
                    {
                        bool anim = _animated != null && i < _animated.Length && _animated[i];
                        if (anim != (pass == 1)) continue;
                        int p = i < _parents.Length ? _parents[i] : -1;
                        var pt = Project(frame[i].Translation);
                        if (p >= 0 && p < frame.Length)
                            dc.DrawLine(anim ? _bonePenLive : _bonePenHeld, Project(frame[p].Translation), pt);
                        var jb = p < 0 ? _rootJoint : (anim ? _joint : _heldJoint);
                        dc.DrawEllipse(jb, null, pt, p < 0 ? 3 : (anim ? 1.8 : 1.2), p < 0 ? 3 : (anim ? 1.8 : 1.2));
                    }
            }
        }
    }
}
