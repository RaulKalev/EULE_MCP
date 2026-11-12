using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Patterns;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Patterns;

/// <summary>revit_list_fill_patterns: the fill patterns of a project or family document, with their grids.</summary>
public sealed class ListFillPatternsTool : IRevitMcpTool
{
    public string Name => "revit_list_fill_patterns";

    public string Description =>
        "Lists the fill patterns of the document, a project or an open family: id, name, target (drafting | model), isSolid, " +
        "orientation and grids [{angle (degrees), origin [x, y], offset, shift, segments}] in mm (paper mm for drafting " +
        "patterns, model mm for model patterns). target filters; includeGrids=false leaves the grids out.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var targetText = ToolArguments.GetString(request.Arguments, "target").Trim();
        string? only = null;
        if (targetText.Length > 0 && !targetText.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            only = PatternSpecParser.ParseTarget(targetText);
            if (only == null) return Task.FromResult(PatternResults.Fail(request, $"target '{targetText}' is not drafting, model or all."));
        }
        var includeGrids = ToolArguments.GetBool(request.Arguments, "includeGrids", true);

        var patterns = FillPatternExecutor.Read(doc)
            .Where(p => only == null || p.Target == only)
            .OrderBy(p => p.Target, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{patterns.Count} fill pattern(s) in '{doc.Title}'.",
            Data = new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                count = patterns.Count,
                patterns = patterns.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    target = p.Target,
                    isSolid = p.IsSolid,
                    orientation = p.IsSolid ? null : p.Orientation,
                    gridCount = p.Grids.Count,
                    grids = includeGrids && !p.IsSolid ? p.Grids.Select(PatternSpecParser.DescribeGrid).ToList() : null
                }).ToList()
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_preview_set_fill_patterns: what revit_set_fill_patterns would create or replace.</summary>
public sealed class PreviewSetFillPatternsTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_fill_patterns";

    public string Description =>
        "Previews revit_set_fill_patterns without changing the model. Same arguments. Per pattern: action (create | replace | " +
        "unchanged) with the current and the new grids; ready and problems list what must be fixed first.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(FillPatternExecutor.Execute(uiapp, request, apply: false));
}

/// <summary>revit_set_fill_patterns: creates fill patterns, or replaces the grids of existing non-solid ones.</summary>
public sealed class SetFillPatternsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_set_fill_patterns";

    public string Description =>
        "Creates fill patterns, and replaces the definition of existing non-solid ones, in the document: a project or an open " +
        "family. Requires approval. patterns [{name, target? (drafting | model, default drafting), orientation? (toView | " +
        "toHost | asText), and either angle?, spacing, crosshatch? (true or {angle?, spacing?}) or grids [{angle, origin [x, y], " +
        "offset, shift, segments}]}]. mm are paper mm for drafting, model mm for model patterns. The solid fill cannot be created " +
        "or edited. One transaction, one undo; any problem leaves the document unchanged. Run revit_preview_set_fill_patterns first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(FillPatternExecutor.Execute(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var errors = new List<string>();
        var definitions = PatternSpecParser.ReadFillPatterns(request.Arguments, "patterns", errors);
        return PatternSpecParser.FillSummary(definitions, errors.Count, ToolArguments.GetString(request.Arguments, "document").Trim());
    }
}

internal static class FillPatternExecutor
{
    internal sealed class ExistingFill
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = FillPatternDefinition.Drafting;
        public bool IsSolid { get; set; }
        public string Orientation { get; set; } = FillPatternDefinition.ToView;
        public FillPatternElement Element { get; set; } = null!;
        public List<FillGridDefinition> Grids { get; set; } = new();
    }

    private sealed class PlannedFill
    {
        public FillPatternDefinition Definition { get; set; } = new();
        public ExistingFill? Existing { get; set; }
        public string Orientation { get; set; } = FillPatternDefinition.ToView;
        public string Action { get; set; } = "create";
    }

    public static List<ExistingFill> Read(Document doc)
    {
        var result = new List<ExistingFill>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
        {
            FillPattern pattern;
            try { pattern = element.GetFillPattern(); }
            catch { continue; }
            var fill = new ExistingFill
            {
                Id = element.Id.Value,
                Name = element.Name,
                Target = pattern.Target == FillPatternTarget.Model ? FillPatternDefinition.Model : FillPatternDefinition.Drafting,
                IsSolid = pattern.IsSolidFill,
                Orientation = pattern.HostOrientation switch
                {
                    FillPatternHostOrientation.ToHost => FillPatternDefinition.ToHost,
                    FillPatternHostOrientation.AsText => FillPatternDefinition.AsText,
                    _ => FillPatternDefinition.ToView
                },
                Element = element
            };
            if (!fill.IsSolid)
            {
                try
                {
                    foreach (var grid in pattern.GetFillGrids())
                        fill.Grids.Add(new FillGridDefinition
                        {
                            AngleDeg = PatternSpecParser.NormalizeAngle(grid.Angle * 180 / Math.PI),
                            OriginXMm = grid.Origin.U * PatternSpecParser.MmPerFoot,
                            OriginYMm = grid.Origin.V * PatternSpecParser.MmPerFoot,
                            OffsetMm = grid.Offset * PatternSpecParser.MmPerFoot,
                            ShiftMm = grid.Shift * PatternSpecParser.MmPerFoot,
                            Segments = PatternSpecParser.SegmentsFromGridLengths(grid.GetSegments().Select(v => v * PatternSpecParser.MmPerFoot).ToList())
                        });
                }
                catch
                {
                    // A pattern whose grids Revit cannot read back is listed without grids.
                }
            }
            result.Add(fill);
        }
        return result;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var problems = new List<string>();
        var warnings = new List<string>();
        var definitions = PatternSpecParser.ReadFillPatterns(request.Arguments, "patterns", problems);
        if (definitions.Count == 0 && problems.Count == 0)
            problems.Add("Provide 'patterns': an array of {name, target?, spacing, angle?, crosshatch?} or {name, target?, grids}.");

        var existing = Read(doc);
        var plan = new List<PlannedFill>();
        foreach (var definition in definitions)
        {
            warnings.AddRange(PatternSpecParser.DensityWarnings(definition));
            var current = existing.FirstOrDefault(e => e.Target == definition.Target && string.Equals(e.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
            var planned = new PlannedFill
            {
                Definition = definition,
                Existing = current,
                Orientation = definition.Orientation ?? current?.Orientation ?? PatternSpecParser.DefaultOrientation(definition.Target)
            };
            plan.Add(planned);

            if (current == null)
            {
                var other = existing.FirstOrDefault(e => e.Target != definition.Target && string.Equals(e.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
                if (other != null)
                    warnings.Add($"{definition.Source}: '{other.Name}' exists as a {other.Target} pattern; this creates a separate {definition.Target} pattern of that name.");
                planned.Action = "create";
                continue;
            }
            if (current.IsSolid)
            {
                problems.Add($"{definition.Source}: '{current.Name}' is the solid fill; it cannot be edited. Use another name.");
                continue;
            }
            planned.Action = PatternSpecParser.SameGrids(current.Grids, definition.Grids) && planned.Orientation == current.Orientation ? "unchanged" : "replace";
        }

        object Describe(PlannedFill p, long? id = null, string? action = null) => new
        {
            name = p.Existing?.Name ?? p.Definition.Name,
            id = id ?? p.Existing?.Id,
            target = p.Definition.Target,
            action = action ?? p.Action,
            orientation = p.Orientation,
            grids = p.Definition.Grids.Select(PatternSpecParser.DescribeGrid).ToList(),
            from = p.Action == "replace"
                ? new { orientation = p.Existing!.Orientation, grids = p.Existing.Grids.Select(PatternSpecParser.DescribeGrid).ToList() }
                : null
        };

        var creates = plan.Count(p => p.Action == "create");
        var replaces = plan.Count(p => p.Action == "replace");
        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = problems.Count == 0
                    ? $"Preview: {creates} fill pattern(s) to create, {replaces} to replace, {plan.Count - creates - replaces} unchanged in '{doc.Title}'."
                    : $"Preview: {problems.Count} problem(s) to fix before changing fill patterns.",
                Data = new
                {
                    ready = problems.Count == 0,
                    problems,
                    document = doc.Title,
                    isFamilyDocument = doc.IsFamilyDocument,
                    patterns = plan.Select(p => Describe(p)).ToList()
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (problems.Count > 0)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "invalid_arguments",
                Message = $"Nothing was changed: {problems.Count} problem(s). {string.Join(" ", problems.Take(3))}",
                Errors = problems,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };

        var ids = new Dictionary<PlannedFill, long>();
        try
        {
            using var tx = new Transaction(doc, "Revit MCP - Fill patterns");
            tx.Start();
            foreach (var planned in plan.Where(p => p.Action != "unchanged"))
            {
                var definition = planned.Definition;
                try
                {
                    var grids = definition.Grids.Select(ToRevit).ToList();
                    var hasDots = definition.Grids.Any(g => g.Segments.Any(s => s.Type == PatternSegment.Dot));
                    if (planned.Existing == null)
                    {
                        var pattern = new FillPattern(definition.Name,
                            definition.Target == FillPatternDefinition.Model ? FillPatternTarget.Model : FillPatternTarget.Drafting,
                            ToRevit(planned.Orientation));
                        pattern.SetFillGrids(grids);
                        if (hasDots) pattern.ExpandDots();
                        ids[planned] = FillPatternElement.Create(doc, pattern).Id.Value;
                    }
                    else
                    {
                        var pattern = planned.Existing.Element.GetFillPattern();
                        pattern.SetFillGrids(grids);
                        pattern.HostOrientation = ToRevit(planned.Orientation);
                        if (hasDots) pattern.ExpandDots();
                        planned.Existing.Element.SetFillPattern(pattern);
                        ids[planned] = planned.Existing.Id;
                    }
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return new McpToolResult
                    {
                        RequestId = request.RequestId,
                        Success = false,
                        Status = "transaction_failed",
                        Message = $"Nothing was changed: Revit refused fill pattern '{definition.Name}' ({ex.Message}).",
                        Errors = [$"{definition.Source} ('{definition.Name}'): {ex.Message}"],
                        Warnings = warnings,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (Exception ex)
        {
            return PatternResults.Fail(request, $"Changing the fill patterns failed and nothing was changed: {ex.Message}", warnings);
        }

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{creates} fill pattern(s) created, {replaces} replaced, {plan.Count - creates - replaces} unchanged in '{doc.Title}'.",
            Data = new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                created = creates,
                replaced = replaces,
                patterns = plan.Select(p => Describe(p, ids.TryGetValue(p, out var id) ? id : null,
                    p.Action == "create" ? "created" : p.Action == "replace" ? "replaced" : null)).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static FillPatternHostOrientation ToRevit(string orientation) => orientation switch
    {
        FillPatternDefinition.ToHost => FillPatternHostOrientation.ToHost,
        FillPatternDefinition.AsText => FillPatternHostOrientation.AsText,
        _ => FillPatternHostOrientation.ToView
    };

    private static FillGrid ToRevit(FillGridDefinition definition)
    {
        var grid = new FillGrid
        {
            Angle = definition.AngleDeg * Math.PI / 180,
            Origin = new UV(definition.OriginXMm / PatternSpecParser.MmPerFoot, definition.OriginYMm / PatternSpecParser.MmPerFoot),
            Offset = definition.OffsetMm / PatternSpecParser.MmPerFoot,
            Shift = definition.ShiftMm / PatternSpecParser.MmPerFoot
        };
        if (definition.Segments.Count > 0)
            grid.SetSegments(PatternSpecParser.ToGridLengths(definition.Segments).Select(v => v / PatternSpecParser.MmPerFoot).ToList());
        return grid;
    }
}
