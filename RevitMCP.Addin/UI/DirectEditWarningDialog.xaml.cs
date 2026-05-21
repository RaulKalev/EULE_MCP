using System.Windows;
using System.Windows.Input;

namespace RevitMCP.Addin.UI;

public partial class DirectEditWarningDialog : Window
{
    public bool DontShowAgain => DontShowAgainCheckBox.IsChecked == true;

    public DirectEditWarningDialog()
    {
        InitializeComponent();
    }

    private void Border_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void EnableButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
