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
        public float Time;                 // absolute clip time (already end-resolved)
        public string EventName = "";
        public bool RelativeToEnd;
        public int Index = -1;             // position in the hkbClipTriggerArray
        public string EventId = "";        // stale-check identity for edits
    }

    public class ClipPreviewView : UserControl
    {
        private enum ViewAxis { Side, Front, Top }

        private readonly SkeletonElement _skel = new();
        private readonly Slider _scrub = new() { Minimum = 0, Maximum = 1000, Margin = new Thickness(6, 0, 6, 0) };
        // Segoe UI Symbol keeps ▶/⏸ as monochrome text glyphs — the default emoji
        // presentation of ⏸ renders wider than the button and gets clipped.
        private readonly Button _play = new()
        {
            Content = "▶",
            Width = 30,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Symbol")
        };
        private readonly Button _addBtn = new()
        {
            Content = "＋",
            Width = 30,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Add annotation at playhead (A)",
            IsEnabled = false
        };
        private readonly Button _viewBtn = new() { Content = "Side", Width = 52, Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _listBtn = new()
        {
            Content = "☰",
            Width = 30,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Annotations & triggers panel"
        };
        private readonly Button _graphBtn = new() { Content = "Show in graph", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        private readonly Button _fbxBtn = new()
        {
            Content = "Export FBX",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0),
            ToolTip = "Export this clip as an FBX: the skeleton, plus every frame baked as a key"
        };
        private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 90, FontSize = 11, Foreground = Brushes.Gainsboro };
        private readonly TextBlock _status = new() { Foreground = Brushes.Gainsboro, Margin = new Thickness(6), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly Canvas _tickOverlay = new() { IsHitTestVisible = true, Height = 24 };
        private readonly Grid _scrubArea = new();
        private readonly TextBlock _legend = new() { Foreground = Brushes.Gray, Margin = new Thickness(6, 0, 6, 4), FontSize = 11 };
        private readonly DataGrid _annGrid = new();
        private readonly DockPanel _annPanel = new() { Width = 300, Visibility = Visibility.Collapsed };
        private readonly Button _annAddBtn = new()
        {
            Content = "＋",
            Width = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 6, 2),
            ToolTip = "Add annotation at playhead",
            IsEnabled = false
        };
        private DataGridTextColumn _colTime = null!, _colTrack = null!, _colText = null!;
        private readonly DataGrid _trigGrid = new();
        private readonly Button _trigAddBtn = new()
        {
            Content = "⚡",
            Width = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 6, 2),
            ToolTip = "Add trigger at playhead",
            IsEnabled = false
        };
        private DataGridTextColumn _colTrigTime = null!;
        private bool _refreshingRows;

        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _watch = new();
        private bool _suppressScrub;
        private AnimationClip? _clip;
        private Skeleton? _skeleton;
        private List<PreviewTrigger> _triggers = new();
        private ViewAxis _view = ViewAxis.Side;

        /// <summary>Set by the host so "Show in graph" can jump back to the clip's node.</summary>
        public Action? OnShowInGraph;

        /// <summary>
        /// Set by the host to persist annotation edits to the animation file. When null,
        /// the timeline stays read-only (no add/edit/delete menus).
        /// </summary>
        public Func<Core.Animation.AnnotationEdit, System.Threading.Tasks.Task>? OnAnnotationEdit;

        /// <summary>Full path of the previewed animation file (default name for annotation export).</summary>
        public string? AnimationPath;

        /// <summary>
        /// Behavior-side clip trigger editing. The host owns the dialogs (they need
        /// the graph's event list) and the write-back; the view only raises intent.
        /// All null → orange ticks stay read-only.
        /// </summary>
        public Action<float>? OnTriggerAdd;
        public Action<PreviewTrigger>? OnTriggerEdit;
        public Action<PreviewTrigger>? OnTriggerDelete;
        public Action<PreviewTrigger, float>? OnTriggerMove;

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
            DockPanel.SetDock(_addBtn, Dock.Left);
            DockPanel.SetDock(_time, Dock.Left);
            DockPanel.SetDock(_graphBtn, Dock.Right);
            DockPanel.SetDock(_fbxBtn, Dock.Right);
            DockPanel.SetDock(_viewBtn, Dock.Right);
            DockPanel.SetDock(_listBtn, Dock.Right);
            controls.Children.Add(_play);
            controls.Children.Add(_addBtn);
            controls.Children.Add(_time);
            controls.Children.Add(_graphBtn);
            controls.Children.Add(_fbxBtn);
            controls.Children.Add(_viewBtn);
            controls.Children.Add(_listBtn);
            controls.Children.Add(_scrubArea);

            var bottom = new StackPanel();
            bottom.Children.Add(_status);
            bottom.Children.Add(_legend);
            bottom.Children.Add(controls);

            BuildAnnotationPanel();

            var root = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            DockPanel.SetDock(_annPanel, Dock.Right);
            root.Children.Add(_annPanel);
            root.Children.Add(_skel);

            Content = root;

            _play.Click += (_, __) => TogglePlay();
            _viewBtn.Click += (_, __) => CycleView();
            _listBtn.Click += (_, __) =>
            {
                bool on = _annPanel.Visibility != Visibility.Visible;
                _annPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                AppSettings.PreviewAnnotationList = on;
            };
            if (AppSettings.PreviewAnnotationList) _annPanel.Visibility = Visibility.Visible;
            _graphBtn.Click += (_, __) => OnShowInGraph?.Invoke();
            _scrub.ValueChanged += OnScrub;
            // Right-click on the timeline adds an annotation at that spot; ticks
            // handle their own right-click (edit/delete) and mark the event handled.
            _scrubArea.MouseRightButtonUp += OnTimelineRightClick;
            _addBtn.Click += (_, __) => { if (_clip != null) _ = AddAnnotationFlow(CurrentTime()); };
            _fbxBtn.Click += (_, __) => ExportFbxFlow();
            // Double-click: on an annotation/trigger tick → edit it, anywhere else on
            // the timeline → add an annotation at that spot. Preview-tunneling fires
            // before the slider can react, so OriginalSource is the actual hit element.
            _scrubArea.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2 || _clip == null) return;
                var ctx = (e.OriginalSource as FrameworkElement)?.DataContext;
                if (ctx is Core.Animation.AnimationAnnotation a && OnAnnotationEdit != null)
                {
                    e.Handled = true;
                    Stop();
                    _ = EditAnnotationFlow(a);
                }
                else if (ctx is PreviewTrigger tr && OnTriggerEdit != null)
                {
                    e.Handled = true;
                    Stop();
                    OnTriggerEdit(tr);
                }
                else if (OnAnnotationEdit != null)
                {
                    e.Handled = true;
                    Stop();
                    _ = AddAnnotationFlow(TimeAtPosition(e.GetPosition(_tickOverlay).X));
                }
            };
            // "A" adds at the playhead — hooked on the host window so focus doesn't matter.
            Loaded += (_, __) =>
            {
                var w = Window.GetWindow(this);
                if (w != null) w.PreviewKeyDown += OnHostKeyDown;
            };
            Unloaded += (_, __) =>
            {
                var w = Window.GetWindow(this);
                if (w != null) w.PreviewKeyDown -= OnHostKeyDown;
            };
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
            _skeleton = null;
            _skel.Clear();
            _status.Text = msg;
            _time.Text = "";
            _tickOverlay.Children.Clear();
            _graphBtn.IsEnabled = false;
            _addBtn.IsEnabled = false;
            _fbxBtn.IsEnabled = false;
            _triggers = new List<PreviewTrigger>();
            RefreshAnnotationRows();
            RefreshTriggerRows();
        }

        public void Show(AnimationClip clip, Skeleton skeleton, List<PreviewTrigger>? triggers = null)
        {
            Stop();
            // Same-duration reload (e.g. right after an annotation edit) keeps the
            // playhead where it was; a different clip starts at 0.
            double keepT = _clip != null && Math.Abs(_clip.Duration - clip.Duration) < 1e-4
                ? (_scrub.Value / 1000.0) * clip.Duration
                : -1;
            _clip = clip;
            _skeleton = skeleton;
            _fbxBtn.IsEnabled = true;
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

            int animCount = animated.Count(a => a);
            _status.Text = $"{clip.NumFrames} frames · {clip.Duration:F2}s · {clip.NumTracks} tracks · "
    + $"{animCount}/{animated.Length} bones animated"
    + (clip.TrackCountExceedsBones ? "  ⚠ more tracks than bones (wrong skeleton?)" : "");
            _legend.Text = OnAnnotationEdit != null
                ? "purple = animation annotations   ·   orange = clip triggers   ·   Ctrl+click a tick to jump   ·   double-click timeline to add, a tick to edit   ·   drag a tick to move (Alt = no snap)"
                : "purple = animation annotations   ·   orange = clip triggers   ·   Ctrl+click a tick to jump";
            _graphBtn.IsEnabled = OnShowInGraph != null;
            _addBtn.IsEnabled = OnAnnotationEdit != null;

            if (keepT >= 0)
            {
                _suppressScrub = true;
                _scrub.Value = clip.Duration > 0 ? (keepT / clip.Duration) * 1000 : 0;
                _suppressScrub = false;
                _skel.SetFrame(clip.FrameAt(keepT));
                UpdateTimeLabel(keepT);
            }
            else
            {
                _suppressScrub = true;
                _scrub.Value = 0;
                _suppressScrub = false;
                _skel.SetFrame(0);
                UpdateTimeLabel(0);
            }
            DrawTicks();
            RefreshAnnotationRows();
            RefreshTriggerRows();
            if (keepT >= 0) HighlightTicks(keepT);
            // Don't restart playback on an in-place reload — keep the paused pose.
            if (AppSettings.PreviewAutoplay && keepT < 0) TogglePlay();
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
                AddTick(a.Time, a.Text, Brushes.MediumPurple, inset, usable, mid, mid + 8, "anim", a);
            foreach (var t in _triggers)
                AddTick(t.Time, t.EventName, Brushes.Orange, inset, usable, mid - 8, mid, "trig", trigger: t);
        }

        private void AddTick(float time, string text, Brush stroke,
            double inset, double usable, double y1, double y2, string kind,
            Core.Animation.AnimationAnnotation? annotation = null,
            PreviewTrigger? trigger = null)
        {
            double x = inset + (time / _clip.Duration) * usable;

            // The visible marker is a 5-corner pentagon (rectangle with a point aimed
            // at the track centerline: triggers sit above and point down, annotations
            // below and point up) and deliberately not hit-testable; a fat transparent
            // line on top carries the tooltip and mouse interactions so clicking
            // doesn't require pixel hunting. HighlightTicks borders it via the Tag,
            // which only the marker has. Points are at x=0 and the marker is placed
            // by its transform, so drag-moves just slide the transform.
            const double half = 3.5, notch = 3;
            var marker = new System.Windows.Shapes.Polygon
            {
                Fill = stroke,
                Points = kind == "trig"
                    ? new PointCollection
                    { new(-half, y1), new(half, y1), new(half, y2 - notch), new(0, y2), new(-half, y2 - notch) }
                    : new PointCollection
                    { new(0, y1), new(half, y1 + notch), new(half, y2), new(-half, y2), new(-half, y1 + notch) },
                RenderTransform = new TranslateTransform(x, 0),
                StrokeLineJoin = PenLineJoin.Round,
                Tag = $"{kind}|{time.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                IsHitTestVisible = false
            };
            _tickOverlay.Children.Add(marker);

            var hit = new System.Windows.Shapes.Line
            {
                X1 = x,
                X2 = x,
                Y1 = y1,
                Y2 = y2,
                Stroke = Brushes.Transparent,
                StrokeThickness = 12,
                DataContext = (object?)annotation ?? trigger,   // double-click routing reads this
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
                Cursor = System.Windows.Input.Cursors.Hand
            };
            // Ctrl+click a tick → seek there; plain press on an editable tick starts
            // a potential drag-to-move (read-only ticks fall through to the slider).
            bool pressed = false, dragging = false;
            double pressX = 0;
            float dragTime = time;
            float TickTime() => annotation?.Time ?? trigger?.Time ?? time;

            void MoveTickTo(float t2)
            {
                double nx = inset + (t2 / _clip!.Duration) * usable;
                ((TranslateTransform)marker.RenderTransform).X = nx;
                hit.X1 = hit.X2 = nx;
            }

            hit.MouseLeftButtonDown += (_, e) =>
            {
                if (_clip == null) return;
                if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
                {
                    Stop();
                    _scrub.Value = (time / _clip.Duration) * 1000;   // fires OnScrub → seeks + redraws
                    e.Handled = true;   // consumed → slider doesn't also move
                    return;
                }
                bool canDrag = (annotation != null && OnAnnotationEdit != null)
                            || (trigger != null && OnTriggerMove != null);
                if (!canDrag)
                    return;   // leave e.Handled = false → slider handles it
                pressed = true;
                dragging = false;
                pressX = e.GetPosition(_tickOverlay).X;
                dragTime = TickTime();
                hit.CaptureMouse();
                e.Handled = true;
            };
            if (annotation != null || trigger != null)
            {
                hit.MouseMove += (_, e) =>
                {
                    if (!pressed || _clip == null) return;
                    double x = e.GetPosition(_tickOverlay).X;
                    // 4px dead zone so a sloppy click doesn't become a micro-move
                    if (!dragging && Math.Abs(x - pressX) < 4) return;
                    dragging = true;
                    Stop();
                    var t2 = TimeAtPosition(x);
                    if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) == 0)
                        t2 = SnapToFrame(t2);
                    dragTime = t2;
                    MoveTickTo(t2);
                    // Nearest-frame display, matching AnnotationDialog (FrameAt would wrap at the end)
                    int fr = Math.Clamp((int)Math.Round(t2 / (_clip.Duration / _clip.NumFrames)), 0, _clip.NumFrames - 1);
                    _time.Text = $"{t2:F3}s · frame {fr}";
                };
                hit.MouseLeftButtonUp += (_, e) =>
                {
                    if (!pressed) return;
                    bool moved = dragging;
                    pressed = false;
                    dragging = false;
                    hit.ReleaseMouseCapture();
                    if (!moved) return;   // plain click: nothing to commit
                    e.Handled = true;
                    if (Math.Abs(dragTime - TickTime()) < 1e-4f)
                    {
                        MoveTickTo(TickTime());
                        UpdateTimeLabel(CurrentTime());
                        return;
                    }
                    if (annotation != null)
                        _ = OnAnnotationEdit?.Invoke(new Core.Animation.AnnotationEdit
                        {
                            Kind = Core.Animation.AnnotationEditKind.Edit,
                            TrackIndex = annotation.TrackIndex,
                            OldTime = annotation.Time,
                            OldText = annotation.Text,
                            NewTime = dragTime,
                            NewText = annotation.Text
                        });
                    else if (trigger != null)
                        OnTriggerMove?.Invoke(trigger, dragTime);
                };
                // Abnormal capture loss (Alt+Tab, popup…) → snap back, nothing committed.
                hit.LostMouseCapture += (_, __) =>
                {
                    if (!pressed && !dragging) return;
                    pressed = false;
                    dragging = false;
                    MoveTickTo(TickTime());
                    UpdateTimeLabel(CurrentTime());
                };
            }
            // right-click an annotation tick → edit/delete menu (handled here so the
            // timeline's own right-click "add" menu doesn't also fire)
            if (annotation != null)
                hit.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (OnAnnotationEdit == null) return;
                    var menu = new ContextMenu { PlacementTarget = hit };
                    var edit = new MenuItem { Header = $"✏ Edit '{annotation.Text}'…" };
                    edit.Click += async (_, __) => await EditAnnotationFlow(annotation);
                    var del = new MenuItem { Header = $"🗑 Delete '{annotation.Text}'" };
                    del.Click += async (_, __) => await OnAnnotationEdit(new Core.Animation.AnnotationEdit
                    {
                        Kind = Core.Animation.AnnotationEditKind.Delete,
                        TrackIndex = annotation.TrackIndex,
                        OldTime = annotation.Time,
                        OldText = annotation.Text
                    });
                    menu.Items.Add(edit);
                    menu.Items.Add(del);
                    menu.IsOpen = true;
                };
            // right-click a trigger tick → edit/delete (host-owned dialogs)
            if (trigger != null)
                hit.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (OnTriggerEdit == null && OnTriggerDelete == null) return;
                    var menu = new ContextMenu { PlacementTarget = hit };
                    if (OnTriggerEdit != null)
                    {
                        var edit = new MenuItem { Header = $"✏ Edit trigger '{trigger.EventName}'…" };
                        edit.Click += (_, __) => { Stop(); OnTriggerEdit(trigger); };
                        menu.Items.Add(edit);
                    }
                    if (OnTriggerDelete != null)
                    {
                        var del = new MenuItem { Header = $"🗑 Delete trigger '{trigger.EventName}'" };
                        del.Click += (_, __) => OnTriggerDelete(trigger);
                        menu.Items.Add(del);
                    }
                    menu.IsOpen = true;
                };
            _tickOverlay.Children.Add(hit);
        }

        // ── annotation editing (host persists via OnAnnotationEdit) ─────────────

        private void OnTimelineRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_clip == null) return;
            e.Handled = true;

            var menu = new ContextMenu { PlacementTarget = _scrubArea };
            float tClick = TimeAtPosition(e.GetPosition(_tickOverlay).X);
            if (OnAnnotationEdit != null)
            {
                var add = new MenuItem { Header = $"＋ Add annotation @ {SnapToFrame(tClick):F3}s…" };
                add.Click += async (_, __) => await AddAnnotationFlow(tClick);
                menu.Items.Add(add);
            }
            if (OnTriggerAdd != null)
            {
                var addTrig = new MenuItem { Header = $"⚡ Add trigger @ {SnapToFrame(tClick):F3}s…" };
                addTrig.Click += (_, __) => { Stop(); OnTriggerAdd(SnapToFrame(tClick)); };
                menu.Items.Add(addTrig);
            }
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            // hkanno-format interchange — copy/export work even in read-only mode.
            var copy = new MenuItem
            {
                Header = "📋 Copy all annotations (hkanno format)",
                IsEnabled = _clip.Annotations.Count > 0
            };
            copy.Click += (_, __) => Clipboard.SetText(
                Core.Animation.HkannoFormat.Dump(_clip.Annotations, _clip.Duration, _clip.NumFrames));
            menu.Items.Add(copy);

            var export = new MenuItem
            {
                Header = "📤 Export annotations to .txt…",
                IsEnabled = _clip.Annotations.Count > 0
            };
            export.Click += (_, __) => ExportAnnotationsFlow();
            menu.Items.Add(export);

            if (OnAnnotationEdit != null)
            {
                var import = new MenuItem { Header = "📥 Import & replace from .txt…" };
                import.Click += async (_, __) => await ImportAnnotationsFileFlow();
                menu.Items.Add(import);

                var paste = new MenuItem
                {
                    Header = "📋 Paste & replace from clipboard",
                    IsEnabled = Clipboard.ContainsText()
                };
                paste.Click += async (_, __) => await ImportAnnotationsTextFlow(
                    Clipboard.GetText(), "the clipboard");
                menu.Items.Add(paste);
            }

            menu.IsOpen = true;
        }

        // ── FBX export ──────────────────────────────────────────────────────────

        /// <summary>
        /// Write the previewed clip out as a binary FBX: the skeleton as a bone
        /// hierarchy, and every frame of every bone baked as a key.
        ///
        /// Translations go out in Havok units unscaled. Blender reads the file's
        /// UnitScaleFactor of 1 as centimetres and divides by 100 on import, so a
        /// bone at Havok 100 arrives at 1.0 Blender unit — which keeps the export
        /// a faithful copy of what the file says rather than a guess at what a
        /// Havok unit is worth in metres.
        /// </summary>
        private void ExportFbxFlow()
        {
            if (_clip == null || _skeleton == null) return;
            Stop();

            var baseName = string.IsNullOrEmpty(AnimationPath)
                ? "animation"
                : System.IO.Path.GetFileNameWithoutExtension(AnimationPath);

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export clip as FBX",
                FileName = baseName + ".fbx",
                Filter = "Autodesk FBX (*.fbx)|*.fbx|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                var tree = Core.Animation.FbxAnimationScene.Build(_skeleton, _clip,
                    new Core.Animation.FbxExportOptions
                    {
                        FrameDuration = _clip.FrameDuration > 0 ? _clip.FrameDuration : 1.0 / 30.0,
                        TakeName = baseName
                    });
                Core.Animation.FbxBinarySerializer.Write(dlg.FileName, tree);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Could not write the FBX:" + Environment.NewLine + Environment.NewLine + ex.Message,
                    "Export FBX", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double fps = _clip.FrameDuration > 0 ? 1.0 / _clip.FrameDuration : 30.0;
            _status.Text = $"Exported {_skeleton.BoneNames.Length} bones x {_clip.NumFrames} frames " +
                           $"at {fps:0.##} fps to {System.IO.Path.GetFileName(dlg.FileName)}";
        }

        // ── hkanno text import/export ───────────────────────────────────────────

        private void ExportAnnotationsFlow()
        {
            if (_clip == null) return;
            var baseName = string.IsNullOrEmpty(AnimationPath)
                ? "annotations"
                : System.IO.Path.GetFileNameWithoutExtension(AnimationPath);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export annotations (hkanno format)",
                FileName = baseName + ".anno.txt",
                Filter = "Annotation text (*.txt)|*.txt|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            System.IO.File.WriteAllText(dlg.FileName,
                Core.Animation.HkannoFormat.Dump(_clip.Annotations, _clip.Duration, _clip.NumFrames));
        }

        private async System.Threading.Tasks.Task ImportAnnotationsFileFlow()
        {
            if (_clip == null || OnAnnotationEdit == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import annotations (hkanno format)",
                Filter = "Annotation text (*.txt)|*.txt|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            await ImportAnnotationsTextFlow(System.IO.File.ReadAllText(dlg.FileName),
                System.IO.Path.GetFileName(dlg.FileName));
        }

        /// <summary>
        /// Parse hkanno-format text and replace the animation's whole annotation set
        /// with it (single undoable step). Times outside [0, duration] are clamped.
        /// </summary>
        private async System.Threading.Tasks.Task ImportAnnotationsTextFlow(string text, string sourceName)
        {
            if (_clip == null || OnAnnotationEdit == null) return;
            Stop();

            var parsed = Core.Animation.HkannoFormat.Parse(text, out var error);
            if (parsed == null)
            {
                MessageBox.Show($"Couldn't parse {sourceName}:\n{error}",
                    "Import Annotations", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int clamped = 0;
            foreach (var a in parsed)
            {
                float c = Math.Clamp(a.Time, 0, _clip.Duration);
                if (Math.Abs(c - a.Time) > 1e-4f) clamped++;
                a.Time = c;
            }

            var msg = $"Replace {_clip.Annotations.Count} annotation(s) with " +
                      $"{parsed.Count} from {sourceName}?" +
                      (clamped > 0 ? $"\n\n⚠ {clamped} annotation(s) fell outside this clip's " +
                                     $"{_clip.Duration:F3}s duration and will be clamped." : "");
            if (MessageBox.Show(msg, "Import Annotations",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            await OnAnnotationEdit(new Core.Animation.AnnotationEdit
            {
                Kind = Core.Animation.AnnotationEditKind.ReplaceAll,
                OldSet = _clip.Annotations
                    .Select(a => new Core.Animation.AnimationAnnotation
                    { Time = a.Time, Text = a.Text, TrackIndex = a.TrackIndex })
                    .ToList(),
                NewSet = parsed
            });
        }

        // ── annotation list panel (table of all annotations) ────────────────────

        /// <summary>One DataGrid row; Source ties it back to the parsed annotation.</summary>
        public sealed class AnnotationRow
        {
            internal Core.Animation.AnimationAnnotation Source = null!;
            public string Time { get; set; } = "";
            public int Frame { get; set; }
            public int Track { get; set; }
            public string Text { get; set; } = "";
        }

        private void BuildAnnotationPanel()
        {
            _annGrid.AutoGenerateColumns = false;
            _annGrid.CanUserAddRows = false;
            _annGrid.CanUserDeleteRows = false;
            _annGrid.CanUserResizeRows = false;
            _annGrid.SelectionMode = DataGridSelectionMode.Single;
            _annGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _annGrid.RowHeaderWidth = 0;
            _annGrid.BorderThickness = new Thickness(0);

            _colTime = new DataGridTextColumn
            {
                Header = "Time (s)",
                Binding = new System.Windows.Data.Binding(nameof(AnnotationRow.Time)),
                Width = 70
            };
            _colTrack = new DataGridTextColumn
            {
                Header = new TextBlock { Text = "Trk", ToolTip = "Annotation track index" },
                Binding = new System.Windows.Data.Binding(nameof(AnnotationRow.Track)),
                IsReadOnly = true,
                Width = 36
            };
            _colText = new DataGridTextColumn
            {
                Header = "Text",
                Binding = new System.Windows.Data.Binding(nameof(AnnotationRow.Text)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            };
            _annGrid.Columns.Add(_colTime);
            _annGrid.Columns.Add(new DataGridTextColumn
            {
                Header = new TextBlock { Text = "Fr", ToolTip = "Frame (nearest to the annotation's time)" },
                Binding = new System.Windows.Data.Binding(nameof(AnnotationRow.Frame)),
                IsReadOnly = true,
                Width = 36
            });
            _annGrid.Columns.Add(_colTrack);
            _annGrid.Columns.Add(_colText);

            // click a row → seek there (rebuilds also change selection; guarded)
            _annGrid.SelectionChanged += (_, __) =>
            {
                if (_refreshingRows || _clip == null || _clip.Duration <= 0) return;
                if (_annGrid.SelectedItem is not AnnotationRow row) return;
                Stop();
                _scrub.Value = (row.Source.Time / _clip.Duration) * 1000;   // fires OnScrub
            };

            _annGrid.CellEditEnding += OnAnnotationCellEdit;

            // Del on a selected row (not while typing in a cell) → delete the annotation
            _annGrid.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Delete) return;
                if (OnAnnotationEdit == null || _annGrid.SelectedItem is not AnnotationRow row) return;
                if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
                e.Handled = true;
                _ = OnAnnotationEdit(new Core.Animation.AnnotationEdit
                {
                    Kind = Core.Animation.AnnotationEditKind.Delete,
                    TrackIndex = row.Source.TrackIndex,
                    OldTime = row.Source.Time,
                    OldText = row.Source.Text
                });
            };

            // right-click a row → select it, then add/edit/delete menu; right-click
            // empty space still offers add-at-playhead
            _annGrid.PreviewMouseRightButtonDown += (_, e) =>
            {
                var r = FindRow(e.OriginalSource);
                if (r != null) _annGrid.SelectedItem = r.Item;
            };
            _annGrid.MouseRightButtonUp += (_, e) =>
            {
                if (_clip == null || OnAnnotationEdit == null) return;
                e.Handled = true;
                var menu = new ContextMenu { PlacementTarget = _annGrid };
                var add = new MenuItem { Header = $"＋ Add annotation @ {SnapToFrame(CurrentTime()):F3}s…" };
                add.Click += async (_, __) => await AddAnnotationFlow(CurrentTime());
                menu.Items.Add(add);
                if (FindRow(e.OriginalSource)?.Item is AnnotationRow row)
                {
                    menu.Items.Add(new Separator());
                    var edit = new MenuItem { Header = $"✏ Edit '{row.Source.Text}'…" };
                    edit.Click += async (_, __) => await EditAnnotationFlow(row.Source);
                    var del = new MenuItem { Header = $"🗑 Delete '{row.Source.Text}'" };
                    del.Click += async (_, __) => await OnAnnotationEdit(new Core.Animation.AnnotationEdit
                    {
                        Kind = Core.Animation.AnnotationEditKind.Delete,
                        TrackIndex = row.Source.TrackIndex,
                        OldTime = row.Source.Time,
                        OldText = row.Source.Text
                    });
                    menu.Items.Add(edit);
                    menu.Items.Add(del);
                }
                menu.IsOpen = true;
            };

            _annAddBtn.Click += (_, __) =>
            {
                if (_clip != null && OnAnnotationEdit != null) _ = AddAnnotationFlow(CurrentTime());
            };

            BuildTriggerGrid();

            // Annotations fill the panel; triggers size to content below them and
            // scroll past ~180px so a long trigger list can't squeeze the annotations out.
            _trigGrid.MaxHeight = 180;
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            static DockPanel Header(string text, Button addBtn)
            {
                var title = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.Gainsboro,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(8, 6, 8, 4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var header = new DockPanel();
                DockPanel.SetDock(addBtn, Dock.Right);
                header.Children.Add(addBtn);
                header.Children.Add(title);
                return header;
            }

            var annHeader = Header("Annotations", _annAddBtn);
            var trigHeader = Header("Triggers", _trigAddBtn);
            Grid.SetRow(annHeader, 0);
            Grid.SetRow(_annGrid, 1);
            Grid.SetRow(trigHeader, 2);
            Grid.SetRow(_trigGrid, 3);
            layout.Children.Add(annHeader);
            layout.Children.Add(_annGrid);
            layout.Children.Add(trigHeader);
            layout.Children.Add(_trigGrid);
            _annPanel.Children.Add(layout);
        }

        // ── trigger list panel (behavior-side clip triggers, below annotations) ──

        /// <summary>One DataGrid row; Source ties it back to the preview trigger.</summary>
        public sealed class TriggerRow
        {
            internal PreviewTrigger Source = null!;
            public string Time { get; set; } = "";
            public int Frame { get; set; }
            public string Event { get; set; } = "";
            public string End { get; set; } = "";
        }

        private void BuildTriggerGrid()
        {
            _trigGrid.AutoGenerateColumns = false;
            _trigGrid.CanUserAddRows = false;
            _trigGrid.CanUserDeleteRows = false;
            _trigGrid.CanUserResizeRows = false;
            _trigGrid.SelectionMode = DataGridSelectionMode.Single;
            _trigGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _trigGrid.RowHeaderWidth = 0;
            _trigGrid.BorderThickness = new Thickness(0);

            _colTrigTime = new DataGridTextColumn
            {
                Header = "Time (s)",
                Binding = new System.Windows.Data.Binding(nameof(TriggerRow.Time)),
                Width = 70
            };
            _trigGrid.Columns.Add(_colTrigTime);
            _trigGrid.Columns.Add(new DataGridTextColumn
            {
                Header = new TextBlock { Text = "Fr", ToolTip = "Frame (nearest to the trigger's time)" },
                Binding = new System.Windows.Data.Binding(nameof(TriggerRow.Frame)),
                IsReadOnly = true,
                Width = 36
            });
            // Event names live in the graph's event list, which only the host knows —
            // renaming goes through the host's edit dialog, so this column stays read-only.
            _trigGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Event",
                Binding = new System.Windows.Data.Binding(nameof(TriggerRow.Event)),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _trigGrid.Columns.Add(new DataGridTextColumn
            {
                Header = new TextBlock { Text = "End", ToolTip = "✓ = time is stored relative to the end of the clip" },
                Binding = new System.Windows.Data.Binding(nameof(TriggerRow.End)),
                IsReadOnly = true,
                Width = 36
            });

            // click a row → seek there (rebuilds also change selection; guarded)
            _trigGrid.SelectionChanged += (_, __) =>
            {
                if (_refreshingRows || _clip == null || _clip.Duration <= 0) return;
                if (_trigGrid.SelectedItem is not TriggerRow row) return;
                Stop();
                _scrub.Value = (row.Source.Time / _clip.Duration) * 1000;   // fires OnScrub
            };

            _trigGrid.CellEditEnding += OnTriggerCellEdit;

            // Del on a selected row (not while typing in a cell) → delete the trigger
            _trigGrid.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Delete) return;
                if (OnTriggerDelete == null || _trigGrid.SelectedItem is not TriggerRow row) return;
                if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
                e.Handled = true;
                OnTriggerDelete(row.Source);
            };

            // right-click a row → select it, then add/edit/delete menu; right-click
            // empty space still offers add-at-playhead
            _trigGrid.PreviewMouseRightButtonDown += (_, e) =>
            {
                var r = FindRow(e.OriginalSource);
                if (r != null) _trigGrid.SelectedItem = r.Item;
            };
            _trigGrid.MouseRightButtonUp += (_, e) =>
            {
                if (_clip == null) return;
                e.Handled = true;
                var menu = new ContextMenu { PlacementTarget = _trigGrid };
                if (OnTriggerAdd != null)
                {
                    var add = new MenuItem { Header = $"⚡ Add trigger @ {SnapToFrame(CurrentTime()):F3}s…" };
                    add.Click += (_, __) => { Stop(); OnTriggerAdd(SnapToFrame(CurrentTime())); };
                    menu.Items.Add(add);
                }
                if (FindRow(e.OriginalSource)?.Item is TriggerRow row)
                {
                    if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                    if (OnTriggerEdit != null)
                    {
                        var edit = new MenuItem { Header = $"✏ Edit trigger '{row.Source.EventName}'…" };
                        edit.Click += (_, __) => { Stop(); OnTriggerEdit(row.Source); };
                        menu.Items.Add(edit);
                    }
                    if (OnTriggerDelete != null)
                    {
                        var del = new MenuItem { Header = $"🗑 Delete trigger '{row.Source.EventName}'" };
                        del.Click += (_, __) => OnTriggerDelete(row.Source);
                        menu.Items.Add(del);
                    }
                }
                if (menu.Items.Count > 0) menu.IsOpen = true;
            };

            _trigAddBtn.Click += (_, __) =>
            {
                if (_clip == null || OnTriggerAdd == null) return;
                Stop();
                OnTriggerAdd(SnapToFrame(CurrentTime()));
            };
        }

        /// <summary>Inline time commit → same move pipeline as dragging a tick (no snap:
        /// a typed value is exact, matching the annotation grid's time column).</summary>
        private void OnTriggerCellEdit(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (_clip == null || OnTriggerMove == null) return;
            if (e.Row.Item is not TriggerRow row || e.EditingElement is not TextBox editor) return;
            if (e.Column != _colTrigTime) return;

            if (!float.TryParse(editor.Text.Trim().Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var newTime))
            {
                // Can't rebuild ItemsSource inside the edit transaction.
                Dispatcher.BeginInvoke(new Action(RefreshTriggerRows), DispatcherPriority.Background);
                return;
            }
            newTime = Math.Clamp(newTime, 0, _clip.Duration);
            if (Math.Abs(newTime - row.Source.Time) < 1e-4f) return;
            OnTriggerMove(row.Source, newTime);
        }

        private void RefreshTriggerRows()
        {
            _refreshingRows = true;
            if (_clip == null)
                _trigGrid.ItemsSource = null;
            else
            {
                float dt = _clip.NumFrames > 0 && _clip.Duration > 0 ? _clip.Duration / _clip.NumFrames : 0;
                _trigGrid.ItemsSource = _triggers.OrderBy(t => t.Time).Select(t => new TriggerRow
                {
                    Source = t,
                    Time = t.Time.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    Frame = dt > 0 ? Math.Clamp((int)Math.Round(t.Time / dt), 0, _clip.NumFrames - 1) : 0,
                    Event = t.EventName,
                    End = t.RelativeToEnd ? "✓" : ""
                }).ToList();
            }
            _trigGrid.IsReadOnly = OnTriggerMove == null;
            _trigAddBtn.IsEnabled = _clip != null && OnTriggerAdd != null;
            _refreshingRows = false;
        }

        /// <summary>Walks up from an event source to the DataGridRow it lives in, if any.</summary>
        private static DataGridRow? FindRow(object source)
        {
            var dep = source as DependencyObject;
            while (dep is System.Windows.Media.Visual && dep is not DataGridRow)
                dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
            return dep as DataGridRow;
        }

        /// <summary>Inline cell commit → the same Edit pipeline the dialog uses.</summary>
        private void OnAnnotationCellEdit(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (_clip == null || OnAnnotationEdit == null) return;
            if (e.Row.Item is not AnnotationRow row || e.EditingElement is not TextBox editor) return;

            var raw = editor.Text.Trim();
            float newTime = row.Source.Time;
            string newText = row.Source.Text;

            if (e.Column == _colTime)
            {
                if (!float.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out newTime))
                { RevertRowsLater(); return; }
                newTime = Math.Clamp(newTime, 0, _clip.Duration);
            }
            else if (e.Column == _colText)
            {
                if (raw.Length == 0) { RevertRowsLater(); return; }
                newText = raw;
            }
            else return;

            if (newText == row.Source.Text && Math.Abs(newTime - row.Source.Time) < 1e-4f)
                return;

            _ = OnAnnotationEdit(new Core.Animation.AnnotationEdit
            {
                Kind = Core.Animation.AnnotationEditKind.Edit,
                TrackIndex = row.Source.TrackIndex,
                OldTime = row.Source.Time,
                OldText = row.Source.Text,
                NewTime = newTime,
                NewText = newText
            });

            // Can't rebuild ItemsSource inside the edit transaction.
            void RevertRowsLater() => Dispatcher.BeginInvoke(new Action(RefreshAnnotationRows),
                DispatcherPriority.Background);
        }

        private void RefreshAnnotationRows()
        {
            _refreshingRows = true;
            if (_clip == null)
                _annGrid.ItemsSource = null;
            else
            {
                float dt = _clip.NumFrames > 0 && _clip.Duration > 0 ? _clip.Duration / _clip.NumFrames : 0;
                _annGrid.ItemsSource = _clip.Annotations.Select(a => new AnnotationRow
                {
                    Source = a,
                    Time = a.Time.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    Frame = dt > 0 ? Math.Clamp((int)Math.Round(a.Time / dt), 0, _clip.NumFrames - 1) : 0,
                    Track = a.TrackIndex,
                    Text = a.Text
                }).ToList();
                _colTrack.Visibility = _clip.AnnotationTrackNames.Count > 1
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            _annGrid.IsReadOnly = OnAnnotationEdit == null;
            _annAddBtn.IsEnabled = _clip != null && OnAnnotationEdit != null;
            _refreshingRows = false;
        }

        /// <summary>Timeline x → clip time, using the same inset math as DrawTicks.</summary>
        private float TimeAtPosition(double x)
        {
            if (_clip == null || _clip.Duration <= 0) return 0;
            double inset = 8;
            double usable = Math.Max(_tickOverlay.ActualWidth - inset * 2, 1);
            return (float)Math.Clamp((x - inset) / usable * _clip.Duration, 0, _clip.Duration);
        }

        private float CurrentTime()
            => _clip == null ? 0 : (float)((_scrub.Value / 1000.0) * _clip.Duration);

        /// <summary>Nearest frame boundary (frame grid = duration/numFrames, matching FrameAt).</summary>
        private float SnapToFrame(float t)
        {
            if (_clip == null || _clip.NumFrames <= 0 || _clip.Duration <= 0) return t;
            float dt = _clip.Duration / _clip.NumFrames;
            return (float)Math.Clamp((float)Math.Round(t / dt) * dt, 0, _clip.Duration);
        }

        private void OnHostKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.A) return;
            if (_clip == null || OnAnnotationEdit == null) return;
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            e.Handled = true;
            _ = AddAnnotationFlow(CurrentTime());
        }

        private async System.Threading.Tasks.Task AddAnnotationFlow(float t)
        {
            if (_clip == null || OnAnnotationEdit == null) return;
            Stop();
            // Single-track files skip the track row and land on track 0 (hkanno
            // convention); multi-track files get a track picker defaulting to 0.
            var dlg = new Dialogs.AnnotationDialog("Add Annotation", "",
                SnapToFrame(t), _clip.Duration, _clip.NumFrames,
                _clip.AnnotationTrackNames)
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            await OnAnnotationEdit(new Core.Animation.AnnotationEdit
            {
                Kind = Core.Animation.AnnotationEditKind.Add,
                TrackIndex = dlg.SelectedTrack,
                NewTime = dlg.AnnotationTime,
                NewText = dlg.AnnotationText
            });
        }

        private async System.Threading.Tasks.Task EditAnnotationFlow(Core.Animation.AnimationAnnotation a)
        {
            if (_clip == null || OnAnnotationEdit == null) return;
            Stop();
            // Deliberately unsnapped — an existing annotation keeps its exact time
            // unless the user changes it.
            var dlg = new Dialogs.AnnotationDialog("Edit Annotation", a.Text,
                a.Time, _clip.Duration, _clip.NumFrames,
                _clip.AnnotationTrackNames, a.TrackIndex, canChangeTrack: false)
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            if (dlg.AnnotationText == a.Text && Math.Abs(dlg.AnnotationTime - a.Time) < 1e-4f)
                return;   // no change

            await OnAnnotationEdit(new Core.Animation.AnnotationEdit
            {
                Kind = Core.Animation.AnnotationEditKind.Edit,
                TrackIndex = a.TrackIndex,
                OldTime = a.Time,
                OldText = a.Text,
                NewTime = dlg.AnnotationTime,
                NewText = dlg.AnnotationText
            });
        }

        private void HighlightTicks(double t)
        {
            foreach (var child in _tickOverlay.Children)
                if (child is System.Windows.Shapes.Polygon marker && marker.Tag is string tag)
                {
                    var parts = tag.Split('|');
                    bool near = parts.Length == 2 &&
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var at)
                        && Math.Abs(at - t) < 0.05;
                    marker.Stroke = near ? Brushes.Lime : null;
                    marker.StrokeThickness = near ? 1.5 : 0;
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
