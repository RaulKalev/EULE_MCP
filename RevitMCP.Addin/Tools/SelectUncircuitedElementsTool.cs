using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class SelectUncircuitedElementsTool : IRevitMcpTool
{
    public string Name => "revit_select_uncircuited_elements";
    public string Description => "Selects elements not assigned to any electrical circuit in the Revit UI. Requires approval. Accepts: categories (string[]), filters (JSON array), replaceSelection (bool, default true), zoomToSelection (bool, default false), limit (default 500).";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly string[] DefaultCategories =
    [
        "Electrical Fixtures", "Lighting Fixtures", "Electrical Equipment",
        "Data Devices", "Fire Alarm Devices", "Security Devices", "Communication Devices"
    ];

    private readonly CategoryResolver _categoryResolver = new();
    private readonly ParameterReader _paramReader = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var categories = ToolArguments.GetStringArray(request.Arguments, "categories");
        if (categories.Length == 0) categories = DefaultCategories;
        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var replaceSelection = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoomToSelection = ToolArguments.GetBool(request.Arguments, "zoomToSelection");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        var uncircuitedIds = new List<ElementId>();
        var warnings = new List<string>(filtersParsed.Warnings);

        var circuitedElemIds = new HashSet<long>(
            new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(c => c.Elements != null)
                .SelectMany(c => c.Elements.Cast<Element>().Select(e => e.Id.Value))
        );

        foreach (var catName in categories)
        {
            if (uncircuitedIds.Count >= limit) break;
            var resolve = _categoryResolver.Resolve(doc, catName);
            if (resolve.Category == null) { warnings.Add($"Category '{catName}' not found — skipped."); continue; }

            foreach (var eid in new FilteredElementCollector(doc)
                .WhereElementIsNotElementType().OfCategoryId(resolve.Category.Id).ToElementIds())
            {
                if (uncircuitedIds.Count >= limit) break;
                var element = doc.GetElement(eid);
                if (element is not FamilyInstance fi || fi.MEPModel == null) continue;
                if (circuitedElemIds.Contains(fi.Id.Value)) continue;

                if (filtersParsed.Items.Count > 0)
                {
                    var allParams = _paramReader.ReadParameters(doc, element,
                        new ParameterReadOptions { IncludeInstanceParameters = true, IncludeTypeParameters = true });
                    if (!PassesFilters(allParams, filtersParsed.Items)) continue;
                }
                uncircuitedIds.Add(eid);
            }
        }

        if (uncircuitedIds.Count == 0)
        {
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = "No uncircuited elements found — nothing to select.",
                Data = new { selectedCount = 0 },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var existing = replaceSelection ? new List<ElementId>() : uidoc.Selection.GetElementIds().ToList();
        var merged = existing.Union(uncircuitedIds).ToList();
        uidoc.Selection.SetElementIds(merged);
        if (zoomToSelection) try { uidoc.ShowElements(merged); } catch { }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Selected {uncircuitedIds.Count} uncircuited element(s).",
            Data = new { selectedCount = uncircuitedIds.Count, selectedElementIds = uncircuitedIds.Select(e => e.Value).ToList() },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static bool PassesFilters(IReadOnlyList<ParameterValueDto> parameters, List<ParameterFilterDto> filters)
    {
        foreach (var filter in filters)
        {
            var matches = parameters.Where(p => ParameterMatcher.Matches(p.Name, filter.ParameterName, filter.MatchMode)).ToList();
            if (matches.Count == 0) { if (filter.Operator == "isEmpty") continue; return false; }
            if (!matches.Any(p => EvalOp(p.Value, filter.Operator, filter.Value))) return false;
        }
        return true;
    }

    private static bool EvalOp(string v, string op, string fv) => op switch
    {
        "equals" => string.Equals(v, fv, StringComparison.OrdinalIgnoreCase),
        "contains" => v.Contains(fv, StringComparison.OrdinalIgnoreCase),
        "isEmpty" => string.IsNullOrEmpty(v),
        "isNotEmpty" => !string.IsNullOrEmpty(v),
        _ => false
    };

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
