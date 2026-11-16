using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.ElementGraphics;

/// <summary>Registers the per-element view graphics tools (#160). Called once from App.OnStartup.</summary>
public static class ElementGraphicsToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new GetElementGraphicsTool());
        handler.RegisterTool(new PreviewSetElementGraphicsTool());
        handler.RegisterTool(new SetElementGraphicsTool());
    }
}
