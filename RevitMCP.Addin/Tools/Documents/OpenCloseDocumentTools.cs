using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>revit_open_document (#101): opens a central as the agent's own local, a detached copy or a plain file.</summary>
public sealed class OpenDocumentTool : IRevitMcpTool
{
    public const string AlreadyOpen = "document_already_open";
    private const string PolicyFile = @"%ProgramData%\RKTools\MCP\Config\FileAccessPolicy.json";

    public string Name => "revit_open_document";
    public string Description =>
        "Opens a model WITHOUT activating it (activate=false, default), so the agent works in its own document while the user " +
        "keeps theirs: a workshared central becomes a new local copy named <central>_<user>_mcp.rvt in localFolder " +
        "(mode=newLocal, default for centrals), or mode=detach (detached, worksets kept) or plain. worksets: all (default), " +
        "editable, lastViewed, closeAll, or a list of names. Pass the returned title as document=… to other tools. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var open = OpenOptionsBuilder.Parse(request.Arguments);
        if (!open.IsValid) return Task.FromResult(DocumentToolSupport.Fail(request, string.Join(" ", open.Errors), "validation_failed"));

        var policy = new FilePathPolicy();
        var pathProblem = policy.ValidateRead(open.Path);
        if (pathProblem != null)
        {
            var hint = pathProblem.Contains("Network")
                ? $" Central models usually sit on a share: an administrator can set \"AllowNetworkPaths\": true in {PolicyFile}."
                : $" Allowed folders are set in {PolicyFile}.";
            return Task.FromResult(DocumentToolSupport.Fail(request, pathProblem + hint, "validation_failed"));
        }
        if (!File.Exists(open.Path)) return Task.FromResult(DocumentToolSupport.Fail(request, $"'{open.Path}' does not exist.", "validation_failed"));

        var active = RevitDocumentResolver.Resolve(uiapp, (string?)null, out _)?.Document;   // the active document
        if (active != null && DocumentToolSupport.CheckNoOpenTransaction(active) is { } busy && busy.Contains("open transaction"))
            return Task.FromResult(DocumentToolSupport.Fail(request, busy, "validation_failed"));

        BasicFileInfo info;
        try { info = BasicFileInfo.Extract(open.Path); }
        catch (Exception ex) { return Task.FromResult(DocumentToolSupport.Fail(request, $"'{open.Path}' could not be read as a Revit model: {ex.Message}", "validation_failed")); }
        if (info.IsWorkshared && info.IsLocal && !info.IsCentral && open.Mode != OpenMode.Detach && open.Mode != OpenMode.Plain)
            return Task.FromResult(DocumentToolSupport.Fail(request,
                $"'{open.Path}' is a local copy (its central is '{info.CentralPath}'). Pass the central path to get your own local, or mode=detach.", "validation_failed"));

        var mode = OpenOptionsBuilder.Effective(open.Mode, info.IsWorkshared, info.IsCentral, out var modeError);
        if (modeError != null) return Task.FromResult(DocumentToolSupport.Fail(request, modeError, "validation_failed"));

        var username = uiapp.Application.Username;
        var openPath = open.Path;
        if (mode == OpenMode.NewLocal)
        {
            var folder = Environment.ExpandEnvironmentVariables(open.LocalFolder ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "Locals"));
            if (open.LocalFolder != null && policy.ValidateWrite(folder) is { } writeProblem)
                return Task.FromResult(DocumentToolSupport.Fail(request, $"localFolder: {writeProblem}", "validation_failed"));
            Directory.CreateDirectory(folder);
            openPath = LocalFileNaming.PathIn(folder, open.Path, username, File.Exists, DateTime.Now);
        }

        var already = FindOpen(uiapp, open.Path, openPath, mode);
        if (already != null)
            return Task.FromResult(DocumentToolSupport.Fail(request,
                $"'{already.Title}' is already open for this model; pass document=\"{already.Title}\" to work in it.", AlreadyOpen,
                new { document = already.Title, path = already.PathName }));

        var source = ModelPathUtils.ConvertUserVisiblePathToModelPath(open.Path);
        var target = source;
        if (mode == OpenMode.NewLocal)
        {
            target = ModelPathUtils.ConvertUserVisiblePathToModelPath(openPath);
            try { WorksharingUtils.CreateNewLocal(source, target); }
            catch (Exception ex) { return Task.FromResult(DocumentToolSupport.Fail(request, $"The local copy could not be created: {ex.Message}", "transaction_failed")); }
        }

        var options = new OpenOptions { Audit = open.Audit };
        if (mode == OpenMode.Detach) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
        var warnings = new List<string>();
        if (info.IsWorkshared) options.SetOpenWorksetsConfiguration(Worksets(source, open, username, warnings));

        Document doc;
        var activated = false;
        try
        {
            if (open.Activate)
            {
                doc = uiapp.OpenAndActivateDocument(target, options, false).Document;
                activated = true;
            }
            else
            {
                doc = uiapp.Application.OpenDocumentFile(target, options);
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(DocumentToolSupport.Fail(request, $"'{open.Path}' could not be opened: {ex.Message}", "transaction_failed"));
        }

        // Worker mode: sync, close and the other worker operations are pre-approved only on documents the
        // connector opened itself.
        RevitMCP.Addin.Approval.WorkerDocumentRegistry.Add(doc.PathName);

        var openCount = 0;
        var closedCount = 0;
        if (doc.IsWorkshared)
        {
            foreach (var w in WorksharingSupport.UserWorksets(doc))
                if (w.IsOpen) openCount++; else closedCount++;
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Opened '{doc.Title}' ({ModeText(mode)}){(activated ? " and activated it" : " without activating it")}. " +
                      $"Pass document=\"{doc.Title}\" to work in it.",
            Data = new
            {
                title = doc.Title,
                path = doc.PathName,
                mode = mode.ToString(),
                isWorkshared = doc.IsWorkshared,
                centralPath = RevitDocumentResolver.GetCentralPath(doc),
                worksets = new { open = openCount, closed = closedCount },
                activated,
                document = doc.Title
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static WorksetConfiguration Worksets(ModelPath source, OpenRequest open, string username, List<string> warnings)
    {
        switch (open.Worksets)
        {
            case WorksetChoice.All: return new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
            case WorksetChoice.LastViewed: return new WorksetConfiguration(WorksetConfigurationOption.OpenLastViewed);
            case WorksetChoice.CloseAll: return new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
        }

        var available = new List<(int Id, string Name, string Owner)>();
        var previews = new Dictionary<int, WorksetId>();
        try
        {
            foreach (var preview in WorksharingUtils.GetUserWorksetInfo(source))
            {
                available.Add((preview.Id.IntegerValue, preview.Name, preview.Owner ?? string.Empty));
                previews[preview.Id.IntegerValue] = preview.Id;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"The model's worksets could not be read ({ex.Message}); all worksets are opened.");
            return new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
        }

        var ids = OpenOptionsBuilder.WorksetsToOpen(open, available, username, out var unknown) ?? new List<int>();
        foreach (var name in unknown) warnings.Add($"No workset '{name}' in the model.");
        var config = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
        if (ids.Count > 0) config.Open(ids.Select(id => previews[id]).ToList());
        return config;
    }

    /// <summary>The same file, or the agent's own local of the same central, already open.</summary>
    private static Document? FindOpen(UIApplication uiapp, string path, string openPath, OpenMode mode)
    {
        var wanted = new[] { DocumentTargetMatcher.NormalizePath(path), DocumentTargetMatcher.NormalizePath(openPath) };
        var central = DocumentTargetMatcher.NormalizePath(path);
        foreach (var doc in RevitDocumentResolver.GetProjectDocuments(uiapp))
        {
            var docPath = string.IsNullOrEmpty(doc.PathName) ? string.Empty : DocumentTargetMatcher.NormalizePath(doc.PathName);
            if (docPath.Length > 0 && wanted.Contains(docPath)) return doc;
            if (mode == OpenMode.NewLocal &&
                doc.Title.EndsWith(LocalFileNaming.Suffix, StringComparison.OrdinalIgnoreCase) &&
                DocumentTargetMatcher.NormalizePath(RevitDocumentResolver.GetCentralPath(doc)) == central)
                return doc;
        }
        return null;
    }

    private static string ModeText(OpenMode mode) => mode switch
    {
        OpenMode.NewLocal => "a new local copy",
        OpenMode.Detach => "detached from central",
        _ => "as a plain file"
    };
}

/// <summary>revit_close_document (#101): closes a non-active document, saving it first or not.</summary>
public sealed class CloseDocumentTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_close_document";
    public string Description =>
        "Closes an open document that is NOT the active one (document is required), with save=true saving it first (a " +
        "workshared local is saved locally; sync with revit_sync_with_central before). Unsaved changes are lost with " +
        "save=false. Ownership is not relinquished automatically — use revit_relinquish first. Needs manual approval unless " +
        "worker mode pre-approves it (docs/worker-mode.md).";
    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Worksharing;

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var doc = target.Document;
        var save = ToolArguments.GetBool(request.Arguments, "save");
        bool modified;
        try { modified = doc.IsModified; } catch { modified = true; }
        return $"DESTRUCTIVE: Close '{doc.Title}'" + (save ? " after saving it" : string.Empty) + ". " +
               (modified && !save ? "It has UNSAVED changes, which will be lost. " : modified ? "Its changes are saved first. " : "It has no unsaved changes. ") +
               "Borrowed elements stay borrowed (relinquish is not automatic).";
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(ToolArguments.GetString(request.Arguments, RevitDocumentResolver.ArgumentName)))
            return Task.FromResult(DocumentToolSupport.Fail(request, "document is required: the title or path of the document to close.", "validation_failed"));
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        if (target.IsActive)
            return Task.FromResult(DocumentToolSupport.Fail(request,
                $"'{doc.Title}' is the active document; the connector cannot close it. Activate another document first.", "validation_failed"));
        var blocked = DocumentToolSupport.CheckNoOpenTransaction(doc);
        if (blocked != null && blocked.Contains("open transaction"))
            return Task.FromResult(DocumentToolSupport.Fail(request, blocked, "validation_failed"));

        var title = doc.Title;
        var closedPath = doc.PathName;
        var save = ToolArguments.GetBool(request.Arguments, "save");
        bool modified;
        try { modified = doc.IsModified; } catch { modified = false; }
        try
        {
            if (save && modified) doc.Save();
            if (!doc.Close(false))
                return Task.FromResult(DocumentToolSupport.Fail(request, $"Revit did not close '{title}'.", "transaction_failed"));
            RevitMCP.Addin.Approval.WorkerDocumentRegistry.Remove(closedPath);
        }
        catch (Exception ex)
        {
            return Task.FromResult(DocumentToolSupport.Fail(request, $"'{title}' could not be closed: {ex.Message}", "transaction_failed"));
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Closed '{title}'" + (save && modified ? " after saving it." : modified ? "; its unsaved changes were discarded." : "."),
            Data = new { document = title, saved = save && modified, discardedChanges = !save && modified },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
