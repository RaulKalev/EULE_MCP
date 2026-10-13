using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.PanelSchematics;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;
using Contract = RevitMCP.Addin.PanelSchematics.PanelSchematicsContract;
using Support = RevitMCP.Addin.Tools.PanelSchematics.PanelSchematicsToolSupport;

namespace RevitMCP.Addin.Tools.PanelSchematics;

/// <summary>revit_panelschematics_preview_edit_empty_rows: what an empty-row edit would change, no changes.</summary>
public sealed class PanelSchematicsPreviewEditEmptyRowsTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_preview_edit_empty_rows";
    public string Description =>
        "Previews adding, changing or removing PanelSchematics empty rows of a panel WITHOUT changing anything: each row's fields " +
        "before and after, circuits renumbered by an insert, detail-item parameters that would follow, and a state token for the write.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(EmptyRowsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_panelschematics_edit_empty_rows: writes empty rows to the panel's Extensible Storage.</summary>
public sealed class PanelSchematicsEditEmptyRowsTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_edit_empty_rows";
    public string Description =>
        "Adds, changes or removes PanelSchematics empty rows of a panel in its Extensible Storage (requires approval). Other rows and " +
        "unknown fields stay as stored; a backup is written first; refused while the plugin's table window is open. Run the preview first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(EmptyRowsExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class EmptyRowsExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = Support.ActiveDocument(uiapp, request, out var failure);
        if (doc == null) return failure!;

        if (apply && Support.WriteGuard(doc, request) is { } blocked) return blocked;

        var panel = PanelSchematicsModel.ResolvePanel(doc, ToolArguments.GetString(request.Arguments, "panel"), out var panelError);
        if (panel == null) return Support.Fail(request, panelError!);

        var errors = new List<string>();
        var operations = ParseOperations(request.Arguments, errors);
        if (errors.Count > 0)
            return Support.Fail(request, "Nothing was written. " + errors[0] + " " + Support.GuideHint, errors: errors);

        var table = PanelTable.Read(doc, panel);
        if (table.EmptyRowsProblem != null)
            return Support.Fail(request, table.EmptyRowsProblem + " Nothing was written: the stored rows would be lost.", "stored_data_unreadable");

        var circuitRows = table.Circuits
            .Select(c => new EmptyRowsEditor.CircuitRow(c.Id.Value.ToString(CultureInfo.InvariantCulture), PanelSchematicsModel.ReaNrOf(c)))
            .ToList();

        int previewId = 0;
        var edit = EmptyRowsEditor.Apply(table.EmptyRowsJson, operations, circuitRows, panel.Id.Value,
            apply ? null : () => "(new row " + ++previewId + ")");
        if (!edit.Success)
            return Support.Fail(request, "Nothing was written. " + edit.Errors[0], errors: edit.Errors);

        var warnings = new List<string>();
        var updateDetailItems = ToolArguments.GetBool(request.Arguments, "updateDetailItems", true);
        var circuitsById = table.Circuits.ToDictionary(c => c.Id.Value.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);

        // What follows the rows: renumbered circuits and the detail items of changed rows (a dry run first)
        List<PanelSchematicsModel.ParameterWrite> Follow(bool write)
        {
            var writes = new List<PanelSchematicsModel.ParameterWrite>();
            var options = new PanelSchematicsModel.RowWriteOptions { Apply = write, DetailItems = updateDetailItems };
            foreach (var renumber in edit.CircuitRenumbers)
                writes.AddRange(PanelSchematicsModel.WriteCircuitRow(doc, circuitsById[renumber.Key],
                    new Dictionary<string, object?> { ["ReaNr"] = renumber.To }, table.DetailItems, options, warnings));

            if (!updateDetailItems) return writes;
            foreach (var change in edit.Changes.Where(c => c.Action != EmptyRowsEditor.Remove && c.Fields.Count > 0))
            {
                if (!table.DetailItems.TryGetValue(change.UniqueId, out var items)) continue;
                var values = change.Fields.ToDictionary(f => f.Field, f => (object?)f.After, StringComparer.Ordinal);
                writes.AddRange(PanelSchematicsModel.WriteDetailItems(items, values, write));
            }
            return writes;
        }

        var planned = Follow(write: false);

        // An insert only works when every pushed circuit can really take its new number
        var stuck = edit.CircuitRenumbers
            .Where(r => !planned.Any(w => w.Target == "circuit" && w.Field == "ReaNr" && w.Status == "set"
                                          && w.ElementId.ToString(CultureInfo.InvariantCulture) == r.Key))
            .Select(r => $"Circuit {r.Key} cannot be renumbered from {r.From} to {r.To}: its 'Rea Nr' parameter is missing or read-only.")
            .ToList();
        if (stuck.Count > 0)
            return Support.Fail(request, "Nothing was written. " + stuck[0] + " Use a free Rea nr instead of insert.", errors: stuck);

        foreach (var removed in edit.Changes.Where(c => c.Action == EmptyRowsEditor.Remove))
            if (table.DetailItems.TryGetValue(removed.UniqueId, out var orphans))
                warnings.Add($"Row {removed.ReaNr} has {orphans.Count} detail item(s) on the schematic; they are not deleted here. Redraw in the plugin (Kirjuta + Joonista).");
        Support.AddSymbolWarnings(edit.Changes.SelectMany(c => c.Fields).Where(f => f.Field == "Symbol").Select(f => f.After), warnings);
        if (edit.Changes.Any(c => c.Action == EmptyRowsEditor.Add))
            warnings.Add("New rows are not drawn by the connector. The user draws them in the plugin (Kirjuta + Joonista).");

        object Summary(IEnumerable<PanelSchematicsModel.ParameterWrite> writes) => new
        {
            panel = Support.DescribePanel(panel),
            rowsBefore = EmptyRowsEditor.TryReadRows(table.EmptyRowsJson, out var beforeRows, out _) ? beforeRows.Count : 0,
            rowsAfter = edit.RowCount,
            changes = edit.Changes.Select(c => new
            {
                action = c.Action,
                uniqueId = c.UniqueId,
                reaNr = c.ReaNr,
                fields = c.Fields.Select(f => new { field = f.Field, before = f.Before, after = f.After })
            }),
            circuitsRenumbered = edit.CircuitRenumbers.Select(r => new { circuitId = long.Parse(r.Key, CultureInfo.InvariantCulture), from = r.From, to = r.To }),
            parameterWrites = writes.Where(w => w.Status != "unchanged").Select(Describe),
            stateToken = table.EmptyRowsToken
        };

        if (!apply)
        {
            Support.AddPluginWindowWarnings(warnings);
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = edit.Changed
                    ? $"Would change the empty rows of '{panel.Name}': {edit.Changes.Count} row change(s), {edit.CircuitRenumbers.Count} circuit(s) renumbered."
                    : $"Nothing would change in the empty rows of '{panel.Name}'.",
                Data = Summary(planned),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (!edit.Changed && planned.All(w => w.Status != "set"))
            return new McpToolResult
            {
                RequestId = request.RequestId, Success = true, Message = $"Nothing to change in the empty rows of '{panel.Name}'.",
                Data = Summary(planned), DurationMs = sw.ElapsedMilliseconds
            };

        if (Support.CheckToken(request, table.EmptyRowsToken) is { } stale)
            return Support.Fail(request, stale, "state_changed");

        var notEditable = new List<string>();
        if (PanelSchematicsStorage.WhyNotEditable(doc, panel) is { } panelBlocked) notEditable.Add(panelBlocked);
        foreach (var elementId in planned.Where(w => w.Status == "set").Select(w => w.ElementId).Distinct())
            if (doc.GetElement(new ElementId(elementId)) is { } element && PanelSchematicsStorage.WhyNotEditable(doc, element) is { } why)
                notEditable.Add(why);
        if (notEditable.Count > 0)
            return Support.Fail(request, "Nothing was written. " + notEditable[0], "not_editable", errors: notEditable);

        string backupPath;
        try
        {
            backupPath = PanelSchematicsModel.WriteBackup(doc, "empty-rows_" + panel.Name, new JObject
            {
                ["panelId"] = panel.Id.Value,
                ["panelName"] = panel.Name,
                ["emptyRowsJson"] = table.EmptyRowsJson,
                ["circuitReaNr"] = new JObject(circuitRows.Select(c => new JProperty(c.Key, c.ReaNr))),
                ["parameterWrites"] = JArray.FromObject(planned.Where(w => w.Status == "set").Select(Describe))
            });
        }
        catch (Exception ex)
        {
            return Support.Fail(request, $"The backup could not be written ({ex.Message}), so nothing was changed.", "backup_failed");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var store = Contract.GetStore(Contract.EmptyRowsKey);
        List<PanelSchematicsModel.ParameterWrite> written = new();
        var transaction = RevitTransactionRunner.Run(doc, "Revit MCP - PanelSchematics empty rows", () =>
        {
            if (edit.Json == null)
                PanelSchematicsStorage.Delete(panel, store);
            else
            {
                var state = new PanelSchematicsStorage.StoreState();
                state.Texts["EmptyRowsJson"] = edit.Json;
                PanelSchematicsStorage.Write(panel, store, state);
            }
            written = Follow(write: true);

            // Read back: exactly the text that was built, or nothing at all
            var after = PanelSchematicsStorage.Read(panel, store);
            var stored = string.IsNullOrWhiteSpace(after.Text("EmptyRowsJson")) ? null : after.Text("EmptyRowsJson");
            if (!after.Readable || !string.Equals(stored, edit.Json, StringComparison.Ordinal))
                throw new InvalidOperationException("The stored empty rows do not match what was written.");
        });

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = transaction.Success,
            Status = transaction.Success ? null : "transaction_failed",
            Message = transaction.Success
                ? $"Empty rows of '{panel.Name}' written: {edit.Changes.Count} row change(s), {edit.RowCount} row(s) stored."
                : $"Writing the empty rows failed and was rolled back: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}",
            Data = new
            {
                result = Summary(transaction.Success ? written : planned),
                backupFile = backupPath,
                transaction = transaction.Diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    internal static object Describe(PanelSchematicsModel.ParameterWrite write) => new
    {
        elementId = write.ElementId,
        target = write.Target,
        field = write.Field,
        parameter = write.Parameter,
        before = write.Before,
        after = write.After,
        status = write.Status
    };

    private static List<EmptyRowsEditor.Operation> ParseOperations(Dictionary<string, object?> args, List<string> errors)
    {
        var operations = new List<EmptyRowsEditor.Operation>();
        var array = Support.GetArray(args, "operations", errors);
        if (array == null || array.Count == 0)
        {
            if (errors.Count == 0)
                errors.Add("operations is required: a list of { action: add|update|remove, uniqueId?, reaNr?, insert?, values? }.");
            return operations;
        }

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JObject item)
            {
                errors.Add($"Operation {i + 1} must be an object.");
                continue;
            }

            var operation = new EmptyRowsEditor.Operation
            {
                Action = Support.Text(item["action"]),
                UniqueId = Support.Text(item["uniqueId"]) is { Length: > 0 } id ? id : null,
                Insert = item["insert"]?.Type == JTokenType.Boolean && item["insert"]!.Value<bool>()
            };

            var reaNr = item["reaNr"];
            if (reaNr != null && reaNr.Type != JTokenType.Null)
            {
                if (int.TryParse(Support.Text(reaNr), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    operation.ReaNr = number;
                else
                    errors.Add($"Operation {i + 1}: reaNr must be a whole number.");
            }

            var values = item["values"];
            if (values is JObject valueObject)
            {
                foreach (var property in valueObject.Properties())
                    operation.Values[property.Name] = Support.ToNode(property.Value) ?? JsonValue.Create(string.Empty);
            }
            else if (values != null && values.Type != JTokenType.Null)
            {
                errors.Add($"Operation {i + 1}: values must be an object of field: value.");
            }
            operations.Add(operation);
        }
        return operations;
    }
}
