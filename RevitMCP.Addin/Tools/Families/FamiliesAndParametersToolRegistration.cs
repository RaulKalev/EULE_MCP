using RevitMCP.Addin.Services;
using RevitMCP.Addin.Tools.Parameters;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>Registers the families and parameters tools (#102). Called once from App.OnStartup.</summary>
public static class FamiliesAndParametersToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListFamiliesTool());
        handler.RegisterTool(new PreviewLoadFamilyTool());
        handler.RegisterTool(new LoadFamilyTool());
        handler.RegisterTool(new ReloadFamilyTool());
        handler.RegisterTool(new PreviewPurgeUnusedTool());
        handler.RegisterTool(new PurgeUnusedTool());
        handler.RegisterTool(new ListProjectParametersTool());
        handler.RegisterTool(new ListSharedParametersTool());
        handler.RegisterTool(new PreviewCreateParametersTool());
        handler.RegisterTool(new CreateParametersTool());
        handler.RegisterTool(new BindParameterCategoriesTool());
    }
}
