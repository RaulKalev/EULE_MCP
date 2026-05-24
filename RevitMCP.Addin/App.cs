using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.Tools;
using RevitMCP.Addin.Tools.Delivery;
using RevitMCP.Addin.Tools.Excel;
using RevitMCP.Addin.Tools.FileSystem;
using RevitMCP.Addin.Tools.ParameterQA;
using RevitMCP.Addin.Tools.Reports;
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
            handler.RegisterTool(new CheckCircuitParameterCompletenessTool());
            handler.RegisterTool(new SelectCircuitElementsTool());
            handler.RegisterTool(new SelectUncircuitedElementsTool());
            handler.RegisterTool(new ExportCircuitHealthToExcelTool());
            handler.RegisterTool(new ExportUncircuitedElementsToExcelTool());
            handler.RegisterTool(new GetCircuitsForSelectedElementsTool());
            handler.RegisterTool(new FindElementsOnCircuitTool());
            handler.RegisterTool(new GetCircuitLoadSummaryTool());
            handler.RegisterTool(new CheckPanelUtilizationTool());
            handler.RegisterTool(new PreviewCircuitNumberingTool());
            handler.RegisterTool(new ApplyCircuitNumberingTool());
            handler.RegisterTool(new PreviewCircuitLoadNamesTool());
            handler.RegisterTool(new ApplyCircuitLoadNamesTool());
            handler.RegisterTool(new SetCircuitParametersBulkTool());

            // Electrical Dashboard (Group A)
            handler.RegisterTool(new GetElectricalDashboardSummaryTool());
            handler.RegisterTool(new GetPanelIssueSummaryTool());
            handler.RegisterTool(new ExportElectricalDashboardToExcelTool());

            // Voltage-Drop Preparation (Group B)
            handler.RegisterTool(new GetCircuitRouteAssumptionsTool());
            handler.RegisterTool(new EstimateCircuitLengthTool());
            handler.RegisterTool(new EstimateCircuitLengthsTool());
            handler.RegisterTool(new ExportVoltageDropInputToExcelTool());
            handler.RegisterTool(new GetVoltageDropPrecheckTool());

            // Fire Alarm / ATS Preset (Group C)
            handler.RegisterTool(new RunFireAlarmCircuitPresetTool());
            handler.RegisterTool(new ExportFireAlarmCircuitPresetToExcelTool());
            handler.RegisterTool(new GetFireAlarmVisualizationDataTool());
            handler.RegisterTool(new GetFireAlarmVoltageDropSummaryTool());
            handler.RegisterTool(new ListCableResistanceProfilesTool());
            handler.RegisterTool(new GetMatchingCableResistanceProfileTool());

            // View / Sheet / Documentation (Phase 1 — Discovery)
            handler.RegisterTool(new ListTitleBlocksTool());
            handler.RegisterTool(new ListViewTemplatesTool());
            handler.RegisterTool(new ListRevisionsTool());
            handler.RegisterTool(new ListRevisionNumberingSequencesTool());
            handler.RegisterTool(new GetSheetRevisionsTool());
            handler.RegisterTool(new GetSheetViewportsTool());
            handler.RegisterTool(new FindUnplacedViewsTool());
            handler.RegisterTool(new GetViewSheetSummaryTool());
            handler.RegisterTool(new ListViewSheetPresetsTool());
            handler.RegisterTool(new GetViewSheetPresetTool());
            handler.RegisterTool(new ValidateViewSheetPresetTool());
            handler.RegisterTool(new RunViewSheetWorkflowPresetTool());

            // View / Sheet / Documentation (Phase 2 — Preview)
            handler.RegisterTool(new PreviewPlaceViewsOnSheetsTool());
            handler.RegisterTool(new PreviewDuplicateSheetsTool());
            handler.RegisterTool(new PreviewCreateSheetsFromTableTool());
            handler.RegisterTool(new PreviewDuplicateViewsTool());
            handler.RegisterTool(new PreviewRenameViewsTool());
            handler.RegisterTool(new PreviewRenameSheetsTool());

            // View / Sheet / Documentation (Phase 3 — Write)
            handler.RegisterTool(new PlaceViewsOnSheetsTool());
            handler.RegisterTool(new DuplicateSheetsTool());
            handler.RegisterTool(new CreateSheetsFromTableTool());
            handler.RegisterTool(new DuplicateViewsTool());
            handler.RegisterTool(new ApplyViewTemplateTool());
            handler.RegisterTool(new SetSheetParametersBulkTool());
            handler.RegisterTool(new SetViewParametersBulkTool());
            handler.RegisterTool(new RenameViewsTool());
            handler.RegisterTool(new RenameSheetsTool());

            // View / Sheet / Documentation (Phase 4 — Destructive)
            handler.RegisterTool(new PreviewDeleteViewsTool());
            handler.RegisterTool(new DeleteViewsTool());
            handler.RegisterTool(new PreviewDeleteSheetsTool());
            handler.RegisterTool(new DeleteSheetsTool());

            // Coordination — Phase 1 — Discovery
            handler.RegisterTool(new ListClashableCategoriesTool());
            handler.RegisterTool(new ListClashableLinksTool());
            handler.RegisterTool(new GetClashCandidatesTool());
            // Coordination — Phase 2 — Detection
            handler.RegisterTool(new DetectHardClashesTool());
            handler.RegisterTool(new DetectClearanceClashesTool());
            handler.RegisterTool(new GetClashSummaryTool());
            // Coordination — Phase 3 — Presets
            handler.RegisterTool(new ListClashPresetsTool());
            handler.RegisterTool(new GetClashPresetTool());
            handler.RegisterTool(new ValidateClashPresetTool());
            handler.RegisterTool(new RunClashPresetTool());
            // Coordination — Phase 4 — Reporting
            handler.RegisterTool(new ExportClashReportToExcelTool());
            handler.RegisterTool(new GetClashDashboardSummaryTool());
            // Coordination — Phase 5 — Navigation & Review View
            handler.RegisterTool(new GetNextClashTool());
            handler.RegisterTool(new GetPreviousClashTool());
            handler.RegisterTool(new CreateClashReviewViewTool());
            handler.RegisterTool(new FocusClashTool());
            handler.RegisterTool(new SelectClashElementsTool());

            // Issue Reports
            handler.RegisterTool(new ExportIssueReportJsonTool());
            handler.RegisterTool(new ExportIssueReportExcelTool());
            handler.RegisterTool(new ExportIssueReportMarkdownTool());
            handler.RegisterTool(new MergeIssueReportsTool());

            // Family Creation
            handler.RegisterTool(new CreatePanelSchematicSymbolFromDwgTool());

            // Skills
            handler.RegisterTool(new ListSkillsTool());
            handler.RegisterTool(new GetSkillDetailsTool());
            handler.RegisterTool(new PreviewSkillRunTool());
            handler.RegisterTool(new RunSkillTool());
            handler.RegisterTool(new RunSkillTaskTool());
            handler.RegisterTool(new CreateProjectSkillOverrideTool());
            handler.RegisterTool(new UpdateProjectSkillOverrideTool());
            handler.RegisterTool(new ResetProjectSkillOverrideTool());
            handler.RegisterTool(new ConfigureSheetNamingSkillTool());

            // Delivery Tools
            handler.RegisterTool(new DeliveryScanFolderTool());
            handler.RegisterTool(new DeliveryCheckAgainstRevitSheetsTool());
            handler.RegisterTool(new DeliveryCheckAgainstExcelRegisterTool());
            handler.RegisterTool(new DeliveryRunFullCheckTool());

            // File System Tools
            handler.RegisterTool(new FileReadTextTool());
            handler.RegisterTool(new FileWriteTextTool());
            handler.RegisterTool(new FileListDirectoryTool());

            // Excel Modifier Tools
            handler.RegisterTool(new ExcelInspectWorkbookTool());
            handler.RegisterTool(new ExcelReadRangeTool());
            handler.RegisterTool(new ExcelUpdateCellsTool());
            handler.RegisterTool(new ExcelInsertRowsTool());
            handler.RegisterTool(new ExcelAppendTableRowsTool());

            // Parameter QA Rule Set Tools
            handler.RegisterTool(new ListParameterQaRuleSetsTool());
            handler.RegisterTool(new RunParameterQaRuleSetTool());

            var eventService = new ExternalEventService(handler);

            var approvalService = new ApprovalService();
            approvalService.SetRedispatch(eventService.Redispatch);
            handler.SetApprovalService(approvalService);
            handler.SetActivityLogger(logger);

            var pipeServer = new PipeServer(RevitMcpDefaults.PipeName, eventService, logger);
            var connector = new ConnectorService(pipeServer, eventService, approvalService);
            _connector = connector;
            _viewModel = new McpWindowViewModel(connector, logger, approvalService);

            // Auto-start the pipe server so [AppLoader] hot-reloads are transparent to agents.
            // OnShutdown calls PanicStop (stops old pipe), then OnStartup creates + starts a fresh one —
            // no manual "Start" button click required after a rebuild.
            connector.Start();
            DiagLog("ConnectorService auto-started.");

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
