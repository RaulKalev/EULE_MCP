using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>Shared lookups for the workset and ownership tools (#101).</summary>
internal static class WorksharingSupport
{
    public const string NotWorkshared = "not_workshared";
    public const string OwnedByOther = "owned_by_other";
    public const int MaxIds = 5000;

    /// <summary>The target document when it is workshared; otherwise a failure.</summary>
    public static Document? Workshared(UIApplication uiapp, McpToolRequest request, out McpToolResult? failure)
    {
        var target = ToolDocument.Resolve(uiapp, request, out failure);
        if (target == null) return null;
        if (!target.Document.IsWorkshared)
        {
            failure = DocumentToolSupport.Fail(request, $"'{target.Document.Title}' is not workshared: it has no worksets or element ownership.", NotWorkshared);
            return null;
        }
        return target.Document;
    }

    public static List<Workset> UserWorksets(Document doc)
        => new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A user workset by name (case-insensitive) or numeric id; null with the reason otherwise.</summary>
    public static Workset? FindWorkset(Document doc, string selector, out string? error)
    {
        error = null;
        var worksets = UserWorksets(doc);
        var trimmed = selector.Trim();
        var match = int.TryParse(trimmed, out var id)
            ? worksets.FirstOrDefault(w => w.Id.IntegerValue == id)
            : worksets.FirstOrDefault(w => string.Equals(w.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            error = $"No user workset '{trimmed}' in '{doc.Title}'. Worksets: {string.Join(", ", worksets.Select(w => w.Name))}.";
        return match;
    }

    public static object Describe(Workset w, WorksetId active, int? elementCount) => new
    {
        id = w.Id.IntegerValue,
        name = w.Name,
        owner = string.IsNullOrEmpty(w.Owner) ? null : w.Owner,
        isOpen = w.IsOpen,
        isEditable = w.IsEditable,
        isDefault = w.IsDefaultWorkset,
        isVisibleByDefault = w.IsVisibleByDefault,
        isActive = w.Id == active,
        elementCount
    };
}

/// <summary>revit_list_worksets (#101): the user worksets of a document with owner, open and editable state.</summary>
public sealed class ListWorksetsTool : IRevitMcpTool
{
    public string Name => "revit_list_worksets";
    public string Description =>
        "Lists the user worksets of a workshared document (active or not): id, name, owner, open, editable (owned by you), " +
        "default, visible by default, and which one is active. countElements=true adds the element count of each workset.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = WorksharingSupport.Workshared(uiapp, request, out var failure);
        if (doc == null) return Task.FromResult(failure!);

        var count = ToolArguments.GetBool(request.Arguments, "countElements");
        var active = doc.GetWorksetTable().GetActiveWorksetId();
        var rows = new List<object>();
        foreach (var w in WorksharingSupport.UserWorksets(doc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int? elements = null;
            if (count)
            {
                try { elements = new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(w.Id)).GetElementCount(); }
                catch { }
            }
            rows.Add(WorksharingSupport.Describe(w, active, elements));
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{rows.Count} user workset(s) in '{doc.Title}'.",
            Data = new { document = doc.Title, worksets = rows },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>Plans revit_set_workset: what an active-workset change or a move of elements to a workset would do.</summary>
internal static class SetWorksetSupport
{
    public sealed class Plan
    {
        public Document Doc = null!;
        public Workset? Active;
        public Workset? Target;
        public List<Element> Move = new();
        public List<object> Skipped = new();
        public int AlreadyThere;
    }

    public static Plan? Build(UIApplication uiapp, McpToolRequest request, out McpToolResult? failure)
    {
        var doc = WorksharingSupport.Workshared(uiapp, request, out failure);
        if (doc == null) return null;
        var plan = new Plan { Doc = doc };

        var active = ToolArguments.GetString(request.Arguments, "activeWorkset").Trim();
        if (active.Length > 0)
        {
            plan.Active = WorksharingSupport.FindWorkset(doc, active, out var error);
            if (plan.Active == null) { failure = DocumentToolSupport.Fail(request, error!, "validation_failed"); return null; }
        }

        var ids = ToolArguments.GetLongArray(request.Arguments, "elementIds").Distinct().ToList();
        var targetName = ToolArguments.GetString(request.Arguments, "workset").Trim();
        if (ids.Count > 0 || targetName.Length > 0)
        {
            if (ids.Count == 0 || targetName.Length == 0)
            {
                failure = DocumentToolSupport.Fail(request, "Moving elements needs both elementIds and workset.", "validation_failed");
                return null;
            }
            if (ids.Count > WorksharingSupport.MaxIds)
            {
                failure = DocumentToolSupport.Fail(request, $"At most {WorksharingSupport.MaxIds} elements per call; this one names {ids.Count}.", "validation_failed");
                return null;
            }
            plan.Target = WorksharingSupport.FindWorkset(doc, targetName, out var error);
            if (plan.Target == null) { failure = DocumentToolSupport.Fail(request, error!, "validation_failed"); return null; }

            foreach (var id in ids)
            {
                var element = doc.GetElement(new ElementId(id));
                if (element == null) { plan.Skipped.Add(new { id, reason = "not found" }); continue; }
                if (element.WorksetId == plan.Target.Id) { plan.AlreadyThere++; continue; }
                var problem = OwnershipReader.EditProblem(doc, element.Id, out _);
                var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (problem != null) plan.Skipped.Add(new { id, reason = problem });
                else if (parameter == null || parameter.IsReadOnly) plan.Skipped.Add(new { id, reason = "its workset cannot be changed (view-specific, a type, or a system element)" });
                else plan.Move.Add(element);
            }
        }

        if (plan.Active == null && plan.Target == null)
        {
            failure = DocumentToolSupport.Fail(request, "Pass activeWorkset, or elementIds with workset.", "validation_failed");
            return null;
        }
        return plan;
    }

    public static object Describe(Plan plan) => new
    {
        document = plan.Doc.Title,
        activeWorkset = plan.Active?.Name,
        workset = plan.Target?.Name,
        move = plan.Move.Count,
        moveIds = plan.Move.Take(200).Select(e => e.Id.Value).ToList(),
        alreadyThere = plan.AlreadyThere,
        skipped = plan.Skipped.Take(200).ToList(),
        skippedCount = plan.Skipped.Count
    };
}

/// <summary>revit_preview_set_workset (#101): what revit_set_workset would change.</summary>
public sealed class PreviewSetWorksetTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_workset";
    public string Description =>
        "Previews revit_set_workset WITHOUT changing anything: which elements would move to the workset, which are " +
        "skipped (owned by someone else, changed in central, workset not changeable) and the active workset change.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var plan = SetWorksetSupport.Build(uiapp, request, out var failure);
        if (plan == null) return Task.FromResult(failure!);
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = (plan.Active != null ? $"Active workset would become '{plan.Active.Name}'. " : string.Empty) +
                      (plan.Target != null ? $"{plan.Move.Count} element(s) would move to '{plan.Target.Name}', {plan.Skipped.Count} skipped, {plan.AlreadyThere} already there." : string.Empty),
            Data = SetWorksetSupport.Describe(plan)
        });
    }
}

/// <summary>revit_set_workset (#101): sets the active workset, or moves elements to a workset.</summary>
public sealed class SetWorksetTool : IRevitMcpTool
{
    public string Name => "revit_set_workset";
    public string Description =>
        "Sets the active workset of a workshared document (activeWorkset=<name or id>; new elements go there), and/or moves " +
        "elements to a workset (elementIds + workset). Elements owned by someone else are skipped with the owner's name. " +
        "Requires approval; preview with revit_preview_set_workset.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var plan = SetWorksetSupport.Build(uiapp, request, out var failure);
        if (plan == null) return Task.FromResult(failure!);
        var doc = plan.Doc;
        var warnings = new List<string>();

        if (plan.Active != null)
        {
            try { doc.GetWorksetTable().SetActiveWorksetId(plan.Active.Id); }
            catch (Exception ex) { return Task.FromResult(DocumentToolSupport.Fail(request, $"The active workset could not be set: {ex.Message}", "transaction_failed")); }
        }

        var moved = new List<long>();
        var skipped = new List<object>(plan.Skipped);
        if (plan.Target != null && plan.Move.Count > 0)
        {
            var (success, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Set Workset", () =>
            {
                foreach (var element in plan.Move)
                {
                    if (WorksetAssigner.TryAssign(doc, element, plan.Target.Id, out var reason)) moved.Add(element.Id.Value);
                    else skipped.Add(new { id = element.Id.Value, reason });
                }
            });
            if (!success)
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "transaction_failed",
                    Message = $"No element was moved: {diagnostics.OriginalError}",
                    Errors = diagnostics.ToErrorLines()
                });
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = (plan.Active != null ? $"Active workset is now '{plan.Active.Name}'. " : string.Empty) +
                      (plan.Target != null ? $"Moved {moved.Count} element(s) to '{plan.Target.Name}', {skipped.Count} skipped." : string.Empty),
            Data = new
            {
                document = doc.Title,
                activeWorkset = plan.Active?.Name,
                workset = plan.Target?.Name,
                movedCount = moved.Count,
                moved = moved.Take(500).ToList(),
                skipped = skipped.Take(200).ToList(),
                skippedCount = skipped.Count
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
