namespace RevitMCP.Addin.Interfaces;

/// <summary>
/// Marker for tools that are audited to perform no Autodesk Revit API access.
/// These tools may execute off the Revit UI thread. Implementations must not
/// read or retain the UIApplication supplied by IRevitMcpTool.ExecuteAsync.
/// </summary>
public interface IBackgroundMcpTool
{
}
