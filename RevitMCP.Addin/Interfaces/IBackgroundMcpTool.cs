namespace RevitMCP.Addin.Interfaces;

/// <summary>
/// Marker for tools that are audited to perform no Autodesk Revit API access.
/// These tools may execute off the Revit UI thread. Implementations must not
/// read or retain the UIApplication supplied by IRevitMcpTool.ExecuteAsync.
/// </summary>
public interface IBackgroundMcpTool
{
}

/// <summary>
/// A background tool that mostly waits (a long-poll such as revit_job_status waitSeconds) and does no file or
/// network I/O, so it does not take one of the bounded background slots other background tools queue for.
/// </summary>
public interface IWaitingMcpTool : IBackgroundMcpTool
{
}
