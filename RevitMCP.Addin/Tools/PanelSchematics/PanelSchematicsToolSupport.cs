using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.PanelSchematics;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.PanelSchematics;

/// <summary>Shared argument parsing and write guards of the revit_panelschematics_* tools.</summary>
internal static class PanelSchematicsToolSupport
{
    public const string GuideHint = "See revit_panelschematics_guide for the fields and rules.";

    public static McpToolResult Fail(McpToolRequest request, string message, string? status = "validation_failed", object? data = null,
        IEnumerable<string>? errors = null)
    {
        var result = new McpToolResult { RequestId = request.RequestId, Success = false, Message = message, Data = data };
        if (status != null) result.Status = status;
        if (errors != null) result.Errors.AddRange(errors);
        return result;
    }

    public static Document? ActiveDocument(UIApplication uiapp, McpToolRequest request, out McpToolResult? failure)
    {
        var doc = uiapp.ActiveUIDocument?.Document;
        failure = doc == null ? Fail(request, "No active document.", null) : null;
        if (doc != null && doc.IsFamilyDocument)
        {
            failure = Fail(request, "The active document is a family; open the project.", null);
            return null;
        }
        return doc;
    }

    /// <summary>
    /// What stops a write before anything is read: a read-only document, an open transaction, or the plugin's
    /// table window, whose in-memory rows would overwrite the change on its next save.
    /// </summary>
    public static McpToolResult? WriteGuard(Document doc, McpToolRequest request)
    {
        try
        {
            if (doc.IsReadOnly)
                return Fail(request, $"'{doc.Title}' is read-only. Nothing was written.", null);
            if (doc.IsModifiable)
                return Fail(request, $"'{doc.Title}' has an open transaction (an edit is in progress). Finish it in Revit and retry.", "revit_busy");
        }
        catch (Exception ex)
        {
            return Fail(request, $"Could not read the state of the document: {ex.Message}", null);
        }

        if (PanelSchematicsModel.OpenPluginWindows().Contains(PanelSchematicsModel.MainWindowName))
            return Fail(request,
                "The PanelSchematics window (Kilbiskeemid) is open. It keeps the panel table in memory and its next save would " +
                "overwrite this change, so nothing was written. Ask the user to close that window, then retry.",
                "plugin_window_open");
        return null;
    }

    /// <summary>Warnings for reads and previews about the plugin's open windows.</summary>
    public static void AddPluginWindowWarnings(List<string> warnings)
    {
        var open = PanelSchematicsModel.OpenPluginWindows();
        if (open.Contains(PanelSchematicsModel.MainWindowName))
            warnings.Add("The PanelSchematics window (Kilbiskeemid) is open: it may hold unsaved edits that are not in these values, and writes are refused until it is closed.");
        else if (open.Count > 0)
            warnings.Add($"A PanelSchematics window is open ({string.Join(", ", open.Distinct())}). It shows the values it loaded; reopen it after a write.");
    }

    /// <summary>
    /// Warns about symbol names that are not in the team's symbol file: the plugin could not draw such a row.
    /// A warning, not an error, because the file may be out of reach on this machine.
    /// </summary>
    public static void AddSymbolWarnings(IEnumerable<string?> symbols, List<string> warnings)
    {
        var names = symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return;

        var library = PanelSchematicsSymbols.Load();
        if (!library.Loaded)
        {
            warnings.Add($"Symbol names were not checked: {library.Problem}");
            return;
        }
        foreach (var name in names.Where(n => !library.Contains(n)))
        {
            var suggestions = library.Suggest(name);
            warnings.Add($"Symbol '{name}' is not in {PanelSchematicsSymbols.FileName}, so the plugin cannot draw it." +
                         (suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", suggestions.Select(s => "'" + s + "'"))}?" : " The names are listed by revit_panelschematics_guide."));
        }
    }

    public static string? CheckToken(McpToolRequest request, string current)
    {
        var expected = ToolArguments.GetString(request.Arguments, "expectedStateToken");
        return expected.Length > 0 && !string.Equals(expected, current, StringComparison.Ordinal)
            ? "The stored data changed since the preview (state token differs). Nothing was written; run the preview again."
            : null;
    }

    public static JArray? GetArray(Dictionary<string, object?> args, string key, List<string> errors)
        => GetToken(args, key, errors) switch
        {
            null => null,
            JArray array => array,
            var other => AddError<JArray>(errors, $"{key} must be a list, got {other.Type}.")
        };

    public static JObject? GetObject(Dictionary<string, object?> args, string key, List<string> errors)
        => GetToken(args, key, errors) switch
        {
            null => null,
            JObject obj => obj,
            var other => AddError<JObject>(errors, $"{key} must be an object, got {other.Type}.")
        };

    private static T? AddError<T>(List<string> errors, string message) where T : class
    {
        errors.Add(message);
        return null;
    }

    private static JToken? GetToken(Dictionary<string, object?> args, string key, List<string> errors)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null)
            return null;
        try
        {
            var token = raw switch
            {
                JToken t => t,
                string json when string.IsNullOrWhiteSpace(json) => null,
                string json => JToken.Parse(json),
                _ => JToken.FromObject(raw)
            };
            return token == null || token.Type == JTokenType.Null ? null : token;
        }
        catch (Exception ex)
        {
            errors.Add($"{key} could not be parsed: {ex.Message}");
            return null;
        }
    }

    public static string Text(JToken? token) => token == null || token.Type == JTokenType.Null
        ? string.Empty
        : token.Type == JTokenType.String ? token.Value<string>() ?? string.Empty : token.ToString(Newtonsoft.Json.Formatting.None);

    public static JsonNode? ToNode(JToken? token)
        => token == null || token.Type == JTokenType.Null ? null : JsonNode.Parse(token.ToString(Newtonsoft.Json.Formatting.None));

    public static JToken ToToken(JsonNode? node)
        => node == null ? JValue.CreateNull() : JToken.Parse(node.ToJsonString());

    public static object DescribePanel(FamilyInstance panel) => new
    {
        id = panel.Id.Value,
        name = panel.Name,
        uniqueId = panel.UniqueId
    };

    /// <summary>Row values as plain CLR values for a circuit write (text, int or double per the field's type).</summary>
    public static Dictionary<string, object?>? CoerceCircuitValues(JObject values, string where, bool circuitRow, List<string> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in values.Properties())
        {
            var field = PanelSchematicsContract.FindRowField(property.Name);
            if (field == null)
            {
                errors.Add($"{where}: '{property.Name}' is not a row field. Fields: {string.Join(", ", PanelSchematicsContract.RowFields.Where(f => !circuitRow || f.CircuitWritable).Select(f => f.Name))}.");
                continue;
            }
            if (circuitRow && !field.CircuitWritable)
            {
                errors.Add($"{where}: {field.Name} exists on empty rows only; a circuit has no parameter for it.");
                continue;
            }
            if (!EmptyRowsEditor.TryCoerce(field, ToNode(property.Value), out var coerced, out var error))
            {
                errors.Add($"{where}: {field.Name} {error}");
                continue;
            }
            result[field.Name] = field.Type switch
            {
                PanelSchematicsContract.FieldType.Whole => coerced!.GetValue<int>(),
                PanelSchematicsContract.FieldType.Number => coerced!.GetValue<double>(),
                PanelSchematicsContract.FieldType.YesNo => coerced!.GetValue<bool>(),
                _ => coerced!.GetValue<string>()
            };
        }
        return errors.Count == 0 ? result : null;
    }
}
