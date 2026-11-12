using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Styles;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.LineStyles;

/// <summary>revit_get_style_usage: which elements use given line styles and filled region types, by view.</summary>
public sealed class GetStyleUsageTool : IRevitMcpTool
{
    public string Name => "revit_get_style_usage";

    public string Description =>
        "Counts the elements that use line styles and/or filled region types (names or ids): detail, model and symbolic lines " +
        "and other curves by line style, filled region boundary edges, and filled and masking regions by type. Per style: count, " +
        "byUse, byView with element ids (maxIds in all, default 50). notCounted says what the count cannot see (loaded " +
        "families, view templates and filters, Linework overrides).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var errors = new List<string>();
        var lineRefs = StyleRequestParser.References(request.Arguments, "lineStyles", errors);
        var regionRefs = StyleRequestParser.References(request.Arguments, "filledRegionTypes", errors);
        if (lineRefs.Count == 0 && regionRefs.Count == 0 && errors.Count == 0)
            errors.Add("Give lineStyles and/or filledRegionTypes: style names or ids.");
        if (errors.Count > 0) return Task.FromResult(LineStyleExecutor.Fail(request, string.Join(" ", errors)));
        var idCap = Math.Min(StyleUsagePlanner.MaxIdCap, Math.Max(0, ToolArguments.GetInt(request.Arguments, "maxIds", StyleUsagePlanner.DefaultIdCap)));

        var lineStyles = StyleUsageCollector.LineStyles(doc);
        var regionTypes = StyleUsageCollector.RegionTypes(doc);
        var warnings = new List<string>();
        var styles = new List<StyleInfo>();
        foreach (var (references, available, what) in new[] { (lineRefs, lineStyles, "line style"), (regionRefs, regionTypes, "filled region type") })
            foreach (var reference in references)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var style = StyleUsagePlanner.Find(reference, available);
                if (style == null) warnings.Add($"No {what} '{reference}'.{StyleUsagePlanner.DidYouMean(reference, available.Select(s => s.Name))}");
                else if (!styles.Contains(style)) styles.Add(style);
            }

        var uses = StyleUsageCollector.Uses(doc, styles.Any(s => s.Kind == StyleKinds.LineStyle), styles.Any(s => s.Kind == StyleKinds.FilledRegionType), cancellationToken);
        var remaining = idCap;
        var usages = new List<StyleUsage>();
        foreach (var style in styles)
        {
            var usage = StyleUsagePlanner.Summarise(style, uses.Uses, remaining);
            remaining -= usage.ByView.Sum(v => v.ElementIds.Count);
            usages.Add(usage);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = styles.Count > 0,
            Status = styles.Count > 0 ? null : StyleDeleteCodes.NotFound,
            Message = styles.Count == 0
                ? "None of the styles was found."
                : string.Join("; ", usages.Select(u => $"'{u.Style.Name}' {u.Count} use(s)")) + $" in '{doc.Title}'.",
            Data = new
            {
                document = doc.Title,
                styles = usages.Select(u => new
                {
                    kind = u.Style.Kind,
                    id = u.Style.Id,
                    name = u.Style.Name,
                    builtIn = u.Style.BuiltIn ? true : (bool?)null,
                    count = u.Count,
                    byUse = u.ByUse,
                    byView = u.ByView.Select(v => new { viewId = v.ViewId, viewName = v.ViewName, count = v.Count, elementIds = v.ElementIds }).ToList(),
                    idsTruncated = u.IdsTruncated ? true : (bool?)null
                }).ToList(),
                skippedCurves = uses.Unreadable > 0 ? uses.Unreadable : (int?)null,
                notCounted = StyleUsagePlanner.NotCounted
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_preview_delete_styles: what revit_delete_styles would reassign and delete, and what it refuses.</summary>
public sealed class PreviewDeleteStylesTool : IRevitMcpTool
{
    public string Name => "revit_preview_delete_styles";

    public string Description =>
        "Previews revit_delete_styles without changing the model. Same arguments. Per style: status (delete | refused | " +
        "not_found), code (style_in_use, builtin_style, last_filled_region_type, …), uses and reassignTo; ready is false " +
        "when anything is refused (then nothing is deleted).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DeleteStylesExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_delete_styles. DESTRUCTIVE: always asks for manual approval.</summary>
public sealed class DeleteStylesTool : IRevitMcpTool, IApprovalSummaryTool, IApprovalRevalidatesTargets
{
    public string Name => "revit_delete_styles";

    public string Description =>
        "DESTRUCTIVE: permanently deletes line styles and filled region types. Always requires manual approval, which Direct " +
        "Edit does not bypass. lineStyles, filledRegionTypes: names or ids; replaceWith {lineStyle?, filledRegionType?} moves " +
        "the elements that use them first. A style still in use without a replacement, Revit's own line styles and the last " +
        "filled region type are refused, and then nothing is deleted. One transaction, one undo. Run " +
        "revit_preview_delete_styles first and show its list.";

    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DeleteStylesExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => DeleteStylesExecutor.Summary(uiapp, request);
}

/// <summary>Collects line styles, filled region types and the elements that use them, as plain data for StyleUsagePlanner.</summary>
internal static class StyleUsageCollector
{
    internal sealed class Collected
    {
        public List<StyleUse> Uses { get; } = new();
        /// <summary>Curves whose line style could not be read.</summary>
        public int Unreadable { get; set; }
    }

    public static List<StyleInfo> LineStyles(Document doc)
    {
        var lines = LineStyleExecutor.LinesCategory(doc);
        if (lines == null) return new List<StyleInfo>();
        return lines.SubCategories.Cast<Category>()
            .Select(c => new StyleInfo { Kind = StyleKinds.LineStyle, Id = c.Id.Value, Name = c.Name, BuiltIn = c.Id.Value < 0 })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<StyleInfo> RegionTypes(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
            .Select(t => new StyleInfo { Kind = StyleKinds.FilledRegionType, Id = t.Id.Value, Name = t.Name })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Every curve element by its line style (the GraphicsStyle's subcategory), a filled region's sketch lines as
    /// boundary edges of that region, and every filled region by its type.
    /// </summary>
    public static Collected Uses(Document doc, bool lineStyles, bool regionTypes, CancellationToken cancellationToken)
    {
        var result = new Collected();
        var viewNames = new Dictionary<long, string?>();
        string? ViewName(long id)
        {
            if (!viewNames.TryGetValue(id, out var name))
                viewNames[id] = name = doc.GetElement(new ElementId(id))?.Name;
            return name;
        }
        long? ViewOf(Element element) => element.OwnerViewId == ElementId.InvalidElementId ? null : element.OwnerViewId.Value;

        var regions = new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>().ToList();
        var boundaryOf = new Dictionary<long, FilledRegion>();
        if (lineStyles)
        {
            var curveFilter = new ElementClassFilter(typeof(CurveElement));
            foreach (var region in regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var id in region.GetDependentElements(curveFilter)) boundaryOf[id.Value] = region;
                }
                catch (Exception)
                {
                    // A region whose dependents cannot be read: its edges are counted as plain curves.
                }
            }

            foreach (var curve in new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).WhereElementIsNotElementType().Cast<CurveElement>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                long styleId;
                try
                {
                    if (curve.LineStyle is not GraphicsStyle style || style.GraphicsStyleCategory == null) continue;
                    styleId = style.GraphicsStyleCategory.Id.Value;
                }
                catch (Exception)
                {
                    result.Unreadable++;
                    continue;
                }
                var isBoundary = boundaryOf.TryGetValue(curve.Id.Value, out var owner);
                var view = isBoundary ? ViewOf(owner!) : ViewOf(curve);
                result.Uses.Add(new StyleUse
                {
                    ElementId = curve.Id.Value,
                    StyleId = styleId,
                    StyleKind = StyleKinds.LineStyle,
                    Use = isBoundary ? StyleUseKinds.RegionBoundary : curve.CurveElementType.ToString(),
                    ViewId = view,
                    ViewName = view == null ? null : ViewName(view.Value),
                    RegionId = isBoundary ? owner!.Id.Value : null
                });
            }
        }

        if (regionTypes)
        {
            foreach (var region in regions)
            {
                var view = ViewOf(region);
                result.Uses.Add(new StyleUse
                {
                    ElementId = region.Id.Value,
                    StyleId = region.GetTypeId().Value,
                    StyleKind = StyleKinds.FilledRegionType,
                    Use = region.IsMasking ? StyleUseKinds.MaskingRegion : StyleUseKinds.FilledRegion,
                    ViewId = view,
                    ViewName = view == null ? null : ViewName(view.Value)
                });
            }
        }
        return result;
    }
}

internal static class DeleteStylesExecutor
{
    public static string? Summary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var doc = target.Document;
        var errors = new List<string>();
        var parsed = StyleRequestParser.ParseDelete(request.Arguments, errors);
        var plan = StyleUsagePlanner.PlanDelete(parsed, StyleUsageCollector.LineStyles(doc), StyleUsageCollector.RegionTypes(doc),
            StyleUsageCollector.Uses(doc, parsed.LineStyles.Count > 0, parsed.FilledRegionTypes.Count > 0, CancellationToken.None).Uses);
        var refused = plan.Problems.Count > 0 || errors.Count > 0 ? " The request is refused as it stands: nothing would be deleted." : string.Empty;
        return $"DESTRUCTIVE: Delete {StyleUsagePlanner.Describe(plan)} in '{doc.Title}'. {plan.Reassign.Count} element(s) are moved to a replacement first.{refused} Manual approval required.";
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var errors = new List<string>();
        var parsed = StyleRequestParser.ParseDelete(request.Arguments, errors);
        if (errors.Count > 0)
            return new McpToolResult { RequestId = request.RequestId, Success = false, Status = "invalid_arguments", Message = string.Join(" ", errors), Errors = errors };

        var lineStyles = StyleUsageCollector.LineStyles(doc);
        var regionTypes = StyleUsageCollector.RegionTypes(doc);
        var collected = StyleUsageCollector.Uses(doc, parsed.LineStyles.Count > 0, parsed.FilledRegionTypes.Count > 0, cancellationToken);
        var plan = StyleUsagePlanner.PlanDelete(parsed, lineStyles, regionTypes, collected.Uses);
        var warnings = new List<string>(plan.Warnings);
        if (collected.Unreadable > 0)
            warnings.Add($"The line style of {collected.Unreadable} curve(s) could not be read; they are not counted.");

        // Revit-side checks: the replacement line style must be one a curve or region boundary can take.
        GraphicsStyle? lineReplacementStyle = null;
        if (plan.Ready && plan.LineReplacement != null && plan.Reassign.Any(u => u.StyleKind == StyleKinds.LineStyle))
        {
            var replacementCategory = LineStyleExecutor.LinesCategory(doc)?.SubCategories.Cast<Category>().FirstOrDefault(c => c.Id.Value == plan.LineReplacement.Id);
            lineReplacementStyle = replacementCategory?.GetGraphicsStyle(GraphicsStyleType.Projection);
            if (lineReplacementStyle == null)
                plan.Problems.Add($"{StyleDeleteCodes.ReplacementNotFound}: the line style '{plan.LineReplacement.Name}' has no graphics style to assign.");
            else
            {
                var refusing = new List<long>();
                foreach (var use in plan.Reassign.Where(u => u.StyleKind == StyleKinds.LineStyle && u.Use != StyleUseKinds.RegionBoundary))
                {
                    if (doc.GetElement(new ElementId(use.ElementId)) is CurveElement curve && !curve.GetLineStyleIds().Contains(lineReplacementStyle.Id))
                        refusing.Add(use.ElementId);
                }
                if (refusing.Count > 0)
                    plan.Problems.Add($"replacement_not_valid: {refusing.Count} curve(s) cannot take the line style '{plan.LineReplacement.Name}' " +
                                      $"(e.g. {string.Join(", ", refusing.Take(10))}); pick another replaceWith.lineStyle.");
                if (plan.Reassign.Any(u => u.Use == StyleUseKinds.RegionBoundary) &&
                    !FilledRegion.IsValidLineStyleIdForFilledRegion(doc, lineReplacementStyle.Id) &&
                    !FilledRegion.IsValidLineStyleIdForFilledRegion(doc, new ElementId(plan.LineReplacement.Id)))
                    plan.Problems.Add($"replacement_not_valid: '{plan.LineReplacement.Name}' is not a line style a filled region boundary can take.");
            }
        }

        object Item(StyleDeleteItem item) => new
        {
            kind = item.Kind,
            reference = item.Reference,
            id = item.Style?.Id,
            name = item.Style?.Name,
            status = item.Status,
            code = item.Code,
            reason = item.Reason,
            uses = item.Uses,
            reassignTo = item.ReassignTo?.Name
        };

        var reassignByUse = plan.Reassign.GroupBy(u => u.Use).ToDictionary(g => g.Key, g => g.Count());
        if (!apply)
        {
            sw.Stop();
            var ready = plan.Ready;
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Status = ready ? null : plan.Items.Select(i => i.Code).FirstOrDefault(c => c != null),
                Message = ready
                    ? $"Preview: {plan.Items.Count} style(s) to delete, {plan.Reassign.Count} element(s) to move to a replacement first, in '{doc.Title}'."
                    : $"Preview: {plan.Problems.Count} problem(s); nothing would be deleted.",
                Data = new
                {
                    ready,
                    problems = plan.Problems,
                    document = doc.Title,
                    replaceWith = new { lineStyle = plan.LineReplacement?.Name, filledRegionType = plan.RegionReplacement?.Name },
                    reassign = reassignByUse,
                    styles = plan.Items.Select(Item).ToList(),
                    notCounted = StyleUsagePlanner.NotCounted
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (!plan.Ready)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = plan.Items.Select(i => i.Code).FirstOrDefault(c => c != null) ?? "invalid_arguments",
                Message = $"Nothing was deleted: {plan.Problems.Count} problem(s). {string.Join(" ", plan.Problems.Take(3))}",
                Errors = plan.Problems,
                Warnings = warnings,
                Data = new { styles = plan.Items.Select(Item).ToList() },
                DurationMs = sw.ElapsedMilliseconds
            };

        cancellationToken.ThrowIfCancellationRequested();
        // Deleting a style must never take a line or a region with it: any curve or region in what Revit deletes rolls it all back.
        var users = new HashSet<long>(collected.Uses.Select(u => u.ElementId));
        users.UnionWith(new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).WhereElementIsNotElementType().ToElementIds().Select(id => id.Value));
        users.UnionWith(new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).ToElementIds().Select(id => id.Value));
        var deleted = new List<long>();
        var regionLineStyleSet = new List<long>();
        try
        {
            using var tx = new Transaction(doc, "Revit MCP - Delete styles");
            tx.Start();
            try
            {
                Reassign(doc, plan, collected.Uses, lineReplacementStyle, regionLineStyleSet);
                foreach (var item in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var removed = doc.Delete(new ElementId(item.Style!.Id));
                    var lost = removed.Select(id => id.Value).Where(users.Contains).ToList();
                    if (lost.Count > 0)
                        throw new InvalidOperationException(
                            $"deleting '{item.Style.Name}' would also delete {lost.Count} line(s) or region(s) (e.g. {string.Join(", ", lost.Take(10))}); revit_get_style_usage may not see how they use it");
                    deleted.AddRange(removed.Select(id => id.Value));
                }
            }
            catch (OperationCanceledException)
            {
                tx.RollBack();
                throw;
            }
            catch (Exception ex)
            {
                tx.RollBack();
                return LineStyleExecutor.Fail(request, $"Nothing was deleted: Revit refused the change ({ex.Message}).", warnings);
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LineStyleExecutor.Fail(request, $"Deleting the styles failed and nothing was deleted: {ex.Message}", warnings);
        }

        if (regionLineStyleSet.Count > 0)
            warnings.Add($"{regionLineStyleSet.Count} filled region(s) took the replacement line style on every boundary edge, because Revit did not let one edge change alone.");

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Deleted {plan.Items.Count} style(s) and moved {plan.Reassign.Count} element(s) to a replacement in '{doc.Title}'. One Undo reverts it.",
            Data = new
            {
                document = doc.Title,
                deleted = plan.Items.Select(i => new { kind = i.Kind, id = i.Style!.Id, name = i.Style.Name, uses = i.Uses, reassignTo = i.ReassignTo?.Name }).ToList(),
                reassigned = reassignByUse,
                regionsWithWholeBoundaryRestyled = regionLineStyleSet.Count > 0 ? regionLineStyleSet : null,
                deletedElementCount = deleted.Distinct().Count(),
                notCounted = StyleUsagePlanner.NotCounted
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Moves the users of the styles being deleted to the replacements: a curve's LineStyle, a region boundary edge's
    /// LineStyle (or, when Revit refuses one edge and every edge of that region moves, FilledRegion.SetLineStyleId), and a
    /// region's type. Throws on anything Revit refuses; the caller rolls back.
    /// </summary>
    private static void Reassign(Document doc, StyleDeletePlan plan, List<StyleUse> allUses, GraphicsStyle? lineStyle, List<long> regionLineStyleSet)
    {
        var moving = new HashSet<long>(plan.Reassign.Where(u => u.StyleKind == StyleKinds.LineStyle).Select(u => u.ElementId));
        var regionsDone = new HashSet<long>();
        foreach (var use in plan.Reassign.Where(u => u.StyleKind == StyleKinds.LineStyle))
        {
            if (use.RegionId is { } done && regionsDone.Contains(done)) continue;
            if (doc.GetElement(new ElementId(use.ElementId)) is not CurveElement curve)
                throw new InvalidOperationException($"curve {use.ElementId} is gone");
            try
            {
                curve.LineStyle = lineStyle!;
            }
            catch (Exception ex) when (use.RegionId != null)
            {
                var regionId = use.RegionId.Value;
                var edges = allUses.Where(u => u.RegionId == regionId).ToList();
                if (edges.Any(e => !moving.Contains(e.ElementId)) || doc.GetElement(new ElementId(regionId)) is not FilledRegion region)
                    throw new InvalidOperationException($"Revit did not let boundary edge {use.ElementId} of filled region {regionId} change its line style ({ex.Message})");
                region.SetLineStyleId(FilledRegion.IsValidLineStyleIdForFilledRegion(doc, lineStyle!.Id) ? lineStyle.Id : new ElementId(plan.LineReplacement!.Id));
                regionsDone.Add(regionId);
                regionLineStyleSet.Add(regionId);
            }
        }

        if (plan.RegionReplacement != null)
        {
            var typeId = new ElementId(plan.RegionReplacement.Id);
            foreach (var use in plan.Reassign.Where(u => u.StyleKind == StyleKinds.FilledRegionType))
            {
                if (doc.GetElement(new ElementId(use.ElementId)) is not FilledRegion region)
                    throw new InvalidOperationException($"filled region {use.ElementId} is gone");
                region.ChangeTypeId(typeId);
            }
        }
    }
}
