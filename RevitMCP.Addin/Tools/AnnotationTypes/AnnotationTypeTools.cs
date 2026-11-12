using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.AnnotationTypes;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.AnnotationTypes;

/// <summary>The text type and dimension type tools. App.OnStartup calls <see cref="Register"/> once.</summary>
public static class AnnotationTypeToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListTextTypesTool());
        handler.RegisterTool(new PreviewSetTextTypesTool());
        handler.RegisterTool(new SetTextTypesTool());
        handler.RegisterTool(new PreviewSetDimensionTypesTool());
        handler.RegisterTool(new SetDimensionTypesTool());
    }
}

/// <summary>revit_list_text_types: the text types of a project or family document.</summary>
public sealed class ListTextTypesTool : IRevitMcpTool
{
    public string Name => "revit_list_text_types";

    public string Description =>
        "Lists the text types of the document, a project or an open family: id, name, font, size (paper mm), widthFactor, " +
        "bold, italic, underline, color (#RRGGBB), background (opaque | transparent), showBorder, leaderBorderOffset (mm), " +
        "tabSize (mm), lineWeight (1-16), leaderArrowhead (arrowhead type name or none) and isDefault. includeUsage=true " +
        "adds usage: how many text notes use each type.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var kind = AnnotationTypeKind.Text;
        var includeUsage = ToolArguments.GetBool(request.Arguments, "includeUsage", false);
        var usage = includeUsage ? kind.Usage(doc) : null;
        var defaultId = kind.DefaultType(doc, null)?.Id.Value;

        var types = kind.Types(doc).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => AnnotationTypeReader.Describe(doc, t, kind, "id", usage, defaultId))
            .ToList();
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{types.Count} text type(s) in '{doc.Title}'.",
            Data = new { document = doc.Title, isFamilyDocument = doc.IsFamilyDocument, count = types.Count, types },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_preview_set_text_types: what revit_set_text_types would create or change.</summary>
public sealed class PreviewSetTextTypesTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_text_types";

    public string Description =>
        "Previews revit_set_text_types without changing the model. Same arguments. Per type: action (create | edit | " +
        "unchanged), duplicateFrom for a new type, usage (text notes of an edited type) and changes [{field, from, to}]; " +
        "ready and problems (a source or arrowhead not found, a name taken, a value Revit's type does not have).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(AnnotationTypeExecutor.Execute(uiapp, request, AnnotationTypeKind.Text, apply: false));
}

/// <summary>revit_set_text_types: creates text types by duplicating, and edits existing ones.</summary>
public sealed class SetTextTypesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_set_text_types";

    public string Description =>
        "Creates text types and edits existing ones in the document: a project or an open family. Requires approval. " +
        "types [{name, duplicateFrom?, newName?, font?, size? (paper mm), widthFactor?, bold?, italic?, underline?, color?, " +
        "background? (opaque | transparent), showBorder?, leaderBorderOffset? (mm), tabSize? (mm), lineWeight? (1-16), " +
        "leaderArrowhead? (arrowhead type name or none)}]. A name the document lacks is created by duplicating duplicateFrom " +
        "(default: the default text type); an existing name gets only the fields given; newName renames it. One transaction, " +
        "one undo; any problem leaves the document unchanged. Editing a type changes every text note that uses it. " +
        "Run revit_preview_set_text_types first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(AnnotationTypeExecutor.Execute(uiapp, request, AnnotationTypeKind.Text, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
        => AnnotationTypeExecutor.Summary(uiapp, request, AnnotationTypeKind.Text);
}

/// <summary>revit_preview_set_dimension_types: what revit_set_dimension_types would create or change.</summary>
public sealed class PreviewSetDimensionTypesTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_dimension_types";

    public string Description =>
        "Previews revit_set_dimension_types without changing the model. Same arguments. Per type: action (create | edit | " +
        "unchanged), style, duplicateFrom for a new type, usage (dimensions of an edited type) and changes [{field, from, to}]; " +
        "ready and problems (a source, style or tick mark not found, a name taken, a field the style does not have).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(AnnotationTypeExecutor.Execute(uiapp, request, AnnotationTypeKind.Dimension, apply: false));
}

/// <summary>revit_set_dimension_types: creates dimension types by duplicating, and edits existing ones.</summary>
public sealed class SetDimensionTypesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_set_dimension_types";

    public string Description =>
        "Creates dimension types and edits existing ones in the document: a project or an open family. Requires approval. " +
        "types [{name, style?, duplicateFrom?, newName?, textSize? (paper mm), textFont?, textWidthFactor?, bold?, italic?, " +
        "underline?, textOffset? (mm), textBackground? (opaque | transparent), color?, lineWeight? (1-16), tickMark? (arrowhead " +
        "type name), tickMarkLineWeight?, witnessLineExtension? (mm), witnessLineGap? (mm), dimensionLineExtension? (mm), " +
        "unitsFormat? ({useProjectSettings?, unit?, accuracy?} or \"project\")}]. style (linear, angular, radial, …) picks among " +
        "types of the same name. A name the document lacks is created by duplicating duplicateFrom (default: the default type " +
        "of style, linear when none) and keeps its style; an existing name gets only the fields given; newName renames it. One " +
        "transaction, one undo; any problem leaves the document unchanged. Editing a type changes every dimension that uses it. " +
        "Run revit_preview_set_dimension_types first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(AnnotationTypeExecutor.Execute(uiapp, request, AnnotationTypeKind.Dimension, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
        => AnnotationTypeExecutor.Summary(uiapp, request, AnnotationTypeKind.Dimension);
}

/// <summary>Text types or dimension types: their fields, the parameters behind them, and how to find them.</summary>
internal sealed class AnnotationTypeKind
{
    public string Label { get; private init; } = string.Empty;
    public string UsageNoun { get; private init; } = string.Empty;
    public bool IsDimension { get; private init; }
    public IReadOnlyList<AnnotationTypeField> Fields { get; private init; } = Array.Empty<AnnotationTypeField>();
    /// <summary>Field key to the parameters that hold it, the first one the type has wins.</summary>
    public IReadOnlyDictionary<string, BuiltInParameter[]> Parameters { get; private init; } = new Dictionary<string, BuiltInParameter[]>();

    public static readonly AnnotationTypeKind Text = new()
    {
        Label = "text type",
        UsageNoun = "text note",
        Fields = AnnotationTypeSpecParser.TextTypeFields,
        Parameters = new Dictionary<string, BuiltInParameter[]>
        {
            ["font"] = [BuiltInParameter.TEXT_FONT],
            ["size"] = [BuiltInParameter.TEXT_SIZE],
            ["widthFactor"] = [BuiltInParameter.TEXT_WIDTH_SCALE],
            ["bold"] = [BuiltInParameter.TEXT_STYLE_BOLD],
            ["italic"] = [BuiltInParameter.TEXT_STYLE_ITALIC],
            ["underline"] = [BuiltInParameter.TEXT_STYLE_UNDERLINE],
            ["color"] = [BuiltInParameter.LINE_COLOR],
            ["background"] = [BuiltInParameter.TEXT_BACKGROUND],
            ["showBorder"] = [BuiltInParameter.TEXT_BOX_VISIBILITY],
            ["leaderBorderOffset"] = [BuiltInParameter.LEADER_OFFSET_SHEET],
            ["tabSize"] = [BuiltInParameter.TEXT_TAB_SIZE],
            ["lineWeight"] = [BuiltInParameter.LINE_PEN],
            ["leaderArrowhead"] = [BuiltInParameter.LEADER_ARROWHEAD]
        }
    };

    public static readonly AnnotationTypeKind Dimension = new()
    {
        Label = "dimension type",
        UsageNoun = "dimension",
        IsDimension = true,
        Fields = AnnotationTypeSpecParser.DimensionTypeFields,
        Parameters = new Dictionary<string, BuiltInParameter[]>
        {
            ["textSize"] = [BuiltInParameter.TEXT_SIZE],
            ["textFont"] = [BuiltInParameter.TEXT_FONT],
            ["textWidthFactor"] = [BuiltInParameter.TEXT_WIDTH_SCALE],
            ["bold"] = [BuiltInParameter.TEXT_STYLE_BOLD],
            ["italic"] = [BuiltInParameter.TEXT_STYLE_ITALIC],
            ["underline"] = [BuiltInParameter.TEXT_STYLE_UNDERLINE],
            ["textOffset"] = [BuiltInParameter.TEXT_DIST_TO_LINE],
            ["textBackground"] = [BuiltInParameter.DIM_TEXT_BACKGROUND, BuiltInParameter.TEXT_BACKGROUND],
            ["color"] = [BuiltInParameter.LINE_COLOR],
            ["lineWeight"] = [BuiltInParameter.LINE_PEN],
            ["tickMark"] = [BuiltInParameter.DIM_LEADER_ARROWHEAD],
            ["tickMarkLineWeight"] = [BuiltInParameter.TICK_MARK_PEN],
            ["witnessLineExtension"] = [BuiltInParameter.WITNS_LINE_EXTENSION],
            ["witnessLineGap"] = [BuiltInParameter.WITNS_LINE_GAP_TO_ELT],
            ["dimensionLineExtension"] = [BuiltInParameter.DIM_LINE_EXTENSION]
        }
    };

    public List<ElementType> Types(Document doc) => IsDimension
        ? new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<ElementType>().Where(t => !string.IsNullOrEmpty(t.Name)).ToList()
        : new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<ElementType>().ToList();

    /// <summary>The dimension style (Linear, Angular, …); null for text types.</summary>
    public string? StyleOf(ElementType type)
    {
        if (type is not DimensionType dimension) return null;
        try { return dimension.StyleType.ToString(); }
        catch { return "Unknown"; }
    }

    /// <summary>The document's default type (of <paramref name="style"/> for dimensions, linear when none), else the first one.</summary>
    public ElementType? DefaultType(Document doc, string? style)
    {
        var types = Types(doc);
        if (!IsDimension)
        {
            ElementId id;
            try { id = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType); }
            catch { id = ElementId.InvalidElementId; }
            return types.FirstOrDefault(t => t.Id == id) ?? types.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        var wanted = style ?? "Linear";
        var group = AnnotationTypeSpecParser.NormalizeStyle(wanted) switch
        {
            "linear" => ElementTypeGroup.LinearDimensionType,
            "angular" => ElementTypeGroup.AngularDimensionType,
            "radial" => ElementTypeGroup.RadialDimensionType,
            "diameter" => ElementTypeGroup.DiameterDimensionType,
            "arclength" => ElementTypeGroup.ArcLengthDimensionType,
            "spotelevation" => ElementTypeGroup.SpotElevationType,
            "spotcoordinate" => ElementTypeGroup.SpotCoordinateType,
            "spotslope" => ElementTypeGroup.SpotSlopeType,
            _ => (ElementTypeGroup?)null
        };
        var defaultId = ElementId.InvalidElementId;
        if (group != null)
        {
            try { defaultId = doc.GetDefaultElementTypeId(group.Value); }
            catch { defaultId = ElementId.InvalidElementId; }
        }
        var ofStyle = types.Where(t => StyleOf(t) is { } s && AnnotationTypeSpecParser.SameStyle(s, wanted)).ToList();
        return ofStyle.FirstOrDefault(t => t.Id == defaultId) ?? ofStyle.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>How many text notes or dimensions use each type id; empty when Revit cannot collect them.</summary>
    public Dictionary<long, int> Usage(Document doc)
    {
        try
        {
            var elements = IsDimension
                ? new FilteredElementCollector(doc).OfClass(typeof(Dimension)).WhereElementIsNotElementType().ToElements()
                : new FilteredElementCollector(doc).OfClass(typeof(TextNote)).WhereElementIsNotElementType().ToElements();
            return elements.GroupBy(e => e.GetTypeId().Value).ToDictionary(g => g.Key, g => g.Count());
        }
        catch
        {
            return new Dictionary<long, int>();
        }
    }

    /// <summary>The key two types of this kind must not share: the name, and for dimension types the style too.</summary>
    public string NameKey(string name, string? style) =>
        (IsDimension ? AnnotationTypeSpecParser.NormalizeStyle(style ?? string.Empty) + "|" : string.Empty) + name.ToLowerInvariant();
}

/// <summary>Reads the fields of a text or dimension type as plain values (mm, #RRGGBB, names).</summary>
internal static class AnnotationTypeReader
{
    public static Parameter? Find(ElementType type, IReadOnlyDictionary<string, BuiltInParameter[]> map, string key)
    {
        if (!map.TryGetValue(key, out var candidates)) return null;
        foreach (var bip in candidates)
        {
            var parameter = type.get_Parameter(bip);
            if (parameter != null) return parameter;
        }
        return null;
    }

    public static StorageType Expected(AnnotationFieldKind kind) => kind switch
    {
        AnnotationFieldKind.Text => StorageType.String,
        AnnotationFieldKind.Length or AnnotationFieldKind.Factor => StorageType.Double,
        AnnotationFieldKind.Arrowhead => StorageType.ElementId,
        _ => StorageType.Integer
    };

    public static Dictionary<string, object?> Read(Document doc, ElementType type, AnnotationTypeKind kind)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in kind.Fields)
        {
            if (field.Kind == AnnotationFieldKind.UnitsFormat)
            {
                values[field.Key] = type is DimensionType dimension ? ReadUnits(dimension) : null;
                continue;
            }
            var parameter = Find(type, kind.Parameters, field.Key);
            values[field.Key] = parameter == null ? null : Plain(doc, field.Kind, parameter);
        }
        return values;
    }

    private static object? Plain(Document doc, AnnotationFieldKind kind, Parameter parameter)
    {
        if (parameter.StorageType != Expected(kind)) return null;
        try
        {
            switch (kind)
            {
                case AnnotationFieldKind.Text: return parameter.AsString() ?? string.Empty;
                case AnnotationFieldKind.Length: return AnnotationTypeSpecParser.FeetToMm(parameter.AsDouble());
                case AnnotationFieldKind.Factor: return Math.Round(parameter.AsDouble(), 6);
                case AnnotationFieldKind.Bool: return parameter.AsInteger() != 0;
                case AnnotationFieldKind.Colour: return AnnotationTypeSpecParser.ColourFromRevit(parameter.AsInteger());
                case AnnotationFieldKind.LineWeight: return parameter.AsInteger();
                case AnnotationFieldKind.Background: return AnnotationTypeSpecParser.BackgroundFromRevit(parameter.AsInteger());
                case AnnotationFieldKind.Arrowhead:
                    var id = parameter.AsElementId();
                    return id == null || id == ElementId.InvalidElementId ? string.Empty : doc.GetElement(id)?.Name ?? string.Empty;
                default: return null;
            }
        }
        catch
        {
            return null;
        }
    }

    public static UnitsFormatValue? ReadUnits(DimensionType type)
    {
        try { return Units(type.GetUnitsFormatOptions()); }
        catch { return null; }
    }

    public static UnitsFormatValue Units(FormatOptions options)
    {
        if (options.UseDefault) return new UnitsFormatValue(true, null, null);
        string? unit = null;
        double? accuracy = null;
        try { unit = AnnotationTypeSpecParser.UnitNameFromTypeId(options.GetUnitTypeId().TypeId); } catch { /* no unit */ }
        try { accuracy = options.Accuracy; } catch { /* no accuracy */ }
        return new UnitsFormatValue(false, unit, accuracy);
    }

    /// <summary>One type for a list: id, name, (style), the fields, usage and isDefault.</summary>
    public static Dictionary<string, object?> Describe(Document doc, ElementType type, AnnotationTypeKind kind, string idKey,
        Dictionary<long, int>? usage, long? defaultId)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [idKey] = type.Id.Value,
            ["name"] = type.Name
        };
        if (kind.IsDimension) row["style"] = kind.StyleOf(type);
        foreach (var pair in Read(doc, type, kind))
        {
            var field = kind.Fields.First(f => f.Key == pair.Key);
            row[pair.Key] = pair.Value is UnitsFormatValue units
                ? new { useProjectSettings = units.UseProjectSettings, unit = units.Unit, accuracy = units.Accuracy }
                : AnnotationTypeSpecParser.Display(field.Kind, pair.Value);
        }
        if (defaultId != null) row["isDefault"] = type.Id.Value == defaultId.Value;
        if (usage != null) row["usage"] = usage.TryGetValue(type.Id.Value, out var count) ? count : 0;
        return row;
    }

    /// <summary>Arrowhead types by name: element types with an arrow style, or of the Arrowhead family.</summary>
    public static Dictionary<string, ElementType> Arrowheads(Document doc)
    {
        var result = new Dictionary<string, ElementType>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>())
        {
            bool isArrowhead;
            try
            {
                isArrowhead = type.get_Parameter(BuiltInParameter.ARROW_TYPE) != null
                              || string.Equals(type.FamilyName, "Arrowhead", StringComparison.OrdinalIgnoreCase);
            }
            catch { isArrowhead = false; }
            if (isArrowhead && !string.IsNullOrEmpty(type.Name) && !result.ContainsKey(type.Name)) result[type.Name] = type;
        }
        return result;
    }
}

internal static class AnnotationTypeExecutor
{
    private const string TypesKey = "types";

    private sealed class Planned
    {
        public AnnotationTypeSpec Spec { get; set; } = new();
        public ElementType? Existing { get; set; }
        /// <summary>The type edited, or the one a new type is duplicated from.</summary>
        public ElementType? Source { get; set; }
        public string? Style { get; set; }
        public string Action { get; set; } = "create";
        public List<AnnotationFieldChange> Changes { get; set; } = new();
        public Dictionary<string, ElementId> ArrowheadIds { get; } = new(StringComparer.Ordinal);
        public FormatOptions? Units { get; set; }
        public int Usage { get; set; }
        public long? ResultId { get; set; }
    }

    public static string Summary(UIApplication uiapp, McpToolRequest request, AnnotationTypeKind kind)
    {
        var errors = new List<string>();
        var specs = AnnotationTypeSpecParser.Read(request.Arguments, TypesKey, kind.Fields, kind.IsDimension, errors);
        var target = ToolDocument.Resolve(uiapp, request, out _);
        var doc = target?.Document;
        var types = doc != null ? kind.Types(doc) : new List<ElementType>();
        var usage = doc != null ? kind.Usage(doc) : new Dictionary<long, int>();
        var affected = 0;
        var items = specs.Take(8).Select(spec =>
        {
            var existing = types.FirstOrDefault(t => string.Equals(t.Name, spec.Name, StringComparison.OrdinalIgnoreCase)
                                                     && (spec.Style == null || (kind.StyleOf(t) is { } s && AnnotationTypeSpecParser.SameStyle(s, spec.Style))));
            var fields = spec.Values.Keys.ToList();
            if (spec.NewName != null) fields.Insert(0, $"rename to '{spec.NewName}'");
            string what;
            if (existing == null) what = $"create from '{spec.DuplicateFrom ?? "the default type"}'";
            else
            {
                var count = usage.TryGetValue(existing.Id.Value, out var n) ? n : 0;
                affected += count;
                what = $"edit, used by {count} {kind.UsageNoun}(s)";
            }
            return $"'{spec.Name}' ({what}{(fields.Count > 0 ? ": " + string.Join(", ", fields) : string.Empty)})";
        }).ToList();
        var document = doc != null ? $"'{doc.Title}'" : "the active document";
        return $"Create or edit {specs.Count} {kind.Label}(s) in {document}: {string.Join("; ", items)}" +
               $"{(specs.Count > 8 ? "; …" : string.Empty)}. Editing a type changes every {kind.UsageNoun} that uses it" +
               $"{(affected > 0 ? $" ({affected} {kind.UsageNoun}(s) here)" : string.Empty)}." +
               $"{(errors.Count > 0 ? $" The request has {errors.Count} invalid item(s)." : string.Empty)}";
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, AnnotationTypeKind kind, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var problems = new List<string>();
        var warnings = new List<string>();
        var specs = AnnotationTypeSpecParser.Read(request.Arguments, TypesKey, kind.Fields, kind.IsDimension, problems);
        if (specs.Count == 0 && problems.Count == 0)
            problems.Add($"Provide '{TypesKey}': an array of {{name, …fields}}.");

        var types = kind.Types(doc);
        var usage = kind.Usage(doc);
        var taken = new HashSet<string>(types.Select(t => kind.NameKey(t.Name, kind.StyleOf(t))), StringComparer.Ordinal);
        Dictionary<string, ElementType>? arrowheads = null;
        var plan = new List<Planned>();

        foreach (var spec in specs)
        {
            var planned = new Planned { Spec = spec };
            plan.Add(planned);
            var label = kind.Label;

            var matches = Match(kind, types, spec.Name, spec.Style);
            if (matches.Count > 1)
            {
                problems.Add($"{spec.Source}: '{spec.Name}' is a {label} of several styles ({string.Join(", ", matches.Select(kind.StyleOf))}); give style.");
                continue;
            }

            if (matches.Count == 1)
            {
                planned.Existing = planned.Source = matches[0];
                planned.Action = "edit";
                planned.Usage = usage.TryGetValue(matches[0].Id.Value, out var count) ? count : 0;
                if (spec.DuplicateFrom != null)
                    warnings.Add($"{spec.Source}: '{matches[0].Name}' exists, so it is edited and duplicateFrom '{spec.DuplicateFrom}' is not used.");
            }
            else
            {
                planned.Action = "create";
                if (spec.NewName != null)
                {
                    problems.Add($"{spec.Source}: newName renames an existing {label}, and there is no {label} '{spec.Name}'" +
                                 AnnotationTypeSpecParser.DidYouMean(spec.Name, types.Select(t => t.Name)));
                    continue;
                }
                if (spec.DuplicateFrom != null)
                {
                    var sources = Match(kind, types, spec.DuplicateFrom, spec.Style);
                    if (sources.Count == 0)
                    {
                        problems.Add($"{spec.Source}.duplicateFrom: there is no {label} '{spec.DuplicateFrom}'" +
                                     (spec.Style != null ? $" of style {spec.Style}" : string.Empty) +
                                     AnnotationTypeSpecParser.DidYouMean(spec.DuplicateFrom, types.Select(t => t.Name)));
                        continue;
                    }
                    if (sources.Count > 1)
                    {
                        problems.Add($"{spec.Source}.duplicateFrom: '{spec.DuplicateFrom}' is a {label} of several styles ({string.Join(", ", sources.Select(kind.StyleOf))}); give style.");
                        continue;
                    }
                    planned.Source = sources[0];
                }
                else
                {
                    planned.Source = kind.DefaultType(doc, spec.Style);
                    if (planned.Source == null)
                    {
                        problems.Add($"{spec.Source}: '{doc.Title}' has no {label}" +
                                     (spec.Style != null ? $" of style {spec.Style}" : string.Empty) + " to duplicate; give duplicateFrom.");
                        continue;
                    }
                }
                if (!taken.Add(kind.NameKey(spec.Name, kind.StyleOf(planned.Source))))
                {
                    problems.Add($"{spec.Source}: another item already creates or renames to '{spec.Name}'.");
                    continue;
                }
            }

            var source = planned.Source!;
            planned.Style = kind.StyleOf(source);

            if (spec.NewName != null && planned.Existing != null && !string.Equals(spec.NewName, planned.Existing.Name, StringComparison.Ordinal))
            {
                var caseOnly = string.Equals(spec.NewName, planned.Existing.Name, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && !taken.Add(kind.NameKey(spec.NewName, planned.Style)))
                {
                    problems.Add($"{spec.Source}.newName: a {label} '{spec.NewName}'{(kind.IsDimension ? $" of style {planned.Style}" : string.Empty)} already exists.");
                    continue;
                }
            }

            var current = AnnotationTypeReader.Read(doc, source, kind);
            var ok = true;
            foreach (var field in kind.Fields.Where(f => spec.Values.ContainsKey(f.Key)))
            {
                var value = spec.Values[field.Key];
                if (field.Kind == AnnotationFieldKind.UnitsFormat)
                {
                    if (source is not DimensionType dimension)
                    {
                        problems.Add($"{spec.Source}.unitsFormat: '{source.Name}' has no units format.");
                        ok = false;
                        continue;
                    }
                    var merged = AnnotationTypeSpecParser.MergeUnits((UnitsFormatSpec)value, current[field.Key] as UnitsFormatValue);
                    var units = BuildUnits(doc, dimension, merged, planned.Style ?? "Linear", out var error);
                    if (units == null)
                    {
                        problems.Add($"{spec.Source}.unitsFormat: {error}");
                        ok = false;
                        continue;
                    }
                    planned.Units = units;
                    continue;
                }

                var parameter = AnnotationTypeReader.Find(source, kind.Parameters, field.Key);
                if (parameter == null)
                {
                    problems.Add($"{spec.Source}.{field.Key}: the {label} '{source.Name}'{(planned.Style != null ? $" ({planned.Style})" : string.Empty)} has no such parameter.");
                    ok = false;
                    continue;
                }
                if (parameter.IsReadOnly || parameter.StorageType != AnnotationTypeReader.Expected(field.Kind))
                {
                    problems.Add($"{spec.Source}.{field.Key}: Revit does not let '{parameter.Definition?.Name}' of '{source.Name}' be set here.");
                    ok = false;
                    continue;
                }
                if (field.Kind == AnnotationFieldKind.Arrowhead)
                {
                    var name = (string)value;
                    if (name.Length == 0)
                    {
                        planned.ArrowheadIds[field.Key] = ElementId.InvalidElementId;
                        continue;
                    }
                    arrowheads ??= AnnotationTypeReader.Arrowheads(doc);
                    if (arrowheads.TryGetValue(name, out var arrowhead)) planned.ArrowheadIds[field.Key] = arrowhead.Id;
                    else
                    {
                        problems.Add($"{spec.Source}.{field.Key}: there is no arrowhead type '{name}' (or none)" +
                                     AnnotationTypeSpecParser.DidYouMean(name, arrowheads.Keys));
                        ok = false;
                    }
                }
            }
            if (!ok) continue;

            planned.Changes = AnnotationTypeSpecParser.Changes(spec, kind.Fields, current);
            if (planned.Units != null)
                planned.Changes = planned.Changes.Select(c => c.Field == "unitsFormat"
                    ? c with { To = AnnotationTypeReader.Units(planned.Units).ToString() }
                    : c).ToList();
            if (planned.Existing != null && spec.NewName != null && !string.Equals(spec.NewName, planned.Existing.Name, StringComparison.Ordinal))
                planned.Changes.Insert(0, new AnnotationFieldChange("name", planned.Existing.Name, spec.NewName));
            if (planned.Existing != null) planned.Action = planned.Changes.Count > 0 ? "edit" : "unchanged";
        }

        object Describe(Planned p, string? action = null) => new
        {
            name = p.Existing?.Name ?? p.Spec.Name,
            id = p.ResultId ?? p.Existing?.Id.Value,
            style = p.Style,
            action = action ?? p.Action,
            duplicateFrom = p.Existing == null ? p.Source?.Name : null,
            usage = p.Existing != null ? p.Usage : (int?)null,
            changes = p.Changes.Count > 0 ? p.Changes.Select(c => new { field = c.Field, from = c.From, to = c.To }).ToList() : null
        };

        var creates = plan.Count(p => p.Action == "create");
        var edits = plan.Count(p => p.Action == "edit");
        var affected = plan.Where(p => p.Action == "edit").Sum(p => p.Usage);
        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = problems.Count == 0
                    ? $"Preview: {creates} {kind.Label}(s) to create, {edits} to edit ({affected} {kind.UsageNoun}(s) use them), " +
                      $"{plan.Count - creates - edits} unchanged in '{doc.Title}'."
                    : $"Preview: {problems.Count} problem(s) to fix before changing {kind.Label}s.",
                Data = new
                {
                    ready = problems.Count == 0,
                    problems,
                    document = doc.Title,
                    isFamilyDocument = doc.IsFamilyDocument,
                    affectedElements = affected,
                    types = plan.Select(p => Describe(p)).ToList()
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
            using var tx = new Transaction(doc, $"Revit MCP - {(kind.IsDimension ? "Dimension" : "Text")} types");
            tx.Start();
            foreach (var planned in plan.Where(p => p.Action != "unchanged"))
            {
                var spec = planned.Spec;
                try
                {
                    var type = planned.Existing ?? planned.Source!.Duplicate(spec.Name);
                    if (planned.Existing != null && spec.NewName != null && !string.Equals(type.Name, spec.NewName, StringComparison.Ordinal))
                        type.Name = spec.NewName;
                    foreach (var field in kind.Fields.Where(f => spec.Values.ContainsKey(f.Key)))
                    {
                        if (field.Kind == AnnotationFieldKind.UnitsFormat)
                        {
                            ((DimensionType)type).SetUnitsFormatOptions(planned.Units!);
                            continue;
                        }
                        var parameter = AnnotationTypeReader.Find(type, kind.Parameters, field.Key)
                                        ?? throw new InvalidOperationException($"the new type has no {field.Key} parameter");
                        if (!Set(parameter, field.Kind, spec.Values[field.Key], planned.ArrowheadIds.TryGetValue(field.Key, out var id) ? id : null))
                            throw new InvalidOperationException($"Revit did not take {field.Key}");
                    }
                    planned.ResultId = type.Id.Value;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return new McpToolResult
                    {
                        RequestId = request.RequestId,
                        Success = false,
                        Status = "transaction_failed",
                        Message = $"Nothing was changed: Revit refused {kind.Label} '{spec.Name}' ({ex.Message}).",
                        Errors = [$"{spec.Source} ('{spec.Name}'): {ex.Message}"],
                        Warnings = warnings,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (Exception ex)
        {
            return Fail(request, $"Changing the {kind.Label}s failed and nothing was changed: {ex.Message}", warnings);
        }

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{creates} {kind.Label}(s) created, {edits} edited ({affected} {kind.UsageNoun}(s) use them), " +
                      $"{plan.Count - creates - edits} unchanged in '{doc.Title}'. One Undo reverts it.",
            Data = new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                created = creates,
                edited = edits,
                affectedElements = affected,
                types = plan.Select(p => Describe(p, p.Action switch { "create" => "created", "edit" => "edited", _ => "unchanged" })).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>Types of this name (without regard to case) and, when given, of this dimension style.</summary>
    private static List<ElementType> Match(AnnotationTypeKind kind, List<ElementType> types, string name, string? style) =>
        types.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                         && (style == null || (kind.StyleOf(t) is { } s && AnnotationTypeSpecParser.SameStyle(s, style))))
            .ToList();

    private static bool Set(Parameter parameter, AnnotationFieldKind kind, object value, ElementId? arrowheadId) => kind switch
    {
        AnnotationFieldKind.Text => parameter.Set((string)value),
        AnnotationFieldKind.Length => parameter.Set(AnnotationTypeSpecParser.MmToFeet((double)value)),
        AnnotationFieldKind.Factor => parameter.Set((double)value),
        AnnotationFieldKind.Bool => parameter.Set((bool)value ? 1 : 0),
        AnnotationFieldKind.Colour => parameter.Set(AnnotationTypeSpecParser.ColourToRevit((string)value)),
        AnnotationFieldKind.LineWeight => parameter.Set((int)value),
        AnnotationFieldKind.Background => parameter.Set(AnnotationTypeSpecParser.BackgroundToRevit((string)value)),
        AnnotationFieldKind.Arrowhead => parameter.Set(arrowheadId ?? ElementId.InvalidElementId),
        _ => false
    };

    /// <summary>
    /// The FormatOptions a dimension type gets, built and checked outside any transaction: project settings, or the
    /// unit and accuracy (each falling back to the type's current one, then the project's). Null with an error when
    /// Revit refuses the unit or accuracy for the style.
    /// </summary>
    private static FormatOptions? BuildUnits(Document doc, DimensionType type, UnitsFormatValue to, string style, out string? error)
    {
        error = null;
        try
        {
            var options = type.GetUnitsFormatOptions();
            if (to.UseProjectSettings)
            {
                options.UseDefault = true;
                return options;
            }

            var normalized = AnnotationTypeSpecParser.NormalizeStyle(style);
            var spec = normalized == "angular" ? SpecTypeId.Angle : normalized == "spotslope" ? SpecTypeId.Slope : SpecTypeId.Length;
            var basis = options.UseDefault ? doc.GetUnits().GetFormatOptions(spec) : options;
            basis.UseDefault = false;
            if (to.Unit != null)
            {
                var unitId = UnitId(to.Unit);
                if (unitId == null || !UnitUtils.IsValidUnit(spec, unitId))
                {
                    error = $"the unit {to.Unit} does not fit a {style} dimension.";
                    return null;
                }
                if (basis.GetUnitTypeId() != unitId)
                {
                    basis.SetSymbolTypeId(new ForgeTypeId());
                    basis.SetUnitTypeId(unitId);
                }
            }
            if (to.Accuracy != null)
            {
                if (!FormatOptions.IsValidAccuracy(basis.GetUnitTypeId(), to.Accuracy.Value))
                {
                    error = $"Revit does not take accuracy {to.Accuracy.Value} for {AnnotationTypeSpecParser.UnitNameFromTypeId(basis.GetUnitTypeId().TypeId)}.";
                    return null;
                }
                basis.Accuracy = to.Accuracy.Value;
            }
            return basis;
        }
        catch (Exception ex)
        {
            error = $"Revit refused the format ({ex.Message}).";
            return null;
        }
    }

    private static ForgeTypeId? UnitId(string unit) => unit switch
    {
        "millimeters" => UnitTypeId.Millimeters,
        "centimeters" => UnitTypeId.Centimeters,
        "decimeters" => UnitTypeId.Decimeters,
        "meters" => UnitTypeId.Meters,
        "feet" => UnitTypeId.Feet,
        "inches" => UnitTypeId.Inches,
        "degrees" => UnitTypeId.Degrees,
        _ => null
    };

    public static McpToolResult Fail(McpToolRequest request, string message, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Message = message,
        Errors = [message],
        Warnings = warnings ?? new List<string>()
    };
}
