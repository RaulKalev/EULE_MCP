using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>revit_get_ownership (#101): who owns elements and worksets, and whether central has newer versions.</summary>
public sealed class GetOwnershipTool : IRevitMcpTool
{
    public string Name => "revit_get_ownership";
    public string Description =>
        "Checkout status of elements (elementIds) and worksets (worksets: names) in a workshared document: owned by you, " +
        "by someone else (who), or nobody; creator and last changed by; and whether central has a newer version or deleted it.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = WorksharingSupport.Workshared(uiapp, request, out var failure);
        if (doc == null) return Task.FromResult(failure!);

        var ids = ToolArguments.GetLongArray(request.Arguments, "elementIds").Distinct().ToList();
        var worksetNames = ToolArguments.GetStringArray(request.Arguments, "worksets").Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (ids.Count == 0 && worksetNames.Count == 0)
            return Task.FromResult(DocumentToolSupport.Fail(request, "Pass elementIds and/or worksets.", "validation_failed"));
        if (ids.Count > WorksharingSupport.MaxIds)
            return Task.FromResult(DocumentToolSupport.Fail(request, $"At most {WorksharingSupport.MaxIds} elements per call; this one names {ids.Count}.", "validation_failed"));

        var elements = new List<OwnershipInfo>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            elements.Add(OwnershipReader.Read(doc, new ElementId(id), withTooltip: true));
        }

        var worksets = new List<object>();
        var errors = new List<string>();
        foreach (var name in worksetNames)
        {
            var w = WorksharingSupport.FindWorkset(doc, name, out var error);
            if (w == null) { errors.Add(error!); continue; }
            worksets.Add(new { id = w.Id.IntegerValue, name = w.Name, owner = string.IsNullOrEmpty(w.Owner) ? null : w.Owner, isEditable = w.IsEditable, isOpen = w.IsOpen });
        }

        var others = elements.Where(e => e.OwnedByOther).ToList();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{elements.Count(e => e.Checkout == "mine")} element(s) owned by you, {others.Count} by others, " +
                      $"{elements.Count(e => e.Checkout == "none")} by nobody; {elements.Count(e => e.UpdatedInCentral)} newer or deleted in central.",
            Data = new
            {
                document = doc.Title,
                elements = elements.Select(e => new
                {
                    id = e.Id,
                    exists = e.Exists,
                    checkout = e.Checkout,
                    owner = e.Owner,
                    creator = e.Creator,
                    lastChangedBy = e.LastChangedBy,
                    central = e.Central
                }).ToList(),
                ownedByOthers = others.GroupBy(e => e.Owner ?? "?").Select(g => new { owner = g.Key, count = g.Count(), ids = g.Select(e => e.Id).Take(100).ToList() }).ToList(),
                worksets
            },
            Warnings = errors,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_checkout_elements (#101): borrows elements so this local can edit them.</summary>
public sealed class CheckoutElementsTool : IRevitMcpTool
{
    public string Name => "revit_checkout_elements";
    public string Description =>
        "Checks out (borrows) elements in a workshared document so this local can edit them without 'owned by another user' " +
        "failures. Reports what was obtained, what you already owned, and what is owned by others (and by whom). Talks to " +
        "central. Requires approval; revit_get_ownership is the preview.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = WorksharingSupport.Workshared(uiapp, request, out var failure);
        if (doc == null) return Task.FromResult(failure!);
        var blocked = DocumentToolSupport.CheckNoOpenTransaction(doc);
        if (blocked != null) return Task.FromResult(DocumentToolSupport.Fail(request, blocked, "validation_failed"));

        var ids = ToolArguments.GetLongArray(request.Arguments, "elementIds").Distinct().ToList();
        if (ids.Count == 0) return Task.FromResult(DocumentToolSupport.Fail(request, "elementIds is required.", "validation_failed"));
        if (ids.Count > WorksharingSupport.MaxIds)
            return Task.FromResult(DocumentToolSupport.Fail(request, $"At most {WorksharingSupport.MaxIds} elements per call; this one names {ids.Count}.", "validation_failed"));

        var alreadyMine = new List<long>();
        var ownedByOthers = new List<object>();
        var missing = new List<long>();
        var candidates = new List<ElementId>();
        foreach (var id in ids)
        {
            var info = OwnershipReader.Read(doc, new ElementId(id));
            if (!info.Exists) missing.Add(id);
            else if (info.Checkout == "mine") alreadyMine.Add(id);
            else if (info.OwnedByOther) ownedByOthers.Add(new { id, owner = info.Owner });
            else candidates.Add(new ElementId(id));
        }

        var obtained = new HashSet<long>();
        string? error = null;
        if (candidates.Count > 0)
        {
            try
            {
                foreach (var id in WorksharingUtils.CheckoutElements(doc, new HashSet<ElementId>(candidates), new TransactWithCentralOptions())) obtained.Add(id.Value);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }
        var failed = candidates.Select(c => c.Value).Where(id => !obtained.Contains(id)).ToList();

        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = error == null,
            Status = error != null ? "transaction_failed" : ownedByOthers.Count > 0 ? WorksharingSupport.OwnedByOther : null,
            Message = error != null
                ? $"Checkout failed: {error}"
                : $"Checked out {obtained.Count} element(s); {alreadyMine.Count} were yours already, {ownedByOthers.Count} are owned by others, {failed.Count} could not be obtained.",
            Data = new
            {
                document = doc.Title,
                checkedOut = obtained.Take(500).ToList(),
                checkedOutCount = obtained.Count,
                alreadyMine = alreadyMine.Take(500).ToList(),
                ownedByOthers = ownedByOthers.Take(200).ToList(),
                failed = failed.Take(200).ToList(),
                missing
            },
            DurationMs = sw.ElapsedMilliseconds
        };
        return Task.FromResult(result);
    }
}

/// <summary>revit_relinquish (#101): gives borrowed elements and worksets back to central.</summary>
public sealed class RelinquishTool : IRevitMcpTool
{
    public string Name => "revit_relinquish";
    public string Description =>
        "Relinquishes ownership in a workshared document without synchronizing: what=[elements (every borrowed element), " +
        "userWorksets, standardWorksets, familyWorksets, viewWorksets] or all (default). Unsaved edits to relinquished " +
        "elements are not lost but can no longer be synced by you. Talks to central. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = WorksharingSupport.Workshared(uiapp, request, out var failure);
        if (doc == null) return Task.FromResult(failure!);
        var blocked = DocumentToolSupport.CheckNoOpenTransaction(doc);
        if (blocked != null) return Task.FromResult(DocumentToolSupport.Fail(request, blocked, "validation_failed"));

        var what = ToolArguments.GetStringArray(request.Arguments, "what").Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0).ToList();
        RelinquishOptions options;
        if (what.Count == 0 || what.Contains("all"))
        {
            options = new RelinquishOptions(true);
        }
        else
        {
            options = new RelinquishOptions(false);
            foreach (var w in what)
            {
                switch (w)
                {
                    case "elements": options.CheckedOutElements = true; break;
                    case "userworksets": options.UserWorksets = true; break;
                    case "standardworksets": options.StandardWorksets = true; break;
                    case "familyworksets": options.FamilyWorksets = true; break;
                    case "viewworksets": options.ViewWorksets = true; break;
                    default:
                        return Task.FromResult(DocumentToolSupport.Fail(request,
                            $"Unknown what value '{w}'. Use elements, userWorksets, standardWorksets, familyWorksets, viewWorksets or all.", "validation_failed"));
                }
            }
        }

        try
        {
            var items = WorksharingUtils.RelinquishOwnership(doc, options, new TransactWithCentralOptions());
            var elements = items?.GetRelinquishedElements()?.Count ?? 0;
            var worksets = items?.GetRelinquishedWorksets()?.Count ?? 0;
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Relinquished {elements} element(s) and {worksets} workset(s) in '{doc.Title}'.",
                Data = new { document = doc.Title, elements, worksets, what = what.Count == 0 ? new List<string> { "all" } : what },
                DurationMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(DocumentToolSupport.Fail(request, $"Relinquish failed: {ex.Message}", "transaction_failed"));
        }
    }
}

/// <summary>revit_reload_latest (#101): pulls central's changes into this local without syncing.</summary>
public sealed class ReloadLatestTool : IRevitMcpTool
{
    public string Name => "revit_reload_latest";
    public string Description =>
        "Reloads the latest changes from central into a workshared local (Revit's Reload Latest) without synchronizing your " +
        "own changes. Talks to central; refresh the model graph afterwards with revit_graph_build. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = WorksharingSupport.Workshared(uiapp, request, out var failure);
        if (doc == null) return Task.FromResult(failure!);
        var blocked = DocumentToolSupport.CheckNoOpenTransaction(doc);
        if (blocked != null) return Task.FromResult(DocumentToolSupport.Fail(request, blocked, "validation_failed"));

        var before = DocumentChangeTracker.Capture(doc);
        try
        {
            doc.ReloadLatest(new ReloadLatestOptions());
        }
        catch (Exception ex)
        {
            return Task.FromResult(DocumentToolSupport.Fail(request, $"Reload latest failed: {ex.Message}", "transaction_failed"));
        }
        var changed = DocumentChangeTracker.Capture(doc) != before;
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = changed
                ? $"Reloaded the latest changes from central into '{doc.Title}'. Run revit_graph_build if you use the model graph."
                : $"'{doc.Title}' was already current with central.",
            Data = new { document = doc.Title, modified = changed },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
