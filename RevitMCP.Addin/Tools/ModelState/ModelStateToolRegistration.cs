using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.ModelState;

/// <summary>Registers the model-state tools (#109): phases, design options, phase writes and the model audit.</summary>
public static class ModelStateToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListPhasesTool());
        handler.RegisterTool(new ListDesignOptionsTool());
        handler.RegisterTool(new PreviewSetPhaseTool());
        handler.RegisterTool(new SetPhaseTool());
        handler.RegisterTool(new SetViewPhaseTool());
        handler.RegisterTool(new AuditModelTool());
    }
}
