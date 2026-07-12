using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Sets a parameter value on one or more electrical circuits, with full support for
/// all storage types including ElementId (needed for Cable Type and similar references).
/// The value argument accepts either a numeric element ID (as string) or an element name.
/// </summary>
public class SetCircuitParameterTool : IRevitMcpTool
{
    public string Name => "revit_set_circuit_parameter";
    public string Description =>
        "Sets a parameter value on one or more electrical circuits. Handles all storage types " +
        "including ElementId (e.g. Cable Type). 'value' can be a numeric element ID or an exact " +
        "element name. Requires approval. Transaction-wrapped.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var circuitIds = ToolArguments.GetLongArray(request.Arguments, "circuitIds");
        var parameterName = ToolArguments.GetString(request.Arguments, "parameterName");
        var value = ToolArguments.GetString(request.Arguments, "value");

        if (circuitIds.Length == 0)
            return Task.FromResult(Fail(request, "circuitIds is required (array of element IDs)."));
        if (string.IsNullOrWhiteSpace(parameterName))
            return Task.FromResult(Fail(request, "parameterName is required."));

        var successes = new List<object>();
        var failures = new List<object>();
        var warnings = new List<string>();

        cancellationToken.ThrowIfCancellationRequested();
        using var tx = new Transaction(doc, "Revit MCP - Set Circuit Parameter");
        tx.Start();

        foreach (var cid in circuitIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var elem = doc.GetElement(new ElementId(cid));
            if (elem == null)
            {
                failures.Add(new { circuitId = cid, reason = "Element not found." });
                continue;
            }
            if (elem is not ElectricalSystem)
            {
                failures.Add(new { circuitId = cid, reason = $"Element {cid} is not an electrical circuit (ElectricalSystem)." });
                continue;
            }

            var param = FindParameter(elem, parameterName);
            if (param == null)
            {
                failures.Add(new { circuitId = cid, reason = $"Parameter '{parameterName}' not found on circuit." });
                continue;
            }
            if (param.IsReadOnly)
            {
                failures.Add(new { circuitId = cid, reason = $"Parameter '{param.Definition.Name}' is read-only." });
                continue;
            }

            try
            {
                var setError = SetParameterValue(doc, param, value, warnings);
                if (setError == null)
                    successes.Add(new { circuitId = cid, parameter = param.Definition.Name, value, storageType = param.StorageType.ToString() });
                else
                    failures.Add(new { circuitId = cid, reason = setError });
            }
            catch (Exception ex)
            {
                failures.Add(new { circuitId = cid, reason = ex.Message });
            }
        }

        if (successes.Count > 0)
            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(tx);
        else
            tx.RollBack();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = successes.Count > 0,
            Message = $"Set '{parameterName}' on {successes.Count} circuit(s). {failures.Count} failed.",
            Data = new
            {
                parameterName,
                value,
                successCount = successes.Count,
                failureCount = failures.Count,
                successes,
                failures
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// Finds a parameter on the element using ContainsNormalized name matching.
    /// </summary>
    private static Parameter? FindParameter(Element element, string parameterName)
    {
        foreach (Parameter p in element.Parameters)
        {
            if (ParameterMatcher.Matches(p.Definition?.Name ?? "", parameterName, "ContainsNormalized"))
                return p;
        }
        return null;
    }

    /// <summary>
    /// Sets the parameter, handling all storage types including ElementId.
    /// Returns null on success or an error string on failure.
    /// </summary>
    private static string? SetParameterValue(Document doc, Parameter param, string value, List<string> warnings)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                param.Set(value);
                return null;

            case StorageType.Integer:
                if (!int.TryParse(value, out var intVal))
                    return $"Cannot convert '{value}' to integer.";
                param.Set(intVal);
                return null;

            case StorageType.Double:
                if (!double.TryParse(value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var dblVal))
                    return $"Cannot convert '{value}' to double.";
                param.Set(dblVal);
                return null;

            case StorageType.ElementId:
                var (resolvedId, resolveError) = ResolveElementId(doc, value, warnings);
                if (resolvedId == null)
                    return resolveError;
                param.Set(resolvedId);
                return null;

            default:
                return $"Unsupported storage type: {param.StorageType}";
        }
    }

    /// <summary>
    /// Resolves a string value to an ElementId.
    /// Priority: (1) numeric parse → direct ElementId check, (2) element name search (types first, then instances).
    /// </summary>
    private static (ElementId? Id, string? Error) ResolveElementId(Document doc, string value, List<string> warnings)
    {
        // 1. Try direct numeric element ID
        if (long.TryParse(value, out var numericId))
        {
            var elemById = doc.GetElement(new ElementId(numericId));
            if (elemById != null)
                return (new ElementId(numericId), null);
            return (null, $"No element found with ID {numericId}.");
        }

        // 2. Name search — types first (wire types, cable types, etc.)
        var typeMatches = new FilteredElementCollector(doc)
            .WhereElementIsElementType()
            .Where(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();

        if (typeMatches.Count > 0)
        {
            if (typeMatches.Count > 1)
                warnings.Add(
                    $"Multiple element types named '{value}': " +
                    string.Join(", ", typeMatches.Select(m => $"ID={m.Id.Value} ({m.GetType().Name})")) +
                    ". Using first match.");
            return (typeMatches[0].Id, null);
        }

        // 3. Fallback: non-type elements by name
        var instMatches = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .Where(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();

        if (instMatches.Count == 0)
            return (null,
                $"No element found with name '{value}'. " +
                "Provide the exact element name or a numeric element ID.");

        if (instMatches.Count > 1)
            warnings.Add(
                $"Multiple elements named '{value}': " +
                string.Join(", ", instMatches.Select(m => $"ID={m.Id.Value} ({m.GetType().Name})")) +
                ". Using first match.");

        return (instMatches[0].Id, null);
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
