using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.Tools;
using RevitMCP.Addin.UI.ViewModels;
using RevitMCP.Core.Configuration;
using ricaun.Revit.UI;

namespace RevitMCP.Addin;

[AppLoader]
public class App : IExternalApplication
{
    private static McpWindowViewModel? _viewModel;
    private static ConnectorService? _connector;

    public static McpWindowViewModel? GetViewModel() => _viewModel;

    private static readonly string DiagLogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCP_startup.log");

    private static void DiagLog(string msg)
    {
        try { System.IO.File.AppendAllText(DiagLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); } catch { }
    }

    public Result OnStartup(UIControlledApplication application)
    {
        try { System.IO.File.WriteAllText(DiagLogPath, $"=== RevitMCP OnStartup {DateTime.Now} ===\r\n"); } catch { }
        DiagLog($"Assembly: {typeof(App).Assembly.Location}");
        try
        {
            DiagLog("Creating ActivityLogger");
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
            handler.RegisterTool(new GetAvailableParametersTool());
            handler.RegisterTool(new ListQueryPresetsTool());
            handler.RegisterTool(new RunQueryPresetTool());
            handler.RegisterTool(new CheckParameterCompletenessTool());
            handler.RegisterTool(new ExportViewListToExcelTool());
            handler.RegisterTool(new ExportSheetListToExcelTool());
            handler.RegisterTool(new ExportScheduleListToExcelTool());
            handler.RegisterTool(new SelectElementsTool());
            handler.RegisterTool(new SelectElementsByQueryTool());
            handler.RegisterTool(new SetParameterTool());
            handler.RegisterTool(new GetElectricalCircuitsTool());
            handler.RegisterTool(new GetCircuitInfoTool());
            handler.RegisterTool(new GetAvailablePanelsTool());
            handler.RegisterTool(new GetAvailableCableTypesTool());
            handler.RegisterTool(new GetAvailableWireTypesTool());
            handler.RegisterTool(new GetCircuitCompatibleElementsTool());
            handler.RegisterTool(new CreateElectricalCircuitTool());
            handler.RegisterTool(new AddElementsToCircuitTool());
            handler.RegisterTool(new ReassignCircuitPanelTool());
            handler.RegisterTool(new ChangeCircuitCableOrWireTypeTool());
            handler.RegisterTool(new SetCircuitParameterTool());
            handler.RegisterTool(new FindUncircuitedElementsTool());
            handler.RegisterTool(new CheckCircuitHealthTool());
            handler.RegisterTool(new ExportPanelCircuitListToExcelTool());
            handler.RegisterTool(new FindCircuitsByElementParameterTool());
            handler.RegisterTool(new TraceCircuitTool());

            var eventService = new ExternalEventService(handler);

            var approvalService = new ApprovalService();
            approvalService.SetRedispatch(eventService.Redispatch);
            handler.SetApprovalService(approvalService);

            var pipeServer = new PipeServer(RevitMcpDefaults.PipeName, eventService, logger);
            var connector = new ConnectorService(pipeServer, eventService, approvalService);
            _connector = connector;
            _viewModel = new McpWindowViewModel(connector, logger, approvalService);

            // Startup validation: log all registered tool names so mismatches (e.g. after a
            // partial rebuild before Revit restart) are visible in the diagnostic log.
            var registeredTools = handler.GetRegisteredToolNames();
            DiagLog($"Registered tools ({registeredTools.Count}): {string.Join(", ", registeredTools)}");

            // Ribbon
            DiagLog("Calling AddRibbonButton");
            AddRibbonButton(application);
            DiagLog("AddRibbonButton OK");

            DiagLog("OnStartup SUCCEEDED");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            DiagLog($"EXCEPTION: {ex.GetType().FullName}: {ex.Message}");
            DiagLog($"StackTrace: {ex.StackTrace}");
            if (ex.InnerException != null)
                DiagLog($"InnerException: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
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

        var panel = application.GetRibbonPanels(tabName).FirstOrDefault(p => p.Name == "MCP")
                    ?? application.CreateRibbonPanel(tabName, "MCP");

        var isDark = Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark;
        var iconName = isDark ? "Light - AI1.tiff" : "Dark - AI1.tiff";

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

        // Only add the button if it doesn't already exist (guard against reloads)
        if (!panel.GetItems().Any(i => i.Name == "RevitMCPConnector"))
            panel.AddItem(buttonData);
    }
}
