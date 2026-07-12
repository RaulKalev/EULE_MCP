using System.Windows;
using System.Windows.Input;

namespace RevitMCP.Addin.UI;

public partial class DirectEditWarningDialog : Window
{
    public bool DontShowAgain => DontShowAgainCheckBox.IsChecked == true;
    public bool IsConfirmed { get; private set; }

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
        IsConfirmed = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
