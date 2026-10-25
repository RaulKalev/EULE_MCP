using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.Views;

/// <summary>
/// The view creation tools (#104). App.OnStartup calls <see cref="Register"/> once:
/// <c>RevitMCP.Addin.Tools.Views.ViewCreationToolRegistration.Register(handler);</c>
/// </summary>
public static class ViewCreationToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListViewFamilyTypesTool());
        handler.RegisterTool(new PreviewCreateViewsTool());
        handler.RegisterTool(new CreateViewsTool());
        handler.RegisterTool(new PreviewSetViewExtentTool());
        handler.RegisterTool(new SetViewExtentTool());
    }
}
