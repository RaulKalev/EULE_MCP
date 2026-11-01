using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.ViewFilters;

/// <summary>
/// The view filter tools (#134). App.OnStartup calls <see cref="Register"/> once:
/// <c>RevitMCP.Addin.Tools.ViewFilters.ViewFilterToolRegistration.Register(handler);</c>
/// </summary>
public static class ViewFilterToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListViewFiltersTool());
        handler.RegisterTool(new ListFilterableParametersTool());
        handler.RegisterTool(new GetViewFiltersTool());
        handler.RegisterTool(new PreviewCreateViewFiltersTool());
        handler.RegisterTool(new CreateViewFiltersTool());
        handler.RegisterTool(new PreviewEditViewFiltersTool());
        handler.RegisterTool(new EditViewFiltersTool());
        handler.RegisterTool(new PreviewDeleteViewFiltersTool());
        handler.RegisterTool(new DeleteViewFiltersTool());
        handler.RegisterTool(new PreviewApplyViewFiltersTool());
        handler.RegisterTool(new ApplyViewFiltersTool());
        handler.RegisterTool(new PreviewCopyViewFiltersTool());
        handler.RegisterTool(new CopyViewFiltersTool());
    }
}
