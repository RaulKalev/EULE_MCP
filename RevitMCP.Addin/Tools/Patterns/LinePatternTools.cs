using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Patterns;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Patterns;

/// <summary>The line and fill pattern tools. App.OnStartup calls <see cref="Register"/> once.</summary>
public static class PatternToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListLinePatternsTool());
        handler.RegisterTool(new PreviewSetLinePatternsTool());
        handler.RegisterTool(new SetLinePatternsTool());
        handler.RegisterTool(new ListFillPatternsTool());
        handler.RegisterTool(new PreviewSetFillPatternsTool());
        handler.RegisterTool(new SetFillPatternsTool());
    }
}

/// <summary>revit_list_line_patterns: the line patterns of a project or family document.</summary>
public sealed class ListLinePatternsTool : IRevitMcpTool
{
    public string Name => "revit_list_line_patterns";

    public string Description =>
        "Lists the line patterns of the document, a project or an open family: id, name and segments [{type: dash|space|dot, " +
        "lengthMm}] (paper mm). Solid is built in and not listed. includeUsage=true adds the line styles that use each pattern.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var includeUsage = ToolArguments.GetBool(request.Arguments, "includeUsage", false);

        var usage = includeUsage ? LinePatternExecutor.StylesByPattern(doc) : null;
        var patterns = LinePatternExecutor.Read(doc).Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{patterns.Count} line pattern(s) in '{doc.Title}' (Solid is built in).",
            Data = new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                count = patterns.Count,
                patterns = patterns.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    segments = PatternSpecParser.DescribeSegments(p.Segments),
                    usedByLineStyles = usage == null ? null : usage.TryGetValue(p.Id, out var styles) ? styles : new List<string>()
                }).ToList()
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_preview_set_line_patterns: what revit_set_line_patterns would create or replace.</summary>
public sealed class PreviewSetLinePatternsTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_line_patterns";

    public string Description =>
        "Previews revit_set_line_patterns without changing the model. Same arguments. Per pattern: action (create | replace | " +
        "unchanged) with the current and the new segments; ready and problems list what must be fixed first.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinePatternExecutor.Execute(uiapp, request, apply: false));
}

/// <summary>revit_set_line_patterns: creates line patterns, or replaces the segments of existing ones.</summary>
public sealed class SetLinePatternsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_set_line_patterns";

    public string Description =>
        "Creates line patterns, and replaces the segments of existing ones, in the document: a project or an open family. " +
        "Requires approval. patterns [{name, segments [{type: dash|space|dot, length (paper mm)}]}]: a dash or dot first, " +
        "alternating with spaces, an even count; a dot has no length. One transaction, one undo; any problem leaves the " +
        "document unchanged. Replacing a pattern changes every line style that uses it. Run revit_preview_set_line_patterns first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinePatternExecutor.Execute(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var errors = new List<string>();
        var definitions = PatternSpecParser.ReadLinePatterns(request.Arguments, "patterns", errors);
        return PatternSpecParser.LineSummary(definitions, errors.Count, ToolArguments.GetString(request.Arguments, "document").Trim());
    }
}

internal static class LinePatternExecutor
{
    internal sealed class ExistingPattern
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public LinePatternElement Element { get; set; } = null!;
        public List<PatternSegment> Segments { get; set; } = new();
    }

    private sealed class PlannedPattern
    {
        public LinePatternDefinition Definition { get; set; } = new();
        public ExistingPattern? Existing { get; set; }
        public string Action { get; set; } = "create";
    }

    public static Dictionary<string, ExistingPattern> Read(Document doc)
    {
        var result = new Dictionary<string, ExistingPattern>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>())
        {
            if (result.ContainsKey(element.Name)) continue;
            var segments = new List<PatternSegment>();
            try
            {
                foreach (var s in element.GetLinePattern().GetSegments())
                    segments.Add(s.Type switch
                    {
                        LinePatternSegmentType.Dot => new PatternSegment(PatternSegment.Dot, 0),
                        LinePatternSegmentType.Space => new PatternSegment(PatternSegment.Space, s.Length * PatternSpecParser.MmPerFoot),
                        _ => new PatternSegment(PatternSegment.Dash, s.Length * PatternSpecParser.MmPerFoot)
                    });
            }
            catch
            {
                // A pattern Revit cannot read back is listed without segments.
            }
            result[element.Name] = new ExistingPattern { Id = element.Id.Value, Name = element.Name, Element = element, Segments = segments };
        }
        return result;
    }

    /// <summary>Line pattern id to the names of the line styles (subcategories of Lines) that use it.</summary>
    public static Dictionary<long, List<string>> StylesByPattern(Document doc)
    {
        var map = new Dictionary<long, List<string>>();
        Category? lines;
        try { lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines); }
        catch { lines = null; }
        if (lines == null) return map;
        foreach (Category sub in lines.SubCategories)
        {
            var id = sub.GetLinePatternId(GraphicsStyleType.Projection);
            if (id == null || id == ElementId.InvalidElementId) continue;
            if (!map.TryGetValue(id.Value, out var names)) map[id.Value] = names = new List<string>();
            names.Add(sub.Name);
        }
        foreach (var names in map.Values) names.Sort(StringComparer.OrdinalIgnoreCase);
        return map;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var problems = new List<string>();
        var definitions = PatternSpecParser.ReadLinePatterns(request.Arguments, "patterns", problems);
        if (definitions.Count == 0 && problems.Count == 0)
            problems.Add("Provide 'patterns': an array of {name, segments: [{type: dash|space|dot, length (mm)}]}.");

        var existing = Read(doc);
        var plan = new List<PlannedPattern>();
        foreach (var definition in definitions)
        {
            existing.TryGetValue(definition.Name, out var current);
            plan.Add(new PlannedPattern
            {
                Definition = definition,
                Existing = current,
                Action = current == null ? "create" : PatternSpecParser.SameSegments(current.Segments, definition.Segments) ? "unchanged" : "replace"
            });
        }

        object Describe(PlannedPattern p, long? id = null, string? action = null) => new
        {
            name = p.Existing?.Name ?? p.Definition.Name,
            id = id ?? p.Existing?.Id,
            action = action ?? p.Action,
            segments = PatternSpecParser.DescribeSegments(p.Definition.Segments),
            from = p.Action == "replace" ? PatternSpecParser.DescribeSegments(p.Existing!.Segments) : null
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
                    ? $"Preview: {creates} line pattern(s) to create, {replaces} to replace, {plan.Count - creates - replaces} unchanged in '{doc.Title}'."
                    : $"Preview: {problems.Count} problem(s) to fix before changing line patterns.",
                Data = new
                {
                    ready = problems.Count == 0,
                    problems,
                    document = doc.Title,
                    isFamilyDocument = doc.IsFamilyDocument,
                    patterns = plan.Select(p => Describe(p)).ToList()
                },
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
                DurationMs = sw.ElapsedMilliseconds
            };

        var ids = new Dictionary<PlannedPattern, long>();
        try
        {
            using var tx = new Transaction(doc, "Revit MCP - Line patterns");
            tx.Start();
            foreach (var planned in plan.Where(p => p.Action != "unchanged"))
            {
                var definition = planned.Definition;
                try
                {
                    var segments = definition.Segments.Select(ToRevit).ToList();
                    if (planned.Existing == null)
                    {
                        var pattern = new LinePattern(definition.Name);
                        pattern.SetSegments(segments);
                        ids[planned] = LinePatternElement.Create(doc, pattern).Id.Value;
                    }
                    else
                    {
                        var pattern = planned.Existing.Element.GetLinePattern();
                        pattern.SetSegments(segments);
                        planned.Existing.Element.SetLinePattern(pattern);
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
                        Message = $"Nothing was changed: Revit refused line pattern '{definition.Name}' ({ex.Message}).",
                        Errors = [$"{definition.Source} ('{definition.Name}'): {ex.Message}"],
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (Exception ex)
        {
            return PatternResults.Fail(request, $"Changing the line patterns failed and nothing was changed: {ex.Message}");
        }

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{creates} line pattern(s) created, {replaces} replaced, {plan.Count - creates - replaces} unchanged in '{doc.Title}'.",
            Data = new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                created = creates,
                replaced = replaces,
                patterns = plan.Select(p => Describe(p, ids.TryGetValue(p, out var id) ? id : null,
                    p.Action == "create" ? "created" : p.Action == "replace" ? "replaced" : null)).ToList()
            },
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static LinePatternSegment ToRevit(PatternSegment segment) => segment.Type switch
    {
        PatternSegment.Dot => new LinePatternSegment(LinePatternSegmentType.Dot, 0),
        PatternSegment.Space => new LinePatternSegment(LinePatternSegmentType.Space, segment.LengthMm / PatternSpecParser.MmPerFoot),
        _ => new LinePatternSegment(LinePatternSegmentType.Dash, segment.LengthMm / PatternSpecParser.MmPerFoot)
    };
}

internal static class PatternResults
{
    public static McpToolResult Fail(McpToolRequest request, string message, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Message = message,
        Errors = [message],
        Warnings = warnings ?? new List<string>()
    };
}
