using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetAvailableParametersTool : IRevitMcpTool
{
    public string Name => "revit_get_available_parameters";
    public string Description => "Discovers all available parameters for a category, selection, or explicit element IDs. Returns parameter metadata, fill statistics, and example values.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    private readonly CategoryResolver _categoryResolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var doc = uidoc.Document;
        var category = ToolArguments.GetString(request.Arguments, "category");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var includeInstance = ToolArguments.GetBool(request.Arguments, "includeInstanceParameters", true);
        var includeType = ToolArguments.GetBool(request.Arguments, "includeTypeParameters", true);
        var sampleLimit = ToolArguments.GetInt(request.Arguments, "sampleLimit", 500);
        var exampleValueLimit = ToolArguments.GetInt(request.Arguments, "exampleValueLimit", 5);

        // Determine element source
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
            sourceIds = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategoryId(resolve.Category.Id)
                .ToElementIds();
        }
        else
        {
            return Task.FromResult(Fail(request, "Provide useSelection=true, elementIds, or category."));
        }

        // Scan elements and aggregate parameter metadata
        var reader = new ParameterReader();
        var readOpts = new ParameterReadOptions
        {
            IncludeInstanceParameters = includeInstance,
            IncludeTypeParameters = includeType
        };

        var paramStats = new Dictionary<string, ParameterStats>();
        int totalScanned = 0;

        foreach (var elementId in sourceIds)
        {
            if (totalScanned >= sampleLimit) break;

            var element = doc.GetElement(elementId);
            if (element?.Category == null) continue;

            totalScanned++;

            var allParams = reader.ReadParameters(doc, element, readOpts);

            foreach (var p in allParams)
            {
                var key = $"{p.Name}|{p.Scope}";

                if (!paramStats.TryGetValue(key, out var stats))
                {
                    stats = new ParameterStats
                    {
                        Name = p.Name,
                        NormalizedName = p.NormalizedName,
                        Scope = p.Scope,
                        StorageType = p.StorageType,
                        IsShared = p.IsShared,
                        Guid = p.Guid,
                        BuiltInParameterName = p.BuiltInParameterName,
                        ParameterId = p.ParameterId ?? 0,
                        IsReadOnly = p.IsReadOnly
                    };
                    paramStats[key] = stats;
                }

                stats.ExistsOnCount++;

                if (string.IsNullOrWhiteSpace(p.Value))
                {
                    stats.EmptyCount++;
                }
                else
                {
                    stats.NonEmptyCount++;
                    if (stats.ExampleValues.Count < exampleValueLimit &&
                        !stats.ExampleValues.Contains(p.Value))
                    {
                        stats.ExampleValues.Add(p.Value);
                    }
                }
            }
        }

        // Build response sorted by name
        var parameters = paramStats.Values
            .OrderBy(p => p.Scope)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new
            {
                p.Name,
                normalizedName = p.NormalizedName,
                scope = p.Scope,
                storageType = p.StorageType,
                isShared = p.IsShared,
                guid = p.Guid,
                builtInParameterName = p.BuiltInParameterName,
                parameterId = p.ParameterId,
                isReadOnly = p.IsReadOnly,
                existsOnCount = p.ExistsOnCount,
                emptyCount = p.EmptyCount,
                nonEmptyCount = p.NonEmptyCount,
                exampleValues = p.ExampleValues
            })
            .ToList();

        var warnings = new List<string>();
        if (totalScanned >= sampleLimit)
            warnings.Add($"Scan capped at sampleLimit={sampleLimit}. More elements may exist.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {parameters.Count} available parameters from {totalScanned} scanned elements.",
            Data = new
            {
                category,
                totalElementsScanned = totalScanned,
                parameterCount = parameters.Count,
                parameters
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };

    private class ParameterStats
    {
        public string Name { get; set; } = string.Empty;
        public string NormalizedName { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string StorageType { get; set; } = string.Empty;
        public bool IsShared { get; set; }
        public string? Guid { get; set; }
        public string? BuiltInParameterName { get; set; }
        public long ParameterId { get; set; }
        public bool IsReadOnly { get; set; }
        public int ExistsOnCount { get; set; }
        public int EmptyCount { get; set; }
        public int NonEmptyCount { get; set; }
        public List<string> ExampleValues { get; set; } = new();
    }
}
