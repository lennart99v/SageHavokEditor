using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using SkyrimHavokEditor.Models.ViewModels;

namespace SkyrimHavokEditor.UI.Dialogs
{
    public partial class SmTransitionDialog : Window
    {
        // ── Bound collections ─────────────────────────────────────────────────────
        public ObservableCollection<IdNamePair> FromStateOptions { get; } = new();
        public ObservableCollection<IdNamePair> EventList { get; } = new();
        public ObservableCollection<IdNamePair> StateOptions { get; } = new();

        // ── Result properties ─────────────────────────────────────────────────────
        public string ResultFromStateId { get; private set; }
        public string ResultEventId { get; private set; }
        public string ResultToStateId { get; private set; }
        public string ResultFlags { get; private set; }

        // ── Dependency properties for combo bindings ──────────────────────────────
        public static readonly DependencyProperty SelectedFromStateIdProperty =
            DependencyProperty.Register(nameof(SelectedFromStateId), typeof(string), typeof(SmTransitionDialog));
        public static readonly DependencyProperty SelectedEventIdProperty =
            DependencyProperty.Register(nameof(SelectedEventId), typeof(string), typeof(SmTransitionDialog));
        public static readonly DependencyProperty SelectedToStateIdProperty =
            DependencyProperty.Register(nameof(SelectedToStateId), typeof(string), typeof(SmTransitionDialog));

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

        // ── Constructor ───────────────────────────────────────────────────────────
        public SmTransitionDialog(
            string title,
            IEnumerable<IdNamePair> fromStateOptions,
            IEnumerable<IdNamePair> events,
            IEnumerable<IdNamePair> toStateOptions,
            string initialFromStateId = null,
            string initialEventId = null,
            string initialToStateId = null,
            string initialFlags = "FLAG_DISABLE_CONDITION")
        {
            InitializeComponent();

            TitleLabel.Text = title;

            foreach (var s in fromStateOptions) FromStateOptions.Add(s);
            foreach (var e in events) EventList.Add(e);
            foreach (var s in toStateOptions) StateOptions.Add(s);

            SelectedFromStateId = initialFromStateId;
            SelectedEventId = initialEventId;
            SelectedToStateId = initialToStateId;
            FlagsBox.Text = initialFlags ?? "";
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
            DialogResult = true;
            Close();
        }
    }
}