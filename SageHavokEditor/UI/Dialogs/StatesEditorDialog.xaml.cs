using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SageHavokEditor.Models.ViewModels;

namespace SageHavokEditor.UI.Dialogs
{
    public partial class SmTransitionDialog : Window
    {
        // ── Bound collections ─────────────────────────────────────────────────────
        public ObservableCollection<IdNamePair> FromStateOptions { get; } = new();
        public ObservableCollection<IdNamePair> EventList { get; } = new();
        public ObservableCollection<IdNamePair> StateOptions { get; } = new();
        /// <summary>
        /// Transition effects offered for the "transition" param: "(none)", every effect
        /// already in the file, and <see cref="NewBlendEffectKey"/>.
        /// </summary>
        public ObservableCollection<IdNamePair> EffectOptions { get; } = new();

        /// <summary>
        /// Synthetic effect key for "＋ New blending effect…". Not an object id, so it cannot
        /// collide with one; the caller creates the hkbBlendingTransitionEffect on confirm.
        /// </summary>
        public const string NewBlendEffectKey = "__NEW_BLEND__";

        // ── Result properties ─────────────────────────────────────────────────────
        public string ResultFromStateId { get; private set; } = "";
        public string ResultEventId { get; private set; } = "";
        public string ResultToStateId { get; private set; } = "";
        public string ResultFlags { get; private set; } = "";
        /// <summary>Chosen effect: an object id, "null", or <see cref="NewBlendEffectKey"/>.</summary>
        public string ResultEffectId { get; private set; } = "null";
        /// <summary>Duration for the effect to create, Havok-formatted. Only meaningful when
        /// <see cref="ResultEffectId"/> is <see cref="NewBlendEffectKey"/>.</summary>
        public string ResultNewBlendDuration { get; private set; } = "0.200000";

        // ── Dependency properties for combo bindings ──────────────────────────────
        public static readonly DependencyProperty SelectedFromStateIdProperty =
            DependencyProperty.Register(nameof(SelectedFromStateId), typeof(string), typeof(SmTransitionDialog));
        public static readonly DependencyProperty SelectedEventIdProperty =
            DependencyProperty.Register(nameof(SelectedEventId), typeof(string), typeof(SmTransitionDialog));
        public static readonly DependencyProperty SelectedToStateIdProperty =
            DependencyProperty.Register(nameof(SelectedToStateId), typeof(string), typeof(SmTransitionDialog));
        public static readonly DependencyProperty SelectedEffectIdProperty =
            DependencyProperty.Register(nameof(SelectedEffectId), typeof(string), typeof(SmTransitionDialog));

        public string SelectedFromStateId
        {
            get => (string)GetValue(SelectedFromStateIdProperty);
            set => SetValue(SelectedFromStateIdProperty, value);
        }
        public string SelectedEventId
        {
            get => (string)GetValue(SelectedEventIdProperty);
            set => SetValue(SelectedEventIdProperty, value);
        }
        public string SelectedToStateId
        {
            get => (string)GetValue(SelectedToStateIdProperty);
            set => SetValue(SelectedToStateIdProperty, value);
        }
        public string SelectedEffectId
        {
            get => (string)GetValue(SelectedEffectIdProperty);
            set => SetValue(SelectedEffectIdProperty, value);
        }

        // ── Constructor ───────────────────────────────────────────────────────────
        public SmTransitionDialog(
            string title,
            IEnumerable<IdNamePair> fromStateOptions,
            IEnumerable<IdNamePair> events,
            IEnumerable<IdNamePair> toStateOptions,
            string? initialFromStateId = null,
            string? initialEventId = null,
            string? initialToStateId = null,
            string initialFlags = "FLAG_DISABLE_CONDITION",
            IEnumerable<IdNamePair>? effectOptions = null,
            string? initialEffectId = null,
            string initialBlendDuration = "0.2")
        {
            InitializeComponent();

            TitleLabel.Text = title;

            foreach (var s in fromStateOptions) FromStateOptions.Add(s);
            foreach (var e in events) EventList.Add(e);
            foreach (var s in toStateOptions) StateOptions.Add(s);
            foreach (var eo in effectOptions ?? Enumerable.Empty<IdNamePair>()) EffectOptions.Add(eo);

            SelectedFromStateId = initialFromStateId ?? "";
            SelectedEventId = initialEventId ?? "";
            SelectedToStateId = initialToStateId ?? "";
            FlagsBox.Text = initialFlags ?? "";
            SelectedEffectId = initialEffectId ?? "null";
            BlendDurationBox.Text = initialBlendDuration;
            UpdateNewBlendVisibility();
        }

        // ── Blend effect ──────────────────────────────────────────────────────────
        private void EffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateNewBlendVisibility();

        /// <summary>The duration box is only meaningful while a new effect is being created.</summary>
        private void UpdateNewBlendVisibility()
        {
            if (NewBlendPanel == null) return;
            NewBlendPanel.Visibility = (EffectCombo?.SelectedValue as string) == NewBlendEffectKey
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // ── Title bar drag ────────────────────────────────────────────────────────
        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        // ── Buttons ───────────────────────────────────────────────────────────────
        private void BtnClose_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void BtnCancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            ResultFromStateId = SelectedFromStateId;
            ResultEventId = SelectedEventId;
            ResultToStateId = SelectedToStateId;
            ResultFlags = FlagsBox.Text;
            ResultEffectId = SelectedEffectId ?? "null";

            if (ResultEffectId == NewBlendEffectKey)
            {
                // A bad duration must not reach the file: Havok reads it as a float and a
                // negative one blends backwards forever. Read a typed comma as a decimal
                // point — Havok's own format is invariant, but the person typing may not be.
                var typed = (BlendDurationBox.Text ?? "").Trim().Replace(',', '.');
                if (!float.TryParse(typed, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var seconds)
                    || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0)
                {
                    MessageBox.Show(this,
                        "Blend duration must be a number of seconds, zero or greater (e.g. 0.2).",
                        "Invalid duration", MessageBoxButton.OK, MessageBoxImage.Warning);
                    BlendDurationBox.Focus();
                    BlendDurationBox.SelectAll();
                    return;
                }
                ResultNewBlendDuration = seconds.ToString("F6", CultureInfo.InvariantCulture);
            }

            DialogResult = true;
            Close();
        }
    }
}
