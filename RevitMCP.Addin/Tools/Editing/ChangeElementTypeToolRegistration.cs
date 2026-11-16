using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.Editing;

/// <summary>Registers revit_preview_change_element_type / revit_change_element_type (#159). Called once from App.OnStartup.</summary>
public static class ChangeElementTypeToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new PreviewChangeElementTypeTool());
        handler.RegisterTool(new ChangeElementTypeTool());
    }
}
