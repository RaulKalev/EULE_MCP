using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Styles;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.LineStyles;

/// <summary>revit_list_object_styles: the categories and subcategories of Object Styles, with their line graphics and material.</summary>
public sealed class ListObjectStylesTool : IRevitMcpTool
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxSubcategories = 200;

    public string Name => "revit_list_object_styles";

    public string Description =>
        "Lists Object Styles of the document, a project or an open family: model, annotation and imported categories with " +
        "projectionWeight, cutWeight (null when the category is not cut), color, pattern, material, canAddSubcategory and " +
        "their subcategories. category filters by name (partial), kind by model | annotation | imported. offset/limit page " +
        "the categories (default 50, max 200). In a family, familyCategory is the family's own category, listed first.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var kind = ObjectStyleKinds.ParseFilter(ToolArguments.GetString(request.Arguments, "kind"));
        if (kind == null)
            return Task.FromResult(LineStyleExecutor.Fail(request, "kind must be model, annotation or imported (or empty for all)."));
        var filter = ToolArguments.GetString(request.Arguments, "category").Trim();
        var includeSubcategories = ToolArguments.GetBool(request.Arguments, "includeSubcategories", true);
        var offset = Math.Max(0, ToolArguments.GetInt(request.Arguments, "offset", 0));
        var limit = Math.Min(MaxLimit, Math.Max(1, ToolArguments.GetInt(request.Arguments, "limit", DefaultLimit)));

        var familyCategoryId = ObjectStyleExecutor.FamilyCategoryId(doc);
        var all = ObjectStyleExecutor.TopLevel(doc)
            .Where(c => kind.Length == 0 || c.Kind == kind)
            .Where(c => filter.Length == 0 || c.Category.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(c => c.Category.Id.Value == familyCategoryId ? 0 : 1)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.Category.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var page = all.Skip(offset).Take(limit).ToList();

        var rows = page.Select(c =>
        {
            var subcategories = includeSubcategories
                ? c.Category.SubCategories.Cast<Category>().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<Category>();
            var row = ObjectStyleExecutor.Describe(doc, c.Category, c.Kind);
            row["canAddSubcategory"] = c.Category.CanAddSubcategory;
            row["subcategoryCount"] = c.Category.SubCategories.Size;
            if (includeSubcategories)
            {
                row["subcategories"] = Newtonsoft.Json.Linq.JArray.FromObject(subcategories.Take(MaxSubcategories)
                    .Select(s => ObjectStyleExecutor.Describe(doc, s, null)).ToList());
                if (subcategories.Count > MaxSubcategories) row["subcategoriesTruncated"] = true;
            }
            return row;
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{page.Count} of {all.Count} categor{(all.Count == 1 ? "y" : "ies")} in '{doc.Title}'" +
                      (offset + page.Count < all.Count ? $"; next offset {offset + page.Count}." : "."),
            Data = new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                familyCategory = familyCategoryId != null ? doc.Settings.Categories.Cast<Category>().FirstOrDefault(c => c.Id.Value == familyCategoryId)?.Name : null,
                total = all.Count,
                offset,
                nextOffset = offset + page.Count < all.Count ? offset + page.Count : (int?)null,
                categories = rows
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_preview_set_object_styles: what revit_set_object_styles would create or change.</summary>
public sealed class PreviewSetObjectStylesTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_object_styles";

    public string Description =>
        "Previews revit_set_object_styles without changing the model. Same arguments. Per item: action (create | edit | " +
        "unchanged) with from/to per field; problems lists what must be fixed first (a category or pattern or material " +
        "that is not found, a cut weight on a category that is not cut, a material on an annotation category).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ObjectStyleExecutor.Execute(uiapp, request, apply: false));
}

/// <summary>revit_set_object_styles: edits categories and subcategories of Object Styles, and creates subcategories.</summary>
public sealed class SetObjectStylesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_set_object_styles";

    public string Description =>
        "Edits Object Styles in the document, a project or an open family. Requires approval. styles [{category, " +
        "subcategory?, projectionWeight?, cutWeight? (1-16), color? (#RRGGBB), pattern? (line pattern name or solid), " +
        "material? (name or none)}]. With subcategory: created under category when missing, else edited; without: the " +
        "category itself is edited. Only the fields given change. Lines subcategories are line styles: use " +
        "revit_set_line_styles. One transaction, one undo. Run revit_preview_set_object_styles first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ObjectStyleExecutor.Execute(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => ObjectStyleExecutor.Summary(request);
}

internal static class ObjectStyleExecutor
{
    internal sealed class TopCategory
    {
        public Category Category { get; set; } = null!;
        public string Kind { get; set; } = ObjectStyleKinds.Model;
    }

    private sealed class PlannedObjectStyle
    {
        public ObjectStyleDefinition Definition { get; set; } = new();
        public Category? Parent { get; set; }
        public Category? Existing { get; set; }
        public ElementId? PatternId { get; set; }
        /// <summary>The material to set; InvalidElementId = none; null = leave it.</summary>
        public ElementId? MaterialId { get; set; }
        public string Action { get; set; } = "edit";
        public Dictionary<string, object?> Changes { get; } = new();
        public long? ResultId { get; set; }
    }

    public static long? FamilyCategoryId(Document doc)
    {
        if (!doc.IsFamilyDocument) return null;
        try { return doc.OwnerFamily?.FamilyCategory?.Id.Value; }
        catch { return null; }
    }

    /// <summary>The top-level categories Object Styles shows: model, annotation and imported, visible in the UI.</summary>
    public static List<TopCategory> TopLevel(Document doc)
    {
        var result = new List<TopCategory>();
        foreach (Category category in doc.Settings.Categories)
        {
            string? kind;
            try
            {
                if (!category.IsVisibleInUI) continue;
                kind = ObjectStyleKinds.Classify(category.CategoryType.ToString(), category.Id.Value, BuiltInName(category));
            }
            catch
            {
                continue;
            }
            if (kind != null) result.Add(new TopCategory { Category = category, Kind = kind });
        }
        return result;
    }

    private static string? BuiltInName(Category category)
    {
        try
        {
            var builtIn = category.BuiltInCategory;
            return builtIn == BuiltInCategory.INVALID ? null : builtIn.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static int? Weight(Category category, GraphicsStyleType type)
    {
        try { return category.GetLineWeight(type); }
        catch { return null; }
    }

    /// <summary>A category or subcategory as a row: id, name, weights, colour, pattern, material.</summary>
    public static Newtonsoft.Json.Linq.JObject Describe(Document doc, Category category, string? kind)
    {
        string? pattern;
        try { pattern = LineStyleExecutor.PatternLabel(LineStyleExecutor.PatternName(doc, category.GetLinePatternId(GraphicsStyleType.Projection))); }
        catch { pattern = null; }
        string? material;
        try { material = category.Material?.Name; }
        catch { material = null; }

        var row = new Newtonsoft.Json.Linq.JObject
        {
            ["id"] = category.Id.Value,
            ["name"] = category.Name
        };
        if (kind != null) row["kind"] = kind;
        if (BuiltInName(category) is { } builtIn) row["builtInCategory"] = builtIn;
        else if (category.Parent != null) row["builtIn"] = category.Id.Value < 0;
        row["projectionWeight"] = Weight(category, GraphicsStyleType.Projection);
        row["cutWeight"] = Weight(category, GraphicsStyleType.Cut);
        row["color"] = LineStyleExecutor.Hex(category.LineColor);
        row["pattern"] = pattern;
        row["material"] = material;
        return row;
    }

    /// <summary>A top-level category by name, built-in name (OST_Walls) or id; null with the close names when not found.</summary>
    private static Category? FindCategory(Document doc, List<TopCategory> all, string reference, out string? hint)
    {
        hint = null;
        var text = reference.Trim();
        Category? found = null;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            found = all.FirstOrDefault(c => c.Category.Id.Value == id)?.Category;
        found ??= all.FirstOrDefault(c => string.Equals(c.Category.Name, text, StringComparison.OrdinalIgnoreCase))?.Category;
        found ??= all.FirstOrDefault(c => string.Equals(BuiltInName(c.Category), text, StringComparison.OrdinalIgnoreCase))?.Category;
        if (found == null)
        {
            // Not in Object Styles (internal, or hidden from the UI), but still a category of the document.
            foreach (Category category in doc.Settings.Categories)
                if (string.Equals(category.Name, text, StringComparison.OrdinalIgnoreCase)) { found = category; break; }
        }
        if (found == null) hint = StyleUsagePlanner.DidYouMean(text, all.Select(c => c.Category.Name));
        return found;
    }

    public static string Summary(McpToolRequest request)
    {
        var errors = new List<string>();
        var definitions = DetailFamilySpecParser.ReadObjectStyleDefinitions(request.Arguments, "styles", errors);
        var document = ToolArguments.GetString(request.Arguments, "document").Trim();
        var list = string.Join("; ", definitions.Take(8).Select(d =>
            $"'{d.Label}'" + (d.HasValues
                ? " (" + string.Join(", ", new[]
                {
                    d.ProjectionWeight != null ? $"projection weight {d.ProjectionWeight}" : null,
                    d.CutWeight != null ? $"cut weight {d.CutWeight}" : null,
                    d.Color != null ? $"colour {d.Color}" : null,
                    d.Pattern != null ? $"pattern {(d.Pattern.Length > 0 ? d.Pattern : "solid")}" : null,
                    d.Material != null ? $"material {(d.Material.Length > 0 ? d.Material : "none")}" : null
                }.Where(s => s != null)) + ")"
                : " (create)")));
        return $"Edit or create {definitions.Count} object style(s) in {(document.Length > 0 ? $"'{document}'" : "the active document")}: {list}" +
               $"{(definitions.Count > 8 ? ", …" : string.Empty)}. Editing a category or subcategory changes every element drawn with it." +
               $"{(errors.Count > 0 ? $" The request has {errors.Count} invalid item(s)." : string.Empty)}";
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var problems = new List<string>();
        var warnings = new List<string>();
        var definitions = DetailFamilySpecParser.ReadObjectStyleDefinitions(request.Arguments, "styles", problems);
        if (definitions.Count == 0 && problems.Count == 0)
            problems.Add("Provide 'styles': an array of {category, subcategory?, projectionWeight?, cutWeight?, color?, pattern?, material?}.");

        var all = TopLevel(doc);
        var patterns = LineStyleExecutor.Patterns(doc);
        Dictionary<string, Material>? materials = null;
        var plan = new List<PlannedObjectStyle>();
        var creating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            var planned = new PlannedObjectStyle { Definition = definition };
            plan.Add(planned);

            var parent = FindCategory(doc, all, definition.Category, out var hint);
            if (parent == null)
            {
                problems.Add($"{definition.Source}: '{doc.Title}' has no category '{definition.Category}'.{hint}");
                continue;
            }
            if (parent.Parent != null)
            {
                problems.Add($"{definition.Source}: '{parent.Name}' is a subcategory of '{parent.Parent.Name}'; give category '{parent.Parent.Name}' and subcategory '{parent.Name}'.");
                continue;
            }
            planned.Parent = parent;
            if (BuiltInName(parent) == "OST_Lines")
            {
                problems.Add($"{definition.Source}: subcategories of Lines are line styles; use revit_set_line_styles for them.");
                continue;
            }

            Category? style = parent;
            if (definition.Subcategory != null)
            {
                style = parent.SubCategories.Cast<Category>().FirstOrDefault(s => string.Equals(s.Name, definition.Subcategory, StringComparison.OrdinalIgnoreCase));
                if (style == null)
                {
                    if (!parent.CanAddSubcategory)
                    {
                        problems.Add($"{definition.Source}: '{parent.Name}' has no subcategory '{definition.Subcategory}' and does not accept new ones." +
                                     StyleUsagePlanner.DidYouMean(definition.Subcategory, parent.SubCategories.Cast<Category>().Select(s => s.Name)));
                        continue;
                    }
                    if (!creating.Add($"{parent.Id.Value}/{definition.Subcategory}"))
                    {
                        problems.Add($"{definition.Source}: '{definition.Label}' is created twice in this request.");
                        continue;
                    }
                }
            }
            planned.Existing = style;
            planned.Action = style == null ? "create" : "edit";
            var isAnnotation = parent.CategoryType == CategoryType.Annotation;

            if (definition.ProjectionWeight is { } projection && (style == null || Weight(style, GraphicsStyleType.Projection) != projection))
                planned.Changes["projectionWeight"] = new { from = style == null ? null : Weight(style, GraphicsStyleType.Projection), to = projection };

            if (definition.CutWeight is { } cut)
            {
                var current = style == null ? null : Weight(style, GraphicsStyleType.Cut);
                if (current == null && !(parent.IsCuttable && !isAnnotation))
                {
                    problems.Add($"{definition.Source}: '{definition.Label}' has no cut line weight (the category is not cut in sections).");
                    continue;
                }
                if (current != cut) planned.Changes["cutWeight"] = new { from = current, to = cut };
            }

            if (definition.Color != null)
            {
                var current = style == null ? null : LineStyleExecutor.Hex(style.LineColor);
                if (!string.Equals(current, definition.Color, StringComparison.OrdinalIgnoreCase))
                    planned.Changes["color"] = new { from = current, to = definition.Color };
            }

            if (definition.Pattern != null)
            {
                planned.PatternId = LineStyleExecutor.ResolvePattern(patterns, definition.Pattern, definition.Source, doc.Title, problems, out var patternName);
                if (planned.PatternId == null) continue;
                var current = style == null ? null : LineStyleExecutor.PatternName(doc, style.GetLinePatternId(GraphicsStyleType.Projection));
                if (current == null || !string.Equals(current, patternName, StringComparison.OrdinalIgnoreCase))
                    planned.Changes["pattern"] = new { from = current == null ? null : LineStyleExecutor.PatternLabel(current), to = LineStyleExecutor.PatternLabel(patternName) };
                else planned.PatternId = null;
            }

            if (definition.Material != null)
            {
                if (isAnnotation)
                {
                    problems.Add($"{definition.Source}: '{definition.Label}' is an annotation category; annotation categories have no material.");
                    continue;
                }
                string? currentMaterial;
                try { currentMaterial = style?.Material?.Name; }
                catch { currentMaterial = null; }
                if (definition.Material.Length == 0) planned.MaterialId = ElementId.InvalidElementId;
                else
                {
                    materials ??= new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                        .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    if (!materials.TryGetValue(definition.Material, out var material))
                    {
                        problems.Add($"{definition.Source}.material '{definition.Material}' is not a material of '{doc.Title}'." +
                                     StyleUsagePlanner.DidYouMean(definition.Material, materials.Keys));
                        continue;
                    }
                    planned.MaterialId = material.Id;
                }
                var to = definition.Material.Length == 0 ? null : materials![definition.Material].Name;
                if (style == null || !string.Equals(currentMaterial, to, StringComparison.OrdinalIgnoreCase))
                    planned.Changes["material"] = new { from = currentMaterial, to };
                else planned.MaterialId = null;
            }

            if (planned.Action == "edit" && planned.Changes.Count == 0) planned.Action = "unchanged";
            if (planned.Action == "edit" && style != null && style.Parent != null && style.Id.Value < 0)
                warnings.Add($"'{definition.Label}' is one of Revit's own subcategories; Revit may refuse some changes to it.");
        }

        object Describe(PlannedObjectStyle p) => new
        {
            category = p.Parent?.Name ?? p.Definition.Category,
            subcategory = p.Definition.Subcategory != null ? p.Existing?.Name ?? p.Definition.Subcategory : null,
            id = p.ResultId ?? p.Existing?.Id.Value,
            action = p.Action,
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
                    ? $"Preview: {creates} subcategor{(creates == 1 ? "y" : "ies")} to create, {edits} object style(s) to edit, {plan.Count - creates - edits} unchanged in '{doc.Title}'."
                    : $"Preview: {problems.Count} problem(s) to fix before changing object styles.",
                Data = new
                {
                    ready = problems.Count == 0,
                    problems,
                    document = doc.Title,
                    isFamilyDocument = doc.IsFamilyDocument,
                    styles = plan.Select(Describe).ToList()
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

        try
        {
            using var tx = new Transaction(doc, "Revit MCP - Object styles");
            tx.Start();
            foreach (var planned in plan.Where(p => p.Action != "unchanged"))
            {
                var definition = planned.Definition;
                try
                {
                    var category = planned.Existing ?? doc.Settings.Categories.NewSubcategory(planned.Parent!, definition.Subcategory!);
                    if (definition.ProjectionWeight is { } projection) category.SetLineWeight(projection, GraphicsStyleType.Projection);
                    if (definition.CutWeight is { } cut) category.SetLineWeight(cut, GraphicsStyleType.Cut);
                    if (definition.Color != null) category.LineColor = LineStyleExecutor.ToColor(definition.Color);
                    if (planned.PatternId != null) category.SetLinePatternId(planned.PatternId, GraphicsStyleType.Projection);
                    if (planned.MaterialId != null)
                        category.Material = planned.MaterialId == ElementId.InvalidElementId ? null : doc.GetElement(planned.MaterialId) as Material;
                    planned.ResultId = category.Id.Value;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return new McpToolResult
                    {
                        RequestId = request.RequestId,
                        Success = false,
                        Status = "transaction_failed",
                        Message = $"Nothing was changed: Revit refused '{definition.Label}' ({ex.Message}).",
                        Errors = [$"{definition.Source} ('{definition.Label}'): {ex.Message}"],
                        Warnings = warnings,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (Exception ex)
        {
            return LineStyleExecutor.Fail(request, $"Changing the object styles failed and nothing was changed: {ex.Message}", warnings);
        }

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{creates} subcategor{(creates == 1 ? "y" : "ies")} created, {edits} object style(s) edited, {plan.Count - creates - edits} unchanged in '{doc.Title}'.",
            Data = new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                created = creates,
                edited = edits,
                styles = plan.Select(p => LineStyleExecutor.WithAction(Describe(p),
                    p.Action == "create" ? "created" : p.Action == "edit" ? "edited" : "unchanged")).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
