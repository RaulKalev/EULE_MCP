using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.UI;

namespace RevitMCP.Addin.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class OpenMcpWindowCommand : IExternalCommand
{
    private static McpWindow? _window;
    private static bool _pendingShow;
    private static bool _contextRefreshSubscribed;
    private static DateTime _lastContextRefresh = DateTime.MinValue;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        // Subscribe context refresh on first click so status panel updates immediately
        if (!_contextRefreshSubscribed)
        {
            commandData.Application.Idling += OnRevitIdlingContextRefresh;
            _contextRefreshSubscribed = true;
        }

        if (_window != null && _window.IsVisible)
        {
            _window.Activate();
            return Result.Succeeded;
        }

        // Defer window creation via Idling — required for Revit 2026 to avoid Dispatcher issues
        if (!_pendingShow)
        {
            _pendingShow = true;
            commandData.Application.Idling += OnRevitIdling;
        }

        return Result.Succeeded;
    }

    private static void OnRevitIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if (sender is not UIApplication uiapp) return;

        uiapp.Idling -= OnRevitIdling;
        _pendingShow = false;

        var vm = App.GetViewModel();
        if (vm == null) return;

        _window = new McpWindow(vm);
        _window.SetRevitOwner(uiapp.MainWindowHandle);
        _window.Show();
    }

    private static void OnRevitIdlingContextRefresh(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if ((DateTime.Now - _lastContextRefresh).TotalSeconds < 2) return;
        _lastContextRefresh = DateTime.Now;

        if (sender is not UIApplication uiapp) return;

        var vm = App.GetViewModel();
        if (vm == null) return;

        var ctx = RevitContextService.Read(uiapp);
        vm.UpdateFromRevitContext(
            ctx.IsDocumentOpen ? ctx.DocumentTitle : "No document open",
            ctx.IsDocumentOpen ? ctx.ActiveViewName : "—",
            ctx.IsWorkshared,
            ctx.RevitUsername,
            ctx.SelectedElementCount);
    }
}
