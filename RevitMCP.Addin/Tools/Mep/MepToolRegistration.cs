using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.Mep;

/// <summary>Registers the MEP routing tools (#106). Called once from App.OnStartup.</summary>
public static class MepToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListMepCurveTypesTool());
        handler.RegisterTool(new PreviewCreateMepRunTool());
        handler.RegisterTool(new CreateMepRunTool());
        handler.RegisterTool(new ConnectMepCurvesTool());
        handler.RegisterTool(new MepRunSummaryTool());
    }
}
