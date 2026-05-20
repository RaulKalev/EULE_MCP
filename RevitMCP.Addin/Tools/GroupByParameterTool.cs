using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Groups model elements by the value of a named parameter and returns counts.
/// Supports partial name matching so "ELENEA_Nimetus" matches "ELENEA_ÜLD 001_Nimetus" etc.
/// </summary>
public class GroupByParameterTool : IRevitMcpTool
{
    public string Name => "revit_group_by_parameter";
    public string Description => "Groups elements by a parameter value and returns counts. parameterName supports partial matching (e.g. 'ELENEA_Nimetus' matches 'ELENEA_ÜLD 001_Nimetus'). Optionally filter by category.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var parameterName = ToolArguments.GetString(request.Arguments, "parameterName");
        var categoryFilter = ToolArguments.GetString(request.Arguments, "category");

        if (string.IsNullOrWhiteSpace(parameterName))
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = "parameterName is required."
            });

        // Build collector — filter by category first if provided (much faster)
        var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();

        if (!string.IsNullOrWhiteSpace(categoryFilter))
        {
            var cat = FindCategory(doc, categoryFilter);
            if (cat == null)
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = $"Category not found: '{categoryFilter}'."
                });
            collector = collector.OfCategoryId(cat.Id);
        }

        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        int matchedElements = 0;
        int totalScanned = 0;
        var warnings = new List<string>();

        // Cache type parameter lookups — many instances share the same type
        var typeValueCache = new Dictionary<ElementId, string?>();

        foreach (var element in collector)
        {
            totalScanned++;

            string? value = null;
            try
            {
                // 1. Check instance parameters first
                foreach (Parameter p in element.Parameters)
                {
                    var defName = p.Definition?.Name;
                    if (defName != null &&
                        defName.IndexOf(parameterName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        value = ReadParamValue(p);
                        break;
                    }
                }

                // 2. Fall back to type parameters (cached per type ID)
                if (value == null)
                {
                    var typeId = element.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                    {
                        if (!typeValueCache.TryGetValue(typeId, out var cached))
                        {
                            cached = null;
                            var elementType = doc.GetElement(typeId);
                            if (elementType != null)
                            {
                                foreach (Parameter p in elementType.Parameters)
                                {
                                    var defName = p.Definition?.Name;
                                    if (defName != null &&
                                        defName.IndexOf(parameterName, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        cached = ReadParamValue(p);
                                        break;
                                    }
                                }
                            }
                            typeValueCache[typeId] = cached;
                        }
                        value = cached;
                    }
                }
            }
            catch
            {
                continue;
            }

            if (value == null) continue;

            matchedElements++;
            if (groups.TryGetValue(value, out var existing))
                groups[value] = existing + 1;
            else
                groups[value] = 1;
        }

        if (totalScanned > 5000 && string.IsNullOrWhiteSpace(categoryFilter))
            warnings.Add($"Scanned {totalScanned} elements — provide a 'category' argument to speed up future calls.");

        var sorted = groups
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new { name = kv.Key, count = kv.Value })
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {matchedElements} elements with parameter matching '{parameterName}'. {sorted.Count} distinct values.",
            Data = new
            {
                parameterName,
                categoryFilter,
                totalScanned,
                matchedElements,
                distinctValues = sorted.Count,
                groups = sorted
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string ReadParamValue(Parameter p)
    {
        try
        {
            if (p.StorageType == StorageType.String)
                return p.AsString() ?? "(empty)";

            var vs = p.AsValueString();
            if (!string.IsNullOrWhiteSpace(vs)) return vs;

            if (p.StorageType == StorageType.Integer)
                return p.AsInteger().ToString();

            return "(empty)";
        }
        catch
        {
            return "(error)";
        }
    }

    private static Autodesk.Revit.DB.Category? FindCategory(Document doc, string name)
    {
        foreach (Autodesk.Revit.DB.Category cat in doc.Settings.Categories)
        {
            if (string.Equals(cat.Name, name, StringComparison.OrdinalIgnoreCase))
                return cat;
        }
        return null;
    }
}
