using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelPages;
using RevitMCP.Addin.ModelPages.Hosting;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelPages;

/// <summary>
/// Writes the parameter values a user typed into a model page. Not advertised by the bridge: only the
/// page listener sends it, and it goes through the same dispatcher as an agent's write — approval in
/// Revit (unless Direct Edit is on), one transaction, the activity log.
///
/// Unlike <c>revit_set_parameter</c> it takes values as the user sees them: numbers with units are
/// parsed in the project's display units, yes/no as words. It writes instance parameters only (a type
/// parameter would change every element of the type from one table cell), by exact name, and skips a
/// value that changed in Revit since the page read it.
/// </summary>
public class PageSetValuesTool : IRevitMcpTool
{
    public const string ToolName = "revit_page_set_values";

    public string Name => ToolName;
    public string Description => "Writes parameter values edited on a model page. Sent by the page listener only. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Fail(request, "no_document", "No document is open in Revit.");

        // Element ids are per document: writing them into another model would hit unrelated elements.
        var modelId = ToolArguments.GetString(request.Arguments, "modelId");
        if (!string.Equals(ModelPageRevitHandler.ModelIdOf(uiapp), modelId, StringComparison.Ordinal))
            return Fail(request, "different_model", $"The page belongs to another model; the active document is '{doc.Title}'. Nothing was changed.");

        var changes = ReadChanges(request, out var problem);
        if (changes == null) return Fail(request, "validation_failed", problem ?? "No changes.");

        var rows = new List<Dictionary<string, object?>>();
        var written = 0;
        cancellationToken.ThrowIfCancellationRequested();
        using (var tx = new Transaction(doc, changes.Count == 1 ? $"Page edit - {changes[0].Parameter}" : $"Page edit - {changes.Count} values"))
        {
            tx.Start();
            foreach (var change in changes)
            {
                var row = new Dictionary<string, object?> { ["id"] = change.Id, ["parameter"] = change.Parameter, ["ok"] = false };
                rows.Add(row);
                try
                {
                    var reason = Apply(doc, change, row);
                    if (reason == null) { row["ok"] = true; written++; }
                    else row["reason"] = reason;
                }
                catch (Exception ex)
                {
                    row["reason"] = ex.Message;
                }
            }

            if (written == 0) tx.RollBack();
            else RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(tx);
        }

        sw.Stop();
        var failed = rows.Count - written;
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = written > 0,
            Status = written > 0 ? null : "nothing_written",
            Message = written > 0
                ? $"Wrote {written} value{(written == 1 ? "" : "s")}" + (failed > 0 ? $"; {failed} not written." : ".")
                : "Nothing was written: " + (rows.Select(r => r.TryGetValue("reason", out var why) ? why as string : null).FirstOrDefault(r => r != null) ?? "no value could be set."),
            Data = new { written, failed, changes = rows },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>Writes one value. Returns why it was not written, or null when it was; fills old and new value into <paramref name="row"/>.</summary>
    private static string? Apply(Document doc, ModelPageChange change, Dictionary<string, object?> row)
    {
        var element = doc.GetElement(new ElementId(change.Id));
        if (element == null) return "The element no longer exists.";

        var parameter = element.LookupParameter(change.Parameter);
        if (parameter == null)
        {
            var typeId = element.GetTypeId();
            var onType = typeId != null && typeId != ElementId.InvalidElementId &&
                         doc.GetElement(typeId)?.LookupParameter(change.Parameter) != null;
            return onType
                ? $"'{change.Parameter}' is a type parameter; a page changes instance parameters only."
                : $"The element has no parameter '{change.Parameter}'.";
        }
        if (parameter.IsReadOnly) return $"'{change.Parameter}' is read-only.";

        var old = ParameterResolver.DisplayValue(parameter);
        row["oldValue"] = old;
        row["value"] = old;
        if (!ModelPageEdits.StillExpected(change.Expected, old))
            return $"The value changed in Revit since the page read it (now \"{old}\"). Refresh the page and try again.";

        var text = change.Value.Trim();
        switch (parameter.StorageType)
        {
            case StorageType.String:
                parameter.Set(change.Value);
                break;
            case StorageType.Integer:
                if (IsYesNo(parameter))
                {
                    if (!ModelPageEdits.TryParseYesNo(text, out var yes)) return $"\"{text}\" is not yes or no.";
                    parameter.Set(yes ? 1 : 0);
                }
                else if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var whole))
                    parameter.Set(whole);
                else if (!parameter.SetValueString(text))
                    return $"\"{text}\" is not a valid value for '{change.Parameter}'.";
                break;
            case StorageType.Double:
                // SetValueString reads the text the way Revit's own fields do: in the project's units, with or without the unit.
                if (text.Length == 0 || !parameter.SetValueString(text))
                    return $"\"{text}\" is not a valid value for '{change.Parameter}'.";
                break;
            default:
                return $"'{change.Parameter}' refers to another element and cannot be edited from a page.";
        }

        row["value"] = ParameterResolver.DisplayValue(parameter);
        return null;
    }

    internal static bool IsYesNo(Parameter parameter)
    {
        try { return parameter.Definition?.GetDataType() == SpecTypeId.Boolean.YesNo; }
        catch { return false; }
    }

    private static List<ModelPageChange>? ReadChanges(McpToolRequest request, out string? problem)
    {
        problem = "No changes.";
        if (!request.Arguments.TryGetValue("changes", out var raw) || raw == null) return null;
        try
        {
            return ModelPageEdits.Parse(raw as JToken ?? JToken.FromObject(raw), out problem);
        }
        catch (Exception ex)
        {
            problem = "The changes could not be read: " + ex.Message;
            return null;
        }
    }

    private static Task<McpToolResult> Fail(McpToolRequest request, string status, string message) =>
        Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Status = status, Message = message });
}
