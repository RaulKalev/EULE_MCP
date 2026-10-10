using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>revit_preview_set_link_visibility (#90): planned V/G changes for links, no changes.</summary>
public sealed class PreviewSetLinkVisibilityTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_link_visibility";
    public string Description =>
        "Previews turning DWG/IFC/RVT links on or off in Visibility/Graphics WITHOUT changing anything. Scope: viewIds, " +
        "allViews=true, and/or viewTemplateIds (DWG only). Views whose imported-category visibility is controlled by a " +
        "view template are skipped unless applyToTemplates=true, which changes the template instead.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinkVisibilityExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_set_link_visibility (#90): turns links on/off in V/G after approval.</summary>
public sealed class SetLinkVisibilityTool : IRevitMcpTool
{
    public string Name => "revit_set_link_visibility";
    public string Description =>
        "Turns DWG/IFC/RVT links on or off in Visibility/Graphics without removing them, in a document (active or not). " +
        "DWG: the link's Imported Categories entry (per view, or the view template with applyToTemplates=true). " +
        "IFC/RVT: hides/unhides the link instances per view. Run revit_preview_set_link_visibility first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinkVisibilityExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class LinkVisibilityExecutor
{
    private const int MaxListedResults = 200;

    private sealed class PlanItem
    {
        public LinkEntry Link { get; set; } = null!;
        public View RequestedView { get; set; } = null!;
        public View? Owner { get; set; }
        public bool? VisibleBefore { get; set; }
        public string Status { get; set; } = "change";
        public string? Reason { get; set; }
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = DocumentToolSupport.ResolveTarget(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        if (!request.Arguments.ContainsKey("visible"))
            return DocumentToolSupport.Fail(request, "visible (true/false) is required.");
        var visible = ToolArguments.GetBool(request.Arguments, "visible");

        var selectors = ToolArguments.GetStringArray(request.Arguments, "links");
        if (selectors.Length == 0)
            return DocumentToolSupport.Fail(request, "links is required: link names or type ids.");

        var errors = new List<string>();
        var links = DocumentToolSupport.ResolveLinks(LinkInventory.Collect(doc), selectors, errors);
        if (errors.Count > 0)
            return DocumentToolSupport.Fail(request, "Nothing was changed. " + string.Join(" ", errors), "validation_failed");

        var applyToTemplates = ToolArguments.GetBool(request.Arguments, "applyToTemplates", false);
        var views = ResolveScope(doc, request, errors);
        if (errors.Count > 0)
            return DocumentToolSupport.Fail(request, string.Join(" ", errors), "validation_failed");
        if (views.Count == 0)
            return DocumentToolSupport.Fail(request, "Give a scope: viewIds, allViews=true or viewTemplateIds.");

        var plan = BuildPlan(doc, links, views, visible, applyToTemplates);
        var toChange = plan.Where(p => p.Status == "change").ToList();

        if (!apply || toChange.Count == 0)
        {
            sw.Stop();
            return Result(request, uiapp, doc, plan, visible, applied: false, sw,
                $"{toChange.Count} view setting(s) would change, {plan.Count(p => p.Status == "unchanged")} already " +
                $"{(visible ? "visible" : "hidden")}, {plan.Count(p => p.Status == "skipped")} skipped.", null);
        }

        var applyErrors = new List<string>();
        var transaction = RevitTransactionRunner.Run(doc, "Revit MCP - Set link visibility", () =>
        {
            foreach (var item in toChange)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ApplyItem(item, visible);
                    item.Status = "changed";
                }
                catch (Exception ex)
                {
                    item.Status = "failed";
                    item.Reason = ex.Message;
                    applyErrors.Add($"'{item.Link.Name}' in '{item.Owner!.Name}': {ex.Message}");
                }
            }
        });

        sw.Stop();
        if (!transaction.Success)
        {
            var failed = Result(request, uiapp, doc, plan, visible, applied: false, sw,
                $"The visibility change failed and was rolled back: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}",
                transaction.Diagnostics);
            failed.Success = false;
            failed.Status = "transaction_failed";
            return failed;
        }

        var result = Result(request, uiapp, doc, plan, visible, applied: true, sw,
            $"Changed {plan.Count(p => p.Status == "changed")} view setting(s) in '{doc.Title}'.", transaction.Diagnostics);
        result.Warnings.AddRange(applyErrors);
        return result;
    }

    private static List<View> ResolveScope(Document doc, McpToolRequest request, List<string> errors)
    {
        var result = new List<View>();
        foreach (var id in ToolArguments.GetLongArray(request.Arguments, "viewIds")
                     .Concat(ToolArguments.GetLongArray(request.Arguments, "viewTemplateIds")).Distinct())
        {
            if (doc.GetElement(new ElementId(id)) is View view) result.Add(view);
            else errors.Add($"View {id} was not found in '{doc.Title}'.");
        }

        if (ToolArguments.GetBool(request.Arguments, "allViews"))
        {
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && IsGraphicalView(v)));
        }

        return result.GroupBy(v => v.Id.Value).Select(g => g.First()).ToList();
    }

    private static bool IsGraphicalView(View view) => view.ViewType switch
    {
        ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan or
        ViewType.Section or ViewType.Elevation or ViewType.Detail or ViewType.ThreeD or ViewType.DraftingView => true,
        _ => false
    };

    private static List<PlanItem> BuildPlan(Document doc, List<LinkEntry> links, List<View> views, bool visible, bool applyToTemplates)
    {
        var plan = new List<PlanItem>();
        var seenOwners = new HashSet<string>();

        foreach (var link in links)
        {
            foreach (var view in views)
            {
                var item = new PlanItem { Link = link, RequestedView = view };
                plan.Add(item);

                if (link.Instances.Count == 0)
                {
                    Skip(item, "The link has no instances.");
                    continue;
                }

                if (link.Kind is LinkKind.Dwg or LinkKind.OtherCad)
                    PlanCad(doc, item, view, visible, applyToTemplates);
                else
                    PlanRevitLink(item, view, visible);

                // A template shared by several requested views is changed once.
                if (item.Owner != null && item.Status != "skipped" &&
                    !seenOwners.Add($"{link.TypeId}:{item.Owner.Id.Value}"))
                    Skip(item, $"Covered by the change to '{item.Owner.Name}'.", "duplicate");
            }
        }

        return plan;
    }

    private static void PlanCad(Document doc, PlanItem item, View view, bool visible, bool applyToTemplates)
    {
        var category = item.Link.Instances[0].Category;
        if (category == null)
        {
            Skip(item, "The DWG link has no import category.");
            return;
        }

        var owner = view;
        if (!view.IsTemplate && view.ViewTemplateId != ElementId.InvalidElementId &&
            doc.GetElement(view.ViewTemplateId) is View template &&
            ControlsParameter(template, BuiltInParameter.VIS_GRAPHICS_IMPORT))
        {
            if (!applyToTemplates)
            {
                Skip(item, $"Imported categories are controlled by view template '{template.Name}'. Pass applyToTemplates=true to change the template.");
                return;
            }
            owner = template;
        }

        item.Owner = owner;
        try
        {
            if (!owner.CanCategoryBeHidden(category.Id))
            {
                Skip(item, "The import category cannot be hidden in this view.");
                return;
            }
            item.VisibleBefore = !owner.GetCategoryHidden(category.Id);
        }
        catch (Exception ex)
        {
            Skip(item, ex.Message);
            return;
        }

        if (item.VisibleBefore == visible) item.Status = "unchanged";
    }

    private static void PlanRevitLink(PlanItem item, View view, bool visible)
    {
        if (view.IsTemplate)
        {
            Skip(item, "IFC/RVT link instances are hidden per view, not in a view template. Use viewIds or allViews.");
            return;
        }

        item.Owner = view;
        try
        {
            var instances = item.Link.Instances;
            if (!instances.Any(i => i.CanBeHidden(view)))
            {
                Skip(item, "The link cannot be hidden in this view.");
                return;
            }
            var hiddenCount = instances.Count(i => i.IsHidden(view));
            item.VisibleBefore = hiddenCount == 0;
            var alreadyThere = visible ? hiddenCount == 0 : hiddenCount == instances.Count;
            if (alreadyThere) item.Status = "unchanged";
        }
        catch (Exception ex)
        {
            Skip(item, ex.Message);
        }
    }

    private static void ApplyItem(PlanItem item, bool visible)
    {
        var owner = item.Owner!;
        if (item.Link.Kind is LinkKind.Dwg or LinkKind.OtherCad)
        {
            owner.SetCategoryHidden(item.Link.Instances[0].Category.Id, !visible);
            return;
        }

        var ids = item.Link.Instances
            .Where(i => i.CanBeHidden(owner) && i.IsHidden(owner) == visible)
            .Select(i => i.Id)
            .ToList();
        if (ids.Count == 0) return;
        if (visible) owner.UnhideElements(ids);
        else owner.HideElements(ids);
    }

    /// <summary>True when the template controls the parameter (it is not in the non-controlled list).</summary>
    private static bool ControlsParameter(View template, BuiltInParameter parameter)
    {
        try
        {
            var id = new ElementId(parameter);
            return !template.GetNonControlledTemplateParameterIds().Contains(id);
        }
        catch
        {
            return true;
        }
    }

    private static void Skip(PlanItem item, string reason, string status = "skipped")
    {
        item.Status = status;
        item.Reason = reason;
    }

    private static McpToolResult Result(
        McpToolRequest request, UIApplication uiapp, Document doc, List<PlanItem> plan, bool visible, bool applied,
        Stopwatch sw, string message, TransactionDiagnostics? diagnostics)
    {
        var rows = plan.Where(p => p.Status != "duplicate").ToList();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = message,
            Data = new
            {
                document = DocumentToolSupport.DescribeDocument(uiapp, doc),
                visible,
                applied,
                summary = rows.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
                results = rows.Take(MaxListedResults).Select(p => new
                {
                    link = p.Link.Name,
                    kind = p.Link.Kind.ToString().ToUpperInvariant(),
                    viewId = p.RequestedView.Id.Value,
                    viewName = p.RequestedView.Name,
                    ownerId = p.Owner?.Id.Value,
                    ownerName = p.Owner?.Name,
                    owner = p.Owner == null ? null : p.Owner.IsTemplate ? "ViewTemplate" : "View",
                    before = p.VisibleBefore,
                    after = p.Status is "changed" or "change" ? visible : p.VisibleBefore,
                    status = p.Status,
                    reason = p.Reason
                }).ToList(),
                truncated = rows.Count > MaxListedResults,
                transaction = diagnostics
            },
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
