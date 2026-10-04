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
    public string Description => "Sets a parameter value on elements. Requires approval. Supports String, Integer, Double, and ElementId storage types. ElementId values can be provided as a numeric element ID or exact element/type name. Resolves the parameter by exact name first and never guesses between ambiguous matches (use exactMatch, builtInParameter or parameterGuid). Runs inside a Revit Transaction.";
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

        var selector = new ParameterTarget
        {
            Name = parameterName,
            ExactMatch = ToolArguments.GetBool(request.Arguments, "exactMatch"),
            BuiltInParameter = NullIfBlank(ToolArguments.GetString(request.Arguments, "builtInParameter")),
            Guid = NullIfBlank(ToolArguments.GetString(request.Arguments, "parameterGuid"))
        };

        if (string.IsNullOrWhiteSpace(parameterName) && selector.BuiltInParameter == null && selector.Guid == null)
            return Task.FromResult(Fail(request, "parameterName is required (or builtInParameter / parameterGuid)."));
        if (selector.BuiltInParameter != null && !Enum.TryParse<BuiltInParameter>(selector.BuiltInParameter, true, out _))
            return Task.FromResult(Fail(request, $"Unknown builtInParameter '{selector.BuiltInParameter}'. Use the BuiltInParameter enum name, e.g. INSTANCE_ELEVATION_PARAM."));
        if (selector.Guid != null && !System.Guid.TryParse(selector.Guid, out _))
            return Task.FromResult(Fail(request, $"parameterGuid '{selector.Guid}' is not a valid GUID."));

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
                    // Shared evaluator: also understands Type / Family names (#88).
                    if (ParameterFilterEvaluator.Passes(allParams, filtersParsed.Items, ParameterReader.ReadIdentity(doc, element)))
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
        var changes = new List<object>();
        var failures = new List<object>();
        var partialMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writtenParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();
        using (var tx = new Transaction(doc, "Revit MCP - Set Parameter"))
        {
            tx.Start();

            foreach (var eid in targetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var element = doc.GetElement(eid);
                if (element == null)
                {
                    failures.Add(new { elementId = eid.Value, reason = "Element not found." });
                    continue;
                }

                // Resolve the parameter: exact name before partial, user/shared before built-in,
                // and never guess between ties (#86).
                var isTypeElement = element is ElementType;
                var resolution = ParameterResolver.Resolve(
                    doc, element, selector,
                    includeInstance: scope != "Type" || isTypeElement,
                    includeType: scope != "Instance" && !isTypeElement);
                var param = ParameterResolver.Selected(resolution);

                if (param == null)
                {
                    failures.Add(new
                    {
                        elementId = eid.Value,
                        reason = resolution.Problem ?? $"Parameter {selector.Describe()} not found.",
                        candidates = resolution.IsAmbiguous
                            ? resolution.Candidates.Select(ParameterResolution.DescribeCandidate).ToList()
                            : null
                    });
                    continue;
                }

                if (param.IsReadOnly)
                {
                    failures.Add(new { elementId = eid.Value, reason = $"Parameter '{param.Definition?.Name}' is read-only." });
                    continue;
                }

                if (resolution.MatchedBy == "partial")
                    partialMatches.Add(param.Definition?.Name ?? string.Empty);

                try
                {
                    var oldValue = ParameterResolver.DisplayValue(param);
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
                    {
                        modifiedIds.Add(eid.Value);
                        writtenParameters.Add(param.Definition?.Name ?? "?");
                        changes.Add(new
                        {
                            elementId = eid.Value,
                            parameter = param.Definition?.Name,
                            builtInParameter = resolution.Selected!.BuiltInParameter,
                            isTypeParameter = resolution.Selected.IsTypeParameter,
                            storageType = param.StorageType.ToString(),
                            oldValue,
                            newValue = ParameterResolver.DisplayValue(param)
                        });
                    }
                    else
                        failures.Add(new { elementId = eid.Value, reason = $"Unsupported storage type: {param.StorageType}" });
                }
                catch (Exception ex)
                {
                    failures.Add(new { elementId = eid.Value, reason = ex.Message });
                }
            }

            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(tx);
        }

        var warnings = filtersParsed.Warnings;
        if (partialMatches.Count > 0)
            warnings.Add($"{selector.Describe()} was resolved by partial name match to: {string.Join(", ", partialMatches.Select(n => $"'{n}'"))}. " +
                         "Check the changes list; pass the exact name with exactMatch=true to avoid partial matching.");

        var writtenNames = writtenParameters.Count == 0
            ? selector.Describe()
            : string.Join(", ", writtenParameters.Select(n => $"'{n}'"));

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Updated parameter {writtenNames} on {modifiedIds.Count} elements. {failures.Count} failed.",
            Data = new
            {
                parameterName,
                value,
                modifiedCount = modifiedIds.Count,
                failedCount = failures.Count,
                modifiedElementIds = modifiedIds,
                changes,
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

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
