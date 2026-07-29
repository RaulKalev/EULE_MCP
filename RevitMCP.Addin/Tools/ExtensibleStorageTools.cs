using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB.ExtensibleStorage;
using RevitMCP.Addin.ExtensibleStorage;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Discovery half of Extensible Storage reading: which schemas exist and what shape their data has.
/// </summary>
public sealed class ListExtensibleStorageSchemasTool : IRevitMcpTool
{
    public string Name => "revit_list_extensible_storage_schemas";

    public string Description =>
        "Lists Revit Extensible Storage schemas visible to this session, including vendor ID, access levels, " +
        "and field definitions (name, value type, container type, sub-schema, unit spec). By default it also " +
        "counts how many elements in the active document carry each schema, so unused schemas are easy to skip. " +
        "Read-only. Use revit_read_extensible_storage to read the stored values.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var nameFilter = ToolArguments.GetString(request.Arguments, "schemaNameFilter");
        var vendorFilter = ToolArguments.GetString(request.Arguments, "vendorIdFilter");
        var includeFields = ToolArguments.GetBool(request.Arguments, "includeFields", true);
        var includeElementCounts = ToolArguments.GetBool(request.Arguments, "includeElementCounts", true);
        var onlyUsed = ToolArguments.GetBool(request.Arguments, "onlyUsedInDocument");
        var limit = Math.Max(0, ToolArguments.GetInt(request.Arguments, "limit", 0));

        var warnings = new List<string>();
        var schemas = ExtensibleStorageReader.ListSchemas()
            .Where(schema => Matches(ExtensibleStorageReader.SafeSchemaName(schema), nameFilter))
            .Where(schema => Matches(SafeVendor(schema), vendorFilter))
            .OrderBy(ExtensibleStorageReader.SafeSchemaName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (schemas.Count == 0)
        {
            warnings.Add(
                "No schemas matched. Revit only exposes schemas already loaded in this session — those " +
                "registered by loaded add-ins and those read from open documents. If the owning add-in has " +
                "not run yet in this session, its schema may not be listed.");
        }

        // onlyUsedInDocument implies counting, otherwise there is nothing to filter on.
        if (onlyUsed)
            includeElementCounts = true;

        var results = new List<object>();
        var totalMatched = 0;
        foreach (var schema in schemas)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                warnings.Add("Listing was cancelled before all schemas were inspected.");
                break;
            }

            var count = includeElementCounts
                ? ExtensibleStorageReader.CountElementsWithSchema(doc, schema.GUID)
                : (int?)null;

            if (onlyUsed && count <= 0)
                continue;

            totalMatched++;
            if (limit > 0 && results.Count >= limit)
                continue;

            results.Add(new
            {
                schema = ExtensibleStorageReader.DescribeSchema(schema, includeFields),
                elementCount = count
            });
        }

        if (limit > 0 && totalMatched > results.Count)
            warnings.Add($"Capped at {limit} schema(s). {totalMatched - results.Count} more matched.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {results.Count} Extensible Storage schema(s).",
            Data = new
            {
                documentTitle = doc.Title,
                schemaCount = results.Count,
                totalMatched,
                elementCountsIncluded = includeElementCounts,
                schemas = results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string SafeVendor(Schema schema)
    {
        try { return schema.VendorId ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool Matches(string value, string filter) =>
        filter.Length == 0 ||
        value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

/// <summary>
/// Reading half: decoded entity values for chosen elements, the current selection, or every
/// element in the document that carries a given schema.
/// </summary>
public sealed class ReadExtensibleStorageTool : IRevitMcpTool
{
    private const int DefaultMaxElements = 25;
    private const int HardMaxElements = 500;

    public string Name => "revit_read_extensible_storage";

    public string Description =>
        "Reads Extensible Storage data written by other Revit add-ins. Target elements with elementIds, " +
        "useSelection=true, or scanDocument=true (which finds every element carrying the requested schema, " +
        "including DataStorage elements holding project-wide data). Narrow to one schema with schemaGuid or " +
        "schemaName; omit both to read every schema present on the target elements. Values are decoded for " +
        "all container shapes (simple, array, map) and nested sub-entities. Read-only — it never writes or " +
        "deletes stored data.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var doc = uidoc.Document;
        var warnings = new List<string>();

        var schemaGuidArg = ToolArguments.GetString(request.Arguments, "schemaGuid");
        var schemaNameArg = ToolArguments.GetString(request.Arguments, "schemaName");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var scanDocument = ToolArguments.GetBool(request.Arguments, "scanDocument");
        var includeValues = ToolArguments.GetBool(request.Arguments, "includeValues", true);
        var maxDepth = Math.Max(1, ToolArguments.GetInt(request.Arguments, "maxDepth", 3));
        var requestedMax = ToolArguments.GetInt(request.Arguments, "maxElements", DefaultMaxElements);
        var maxElements = requestedMax <= 0
            ? DefaultMaxElements
            : Math.Min(requestedMax, HardMaxElements);
        if (requestedMax > HardMaxElements)
            warnings.Add($"maxElements {requestedMax} exceeds the hard cap; using {HardMaxElements}.");

        // ---- resolve the schemas to read
        var targetSchemas = ResolveSchemas(schemaGuidArg, schemaNameArg, warnings, out var resolveError);
        if (resolveError != null)
            return Task.FromResult(Fail(request, resolveError));

        // ---- resolve the elements to read
        var elements = ResolveElements(
            doc, uidoc, elementIds, useSelection, scanDocument, targetSchemas, warnings, out var elementError);
        if (elementError != null)
            return Task.FromResult(Fail(request, elementError));

        var totalElements = elements.Count;
        if (totalElements > maxElements)
        {
            warnings.Add($"Capped at {maxElements} element(s). {totalElements - maxElements} more carry matching data. Raise maxElements (up to {HardMaxElements}) or narrow the query.");
            elements = elements.Take(maxElements).ToList();
        }

        // A schema filter narrows what we read per element; without one we read whatever each element has.
        var schemaLookup = targetSchemas.ToDictionary(s => s.GUID);
        var readSchemas = new Dictionary<Guid, Schema>();

        var payloads = new List<object>();
        var entitiesRead = 0;

        foreach (var element in elements)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                warnings.Add("Read was cancelled before all elements were processed.");
                break;
            }

            var guids = SafeSchemaGuids(element);
            if (guids.Count == 0)
                continue;

            var entities = new List<object>();
            foreach (var guid in guids)
            {
                if (schemaLookup.Count > 0 && !schemaLookup.ContainsKey(guid))
                    continue;

                var schema = schemaLookup.TryGetValue(guid, out var known)
                    ? known
                    : ExtensibleStorageReader.LookupSchema(guid);

                if (schema == null)
                {
                    entities.Add(new
                    {
                        schemaGuid = guid.ToString(),
                        schemaName = (string?)null,
                        readAccessGranted = false,
                        errors = new[]
                        {
                            "The element carries data for this schema, but the schema is not loaded in this " +
                            "Revit session, so its fields cannot be decoded."
                        }
                    });
                    continue;
                }

                readSchemas[guid] = schema;

                if (!includeValues)
                {
                    entities.Add(new
                    {
                        schemaGuid = guid.ToString(),
                        schemaName = ExtensibleStorageReader.SafeSchemaName(schema)
                    });
                    entitiesRead++;
                    continue;
                }

                var read = ExtensibleStorageReader.ReadEntity(doc, element, schema, maxDepth);
                if (read == null)
                    continue;

                entities.Add(read.ToPayload());
                entitiesRead++;
            }

            if (entities.Count == 0)
                continue;

            payloads.Add(new
            {
                elementId = element.Id.Value,
                uniqueId = element.UniqueId,
                name = ExtensibleStorageReader.SafeElementName(element),
                category = element.Category?.Name ?? string.Empty,
                elementClass = element.GetType().Name,
                isElementType = element is ElementType,
                entityCount = entities.Count,
                entities
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Read {entitiesRead} entit{(entitiesRead == 1 ? "y" : "ies")} on {payloads.Count} element(s).",
            Data = new
            {
                documentTitle = doc.Title,
                elementCount = payloads.Count,
                elementsInspected = elements.Count,
                totalElementsMatched = totalElements,
                entityCount = entitiesRead,
                schemas = readSchemas.Values
                    .Select(s => new
                    {
                        schemaGuid = s.GUID.ToString(),
                        schemaName = ExtensibleStorageReader.SafeSchemaName(s)
                    })
                    .ToList(),
                elements = payloads
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>Resolves the schema filter. An empty list means "every schema found on the elements".</summary>
    private static List<Schema> ResolveSchemas(
        string schemaGuidArg,
        string schemaNameArg,
        List<string> warnings,
        out string? error)
    {
        error = null;
        var schemas = new List<Schema>();

        if (schemaGuidArg.Length > 0)
        {
            if (!Guid.TryParse(schemaGuidArg, out var guid))
            {
                error = $"'{schemaGuidArg}' is not a valid schema GUID.";
                return schemas;
            }

            var schema = ExtensibleStorageReader.LookupSchema(guid);
            if (schema == null)
            {
                error = $"Schema {guid} is not loaded in this Revit session. " +
                        "Run revit_list_extensible_storage_schemas to see the available schemas.";
                return schemas;
            }

            schemas.Add(schema);
            return schemas;
        }

        if (schemaNameArg.Length > 0)
        {
            var matches = ExtensibleStorageReader.ListSchemas()
                .Where(s => ExtensibleStorageReader.SafeSchemaName(s)
                    .IndexOf(schemaNameArg, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (matches.Count == 0)
            {
                error = $"No loaded schema name contains '{schemaNameArg}'. " +
                        "Run revit_list_extensible_storage_schemas to see the available schemas.";
                return schemas;
            }

            if (matches.Count > 1)
            {
                warnings.Add(
                    $"schemaName '{schemaNameArg}' matched {matches.Count} schemas: " +
                    string.Join(", ", matches.Select(ExtensibleStorageReader.SafeSchemaName)) +
                    ". All of them were read; pass schemaGuid to target one.");
            }

            schemas.AddRange(matches);
        }

        return schemas;
    }

    /// <summary>Resolves the target elements from explicit IDs, the selection, or a document scan.</summary>
    private static List<Element> ResolveElements(
        Document doc,
        UIDocument uidoc,
        long[] elementIds,
        bool useSelection,
        bool scanDocument,
        List<Schema> targetSchemas,
        List<string> warnings,
        out string? error)
    {
        error = null;
        var elements = new List<Element>();

        if (elementIds.Length > 0)
        {
            foreach (var id in elementIds)
            {
                var element = doc.GetElement(new ElementId(id));
                if (element == null)
                    warnings.Add($"Element {id} was not found.");
                else
                    elements.Add(element);
            }
            return elements;
        }

        if (useSelection)
        {
            var selected = uidoc.Selection.GetElementIds();
            if (selected.Count == 0)
            {
                error = "useSelection=true but nothing is selected in Revit.";
                return elements;
            }

            foreach (var id in selected)
            {
                var element = doc.GetElement(id);
                if (element != null)
                    elements.Add(element);
            }
            return elements;
        }

        if (scanDocument)
        {
            if (targetSchemas.Count == 0)
            {
                error = "scanDocument=true requires schemaGuid or schemaName. " +
                        "Scanning the whole model for every schema at once is not supported — " +
                        "run revit_list_extensible_storage_schemas first to pick one.";
                return elements;
            }

            var seen = new HashSet<long>();
            foreach (var schema in targetSchemas)
            {
                foreach (var element in ExtensibleStorageReader.CollectElementsWithSchema(doc, schema.GUID))
                {
                    if (seen.Add(element.Id.Value))
                        elements.Add(element);
                }
            }

            if (elements.Count == 0)
            {
                warnings.Add(
                    "No element in this document carries data for the requested schema. The schema is loaded " +
                    "in the session but unused in this model.");
            }
            return elements;
        }

        error = "Provide elementIds, set useSelection=true, or set scanDocument=true with a schemaGuid/schemaName.";
        return elements;
    }

    private static IList<Guid> SafeSchemaGuids(Element element)
    {
        try { return element.GetEntitySchemaGuids(); }
        catch { return new List<Guid>(); }
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
