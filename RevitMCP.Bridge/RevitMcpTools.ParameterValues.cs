using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>One value per element (#165, docs/parameter-values.md): ElementId → value writes in one transaction.</summary>
internal sealed partial class RevitMcpTools
{
    private const string ParameterValuesRowsDescription =
        "Rows [{elementId | uniqueId, value, parameterName?}] (or an object {\"<elementId>\": \"value\"}). For thousands of rows use sourceFile instead.";

    [McpServerTool(Name = "revit_preview_set_parameter_values", ReadOnly = true),
     Description("Previews writing a different value to each element WITHOUT changing the model. Same arguments as revit_set_parameter_values. Returns counts only — rows, willChange, unchanged, notFound, noParameter, readOnly, invalidValue, conflict — with up to sampleSize example changes and problems, and the duplicate values when unique=true.")]
    public Task<string> PreviewSetParameterValues(
        [Description("Default parameter name (rows may name their own).")] string? parameterName = null,
        [Description(ParameterValuesRowsDescription)] object? values = null,
        [Description("Rows from a file instead: .xlsx/.xlsm/.csv/.json. The file may be open in Excel.")] string? sourceFile = null,
        [Description("Worksheet of an Excel sourceFile (default: the first visible sheet).")] string? sourceSheet = null,
        [Description("Element id column (default: ElementId / Id).")] string? idColumn = null,
        [Description("UniqueId column (default: UniqueId), used when a row has no element id.")] string? uniqueIdColumn = null,
        [Description("Value column (default: Value).")] string? valueColumn = null,
        [Description("Optional per-row parameter column.")] string? parameterColumn = null,
        [Description("Instance (default) | Type | Any.")] string scope = "Instance",
        [Description("Accept only an exact parameter name (default true).")] bool exactMatch = true,
        [Description("BuiltInParameter enum name for the default parameter.")] string? builtInParameter = null,
        [Description("Shared parameter GUID for the default parameter.")] string? parameterGuid = null,
        [Description("Skip elements that already hold the value (default true).")] bool onlyIfDifferent = true,
        [Description("Report values that more than one element would hold (input and the targets' categories).")] bool unique = false,
        [Description("Example changes and problems to return (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendParameterValues("revit_preview_set_parameter_values", parameterName, values, sourceFile, sourceSheet, idColumn, uniqueIdColumn,
            valueColumn, parameterColumn, scope, exactMatch, builtInParameter, parameterGuid, onlyIfDifferent, unique, sampleSize, document, cancellationToken);

    [McpServerTool(Name = "revit_set_parameter_values"),
     Description("Writes a different value to each element — Object_IDs, device numbers, room codes from Excel — in ONE transaction and ONE approval. Requires approval. Rows from values or sourceFile (.xlsx/.xlsm/.csv/.json with sourceSheet, idColumn/uniqueIdColumn, valueColumn, parameterColumn). Elements by elementId or uniqueId. parameterName (or builtInParameter / parameterGuid) is the default parameter, exactMatch defaults to true. String, Integer, Double (internal units) and ElementId (id or name) parameters. onlyIfDifferent (default true) skips elements that already hold the value. unique=true writes nothing when a value would end up on more than one element (in the input, or in the model within the targets' categories). The result is aggregated: counts and up to sampleSize examples. Run revit_preview_set_parameter_values first; to be able to roll back, revit_checkpoint_create with the ids or categories and parameters=[the parameter] before.")]
    public Task<string> SetParameterValues(
        [Description("Default parameter name (rows may name their own).")] string? parameterName = null,
        [Description(ParameterValuesRowsDescription)] object? values = null,
        [Description("Rows from a file instead: .xlsx/.xlsm/.csv/.json. The file may be open in Excel.")] string? sourceFile = null,
        [Description("Worksheet of an Excel sourceFile (default: the first visible sheet).")] string? sourceSheet = null,
        [Description("Element id column (default: ElementId / Id).")] string? idColumn = null,
        [Description("UniqueId column (default: UniqueId), used when a row has no element id.")] string? uniqueIdColumn = null,
        [Description("Value column (default: Value).")] string? valueColumn = null,
        [Description("Optional per-row parameter column.")] string? parameterColumn = null,
        [Description("Instance (default) | Type | Any.")] string scope = "Instance",
        [Description("Accept only an exact parameter name (default true).")] bool exactMatch = true,
        [Description("BuiltInParameter enum name for the default parameter.")] string? builtInParameter = null,
        [Description("Shared parameter GUID for the default parameter.")] string? parameterGuid = null,
        [Description("Skip elements that already hold the value (default true).")] bool onlyIfDifferent = true,
        [Description("Write nothing if a value would be held by more than one element.")] bool unique = false,
        [Description("Example changes and problems to return (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendParameterValues("revit_set_parameter_values", parameterName, values, sourceFile, sourceSheet, idColumn, uniqueIdColumn,
            valueColumn, parameterColumn, scope, exactMatch, builtInParameter, parameterGuid, onlyIfDifferent, unique, sampleSize, document, cancellationToken);

    private async Task<string> SendParameterValues(string toolName, string? parameterName, object? values, string? sourceFile,
        string? sourceSheet, string? idColumn, string? uniqueIdColumn, string? valueColumn, string? parameterColumn, string scope,
        bool exactMatch, string? builtInParameter, string? parameterGuid, bool onlyIfDifferent, bool unique, int sampleSize,
        string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["parameterName"] = parameterName ?? string.Empty,
            ["values"] = values == null ? null : ToJToken(values),
            ["sourceFile"] = sourceFile ?? string.Empty,
            ["sourceSheet"] = sourceSheet ?? string.Empty,
            ["idColumn"] = idColumn ?? string.Empty,
            ["uniqueIdColumn"] = uniqueIdColumn ?? string.Empty,
            ["valueColumn"] = valueColumn ?? string.Empty,
            ["parameterColumn"] = parameterColumn ?? string.Empty,
            ["scope"] = string.IsNullOrWhiteSpace(scope) ? "Instance" : scope,
            ["exactMatch"] = exactMatch,
            ["builtInParameter"] = builtInParameter ?? string.Empty,
            ["parameterGuid"] = parameterGuid ?? string.Empty,
            ["onlyIfDifferent"] = onlyIfDifferent,
            ["unique"] = unique,
            ["sampleSize"] = sampleSize,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}
