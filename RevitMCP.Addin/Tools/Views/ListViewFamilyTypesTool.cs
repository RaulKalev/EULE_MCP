using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Views;

/// <summary>revit_list_view_family_types (#104): the ViewFamilyTypes revit_create_views can use, per ViewFamily.</summary>
public sealed class ListViewFamilyTypesTool : IRevitMcpTool
{
    public string Name => "revit_list_view_family_types";

    public string Description =>
        "Lists the view family types of the document per ViewFamily (FloorPlan, CeilingPlan, StructuralPlan, AreaPlan, " +
        "Section, Detail, Elevation, ThreeDimensional, Drafting, Legend, …): id, name, viewFamily, defaultTemplateId and " +
        "defaultTemplateName (the template new views of the type get), isDocumentDefault, and the revit_create_views kinds " +
        "that use it. Optional viewFamily filter (a ViewFamily name or a kind such as floorPlan, section, threeD). Also " +
        "returns the area schemes (for kind areaPlan) and the legends (sourceLegend for kind legend).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var filterText = ToolArguments.GetString(request.Arguments, "viewFamily").Trim();
        ViewFamily? filter = null;
        if (filterText.Length > 0)
        {
            var kind = ViewKinds.Normalize(filterText);
            if (Enum.TryParse<ViewFamily>(filterText, true, out var parsed) && parsed != ViewFamily.Invalid) filter = parsed;
            else if (kind == ViewKinds.Callout) filter = ViewFamily.Detail;
            else if (kind != null) filter = ViewReferenceResolver.FamilyForKind(kind);
            if (filter == null)
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "validation_failed",
                    Message = $"Unknown viewFamily '{filterText}'. Use a ViewFamily ({string.Join(", ", Enum.GetNames(typeof(ViewFamily)).Where(n => n != "Invalid"))}) or a kind ({string.Join(", ", ViewKinds.All)})."
                });
        }

        var types = ViewFamilyTypeResolver.OfFamily(doc, filter)
            .OrderBy(t => t.ViewFamily.ToString(), StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var defaults = new Dictionary<ViewFamily, long>();
        foreach (var family in types.Select(t => t.ViewFamily).Distinct())
            if (ViewFamilyTypeResolver.DocumentDefault(doc, family) is { } preferred)
                defaults[family] = preferred.Id.Value;

        var rows = types.Select(t =>
        {
            var templateId = SafeTemplateId(t);
            var template = templateId != null ? doc.GetElement(templateId) as View : null;
            return new
            {
                id = t.Id.Value,
                name = t.Name,
                viewFamily = t.ViewFamily.ToString(),
                defaultTemplateId = template != null ? template.Id.Value : (long?)null,
                defaultTemplateName = template?.Name,
                isDocumentDefault = defaults.TryGetValue(t.ViewFamily, out var id) && id == t.Id.Value,
                kinds = KindsFor(t.ViewFamily)
            };
        }).ToList();

        var areaSchemes = filter is null or ViewFamily.AreaPlan
            ? new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).ToElements().Select(s => new { id = s.Id.Value, name = s.Name }).ToList()
            : null;
        var legends = filter is null or ViewFamily.Legend
            ? new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend)
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Select(v => new { id = v.Id.Value, name = v.Name }).ToList()
            : null;

        sw.Stop();
        var warnings = new List<string>();
        if (legends is { Count: 0 })
            warnings.Add("The model has no legend: Revit's API cannot create one, so kind=legend needs a legend made by hand first.");
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {rows.Count} view family type(s){(filter != null ? $" of family {filter}" : string.Empty)}.",
            Data = new { count = rows.Count, viewFamilyTypes = rows, areaSchemes, legends },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static ElementId? SafeTemplateId(ViewFamilyType type)
    {
        try
        {
            var id = type.DefaultTemplateId;
            return id == null || id == ElementId.InvalidElementId ? null : id;
        }
        catch
        {
            return null;
        }
    }

    private static string[] KindsFor(ViewFamily family) => family switch
    {
        ViewFamily.FloorPlan => new[] { ViewKinds.FloorPlan, ViewKinds.Callout },
        ViewFamily.CeilingPlan => new[] { ViewKinds.CeilingPlan, ViewKinds.Callout },
        ViewFamily.StructuralPlan => new[] { ViewKinds.StructuralPlan, ViewKinds.Callout },
        ViewFamily.Section => new[] { ViewKinds.Section, ViewKinds.Callout },
        ViewFamily.Elevation => new[] { ViewKinds.Callout },
        ViewFamily.Detail => new[] { ViewKinds.Callout },
        ViewFamily.ThreeDimensional => new[] { ViewKinds.ThreeD },
        ViewFamily.Drafting => new[] { ViewKinds.Drafting },
        _ => Array.Empty<string>()
    };
}
