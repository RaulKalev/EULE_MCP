using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class SetParameterTool : IRevitMcpTool
{
    public string Name => "revit_set_parameter";
    public string Description => "Sets a parameter value on elements. Requires approval. Supports String, Integer, Double, and ElementId storage types. ElementId values can be provided as a numeric element ID or exact element/type name. Runs inside a Revit Transaction.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Parameters;

    private readonly CategoryResolver _categoryResolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var doc = uidoc.Document;
        var parameterName = ToolArguments.GetString(request.Arguments, "parameterName");
        var value = ToolArguments.GetString(request.Arguments, "value");
        var scope = ToolArguments.GetString(request.Arguments, "scope", "Instance");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var category = ToolArguments.GetString(request.Arguments, "category");
        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        if (string.IsNullOrWhiteSpace(parameterName))
            return Task.FromResult(Fail(request, "parameterName is required."));

        // Determine target elements
        IEnumerable<ElementId> sourceIds;

        if (useSelection)
        {
            sourceIds = uidoc.Selection.GetElementIds();
        }
        else if (elementIds.Length > 0)
        {
            sourceIds = elementIds.Select(id => new ElementId(id));
        }
        else if (!string.IsNullOrWhiteSpace(category))
        {
            var resolve = _categoryResolver.Resolve(doc, category);
            if (resolve.Category == null)
            {
                var sug = resolve.Suggestions.Count > 0
                    ? $" Did you mean: {string.Join(", ", resolve.Suggestions)}?"
                    : string.Empty;
                return Task.FromResult(Fail(request, resolve.Message + sug));
            }

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategoryId(resolve.Category.Id)
                .ToElementIds();

            // Apply filters if provided
            if (filtersParsed.Items.Count > 0)
            {
                var reader = new ParameterReader();
                var readOpts = new ParameterReadOptions
                {
                    IncludeInstanceParameters = true,
                    IncludeTypeParameters = true
                };

                var filtered = new List<ElementId>();
                foreach (var eid in collector)
                {
                    if (filtered.Count >= limit) break;
                    var element = doc.GetElement(eid);
                    if (element?.Category == null) continue;

                    var allParams = reader.ReadParameters(doc, element, readOpts);
                    if (PassesFilters(allParams, filtersParsed.Items))
                        filtered.Add(eid);
                }
                sourceIds = filtered;
            }
            else
            {
                sourceIds = collector.Take(limit);
            }
        }
        else
        {
            return Task.FromResult(Fail(request, "Provide useSelection=true, elementIds, or category."));
        }

        var targetIds = sourceIds.Take(limit).ToList();
        if (targetIds.Count == 0)
            return Task.FromResult(Fail(request, "No target elements found."));

        // Run in transaction
        var modifiedIds = new List<long>();
        var failures = new List<object>();

        using (var tx = new Transaction(doc, "Revit MCP - Set Parameter"))
        {
            tx.Start();

            foreach (var eid in targetIds)
            {
                var element = doc.GetElement(eid);
                if (element == null)
                {
                    failures.Add(new { elementId = eid.Value, reason = "Element not found." });
                    continue;
                }

                // Find the parameter
                Parameter? param = null;
                foreach (Parameter p in element.Parameters)
                {
                    if (ParameterMatcher.Matches(p.Definition?.Name ?? "", parameterName, "ContainsNormalized"))
                    {
                        // Respect scope filter
                        if (scope == "Type" && element is not ElementType) continue;
                        if (scope == "Instance" && element is ElementType) continue;
                        param = p;
                        break;
                    }
                }

                // Also check type parameters if scope allows
                if (param == null && scope != "Instance")
                {
                    var typeId = element.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                    {
                        var typeElem = doc.GetElement(typeId);
                        if (typeElem != null)
                        {
                            foreach (Parameter p in typeElem.Parameters)
                            {
                                if (ParameterMatcher.Matches(p.Definition?.Name ?? "", parameterName, "ContainsNormalized"))
                                {
                                    param = p;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (param == null)
                {
                    failures.Add(new { elementId = eid.Value, reason = $"Parameter '{parameterName}' not found." });
                    continue;
                }

                if (param.IsReadOnly)
                {
                    failures.Add(new { elementId = eid.Value, reason = "Parameter is read-only." });
                    continue;
                }

                try
                {
                    bool set = param.StorageType == StorageType.ElementId
                        ? SetElementId(doc, param, value)
                        : param.StorageType switch
                        {
                            StorageType.String => SetString(param, value),
                            StorageType.Integer => SetInteger(param, value),
                            StorageType.Double => SetDouble(param, value),
                            _ => false
                        };

                    if (set)
                        modifiedIds.Add(eid.Value);
                    else
                        failures.Add(new { elementId = eid.Value, reason = $"Unsupported storage type: {param.StorageType}" });
                }
                catch (Exception ex)
                {
                    failures.Add(new { elementId = eid.Value, reason = ex.Message });
                }
            }

            tx.Commit();
        }

        var warnings = filtersParsed.Warnings;

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Updated parameter '{parameterName}' on {modifiedIds.Count} elements. {failures.Count} failed.",
            Data = new
            {
                parameterName,
                value,
                modifiedCount = modifiedIds.Count,
                failedCount = failures.Count,
                modifiedElementIds = modifiedIds,
                failures
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static bool SetString(Parameter p, string value)
    {
        p.Set(value);
        return true;
    }

    private static bool SetInteger(Parameter p, string value)
    {
        if (!int.TryParse(value, out var intVal)) return false;
        p.Set(intVal);
        return true;
    }

    private static bool SetDouble(Parameter p, string value)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var dblVal)) return false;
        p.Set(dblVal);
        return true;
    }

    private static bool SetElementId(Document doc, Parameter p, string value)
    {
        // 1. Direct numeric element ID
        if (long.TryParse(value, out var numId))
        {
            var elemById = doc.GetElement(new ElementId(numId));
            if (elemById == null) return false;
            p.Set(new ElementId(numId));
            return true;
        }

        // 2. Name search — types first (wire types, cable types, etc.), then instances
        var typeMatch = new FilteredElementCollector(doc)
            .WhereElementIsElementType()
            .FirstOrDefault(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase));

        if (typeMatch != null) { p.Set(typeMatch.Id); return true; }

        var instMatch = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .FirstOrDefault(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase));

        if (instMatch != null) { p.Set(instMatch.Id); return true; }

        return false;
    }

    private static bool PassesFilters(IReadOnlyList<ParameterValueDto> parameters, List<ParameterFilterDto> filters)
    {
        foreach (var filter in filters)
        {
            var candidates = parameters.Where(p =>
                ParameterMatcher.Matches(p.Name, filter.ParameterName, filter.MatchMode)).ToList();

            if (candidates.Count == 0)
            {
                if (filter.Operator == "isEmpty") continue;
                return false;
            }

            if (!candidates.Any(p => EvaluateOperator(p.Value, filter.Operator, filter.Value)))
                return false;
        }
        return true;
    }

    private static bool EvaluateOperator(string value, string op, string filterValue) =>
        op switch
        {
            "equals" => string.Equals(value, filterValue, StringComparison.OrdinalIgnoreCase),
            "contains" => value.Contains(filterValue, StringComparison.OrdinalIgnoreCase),
            "isEmpty" => string.IsNullOrEmpty(value),
            "isNotEmpty" => !string.IsNullOrEmpty(value),
            _ => false
        };

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
