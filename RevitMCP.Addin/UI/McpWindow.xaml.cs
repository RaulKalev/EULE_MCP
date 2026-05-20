using System.Windows;
using System.Windows.Interop;
using RevitMCP.Addin.UI.ViewModels;

namespace RevitMCP.Addin.UI;

public partial class McpWindow : Window
{
    public McpWindowViewModel ViewModel { get; }

    public McpWindow(McpWindowViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public void SetRevitOwner(IntPtr revitHandle)
    {
        var helper = new WindowInteropHelper(this);
        helper.Owner = revitHandle;
    }
}
