using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SkyrimHavokEditor.UI.Dialogs
{
    public class SettingsDialog : Window
    {
        // Theme brush helpers — pull from app resources, fall back to dark defaults
        private static Brush B(string key, Color fallback)
            => Application.Current.Resources[key] as Brush ?? new SolidColorBrush(fallback);

        private Brush BgPanel => B("BgPanelBrush", Color.FromRgb(0x1E, 0x1E, 0x24));
        private Brush BgInput => B("BgInputBrush", Color.FromRgb(0x2A, 0x2A, 0x32));
        private Brush TextPrimary => B("TextPrimaryBrush", Color.FromRgb(0xE0, 0xE0, 0xE0));
        private Brush TextSecondary => B("TextSecondaryBrush", Color.FromRgb(0x9D, 0x9D, 0x9D));
        private Brush InputText => B("InputTextBrush", Color.FromRgb(0xE0, 0xE0, 0xE0));
        private Brush Border_ => B("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x44));

        private readonly TextBox _gamePath = new();
        private readonly TextBox _meshesPath = new();
        private readonly TextBlock _meshesEffective = new() { FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };
        private readonly ComboBox _theme = new();
        private readonly ComboBox _previewAxis = new();
        private readonly CheckBox _previewAutoplay = new() { Content = "Start playing automatically", VerticalAlignment = VerticalAlignment.Center };

        public bool PathsChanged { get; private set; }
        public bool ThemeChanged { get; private set; }

        private readonly bool _origDark;
        private readonly string _origGame, _origMeshes;

        public SettingsDialog(Window owner)
        {
            Owner = owner;
            Title = "Settings";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = BgPanel;

            StyleInput(_gamePath);
            StyleInput(_meshesPath);
            StyleCombo(_theme);
            StyleCombo(_previewAxis);
            _meshesEffective.Foreground = TextSecondary;
            _previewAutoplay.Foreground = TextPrimary;   // ← the invisible-label fix

            _origGame = AppSettings.GamePath;
            _origMeshes = AppSettings.MeshesPath;
            _origDark = AppSettings.IsDarkMode;

            _gamePath.Text = AppSettings.GamePath;
            _meshesPath.Text = "";
            UpdateMeshesEffective();
            _gamePath.TextChanged += (_, __) => UpdateMeshesEffective();
            _meshesPath.TextChanged += (_, __) => UpdateMeshesEffective();

            _theme.Items.Add("Dark");
            _theme.Items.Add("Light");
            _theme.SelectedIndex = AppSettings.IsDarkMode ? 0 : 1;

            _previewAxis.Items.Add("Side");
            _previewAxis.Items.Add("Front");
            _previewAxis.Items.Add("Top");
            _previewAxis.SelectedItem = AppSettings.PreviewDefaultAxis;
            if (_previewAxis.SelectedIndex < 0) _previewAxis.SelectedIndex = 0;

            _previewAutoplay.IsChecked = AppSettings.PreviewAutoplay;

            Content = BuildLayout();
        }

        private void StyleInput(TextBox tb)
        {
            tb.FontSize = 12;
            tb.Padding = new Thickness(4, 3, 4, 3);
            tb.Background = BgInput;
            tb.Foreground = InputText;
            tb.BorderBrush = Border_;
            tb.CaretBrush = InputText;
        }

        private void StyleCombo(ComboBox cb)
        {
            cb.FontSize = 12;
            cb.Padding = new Thickness(4, 3, 4, 3);
            cb.Background = BgInput;
            cb.Foreground = InputText;
            cb.BorderBrush = Border_;
        }

        private void UpdateMeshesEffective()
        {
            var custom = _meshesPath.Text?.Trim() ?? "";
            string effective = !string.IsNullOrEmpty(custom)
                ? custom
                : (string.IsNullOrEmpty(_gamePath.Text?.Trim())
                    ? "(set a game path)"
                    : Path.Combine(_gamePath.Text.Trim(), "Data", "Meshes"));
            _meshesEffective.Text = "Effective: " + effective;
        }

        private UIElement BuildLayout()
        {
            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(SectionHeader("PATHS"));
            root.Children.Add(PathRow("Skyrim folder", _gamePath, BrowseGame,
                "Folder containing SkyrimSE.exe and Data\\"));
            root.Children.Add(PathRow("Meshes override", _meshesPath, BrowseMeshes,
                "Leave blank to use <game>\\Data\\Meshes"));
            root.Children.Add(_meshesEffective);

            root.Children.Add(SectionHeader("APPEARANCE"));
            root.Children.Add(LabeledRow("Theme", _theme));

            root.Children.Add(SectionHeader("PREVIEW"));
            root.Children.Add(LabeledRow("Default view", _previewAxis));
            root.Children.Add(new Border { Child = _previewAutoplay, Margin = new Thickness(0, 6, 0, 0) });

            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var save = new Button { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 4, 0, 4), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 80, Padding = new Thickness(0, 4, 0, 4), IsCancel = true };
            save.Click += Save_Click;
            cancel.Click += (_, __) => { DialogResult = false; };
            btns.Children.Add(save);
            btns.Children.Add(cancel);
            root.Children.Add(btns);

            return root;
        }

        private TextBlock SectionHeader(string text) => new()
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = TextSecondary,
            Margin = new Thickness(0, 14, 0, 6)
        };

        private UIElement PathRow(string label, TextBox box, Action browse, string hint)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock { Text = label, Foreground = TextPrimary, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, ToolTip = hint };
            Grid.SetColumn(box, 1);
            var browseBtn = new Button { Content = "📂", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0) };
            browseBtn.Click += (_, __) => browse();
            Grid.SetColumn(browseBtn, 2);

            grid.Children.Add(lbl);
            grid.Children.Add(box);
            grid.Children.Add(browseBtn);
            return grid;
        }

        private UIElement LabeledRow(string label, FrameworkElement control)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lbl = new TextBlock { Text = label, Foreground = TextPrimary, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            Grid.SetColumn(control, 1);
            grid.Children.Add(lbl);
            grid.Children.Add(control);
            return grid;
        }

        private void BrowseGame()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Locate SkyrimSE.exe",
                Filter = "Skyrim|SkyrimSE.exe;TESV.exe|All files|*.*",
                CheckFileExists = true
            };
            if (!string.IsNullOrEmpty(_gamePath.Text) && Directory.Exists(_gamePath.Text))
                dlg.InitialDirectory = _gamePath.Text;
            if (dlg.ShowDialog() == true)
                _gamePath.Text = Path.GetDirectoryName(dlg.FileName) ?? "";
        }

        private void BrowseMeshes()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select Meshes folder" };
            if (dlg.ShowDialog() == true)
                _meshesPath.Text = dlg.FolderName;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.GamePath = _gamePath.Text?.Trim() ?? "";
            AppSettings.MeshesPath = _meshesPath.Text?.Trim() ?? "";
            AppSettings.IsDarkMode = _theme.SelectedIndex == 0;
            AppSettings.PreviewDefaultAxis = _previewAxis.SelectedItem as string ?? "Side";
            AppSettings.PreviewAutoplay = _previewAutoplay.IsChecked == true;

            PathsChanged = (AppSettings.GamePath != _origGame) || (AppSettings.MeshesPath != _origMeshes);
            ThemeChanged = AppSettings.IsDarkMode != _origDark;

            DialogResult = true;
        }
    }
}