using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>revit_preview_link_dwg: which DWG would link into which view with which options, without changes.</summary>
public sealed class PreviewLinkDwgTool : IRevitMcpTool
{
    public string Name => "revit_preview_link_dwg";

    public string Description =>
        "Previews linking DWG files into views WITHOUT changing the model. Same arguments as revit_link_dwg. Per file: " +
        "the view it goes into, whether it is linked current view only (always in plan, section and drafting views; " +
        "never possible in 3D views), placement, unit (auto = Revit detects the scale), and willLink | willSkip " +
        "(already linked there) | invalid with the reason.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinkDwgExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_link_dwg: links DWG files into views, current view only, scale auto-detected.</summary>
public sealed class LinkDwgTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_link_dwg";

    public string Description =>
        "Links DWG files into views (Insert > Link CAD). Requires approval. links: [{path, viewId? | view?, …}] or paths " +
        "[...] with viewId / view (default: the active view). Linked into a plan, section or drafting view, a DWG is " +
        "always current view only (thisViewOnly=false is allowed for plans and sections); in a 3D view it is model-wide. " +
        "unit: auto (default, Revit detects the scale) | mm | cm | dm | m | in | ft. placement: origin (default, " +
        "origin to origin) | center | shared. colors: preserve (default) | blackAndWhite | invert. layers: all | " +
        "visible. correctLinesOffAxis (default true), pin (default false). ifExists: skip (default: the same file is " +
        "already linked into that view) | linkAgain. Each file is its own transaction, all are one undo step. Returns " +
        "the instance id and the linked extents in metres, with a warning when the size suggests wrong units. Run " +
        "revit_preview_link_dwg first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinkDwgExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => LinkDwgExecutor.Summary(uiapp, request);
}

internal static class LinkDwgExecutor
{
    private const double MetresPerFoot = 0.3048;

    public static string Summary(UIApplication uiapp, McpToolRequest request)
    {
        var errors = new List<string>();
        var requests = DwgLinkPlanner.Parse(request.Arguments, errors);
        var target = ToolDocument.Resolve(uiapp, request, out _);
        var plan = target == null ? null : DwgLinkPlanner.Plan(requests, BuildContext(uiapp, target), IfExists(request));
        var files = requests.Select(r => Path.GetFileName(r.Path)).Distinct().ToList();
        var views = plan?.Where(p => p.Status == "willLink" && p.View != null).Select(p => $"'{p.View!.Name}'").Distinct().ToList() ?? new List<string>();
        var first = requests.FirstOrDefault() ?? new DwgLinkRequest();
        return $"Link {requests.Count} DWG file(s) ({string.Join(", ", files.Take(5))}{(files.Count > 5 ? ", …" : string.Empty)})" +
               $"{(views.Count > 0 ? $" into {string.Join(", ", views.Take(5))}{(views.Count > 5 ? ", …" : string.Empty)}" : string.Empty)}" +
               $"{(target != null ? $" of '{target.Document.Title}'" : string.Empty)}: current view only in plans, sections and drafting views, " +
               $"unit {first.Unit}{(first.Unit == "auto" ? " (Revit detects the scale)" : string.Empty)}, placement {first.Placement}" +
               $"{(plan != null && plan.Any(p => p.Status == "willSkip") ? $"; {plan.Count(p => p.Status == "willSkip")} already linked and skipped" : string.Empty)}.";
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = DocumentToolSupport.ResolveTarget(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        if (doc.IsFamilyDocument)
            return DocumentToolSupport.Fail(request, "The target is a family document; link DWG files into a project.");

        var errors = new List<string>();
        var requests = DwgLinkPlanner.Parse(request.Arguments, errors);
        var ifExists = IfExists(request);
        if (!string.Equals(ifExists, "skip", StringComparison.OrdinalIgnoreCase) && !string.Equals(ifExists, "linkAgain", StringComparison.OrdinalIgnoreCase))
            errors.Add($"ifExists '{ifExists}' is not one of skip | linkAgain.");
        if (errors.Count > 0) return DocumentToolSupport.Fail(request, string.Join(" ", errors), "invalid_arguments");

        var plan = DwgLinkPlanner.Plan(requests, BuildContext(uiapp, target), ifExists);

        if (!apply)
        {
            sw.Stop();
            var willLink = plan.Count(p => p.Status == "willLink");
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preview: {willLink} DWG file(s) would link, {plan.Count(p => p.Status == "willSkip")} already linked, " +
                          $"{plan.Count(p => p.Status == "invalid")} cannot be linked.",
                Data = new
                {
                    documentTitle = doc.Title,
                    ifExists,
                    links = plan.Select(p => Describe(p, null)).ToList()
                },
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var openError = DocumentToolSupport.CheckNoOpenTransaction(doc);
        if (openError != null) return DocumentToolSupport.Fail(request, openError, "transaction_failed");

        var outcomes = plan.ToDictionary(p => p, p => new LinkOutcome
        {
            Status = p.Status == "willSkip" ? "skipped" : p.Status == "invalid" ? "failed" : "notAttempted",
            Reason = p.Reason
        });
        var warnings = new List<string>();
        string? groupError = null;
        var toLink = plan.Where(p => p.Status == "willLink").ToList();
        if (toLink.Count > 0)
        {
            using var transactions = ItemTransactions.Start(doc, "Revit MCP - Link DWG", out var startError);
            if (transactions == null) return DocumentToolSupport.Fail(request, startError!, "transaction_failed");
            using var dialogs = new ModalDialogGuard(uiapp);
            foreach (var item in toLink)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    outcomes[item] = new LinkOutcome { Status = "failed", Reason = "Not attempted: the request timed out." };
                    continue;
                }
                var dialogsBefore = dialogs.Dismissed.Count;
                outcomes[item] = LinkOne(doc, transactions, item);
                var said = dialogs.Dismissed.Skip(dialogsBefore).Distinct().ToList();
                if (said.Count > 0) outcomes[item].Warnings.AddRange(said.Select(d => $"Revit said: {d}"));
            }
            groupError = transactions.Finish();
            warnings.AddRange(transactions.Warnings.Distinct().Select(w => "Revit: " + w));
            if (groupError != null)
                foreach (var outcome in outcomes.Values.Where(o => o.Status == "linked"))
                {
                    outcome.Status = "failed";
                    outcome.Reason = groupError;
                }
        }

        sw.Stop();
        var linked = outcomes.Values.Count(o => o.Status == "linked");
        var skipped = outcomes.Values.Count(o => o.Status == "skipped");
        var failed = outcomes.Values.Count(o => o.Status == "failed");
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = linked + skipped > 0 && groupError == null,
            Status = linked + skipped == 0 ? "transaction_failed" : null,
            Message = $"Linked {linked}, skipped {skipped}, failed {failed} of {plan.Count} DWG file(s){(groupError != null ? $": {groupError}" : ".")}",
            Data = new
            {
                documentTitle = doc.Title,
                ifExists,
                linked,
                skipped,
                failed,
                links = plan.Select(p => Describe(p, outcomes[p])).ToList()
            },
            Warnings = warnings.Concat(outcomes.Values.SelectMany(o => o.Warnings)).ToList(),
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private sealed class LinkOutcome
    {
        public string Status { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public long? InstanceId { get; set; }
        public long? TypeId { get; set; }
        public object? ExtentsM { get; set; }
        public List<string> Warnings { get; } = new();
    }

    private static LinkOutcome LinkOne(Document doc, ItemTransactions transactions, DwgLinkPlanItem item)
    {
        var outcome = new LinkOutcome();
        var view = doc.GetElement(new ElementId(item.View!.Id)) as View;
        if (view == null) return new LinkOutcome { Status = "failed", Reason = $"View {item.View.Id} is gone." };

        var r = item.Request;
        var options = new DWGImportOptions
        {
            ThisViewOnly = item.ThisViewOnly,
            Unit = UnitOf(r.Unit),
            Placement = PlacementOf(r.Placement),
            ColorMode = r.Colors switch
            {
                "blackAndWhite" => ImportColorMode.BlackAndWhite,
                "invert" => ImportColorMode.Inverted,
                _ => ImportColorMode.Preserved
            },
            VisibleLayersOnly = r.Layers == "visible",
            AutoCorrectAlmostVHLines = r.CorrectLinesOffAxis,
            OrientToView = true
        };

        var error = transactions.Run($"Revit MCP - Link {Path.GetFileName(r.Path)}", () =>
        {
            if (!doc.Link(r.Path, options, view, out var instanceId) || instanceId == null || instanceId == ElementId.InvalidElementId)
                return "Revit did not link the file: it may be damaged, from a newer AutoCAD than Revit reads, or hold nothing Revit can import.";
            var instance = doc.GetElement(instanceId) as ImportInstance;
            outcome.InstanceId = instanceId.Value;
            outcome.TypeId = instance?.GetTypeId().Value;
            if (r.Pin && instance != null) instance.Pinned = true;
            if (instance != null)
            {
                var box = instance.get_BoundingBox(item.ThisViewOnly ? view : null) ?? instance.get_BoundingBox(null);
                if (box != null)
                {
                    var width = (box.Max.X - box.Min.X) * MetresPerFoot;
                    var height = (box.Max.Y - box.Min.Y) * MetresPerFoot;
                    outcome.ExtentsM = new { width = Math.Round(width, 3), height = Math.Round(height, 3) };
                    var check = DwgLinkPlanner.CheckExtents(width, height);
                    if (check != null) outcome.Warnings.Add($"{Path.GetFileName(r.Path)}: {check}");
                }
            }
            return null;
        });

        if (error != null) return new LinkOutcome { Status = "failed", Reason = error };
        outcome.Status = "linked";
        return outcome;
    }

    private static object Describe(DwgLinkPlanItem item, LinkOutcome? outcome) => new
    {
        path = item.Request.Path,
        view = item.View == null ? null : new { id = item.View.Id, name = item.View.Name, type = item.View.ViewType },
        status = outcome?.Status ?? item.Status,
        thisViewOnly = item.View == null ? (bool?)null : item.ThisViewOnly,
        unit = item.Request.Unit,
        placement = item.Request.Placement,
        colors = item.Request.Colors,
        layers = item.Request.Layers,
        pin = item.Request.Pin,
        instanceId = outcome?.InstanceId,
        typeId = outcome?.TypeId,
        extentsM = outcome?.ExtentsM,
        existingInstanceId = item.ExistingInstanceId,
        reason = outcome?.Reason ?? item.Reason,
        notes = item.Notes
    };

    private static DwgLinkContext BuildContext(UIApplication uiapp, RevitDocumentResolver.DocumentTarget target)
    {
        var doc = target.Document;
        var activeView = ToolDocument.UiDocumentOf(uiapp, target)?.ActiveView;
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Select(v => new DwgLinkViewInfo { Id = v.Id.Value, Name = v.Name, ViewType = v.ViewType.ToString(), Kind = KindOf(v.ViewType), IsTemplate = v.IsTemplate })
            .ToList();
        var existing = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
            .Where(i => i.IsLinked)
            .Select(i => new DwgExistingLink
            {
                InstanceId = i.Id.Value,
                OwnerViewId = i.OwnerViewId == ElementId.InvalidElementId ? 0 : i.OwnerViewId.Value,
                Path = doc.GetElement(i.GetTypeId()) is { } type ? LinkInventory.GetPath(type) : string.Empty
            })
            .Where(e => e.Path.Length > 0)
            .ToList();
        var policy = new FilePathPolicy();
        return new DwgLinkContext
        {
            ActiveViewId = activeView?.Id.Value ?? 0,
            Views = views,
            Existing = existing,
            FileExists = File.Exists,
            ValidateRead = policy.ValidateRead
        };
    }

    private static string KindOf(ViewType type) => type switch
    {
        ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan => "plan",
        ViewType.Section or ViewType.Elevation or ViewType.Detail => "section",
        ViewType.DraftingView => "drafting",
        ViewType.ThreeD => "3d",
        _ => "other"
    };

    private static ImportUnit UnitOf(string unit) => unit switch
    {
        "mm" => ImportUnit.Millimeter,
        "cm" => ImportUnit.Centimeter,
        "dm" => ImportUnit.Decimeter,
        "m" => ImportUnit.Meter,
        "in" => ImportUnit.Inch,
        "ft" => ImportUnit.Foot,
        _ => ImportUnit.Default
    };

    private static ImportPlacement PlacementOf(string placement) => placement switch
    {
        "center" => ImportPlacement.Centered,
        "shared" => ImportPlacement.Shared,
        _ => ImportPlacement.Origin
    };

    private static string IfExists(McpToolRequest request) => ToolArguments.GetString(request.Arguments, "ifExists", "skip");
}
