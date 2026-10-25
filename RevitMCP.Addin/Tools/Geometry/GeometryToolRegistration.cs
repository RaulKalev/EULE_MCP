using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.Geometry;

/// <summary>
/// Registers the geometry query tools (#107). All six are read-only. Called once from <c>App.OnStartup</c>:
/// <c>GeometryToolRegistration.Register(handler);</c>
/// </summary>
public static class GeometryToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new GetElementGeometryTool());
        handler.RegisterTool(new FindElementsInBoxTool());
        handler.RegisterTool(new FindElementsNearTool());
        handler.RegisterTool(new FindNearestSurfaceTool());
        handler.RegisterTool(new CheckLineOfSightTool());
        handler.RegisterTool(new MeasureClearanceTool());
    }
}
