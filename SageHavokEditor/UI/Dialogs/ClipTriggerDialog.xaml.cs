using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace SageHavokEditor.UI.Dialogs
{
    /// <summary>
    /// Editor for one hkbClipTrigger: the event it raises (editable combo — an
    /// unknown name means "create this event"), linked time/frame fields (same
    /// frame grid as AnnotationDialog), and the relativeToEndOfClip anchor.
    /// TriggerTime is always the ABSOLUTE clip time; the host converts to the
    /// stored (negative) localTime when RelativeToEnd is set.
    /// </summary>
    public partial class ClipTriggerDialog : Window
    {
        private readonly float _duration;
        private readonly int _numFrames;
        private bool _syncing;

        public string EventName { get; private set; } = "";
        public float TriggerTime { get; private set; }
        public bool RelativeToEnd { get; private set; }

        public ClipTriggerDialog(string title, IEnumerable<string> eventNames,
                                 string? initialEventName, float initialTime,
                                 bool initialRelToEnd, float duration, int numFrames)
        {
            InitializeComponent();
            Title = title;
            _duration = Math.Max(duration, 0.0001f);
            _numFrames = Math.Max(numFrames, 1);

            foreach (var n in eventNames) CmbEvent.Items.Add(n);
            CmbEvent.Text = initialEventName ?? "";
            ChkRelEnd.IsChecked = initialRelToEnd;

            RangeText.Text = $"of {_duration:F3}s · {_numFrames} frames";
            SetTimeFields(initialTime);

            CmbEvent.Focus();
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
            var name = CmbEvent.Text?.Trim() ?? "";
            if (name.Length == 0)
            {
                CmbEvent.Focus();
                return;
            }
            if (!TryParseFloat(TxtTime.Text, out var t))
            {
                TxtTime.Focus();
                TxtTime.SelectAll();
                return;
            }
            EventName = name;
            TriggerTime = Math.Clamp(t, 0, _duration);
            RelativeToEnd = ChkRelEnd.IsChecked == true;
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
