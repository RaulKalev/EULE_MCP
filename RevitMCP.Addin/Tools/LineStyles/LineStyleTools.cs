using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.Styles;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.LineStyles;

/// <summary>The line style tools. App.OnStartup calls <see cref="Register"/> once.</summary>
public static class LineStyleToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListLineStylesTool());
        handler.RegisterTool(new PreviewSetLineStylesTool());
        handler.RegisterTool(new SetLineStylesTool());
        handler.RegisterTool(new ListObjectStylesTool());
        handler.RegisterTool(new PreviewSetObjectStylesTool());
        handler.RegisterTool(new SetObjectStylesTool());
        handler.RegisterTool(new GetStyleUsageTool());
        handler.RegisterTool(new PreviewDeleteStylesTool());
        handler.RegisterTool(new DeleteStylesTool());
    }
}

/// <summary>revit_list_line_styles: the line styles of a project or family document.</summary>
public sealed class ListLineStylesTool : IRevitMcpTool
{
    public string Name => "revit_list_line_styles";

    public string Description =>
        "Lists the line styles (subcategories of Lines) of the document, a project or an open family: id, name, weight " +
        "(projection line weight 1-16), color (#RRGGBB), pattern (line pattern name, Solid when none) and builtIn (Revit's " +
        "own styles such as <Thin Lines>). canAddStyles says whether the document accepts new styles. " +
        "includePatterns=true also lists the line pattern names a style can use.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var lines = LineStyleExecutor.LinesCategory(doc);
        if (lines == null) return Task.FromResult(LineStyleExecutor.Fail(request, $"'{doc.Title}' has no Lines category."));

        var styles = LineStyleExecutor.Read(doc, lines).Values.OrderBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var includePatterns = ToolArguments.GetBool(request.Arguments, "includePatterns", false);
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{styles.Count} line style(s) in '{doc.Title}'.",
            Data = new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                canAddStyles = lines.CanAddSubcategory,
                count = styles.Count,
                styles = styles.Select(s => new
                {
                    id = s.Id,
                    name = s.Info.Name,
                    weight = s.Info.Weight,
                    color = s.Info.Color.Length > 0 ? s.Info.Color : null,
                    pattern = s.Info.Pattern.Length > 0 ? s.Info.Pattern : "Solid",
                    builtIn = s.BuiltIn
                }).ToList(),
                patterns = includePatterns ? LineStyleExecutor.PatternNames(doc) : null
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_preview_set_line_styles: what revit_set_line_styles would create or change.</summary>
public sealed class PreviewSetLineStylesTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_line_styles";

    public string Description =>
        "Previews revit_set_line_styles without changing the model. Same arguments. Per style: action (create | edit | " +
        "unchanged) and from/to for name, weight, color and pattern; problems lists what must be fixed first (a pattern the " +
        "document does not have, a document that does not accept new line styles, a rename to a name in use).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LineStyleExecutor.Execute(uiapp, request, apply: false));
}

/// <summary>revit_set_line_styles: creates line styles, or edits those that exist, in a project or a family document.</summary>
public sealed class SetLineStylesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_set_line_styles";

    public string Description =>
        "Creates line styles, and edits the ones that already exist, in the document: a project or an open family. " +
        "Requires approval. styles [{name, weight? (line weight 1-16), color? (#RRGGBB), pattern? (a line pattern name of " +
        "the document, or solid), newName? (renames an existing style; not Revit's own)}]. A name the document does not have " +
        "is created (weight 1, black and solid unless given); an existing name gets only the fields given. One transaction, " +
        "one undo; any problem leaves the document unchanged. " +
        "Editing a style changes every line that uses it. Run revit_preview_set_line_styles first, and " +
        "revit_list_line_styles includePatterns=true for the pattern names.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LineStyleExecutor.Execute(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => LineStyleExecutor.Summary(request);
}

internal static class LineStyleExecutor
{
    internal sealed class ExistingStyle
    {
        public long Id { get; set; }
        public Category Category { get; set; } = null!;
        public ProjectLineStyleInfo Info { get; set; } = new();
        public bool BuiltIn { get; set; }
    }

    private sealed class PlannedStyle
    {
        public LineStyleDefinition Definition { get; set; } = new();
        public ExistingStyle? Existing { get; set; }
        public ProjectLineStyleInfo To { get; set; } = new();
        /// <summary>The line pattern to set; null = leave the pattern as it is.</summary>
        public ElementId? PatternId { get; set; }
        public string Action { get; set; } = "create";
        /// <summary>The name after the change: the new name when renamed.</summary>
        public string FinalName => Definition.NewName ?? Existing?.Info.Name ?? Definition.Name;
        public Dictionary<string, object> Changes { get; } = new();
    }

    public static Category? LinesCategory(Document doc)
    {
        try { return doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines); }
        catch { return null; }
    }

    public static Dictionary<string, ExistingStyle> Read(Document doc, Category lines)
    {
        var result = new Dictionary<string, ExistingStyle>(StringComparer.OrdinalIgnoreCase);
        var solid = LinePatternElement.GetSolidPatternId();
        foreach (Category sub in lines.SubCategories)
        {
            var patternId = sub.GetLinePatternId(GraphicsStyleType.Projection);
            var pattern = patternId == ElementId.InvalidElementId || patternId == solid ? null : doc.GetElement(patternId) as LinePatternElement;
            result[sub.Name] = new ExistingStyle
            {
                Id = sub.Id.Value,
                Category = sub,
                BuiltIn = sub.Id.Value < 0,
                Info = new ProjectLineStyleInfo
                {
                    Name = sub.Name,
                    Weight = sub.GetLineWeight(GraphicsStyleType.Projection),
                    Color = Hex(sub.LineColor) ?? string.Empty,
                    Pattern = pattern?.Name ?? string.Empty
                }
            };
        }
        return result;
    }

    /// <summary>The document's line patterns by name, ignoring case.</summary>
    public static Dictionary<string, LinePatternElement> Patterns(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The line pattern id for a definition's pattern (empty = solid), with the document's spelling in
    /// <paramref name="name"/>; null with a problem added when the document has no such pattern.
    /// </summary>
    public static ElementId? ResolvePattern(Dictionary<string, LinePatternElement> patterns, string pattern, string source, string documentTitle,
        List<string> problems, out string name)
    {
        name = string.Empty;
        if (pattern.Length == 0) return LinePatternElement.GetSolidPatternId();
        if (patterns.TryGetValue(pattern, out var found))
        {
            name = found.Name;
            return found.Id;
        }
        problems.Add($"{source}.pattern '{pattern}' is not a line pattern of '{documentTitle}' (use a pattern name or solid)." +
                     StyleUsagePlanner.DidYouMean(pattern, patterns.Keys));
        return null;
    }

    public static string? Hex(Color? color) => color != null && color.IsValid ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" : null;

    /// <summary>A line pattern's name; empty for solid or none.</summary>
    public static string PatternName(Document doc, ElementId patternId) =>
        patternId == ElementId.InvalidElementId || patternId == LinePatternElement.GetSolidPatternId()
            ? string.Empty
            : (doc.GetElement(patternId) as LinePatternElement)?.Name ?? string.Empty;

    /// <summary>
    /// Renames a subcategory. Category.Name is read-only in the Revit 2024 and 2026 APIs, so the name is set on the
    /// subcategory's projection GraphicsStyle element (Element.Name has a setter), which carries the subcategory's name.
    /// Not verified in Revit: when Revit refuses it, this throws and the caller's transaction is rolled back; the caller
    /// checks the name after the commit.
    /// </summary>
    public static void Rename(Category category, string newName)
    {
        var style = category.GetGraphicsStyle(GraphicsStyleType.Projection)
                    ?? throw new InvalidOperationException($"'{category.Name}' has no graphics style to rename.");
        style.Name = newName;
    }

    public static List<string> PatternNames(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Select(e => e.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public static string Summary(McpToolRequest request)
    {
        var errors = new List<string>();
        var definitions = DetailFamilySpecParser.ReadLineStyleDefinitions(request.Arguments, "styles", errors, allowRename: true);
        var document = ToolArguments.GetString(request.Arguments, "document").Trim();
        var list = string.Join("; ", definitions.Take(8).Select(d =>
            $"'{d.Name}'" + (d.NewName != null ? $" renamed to '{d.NewName}'" : string.Empty) + (d.HasValues
                ? " (" + string.Join(", ", new[]
                {
                    d.Weight != null ? $"weight {d.Weight}" : null,
                    d.Color != null ? $"colour {d.Color}" : null,
                    d.Pattern != null ? $"pattern {(d.Pattern.Length > 0 ? d.Pattern : "solid")}" : null
                }.Where(s => s != null)) + ")"
                : string.Empty)));
        return $"Create or edit {definitions.Count} line style(s) in {(document.Length > 0 ? $"'{document}'" : "the active document")}: {list}" +
               $"{(definitions.Count > 8 ? ", …" : string.Empty)}. Editing a style changes every line that uses it." +
               $"{(errors.Count > 0 ? $" The request has {errors.Count} invalid item(s)." : string.Empty)}";
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var lines = LinesCategory(doc);
        if (lines == null) return Fail(request, $"'{doc.Title}' has no Lines category.");

        var problems = new List<string>();
        var warnings = new List<string>();
        var definitions = DetailFamilySpecParser.ReadLineStyleDefinitions(request.Arguments, "styles", problems, allowRename: true);
        if (definitions.Count == 0 && problems.Count == 0)
            problems.Add("Provide 'styles': an array of {name, weight?, color?, pattern?, newName?}.");

        var existing = Read(doc, lines);
        var patterns = Patterns(doc);

        var plan = new List<PlannedStyle>();
        foreach (var definition in definitions)
        {
            existing.TryGetValue(definition.Name, out var current);
            var planned = new PlannedStyle
            {
                Definition = definition,
                Existing = current,
                To = DetailFamilySpecParser.MergeLineStyle(definition, current?.Info)
            };
            plan.Add(planned);

            if (definition.NewName != null)
            {
                if (current == null)
                {
                    problems.Add($"{definition.Source}: '{doc.Title}' has no line style '{definition.Name}' to rename." +
                                 StyleUsagePlanner.DidYouMean(definition.Name, existing.Keys));
                    continue;
                }
                if (current.BuiltIn)
                {
                    problems.Add($"{definition.Source}: '{current.Info.Name}' is one of Revit's own line styles and cannot be renamed.");
                    continue;
                }
                if (existing.TryGetValue(definition.NewName, out var taken) && taken.Id != current.Id)
                {
                    problems.Add($"{definition.Source}.newName: '{doc.Title}' already has a line style '{taken.Info.Name}'.");
                    continue;
                }
                if (!string.Equals(definition.NewName, current.Info.Name, StringComparison.Ordinal))
                    planned.Changes["name"] = new { from = current.Info.Name, to = definition.NewName };
            }

            if (current == null && !lines.CanAddSubcategory)
            {
                problems.Add($"{definition.Source}: '{doc.Title}' does not accept new line styles, and it has no style '{definition.Name}'.");
                continue;
            }

            if (definition.Pattern != null)
            {
                planned.PatternId = ResolvePattern(patterns, definition.Pattern, definition.Source, doc.Title, problems, out var patternName);
                if (planned.PatternId == null) continue;
                planned.To.Pattern = patternName;
            }

            if (current == null)
            {
                planned.Action = "create";
                continue;
            }

            if (definition.Weight != null && definition.Weight != current.Info.Weight)
                planned.Changes["weight"] = new { from = current.Info.Weight, to = definition.Weight };
            if (definition.Color != null && !string.Equals(definition.Color, current.Info.Color, StringComparison.OrdinalIgnoreCase))
                planned.Changes["color"] = new { from = current.Info.Color.Length > 0 ? current.Info.Color : null, to = definition.Color };
            if (definition.Pattern != null && !string.Equals(planned.To.Pattern, current.Info.Pattern, StringComparison.OrdinalIgnoreCase))
                planned.Changes["pattern"] = new { from = PatternLabel(current.Info.Pattern), to = PatternLabel(planned.To.Pattern) };
            planned.Action = planned.Changes.Count > 0 ? "edit" : "unchanged";
            if (planned.Action == "edit" && current.BuiltIn)
                warnings.Add($"'{current.Info.Name}' is one of Revit's own line styles; Revit may refuse some changes to it.");
        }

        object Describe(PlannedStyle p, long? id = null) => new
        {
            name = p.FinalName,
            id = id ?? p.Existing?.Id,
            action = p.Action,
            weight = p.To.Weight,
            color = p.To.Color,
            pattern = PatternLabel(p.To.Pattern),
            changes = p.Changes.Count > 0 ? p.Changes : null
        };

        var creates = plan.Count(p => p.Action == "create");
        var edits = plan.Count(p => p.Action == "edit");
        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = problems.Count == 0
                    ? $"Preview: {creates} line style(s) to create, {edits} to edit, {plan.Count - creates - edits} unchanged in '{doc.Title}'."
                    : $"Preview: {problems.Count} problem(s) to fix before changing line styles.",
                Data = new
                {
                    ready = problems.Count == 0,
                    problems,
                    document = doc.Title,
                    isFamilyDocument = doc.IsFamilyDocument,
                    styles = plan.Select(p => Describe(p)).ToList()
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

        var ids = new Dictionary<PlannedStyle, long>();
        try
        {
            using var tx = new Transaction(doc, "Revit MCP - Line styles");
            tx.Start();
            foreach (var planned in plan.Where(p => p.Action != "unchanged"))
            {
                var definition = planned.Definition;
                try
                {
                    var category = planned.Existing?.Category ?? doc.Settings.Categories.NewSubcategory(lines, definition.Name);
                    var creating = planned.Existing == null;
                    if (creating || definition.Weight != null)
                        category.SetLineWeight(planned.To.Weight ?? DetailFamilySpecParser.DefaultLineWeight, GraphicsStyleType.Projection);
                    if (creating || definition.Color != null)
                        category.LineColor = ToColor(planned.To.Color);
                    if (planned.PatternId != null)
                        category.SetLinePatternId(planned.PatternId, GraphicsStyleType.Projection);
                    if (planned.Changes.ContainsKey("name"))
                        Rename(category, definition.NewName!);
                    ids[planned] = category.Id.Value;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return new McpToolResult
                    {
                        RequestId = request.RequestId,
                        Success = false,
                        Status = "transaction_failed",
                        Message = $"Nothing was changed: Revit refused line style '{definition.Name}' ({ex.Message}).",
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
            return Fail(request, $"Changing the line styles failed and nothing was changed: {ex.Message}", warnings);
        }

        // The rename goes through the style's GraphicsStyle element: say so when Revit kept the old name.
        var after = Read(doc, lines);
        foreach (var renamed in plan.Where(p => p.Changes.ContainsKey("name")))
            if (!after.TryGetValue(renamed.Definition.NewName!, out var now) || now.Id != renamed.Existing!.Id)
                warnings.Add($"Revit did not rename '{renamed.Existing!.Info.Name}' to '{renamed.Definition.NewName}'; its other changes were made. Rename it in Manage > Additional Settings > Line Styles.");

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{creates} line style(s) created, {edits} edited, {plan.Count - creates - edits} unchanged in '{doc.Title}'.",
            Data = new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                created = creates,
                edited = edits,
                styles = plan.Select(p => Describe(p, ids.TryGetValue(p, out var id) ? id : null))
                    .Select((s, i) => plan[i].Action == "create" ? WithAction(s, "created") : plan[i].Action == "edit" ? WithAction(s, "edited") : s)
                    .ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    internal static object WithAction(object described, string action)
    {
        var body = Newtonsoft.Json.Linq.JObject.FromObject(described);
        body["action"] = action;
        return body;
    }

    internal static string PatternLabel(string pattern) => pattern.Length > 0 ? pattern : "Solid";

    internal static Color ToColor(string hex) => new(
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    public static McpToolResult Fail(McpToolRequest request, string message, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Message = message,
        Errors = [message],
        Warnings = warnings ?? new List<string>()
    };
}
