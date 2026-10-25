using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Registers the transform and group tools (#108): mirror, array, list/create/ungroup/place groups. App.OnStartup calls
/// <see cref="Register"/> once, next to the move and copy tools.
/// </summary>
public static class TransformsAndGroupsToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new PreviewMirrorElementsTool());
        handler.RegisterTool(new MirrorElementsTool());
        handler.RegisterTool(new PreviewArrayElementsTool());
        handler.RegisterTool(new ArrayElementsTool());
        handler.RegisterTool(new ListGroupsTool());
        handler.RegisterTool(new PreviewCreateGroupTool());
        handler.RegisterTool(new CreateGroupTool());
        handler.RegisterTool(new UngroupTool());
        handler.RegisterTool(new UngroupAndDeleteTypeTool());
        handler.RegisterTool(new PreviewPlaceGroupInstancesTool());
        handler.RegisterTool(new PlaceGroupInstancesTool());
    }
}
