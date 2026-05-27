using System.Windows;

namespace SageHavokEditor.UI.Dialogs
{
    public partial class SummaryPopup : Window
    {
        public SummaryPopup(string summaryText)
        {
            InitializeComponent();
            SummaryTextBox.Text = summaryText;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(SummaryTextBox.Text);
            MessageBox.Show("Summary copied to clipboard!");
        }
    }
}
