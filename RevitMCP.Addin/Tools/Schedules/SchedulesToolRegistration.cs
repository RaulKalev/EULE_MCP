using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.Schedules;

/// <summary>Registers the schedule tools (#103). Called once from App.OnStartup.</summary>
public static class SchedulesToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new GetScheduleTool());
        handler.RegisterTool(new ListSchedulableFieldsTool());
        handler.RegisterTool(new PreviewCreateScheduleTool());
        handler.RegisterTool(new CreateScheduleTool());
        handler.RegisterTool(new PreviewEditScheduleTool());
        handler.RegisterTool(new EditScheduleTool());
        handler.RegisterTool(new PlaceScheduleOnSheetTool());
        handler.RegisterTool(new ExportScheduleTool());
    }
}
