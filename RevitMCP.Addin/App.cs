using Autodesk.Revit.UI;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.Tools;
using RevitMCP.Addin.UI.ViewModels;
using RevitMCP.Core.Configuration;

namespace RevitMCP.Addin;

public class App : IExternalApplication
{
    private static McpWindowViewModel? _viewModel;
    private static ConnectorService? _connector;

    public static McpWindowViewModel? GetViewModel() => _viewModel;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // Build services
            var logger = new ActivityLogger();
            var handler = new ExternalEventHandler();
            handler.RegisterTool(new GetConnectionStatusTool());
            handler.RegisterTool(new GetSelectedElementsTool());
            handler.RegisterTool(new ListViewsTool());
            handler.RegisterTool(new ListSheetsTool());
            handler.RegisterTool(new ListSchedulesTool());
            handler.RegisterTool(new GetElementParametersTool());
            handler.RegisterTool(new CountElementsTool());
            handler.RegisterTool(new GroupByParameterTool());
            handler.RegisterTool(new FindElementsByParameterTool());
            handler.RegisterTool(new GetElementsInfoTool());
            handler.RegisterTool(new GroupElementsTool());
            handler.RegisterTool(new ExportQueryToExcelTool());

            var eventService = new ExternalEventService(handler);
            var pipeServer = new PipeServer(RevitMcpDefaults.PipeName, eventService, logger);
            var connector = new ConnectorService(pipeServer, eventService);
            _connector = connector;
            _viewModel = new McpWindowViewModel(connector, logger);

            // Ribbon
            AddRibbonButton(application);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitMCP Startup Error", ex.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _connector?.PanicStop();
        return Result.Succeeded;
    }

    private static void AddRibbonButton(UIControlledApplication application)
    {
        const string tabName = "RK Tools";

        try { application.CreateRibbonTab(tabName); } catch { }

        var panel = application.CreateRibbonPanel(tabName, "MCP");

        var isDark = Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark;
        var iconName = isDark ? "Light - RevitMCP.tiff" : "Dark - RevitMCP.tiff";

        var buttonData = new PushButtonData(
            "RevitMCPConnector",
            "MCP\nConnector",
            typeof(App).Assembly.Location,
            typeof(Commands.OpenMcpWindowCommand).FullName!)
        {
            ToolTip = "Start or stop the Revit MCP Connector for AI agent access.",
            LongDescription = "Opens the Revit MCP Connector window. Start the connector to allow Claude Code or Codex to inspect the active model."
        };

        try
        {
            var packUri = $"pack://application:,,,/RevitMCP.Addin;component/Assets/{iconName}";
            buttonData.LargeImage = new System.Windows.Media.Imaging.BitmapImage(new Uri(packUri));
        }
        catch
        {
            // Icon loading failure is non-fatal
        }

        panel.AddItem(buttonData);
    }
}
