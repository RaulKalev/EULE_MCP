using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RevitMCP.Setup.ViewModels;

namespace RevitMCP.Setup;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the EULE MCP package folder (Dropbox)",
            InitialDirectory = _viewModel.PackageRoot
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.PackageRoot = dialog.FolderName;
    }

    private void OnLogChanged(object sender, TextChangedEventArgs e) =>
        ((TextBox)sender).ScrollToEnd();
}
