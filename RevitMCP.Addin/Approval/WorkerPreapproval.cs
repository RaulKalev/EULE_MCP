using System.IO;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Applies <see cref="WorkerPolicy"/> to a live request: reads the user config (cached until the file changes),
/// gathers the call's facts from Revit and asks the policy. Called by the dispatcher before a call is queued for
/// approval; a covered call runs at once with the reason in its warnings and in the activity log.
/// </summary>
public static class WorkerPreapproval
{
    private static readonly object Gate = new();
    private static WorkerPolicy _cached = WorkerPolicy.Disabled;
    private static DateTime _cachedStamp = DateTime.MinValue;
    private static string? _cachedPath;

    /// <summary>The current policy from the user config; <see cref="WorkerPolicy.Disabled"/> when absent or unreadable.</summary>
    public static WorkerPolicy Current
    {
        get
        {
            try
            {
                var (path, _) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return WorkerPolicy.Disabled;
                var stamp = File.GetLastWriteTimeUtc(path);
                lock (Gate)
                {
                    if (path == _cachedPath && stamp == _cachedStamp) return _cached;
                    var (config, _) = new JsonConfigService().Read(path!, createIfMissing: false);
                    _cached = WorkerPolicy.FromConfig(config);
                    _cachedStamp = stamp;
                    _cachedPath = path;
                    return _cached;
                }
            }
            catch
            {
                return WorkerPolicy.Disabled;
            }
        }
    }

    /// <summary>
    /// True when worker mode pre-approves this call. <paramref name="reason"/> carries the approving rule, or why a
    /// worker-mode tool was not covered (null when worker mode does not apply to the tool or is off).
    /// </summary>
    public static bool Covers(UIApplication app, string toolName, McpToolRequest request, out string? reason)
    {
        reason = null;
        if (!WorkerPolicy.ToolOperations.TryGetValue(toolName, out var op)) return false;
        var policy = Current;
        if (!policy.IsActive) return false;

        try
        {
            var call = op == WorkerPolicy.OpOpen ? OpenCall(toolName, request) : DocumentCall(app, toolName, request);
            if (call == null) return false;   // the tool's own validation will report the problem
            return policy.Covers(call, out reason);
        }
        catch (Exception ex)
        {
            reason = $"Worker mode could not check the call ({ex.Message}); it waits for approval.";
            return false;
        }
    }

    private static WorkerPolicy.Call? OpenCall(string toolName, McpToolRequest request)
    {
        var open = OpenOptionsBuilder.Parse(request.Arguments);
        if (!open.IsValid) return null;
        return new WorkerPolicy.Call
        {
            ToolName = toolName,
            Path = open.Path,
            Activate = open.Activate
        };
    }

    private static WorkerPolicy.Call? DocumentCall(UIApplication app, string toolName, McpToolRequest request)
    {
        var all = ToolArguments.GetBool(request.Arguments, "all");
        if (all) return new WorkerPolicy.Call { ToolName = toolName, AllDocuments = true };

        var target = RevitDocumentResolver.Resolve(app, request, out _);
        if (target == null) return null;
        var doc = target.Document;
        bool modified;
        try { modified = doc.IsModified; } catch { modified = true; }
        var central = doc.IsWorkshared ? RevitDocumentResolver.GetCentralPath(doc) : string.Empty;
        return new WorkerPolicy.Call
        {
            ToolName = toolName,
            Path = doc.PathName,
            CentralPath = central,
            OpenedByConnector = WorkerDocumentRegistry.Contains(doc.PathName),
            Save = ToolArguments.GetBool(request.Arguments, "save"),
            IsModified = modified
        };
    }
}
