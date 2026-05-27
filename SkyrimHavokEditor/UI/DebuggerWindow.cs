using System.Windows;
using System.Windows.Media;
using SkyrimHavokEditor.UI.Dialogs;

namespace SkyrimHavokEditor.UI
{
    public class DebuggerWindow : Window
    {
        public DebuggerWindow(DebuggerViewModel vm, Window owner)
        {
            Title = "Live Debugger";
            Width = 260;
            Height = 700;
            WindowStyle = WindowStyle.ToolWindow;
            ShowInTaskbar = false;
            Owner = owner;          // ← stays in front of editor
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x1A));

            var panel = new DebugPanelView { DataContext = vm };
            Content = panel;

            Closing += (_, e) =>
            {
                e.Cancel = true;
                Hide();
                ReturnToDock?.Invoke();
            };
        }

        public System.Action? ReturnToDock { get; set; }
    }
}
