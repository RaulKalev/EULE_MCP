using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.PanelSchematics;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;
using Support = RevitMCP.Addin.Tools.PanelSchematics.PanelSchematicsToolSupport;

namespace RevitMCP.Addin.Tools.PanelSchematics;

/// <summary>revit_panelschematics_preview_set_circuit_rows: which parameters a circuit-row write would set, no changes.</summary>
public sealed class PanelSchematicsPreviewSetCircuitRowsTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_preview_set_circuit_rows";
    public string Description =>
        "Previews writing PanelSchematics row values to circuits WITHOUT changing anything: per row field the parameter on the " +
        "circuit, on its connected elements and on its schematic detail items, with before/after and whether it can be set.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CircuitRowsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_panelschematics_set_circuit_rows: writes row values to circuits the way the plugin's Kirjuta does.</summary>
public sealed class PanelSchematicsSetCircuitRowsTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_set_circuit_rows";
    public string Description =>
        "Writes PanelSchematics row values to circuits (requires approval): each field goes to the plugin's parameter on the circuit, " +
        "is mirrored to the connected elements and to the row's schematic detail items. Only the given fields are touched; a backup is " +
        "written first; refused while the plugin's table window is open. Run the preview first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CircuitRowsExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class CircuitRowsExecutor
{
    private sealed class RowEdit
    {
        public ElectricalSystem Circuit = null!;
        public Dictionary<string, object?> Values = new();
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = Support.ActiveDocument(uiapp, request, out var failure);
        if (doc == null) return failure!;

        if (apply && Support.WriteGuard(doc, request) is { } blocked) return blocked;

        var errors = new List<string>();
        var rows = Support.GetArray(request.Arguments, "rows", errors);
        if (errors.Count == 0 && (rows == null || rows.Count == 0))
            errors.Add("rows is required: a list of { circuitId, values: { field: value } }.");

        var edits = new List<RowEdit>();
        for (int i = 0; rows != null && i < rows.Count; i++)
        {
            var where = $"Row {i + 1}";
            if (rows[i] is not JObject item)
            {
                errors.Add($"{where} must be an object.");
                continue;
            }
            if (!long.TryParse(Support.Text(item["circuitId"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var circuitId)
                || doc.GetElement(new ElementId(circuitId)) is not ElectricalSystem circuit)
            {
                errors.Add($"{where}: circuitId '{Support.Text(item["circuitId"])}' is not an electrical circuit. Empty rows are changed with revit_panelschematics_edit_empty_rows.");
                continue;
            }
            if (edits.Any(e => e.Circuit.Id == circuit.Id))
            {
                errors.Add($"{where}: circuit {circuitId} is given twice.");
                continue;
            }
            if (item["values"] is not JObject valueObject || valueObject.Count == 0)
            {
                errors.Add($"{where}: values is required, an object of field: value.");
                continue;
            }
            var values = Support.CoerceCircuitValues(valueObject, where, circuitRow: true, errors);
            if (values != null)
                edits.Add(new RowEdit { Circuit = circuit, Values = values });
        }
        if (errors.Count > 0)
            return Support.Fail(request, "Nothing was written. " + errors[0] + " " + Support.GuideHint, errors: errors);

        // One Rea nr per row of a panel, counting the panel's empty rows
        foreach (var byPanel in edits.Where(e => e.Values.ContainsKey("ReaNr")).GroupBy(e => PanelIdOf(e.Circuit)))
        {
            if (byPanel.Key <= 0 || doc.GetElement(new ElementId(byPanel.Key)) is not FamilyInstance panel) continue;
            var table = PanelTable.Read(doc, panel);
            var numbers = table.Rows.Where(r => !r.IsCircuit || byPanel.All(e => e.Circuit.Id.Value != r.CircuitId)).Select(r => r.ReaNr).ToList();
            numbers.AddRange(byPanel.Select(e => (int)e.Values["ReaNr"]!));
            foreach (var edit in byPanel)
            {
                var number = (int)edit.Values["ReaNr"]!;
                if (number <= 0)
                    errors.Add($"Circuit {edit.Circuit.Id.Value}: Rea nr must be 1 or more.");
                else if (numbers.Count(n => n == number) > 1)
                    errors.Add($"Circuit {edit.Circuit.Id.Value}: Rea nr {number} would be used by more than one row of '{panel.Name}'.");
            }
        }
        if (errors.Count > 0)
            return Support.Fail(request, "Nothing was written. " + errors[0], errors: errors);

        var warnings = new List<string>();
        var detailItems = PanelSchematicsModel.DetailItemsByRowId(doc);
        var options = new PanelSchematicsModel.RowWriteOptions
        {
            ConnectedElements = ToolArguments.GetBool(request.Arguments, "updateConnectedElements", true),
            DetailItems = ToolArguments.GetBool(request.Arguments, "updateDetailItems", true)
        };

        List<PanelSchematicsModel.ParameterWrite> Run(bool write, List<string> sink)
        {
            options.Apply = write;
            var writes = new List<PanelSchematicsModel.ParameterWrite>();
            foreach (var edit in edits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writes.AddRange(PanelSchematicsModel.WriteCircuitRow(doc, edit.Circuit, edit.Values, detailItems, options, sink));
            }
            return writes;
        }

        var planned = Run(write: false, warnings);
        foreach (var problem in planned.Where(w => w.Target == "circuit" && w.Status is "missing" or "readOnly" or "invalid"))
            warnings.Add($"Circuit {problem.ElementId}: '{problem.Parameter}' ({problem.Field}) is {problem.Status}; it will not be written.");
        Support.AddMirrorWarnings(planned, warnings);
        foreach (var edit in edits.Where(e => PanelSchematicsModel.RowIdOf(e.Circuit).Length == 0 && options.DetailItems))
            warnings.Add($"Circuit {edit.Circuit.Id.Value} has no 'id' value yet, so it has no detail items to update (the plugin assigns it when the row is drawn).");

        Support.AddSymbolWarnings(edits.Where(e => e.Values.ContainsKey("Symbol")).Select(e => e.Values["Symbol"] as string), warnings);

        var token = EmptyRowsEditor.StateToken(planned.Select(w => w.ElementId + "|" + w.Parameter + "|" + w.Before).ToArray());
        int toSet = planned.Count(w => w.Status == "set");

        object Summary(IEnumerable<PanelSchematicsModel.ParameterWrite> writes) => new
        {
            circuits = edits.Select(e => new
            {
                circuitId = e.Circuit.Id.Value,
                name = e.Circuit.Name,
                rowId = PanelSchematicsModel.RowIdOf(e.Circuit),
                writes = writes.Where(w => w.Target == "circuit" && w.ElementId == e.Circuit.Id.Value).Select(EmptyRowsExecutor.Describe)
            }),
            mirroredWrites = writes.Where(w => w.Target != "circuit" && w.Status != "unchanged").Select(EmptyRowsExecutor.Describe),
            counts = new
            {
                set = writes.Count(w => w.Status == "set"),
                unchanged = writes.Count(w => w.Status == "unchanged"),
                notWritable = writes.Count(w => w.Status is "missing" or "readOnly" or "invalid"),
                notOnElement = writes.Count(w => w.Status == PanelSchematicsModel.NotOnElement)
            },
            stateToken = token
        };

        if (!apply)
        {
            Support.AddPluginWindowWarnings(warnings);
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Would set {toSet} parameter value(s) for {edits.Count} circuit row(s).",
                Data = Summary(planned),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (toSet == 0)
            return new McpToolResult
            {
                RequestId = request.RequestId, Success = true, Message = "Nothing to change: every value is already set (or cannot be written).",
                Data = Summary(planned), Warnings = warnings, DurationMs = sw.ElapsedMilliseconds
            };

        if (Support.CheckToken(request, token) is { } stale)
            return Support.Fail(request, stale, "state_changed");

        var notEditable = new List<string>();
        foreach (var elementId in planned.Where(w => w.Status == "set").Select(w => w.ElementId).Distinct())
            if (doc.GetElement(new ElementId(elementId)) is { } element && PanelSchematicsStorage.WhyNotEditable(doc, element) is { } why)
                notEditable.Add(why);
        if (notEditable.Count > 0)
            return Support.Fail(request, "Nothing was written. " + notEditable[0], "not_editable", errors: notEditable);

        string backupPath;
        try
        {
            backupPath = PanelSchematicsModel.WriteBackup(doc, "circuit-rows", new JObject
            {
                ["parameterWrites"] = JArray.FromObject(planned.Where(w => w.Status == "set").Select(EmptyRowsExecutor.Describe))
            });
        }
        catch (Exception ex)
        {
            return Support.Fail(request, $"The backup could not be written ({ex.Message}), so nothing was changed.", "backup_failed");
        }

        var written = new List<PanelSchematicsModel.ParameterWrite>();
        var transaction = RevitTransactionRunner.Run(doc, "Revit MCP - PanelSchematics circuit rows", () => written = Run(write: true, new List<string>()));

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = transaction.Success,
            Status = transaction.Success ? null : "transaction_failed",
            Message = transaction.Success
                ? $"Set {written.Count(w => w.Status == "set")} parameter value(s) for {edits.Count} circuit row(s)."
                : $"Writing the circuit rows failed and was rolled back: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}",
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

    private static long PanelIdOf(ElectricalSystem circuit)
    {
        try { return circuit.BaseEquipment?.Id.Value ?? -1L; }
        catch { return -1L; }
    }
}
