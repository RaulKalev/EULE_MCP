using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Discovery entry point for the family type tools — turns a category or name fragment into the
/// type ids the duplicate and edit tools take.
/// </summary>
public class ListFamilyTypesTool : IRevitMcpTool
{
    private const int DefaultLimit = 100;
    private const int HardLimit = 1000;
    private const int ParameterDetailLimit = 25;

    public string Name => "revit_list_family_types";

    public string Description =>
        "Lists family types (loadable family symbols and system types) in the active document. " +
        "Filter with category, familyName, typeName, or typeIds. Optional: includeSystemTypes, " +
        "includeLoadableTypes, includeInstanceCounts, includeParameters (editable type parameters and " +
        "their current values), limit (default 100). Read-only. Returns the typeIds used by " +
        "revit_duplicate (entity=familyTypes) and revit_edit_family_types.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    private readonly CategoryResolver _categoryResolver = new();

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var categoryName = ToolArguments.GetString(request.Arguments, "category").Trim();
        var familyName = ToolArguments.GetString(request.Arguments, "familyName").Trim();
        var typeName = ToolArguments.GetString(request.Arguments, "typeName").Trim();
        var typeIds = ToolArguments.GetLongArray(request.Arguments, "typeIds");
        var includeSystemTypes = ToolArguments.GetBool(request.Arguments, "includeSystemTypes", true);
        var includeLoadableTypes = ToolArguments.GetBool(request.Arguments, "includeLoadableTypes", true);
        var includeInstanceCounts = ToolArguments.GetBool(request.Arguments, "includeInstanceCounts");
        var includeParameters = ToolArguments.GetBool(request.Arguments, "includeParameters");
        var requestedLimit = ToolArguments.GetInt(request.Arguments, "limit", DefaultLimit);

        var warnings = new List<string>();

        var limit = requestedLimit <= 0 ? DefaultLimit : Math.Min(requestedLimit, HardLimit);
        if (requestedLimit > HardLimit)
            warnings.Add($"limit {requestedLimit} exceeds the hard cap; using {HardLimit}.");

        if (!includeSystemTypes && !includeLoadableTypes)
            return Task.FromResult(Fail(request,
                "includeSystemTypes and includeLoadableTypes are both false — nothing can match."));

        long? categoryId = null;
        if (categoryName.Length > 0)
        {
            var resolved = _categoryResolver.Resolve(doc, categoryName);
            if (resolved.Category == null)
            {
                var suggestion = resolved.Suggestions.Count > 0
                    ? $" Did you mean: {string.Join(", ", resolved.Suggestions)}?"
                    : string.Empty;
                return Task.FromResult(Fail(request, resolved.Message + suggestion));
            }
            categoryId = resolved.Category.Id.Value;
        }

        IEnumerable<ElementType> source;
        if (typeIds.Length > 0)
        {
            var byId = new List<ElementType>();
            foreach (var id in typeIds.Distinct())
            {
                if (doc.GetElement(new ElementId(id)) is ElementType type)
                    byId.Add(type);
                else
                    warnings.Add($"Type {id} was not found or is not a family type.");
            }
            source = byId;
        }
        else
        {
            source = FamilyTypeSupport.CollectTypes(doc);
        }

        var matched = source
            .Where(t => categoryId == null || t.Category?.Id.Value == categoryId)
            .Where(t => t is FamilySymbol ? includeLoadableTypes : includeSystemTypes)
            .Where(t => Contains(FamilyTypeSupport.SafeFamilyName(t), familyName))
            .Where(t => Contains(FamilyTypeSupport.SafeName(t), typeName))
            .OrderBy(t => t.Category?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(FamilyTypeSupport.SafeFamilyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(FamilyTypeSupport.SafeName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();

        var totalMatched = matched.Count;
        var page = matched.Take(limit).ToList();
        if (totalMatched > page.Count)
            warnings.Add($"Capped at {limit} type(s). {totalMatched - page.Count} more matched — narrow the filters or raise limit.");

        Dictionary<long, int>? instanceCounts = null;
        if (includeInstanceCounts)
            instanceCounts = FamilyTypeSupport.CountInstancesByType(doc);

        if (includeParameters && page.Count > ParameterDetailLimit)
        {
            warnings.Add(
                $"includeParameters was requested for {page.Count} types; parameters are returned for the " +
                $"first {ParameterDetailLimit} only. Narrow the filters to see the rest.");
        }

        var payload = new List<object>();
        for (var index = 0; index < page.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = page[index];
            var withParameters = includeParameters && index < ParameterDetailLimit;

            payload.Add(new
            {
                typeId = type.Id.Value,
                familyName = FamilyTypeSupport.SafeFamilyName(type),
                typeName = FamilyTypeSupport.SafeName(type),
                category = type.Category?.Name ?? string.Empty,
                categoryId = type.Category?.Id.Value,
                kind = FamilyTypeSupport.KindOf(type),
                instanceCount = instanceCounts != null
                    ? instanceCounts.TryGetValue(type.Id.Value, out var count) ? count : 0
                    : (int?)null,
                parameters = withParameters ? DescribeParameters(type) : null
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {payload.Count} of {totalMatched} matching family type(s).",
            Data = new
            {
                documentTitle = doc.Title,
                totalMatched,
                returned = payload.Count,
                types = payload
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>Writable type parameters only — the ones the edit and duplicate tools can act on.</summary>
    private static List<object> DescribeParameters(ElementType type)
    {
        var results = new List<object>();
        try
        {
            foreach (Parameter parameter in type.Parameters)
            {
                try
                {
                    if (parameter.IsReadOnly)
                        continue;

                    results.Add(new
                    {
                        name = parameter.Definition?.Name ?? string.Empty,
                        storageType = parameter.StorageType.ToString(),
                        value = FamilyTypeSupport.ReadDisplayValue(parameter)
                    });
                }
                catch { }
            }
        }
        catch { }
        return results;
    }

    private static bool Contains(string value, string filter) =>
        filter.Length == 0 || value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}
