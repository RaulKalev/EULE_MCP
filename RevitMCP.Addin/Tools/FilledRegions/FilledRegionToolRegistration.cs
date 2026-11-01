using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.FilledRegions;

/// <summary>
/// The filled region tools (#135). App.OnStartup calls <see cref="Register"/> once:
/// <c>RevitMCP.Addin.Tools.FilledRegions.FilledRegionToolRegistration.Register(handler);</c>
/// </summary>
public static class FilledRegionToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListFilledRegionsTool());
        handler.RegisterTool(new ListFilledRegionTypesTool());
        handler.RegisterTool(new PreviewCreateFilledRegionsTool());
        handler.RegisterTool(new CreateFilledRegionsTool());
        handler.RegisterTool(new PreviewEditFilledRegionsTool());
        handler.RegisterTool(new EditFilledRegionsTool());
        handler.RegisterTool(new PreviewSetFilledRegionTypesTool());
        handler.RegisterTool(new SetFilledRegionTypesTool());
    }
}
