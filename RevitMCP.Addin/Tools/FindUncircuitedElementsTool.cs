using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class FindUncircuitedElementsTool : IRevitMcpTool
{
    public string Name => "revit_find_uncircuited_elements";
    public string Description => "Finds FamilyInstance elements in electrical/lighting/data/fire/security categories that are not assigned to any electrical circuit. Checks via MEPModel.ElectricalSystems. Accepts: categories (string[]), useSelection (bool), filters (JSON array), returnParameters (string[]), limit (int).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly string[] DefaultCategories =
    [
        "Electrical Fixtures",
        "Lighting Fixtures",
        "Electrical Equipment",
        "Data Devices",
        "Fire Alarm Devices",
        "Security Devices",
        "Communication Devices"
    ];

    private readonly CategoryResolver _categoryResolver = new();
    private readonly ParameterReader _paramReader = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var categories = ToolArguments.GetStringArray(request.Arguments, "categories");
        if (categories.Length == 0) categories = DefaultCategories;
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var returnParams = ToolArguments.GetStringArray(request.Arguments, "returnParameters");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 1000);

        var uncircuited = new List<object>();
        var warnings = new List<string>(filtersParsed.Warnings);
        var skippedNoMep = 0;

        var circuitedElemIds = new HashSet<long>(
            new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(c => c.Elements != null)
                .SelectMany(c => c.Elements.Cast<Element>().Select(e => e.Id.Value))
        );

        IEnumerable<Element> candidates;
        if (useSelection)
        {
            candidates = uidoc.Selection.GetElementIds()
                .Select(eid => doc.GetElement(eid))
                .Where(e => e != null)!;
        }
        else
        {
            var allElements = new List<Element>();
            foreach (var catName in categories)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var resolve = _categoryResolver.Resolve(doc, catName);
                if (resolve.Category == null)
                {
                    warnings.Add($"Category '{catName}' not found — skipped.");
                    continue;
                }
                var collected = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .OfCategoryId(resolve.Category.Id)
                    .Cast<Element>()
                    .ToList();
                allElements.AddRange(collected);
            }
            candidates = allElements;
        }

        foreach (var element in candidates)
        {
            if (uncircuited.Count >= limit) break;
            if (cancellationToken.IsCancellationRequested) break;
            if (element == null) continue;

            if (element is not FamilyInstance fi || fi.MEPModel == null)
            {
                skippedNoMep++;
                continue;
            }

            if (circuitedElemIds.Contains(fi.Id.Value)) continue;

            // Apply parameter filters
            if (filtersParsed.Items.Count > 0)
            {
                var readOpts = new ParameterReadOptions { IncludeInstanceParameters = true, IncludeTypeParameters = true };
                var allParams = _paramReader.ReadParameters(doc, element, readOpts);
                if (!PassesFilters(allParams, filtersParsed.Items)) continue;
            }

            uncircuited.Add(BuildDto(doc, element, returnParams));
        }

        if (skippedNoMep > 0)
            warnings.Add($"{skippedNoMep} element(s) skipped — not a FamilyInstance or no MEP model.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {uncircuited.Count} uncircuited element(s).",
            Data = new
            {
                uncircuitedCount = uncircuited.Count,
                categoriesChecked = useSelection ? new[] { "(selection)" } : categories,
                elements = uncircuited
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private object BuildDto(Document doc, Element element, string[] returnParams)
    {
        var typeId = element.GetTypeId();
        var typeElem = typeId != null && typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
        string level = "";
        try
        {
            var lvl = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                   ?? element.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
            if (lvl != null) level = doc.GetElement(lvl.AsElementId())?.Name ?? "";
        }
        catch { }

        Dictionary<string, string>? parameters = null;
        if (returnParams.Length > 0)
        {
            parameters = new Dictionary<string, string>();
            var readOpts = new ParameterReadOptions { IncludeInstanceParameters = true, IncludeTypeParameters = true };
            foreach (var p in _paramReader.ReadParameters(doc, element, readOpts))
            {
                if (returnParams.Any(rp => ParameterMatcher.Matches(p.Name, rp, "ContainsNormalized")))
                    parameters[p.Name] = p.Value;
            }
        }

        return new
        {
            elementId = element.Id.Value,
            category = element.Category?.Name ?? "",
            family = element is FamilyInstance fi2 ? fi2.Symbol?.Family?.Name ?? "" : "",
            type = typeElem?.Name ?? element.Name,
            level,
            parameters
        };
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
