namespace SkyrimHavokEditor.UI.Dialogs
{
    public partial class InputDialog : System.Windows.Window
    {
        public string InputText => TxtInput.Text;

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            TxtInput.Text = defaultValue;
            TxtInput.Focus();
            TxtInput.SelectAll();
            TxtInput.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) { DialogResult = true; Close(); }
                if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; Close(); }
            };
        }

        private void BtnOk_Click(object sender, System.Windows.RoutedEventArgs e)
        { DialogResult = true; Close(); }

        private void BtnCancel_Click(object sender, System.Windows.RoutedEventArgs e)
        { DialogResult = false; Close(); }
    }
}
