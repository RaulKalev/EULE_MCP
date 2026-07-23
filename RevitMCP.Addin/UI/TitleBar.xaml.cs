using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RevitMCP.Addin.UI;

public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
    }

    private void Border_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            Window.GetWindow(this)?.DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is McpWindow window)
            window.MinimizeToRevit();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is McpWindow window)
            window.HideAndSave();
    }
}
