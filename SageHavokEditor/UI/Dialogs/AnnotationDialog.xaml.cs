using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace SageHavokEditor.UI.Dialogs
{
    /// <summary>
    /// Single-dialog editor for one animation annotation: text plus linked time/frame
    /// fields (editing either updates the other). The frame grid matches
    /// AnimationClip.FrameAt — frame i starts at i · duration/numFrames.
    /// </summary>
    public partial class AnnotationDialog : Window
    {
        private readonly float _duration;
        private readonly int _numFrames;
        private bool _syncing;

        public string AnnotationText { get; private set; } = "";
        public float AnnotationTime { get; private set; }
        public int SelectedTrack { get; private set; }

        /// <param name="trackNames">One entry per annotation track ("" for unnamed); the
        /// track row only appears when the animation has more than one track.</param>
        /// <param name="canChangeTrack">False on edit — an existing annotation stays on
        /// its track (moving between tracks isn't supported), so the combo is locked.</param>
        public AnnotationDialog(string title, string initialText, float initialTime,
                                float duration, int numFrames,
                                System.Collections.Generic.IReadOnlyList<string>? trackNames = null,
                                int initialTrack = 0, bool canChangeTrack = true)
        {
            InitializeComponent();
            Title = title;
            _duration = Math.Max(duration, 0.0001f);
            _numFrames = Math.Max(numFrames, 1);
            SelectedTrack = initialTrack;

            if (trackNames is { Count: > 1 })
            {
                TrackRow.Visibility = Visibility.Visible;
                for (int i = 0; i < trackNames.Count; i++)
                    CmbTrack.Items.Add(string.IsNullOrEmpty(trackNames[i])
                        ? $"Track {i}" : $"Track {i} — {trackNames[i]}");
                CmbTrack.SelectedIndex = Math.Clamp(initialTrack, 0, trackNames.Count - 1);
                CmbTrack.IsEnabled = canChangeTrack;
                if (!canChangeTrack)
                    CmbTrack.ToolTip = "An annotation can't move between tracks.";
            }

            RangeText.Text = $"of {_duration:F3}s · {_numFrames} frames";
            TxtText.Text = initialText;
            SetTimeFields(initialTime);

            TxtText.Focus();
            TxtText.SelectAll();
        }

        private float FrameDt => _duration / _numFrames;

        private void SetTimeFields(float time)
        {
            _syncing = true;
            TxtTime.Text = time.ToString("F3", CultureInfo.InvariantCulture);
            TxtFrame.Text = TimeToFrame(time).ToString(CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private int TimeToFrame(float t) =>
            Math.Clamp((int)Math.Round(t / FrameDt), 0, _numFrames - 1);

        private static bool TryParseFloat(string raw, out float v) =>
            float.TryParse((raw ?? "").Trim().Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private void TxtTime_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncing || !TryParseFloat(TxtTime.Text, out var t)) return;
            _syncing = true;
            TxtFrame.Text = TimeToFrame(t).ToString(CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void TxtFrame_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncing || !int.TryParse(TxtFrame.Text.Trim(), out var f)) return;
            f = Math.Clamp(f, 0, _numFrames - 1);
            _syncing = true;
            TxtTime.Text = (f * FrameDt).ToString("F3", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var text = TxtText.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                TxtText.Focus();
                return;
            }
            if (!TryParseFloat(TxtTime.Text, out var t))
            {
                TxtTime.Focus();
                TxtTime.SelectAll();
                return;
            }
            AnnotationText = text;
            AnnotationTime = Math.Clamp(t, 0, _duration);
            if (TrackRow.Visibility == Visibility.Visible && CmbTrack.SelectedIndex >= 0)
                SelectedTrack = CmbTrack.SelectedIndex;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
