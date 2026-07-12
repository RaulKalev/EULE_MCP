using Autodesk.Revit.UI;
using Autodesk.Revit.DB.Events;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.Tools;
using RevitMCP.Addin.Tools.Configuration;
using RevitMCP.Addin.Tools.Delivery;
using RevitMCP.Addin.Tools.Excel;
using RevitMCP.Addin.Tools.FileSystem;
#if !REVIT2024
using RevitMCP.Addin.Tools.IfcSpaceToRoom;
#endif
using RevitMCP.Addin.Tools.ParameterQA;
using RevitMCP.Addin.Tools.Reports;
using RevitMCP.Addin.Tools.Standards;
#if !REVIT2024
using RevitMCP.Addin.UI.ViewModels;
#endif
using RevitMCP.Core.Configuration;
using ricaun.Revit.UI;

namespace RevitMCP.Addin;

[AppLoader]
public class App : IExternalApplication
{
#if !REVIT2024
    private static McpWindowViewModel? _viewModel;
    public static McpWindowViewModel? GetViewModel() => _viewModel;
#endif
    private static ConnectorService? _connector;
    public static ConnectorService? GetConnector() => _connector;

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
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
            DiagLog("Creating ActivityLogger");
            // Build services
            var logger = new ActivityLogger();
            var handler = new ExternalEventHandler();
            handler.RegisterTool(new GetConnectionStatusTool());
            handler.RegisterTool(new GetSelectedElementsTool());
            handler.RegisterTool(new InspectSelectedElementsTool());
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
#if !REVIT2024
            handler.RegisterTool(new SetParameterTool());
#endif
            handler.RegisterTool(new GetElectricalCircuitsTool());
            handler.RegisterTool(new GetCircuitInfoTool());
            handler.RegisterTool(new GetAvailablePanelsTool());
            handler.RegisterTool(new GetAvailableCableTypesTool());
            handler.RegisterTool(new GetAvailableWireTypesTool());
            handler.RegisterTool(new GetCircuitCompatibleElementsTool());
#if !REVIT2024
            handler.RegisterTool(new CreateElectricalCircuitTool());
            handler.RegisterTool(new AddElementsToCircuitTool());
            handler.RegisterTool(new ReassignCircuitPanelTool());
            handler.RegisterTool(new ChangeCircuitCableOrWireTypeTool());
            handler.RegisterTool(new SetCircuitPathModeTool());
            handler.RegisterTool(new SetCircuitParameterTool());
#endif
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
#if !REVIT2024
            handler.RegisterTool(new ApplyCircuitNumberingTool());
#endif
            handler.RegisterTool(new PreviewCircuitLoadNamesTool());
#if !REVIT2024
            handler.RegisterTool(new ApplyCircuitLoadNamesTool());
            handler.RegisterTool(new SetCircuitParametersBulkTool());
#endif

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
#if !REVIT2024
            handler.RegisterTool(new RunFireAlarmCircuitPresetTool());
#endif
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
#if !REVIT2024
            handler.RegisterTool(new RunViewSheetWorkflowPresetTool());
#endif

            // View / Sheet / Documentation (Phase 2 — Preview)
            handler.RegisterTool(new PreviewPlaceViewsOnSheetsTool());
            handler.RegisterTool(new PreviewDuplicateSheetsTool());
            handler.RegisterTool(new PreviewCreateSheetsFromTableTool());
            handler.RegisterTool(new PreviewDuplicateViewsTool());
            handler.RegisterTool(new PreviewRenameViewsTool());
            handler.RegisterTool(new PreviewRenameSheetsTool());

#if !REVIT2024
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

            // IFC Space to Room — full set (entire folder excluded under REVIT2024)
            handler.RegisterTool(new ListIfcLinksTool());
            handler.RegisterTool(new PreviewIfcSpacesTool());
            handler.RegisterTool(new PreviewIfcSpaceGeometryTool());
            handler.RegisterTool(new ConvertIfcSpacesToRoomsTool());
            handler.RegisterTool(new ValidateIfcSpaceRoomConversionTool());
            handler.RegisterTool(new SyncIfcSpaceRoomDataTool());
#endif

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
#if !REVIT2024
            handler.RegisterTool(new RunClashPresetTool());
#endif
            // Coordination — Phase 4 — Reporting
            handler.RegisterTool(new ExportClashReportToExcelTool());
            handler.RegisterTool(new GetClashDashboardSummaryTool());
            // Coordination — Phase 5 — Navigation & Review View
            handler.RegisterTool(new GetNextClashTool());
            handler.RegisterTool(new GetPreviousClashTool());
#if !REVIT2024
            handler.RegisterTool(new CreateClashReviewViewTool());
            handler.RegisterTool(new FocusClashTool());
#endif
            handler.RegisterTool(new SelectClashElementsTool());

            // Issue Reports
            handler.RegisterTool(new ExportIssueReportJsonTool());
            handler.RegisterTool(new ExportIssueReportExcelTool());
            handler.RegisterTool(new ExportIssueReportMarkdownTool());
            handler.RegisterTool(new ExportIssueReportHtmlDashboardTool());
            handler.RegisterTool(new MergeIssueReportsTool());

#if !REVIT2024
            // Family Creation
            handler.RegisterTool(new CreatePanelSchematicSymbolFromDwgTool());
#endif

            // Skills
            handler.RegisterTool(new ListSkillsTool());
            handler.RegisterTool(new GetSkillDetailsTool());
            handler.RegisterTool(new PreviewSkillRunTool());
#if !REVIT2024
            handler.RegisterTool(new RunSkillTool());
            handler.RegisterTool(new RunSkillTaskTool());
            handler.RegisterTool(new CreateProjectSkillOverrideTool());
            handler.RegisterTool(new UpdateProjectSkillOverrideTool());
            handler.RegisterTool(new ResetProjectSkillOverrideTool());
            handler.RegisterTool(new ConfigureSheetNamingSkillTool());
#endif
            handler.RegisterTool(new CompareSkillOverrideToMasterTool());
#if !REVIT2024
            handler.RegisterTool(new ProposeSkillMasterUpdateTool());
#endif
            handler.RegisterTool(new ExportSkillOverrideDiffMarkdownTool());

            // Standards
            handler.RegisterTool(new StandardsListSourcesTool());
            handler.RegisterTool(new StandardsIndexSourcesTool());
            handler.RegisterTool(new StandardsSearchTool());
            handler.RegisterTool(new StandardsGetDocumentChunkTool());
            handler.RegisterTool(new StandardsValidateSourceConfigTool());

            // Delivery Tools
            handler.RegisterTool(new DeliveryScanFolderTool());
            handler.RegisterTool(new DeliveryCheckAgainstRevitSheetsTool());
            handler.RegisterTool(new DeliveryCheckAgainstExcelRegisterTool());
            handler.RegisterTool(new DeliveryRunFullCheckTool());

            // File System Tools
            handler.RegisterTool(new FileReadTextTool());
            handler.RegisterTool(new FileWriteTextTool());
            handler.RegisterTool(new FileListDirectoryTool());
            handler.RegisterTool(new FileInspectTool());
            handler.RegisterTool(new FileCopyTool());
            handler.RegisterTool(new FileBackupTool());

            // Excel Modifier Tools
            handler.RegisterTool(new ExcelInspectWorkbookTool());
            handler.RegisterTool(new ExcelReadRangeTool());
            handler.RegisterTool(new ExcelUpdateCellsTool());
            handler.RegisterTool(new ExcelInsertRowsTool());
            handler.RegisterTool(new ExcelAppendTableRowsTool());

            // Parameter QA Rule Set Tools
            handler.RegisterTool(new ListParameterQaRuleSetsTool());
            handler.RegisterTool(new RunParameterQaRuleSetTool());

            // Configuration / State Tools
            handler.RegisterTool(new ConfigReadTool());
            handler.RegisterTool(new ConfigWriteTool());
            handler.RegisterTool(new ConfigUpdateTool());
            handler.RegisterTool(new ConfigGetProjectConfigTool());
            handler.RegisterTool(new ConfigSetProjectConfigTool());

            var eventService = new ExternalEventService(handler);

            var approvalService = new ApprovalService();
            approvalService.SetRedispatch(eventService.Redispatch);
            handler.SetApprovalService(approvalService);
            handler.SetActivityLogger(logger);

            // Each Revit process hosts its own unique pipe so multiple running Revit
            // instances (e.g. 2024 and 2026 side by side) never contend for the same pipe.
            // The bridge discovers instances via the shared registry file written below.
            var revitVersion = application.ControlledApplication.VersionNumber;
            var processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            var pipeName = RevitMcpDefaults.BuildPipeName(revitVersion, processId);
            DiagLog($"Pipe name: {pipeName}");

            var pipeServer = new PipeServer(pipeName, eventService, logger);
            var registration = new InstanceRegistrationService(revitVersion, processId, pipeName);
            var connector = new ConnectorService(pipeServer, eventService, approvalService, registration);
            _connector = connector;
#if !REVIT2024
            _viewModel = new McpWindowViewModel(connector, logger, approvalService);
#endif

            // Auto-start the pipe server so [AppLoader] hot-reloads are transparent to agents.
            // OnShutdown calls PanicStop (stops old pipe), then OnStartup creates + starts a fresh one —
            // no manual "Start" button click required after a rebuild.
            connector.Start();
            DiagLog("ConnectorService auto-started.");

            // Startup validation: log all registered tool names so mismatches (e.g. after a
            // partial rebuild before Revit restart) are visible in the diagnostic log.
            var registeredTools = handler.GetRegisteredToolNames();
            DiagLog($"Registered tools ({registeredTools.Count}): {string.Join(", ", registeredTools)}");

#if !REVIT2024
            // Ribbon (depends on UI window — Revit 2024 build is headless)
            DiagLog("Calling AddRibbonButton");
            AddRibbonButton(application);
            DiagLog("AddRibbonButton OK");
#endif

            DiagLog("OnStartup SUCCEEDED");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
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
        application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        _connector?.PanicStop();
        return Result.Succeeded;
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        DocumentChangeTracker.MarkChanged(e.GetDocument());
    }

#if !REVIT2024
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
#endif
}
