using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.PanelSchematics;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;
using Contract = RevitMCP.Addin.PanelSchematics.PanelSchematicsContract;
using Support = RevitMCP.Addin.Tools.PanelSchematics.PanelSchematicsToolSupport;

namespace RevitMCP.Addin.Tools.PanelSchematics;

/// <summary>revit_panelschematics_preview_set_row_groups: what replacing a panel's row groups would store, no changes.</summary>
public sealed class PanelSchematicsPreviewSetRowGroupsTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_preview_set_row_groups";
    public string Description =>
        "Previews replacing the PanelSchematics row groups of a panel WITHOUT changing anything: the groups stored now, the groups " +
        "that would be stored (rows resolved to identities) and a state token for the write.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(RowGroupsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_panelschematics_set_row_groups: stores a panel's row groups.</summary>
public sealed class PanelSchematicsSetRowGroupsTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_set_row_groups";
    public string Description =>
        "Replaces the PanelSchematics row groups of a panel in its Extensible Storage (requires approval). 'groups' is the complete " +
        "wanted list; an empty list removes all groups. Refused when the stored groups cannot be read or the plugin's table window is " +
        "open; a backup is written first. Run the preview first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(RowGroupsExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class RowGroupsExecutor
{
    private const string Field = "RowGroupsJson";

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = Support.ActiveDocument(uiapp, request, out var failure);
        if (doc == null) return failure!;

        if (apply && Support.WriteGuard(doc, request) is { } blocked) return blocked;

        var panel = PanelSchematicsModel.ResolvePanel(doc, ToolArguments.GetString(request.Arguments, "panel"), out var panelError);
        if (panel == null) return Support.Fail(request, panelError!);

        var errors = new List<string>();
        var items = Support.GetArray(request.Arguments, "groups", errors);
        if (errors.Count == 0 && items == null)
            errors.Add("groups is required: the complete list of { id?, parent, dependents: [...], links?: [...] }; [] removes all groups.");
        if (errors.Count > 0)
            return Support.Fail(request, "Nothing was written. " + errors[0], errors: errors);

        var store = Contract.GetStore(Contract.RowGroupsKey);
        var state = PanelSchematicsStorage.Read(panel, store);
        var storedJson = state.Exists ? state.Text(Field) : null;
        var stored = new List<RowGroupsPayload.Group>();
        if (!state.Readable || (state.Exists && RowGroupsPayload.TryParse(storedJson, out stored) != RowGroupsPayload.ParseStatus.Ok))
            return Support.Fail(request,
                (state.Problem ?? $"The stored row groups of '{panel.Name}' cannot be read (inconsistent, or written by a newer plugin).") +
                " They are not overwritten.", "stored_data_unreadable");

        var table = PanelTable.Read(doc, panel);
        var known = new HashSet<string>(table.Rows.Select(r => r.Identity), StringComparer.Ordinal);
        var kept = new HashSet<string>(stored.SelectMany(g => g.Dependents.Concat(new[] { g.Parent })), StringComparer.Ordinal);
        var storedIds = new HashSet<string>(stored.Select(g => g.Id), StringComparer.Ordinal);

        var groups = new List<RowGroupsPayload.Group>();
        for (int i = 0; i < items!.Count; i++)
        {
            var where = $"Group {i + 1}";
            if (items[i] is not JObject item)
            {
                errors.Add($"{where} must be an object.");
                continue;
            }

            var group = new RowGroupsPayload.Group { Id = Support.Text(item["id"]).Trim() };
            if (group.Id.Length == 0)
                group.Id = apply ? Guid.NewGuid().ToString() : $"(new group {i + 1})";

            group.Parent = ResolveRow(doc, Support.Text(item["parent"]), table, known, kept, where + " parent", errors) ?? string.Empty;
            foreach (var dependent in item["dependents"] as JArray ?? new JArray())
                if (ResolveRow(doc, Support.Text(dependent), table, known, kept, where + " dependent", errors) is { } identity)
                    group.Dependents.Add(identity);
            foreach (var link in item["links"] as JArray ?? new JArray())
                if (ResolveLink(doc, Support.Text(link), where, errors) is { } uniqueId)
                    group.Links.Add(uniqueId);
            groups.Add(group);
        }
        if (errors.Count == 0)
            errors.AddRange(RowGroupsPayload.Validate(groups));
        if (errors.Count > 0)
            return Support.Fail(request, "Nothing was written. " + errors[0] + " Row identities are in revit_panelschematics_get_panel_table.", errors: errors);

        // Keep the stored form of a group that is unchanged, so only real changes show
        var newJson = RowGroupsPayload.Serialize(groups);
        var unchanged = state.Exists
            ? string.Equals(RowGroupsPayload.Serialize(stored), newJson, StringComparison.Ordinal)
            : groups.Count == 0;
        var token = EmptyRowsEditor.StateToken(storedJson);

        var warnings = new List<string>();
        if (stored.Any(g => !groups.Any(n => n.Id == g.Id)))
            warnings.Add($"{stored.Count(g => !groups.Any(n => n.Id == g.Id))} stored group(s) are not in the new list and would be removed.");

        object Summary() => new
        {
            panel = Support.DescribePanel(panel),
            before = stored.Select(Describe),
            after = groups.Select(Describe),
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
                Message = unchanged ? $"The row groups of '{panel.Name}' would not change." : $"Would store {groups.Count} row group(s) on '{panel.Name}' (now {stored.Count}).",
                Data = Summary(),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (unchanged)
            return new McpToolResult
            {
                RequestId = request.RequestId, Success = true, Message = $"The row groups of '{panel.Name}' are already as given.",
                Data = Summary(), DurationMs = sw.ElapsedMilliseconds
            };

        if (Support.CheckToken(request, token) is { } stale)
            return Support.Fail(request, stale, "state_changed");
        if (PanelSchematicsStorage.WhyNotEditable(doc, panel) is { } notEditable)
            return Support.Fail(request, "Nothing was written. " + notEditable, "not_editable");
        if (RowGroupsPayload.TryParse(newJson, out _) != RowGroupsPayload.ParseStatus.Ok)
            return Support.Fail(request, "The new row groups would not be readable by the plugin, so nothing was written.");

        string backupPath;
        try
        {
            backupPath = PanelSchematicsModel.WriteBackup(doc, "row-groups_" + panel.Name, new JObject
            {
                ["panelId"] = panel.Id.Value,
                ["panelName"] = panel.Name,
                ["rowGroupsJson"] = storedJson
            });
        }
        catch (Exception ex)
        {
            return Support.Fail(request, $"The backup could not be written ({ex.Message}), so nothing was changed.", "backup_failed");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var transaction = RevitTransactionRunner.Run(doc, "Revit MCP - PanelSchematics row groups", () =>
        {
            var values = new PanelSchematicsStorage.StoreState();
            values.Texts[Field] = newJson;
            PanelSchematicsStorage.Write(panel, store, values);
            if (PanelSchematicsStorage.Read(panel, store).Text(Field) != newJson)
                throw new InvalidOperationException("The stored row groups do not match what was written.");
        });

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = transaction.Success,
            Status = transaction.Success ? null : "transaction_failed",
            Message = transaction.Success
                ? $"Stored {groups.Count} row group(s) on '{panel.Name}'."
                : $"Writing the row groups failed and was rolled back: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}",
            Data = new { result = Summary(), backupFile = backupPath, transaction = transaction.Diagnostics },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Describe(RowGroupsPayload.Group group) => new
    {
        id = group.Id,
        parent = group.Parent,
        dependents = group.Dependents,
        links = group.Links
    };

    /// <summary>
    /// A row as an identity. Accepts the identity itself (C:… / E:…), a circuit's element id, or an empty row's
    /// UniqueId. A row that is not in the table is accepted only when a stored group already holds it (the plugin
    /// keeps such members too).
    /// </summary>
    private static string? ResolveRow(Document doc, string text, PanelTable table, HashSet<string> known, HashSet<string> kept,
        string where, List<string> errors)
    {
        text = text.Trim();
        string? identity = null;
        if (RowGroupsPayload.IsIdentity(text))
            identity = text;
        else if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var circuitId))
            identity = table.Rows.FirstOrDefault(r => r.CircuitId == circuitId)?.Identity;
        else if (text.Length > 0)
            identity = table.Rows.FirstOrDefault(r => !r.IsCircuit && string.Equals(r.UniqueId, text, StringComparison.OrdinalIgnoreCase))?.Identity;

        if (identity == null || (!known.Contains(identity) && !kept.Contains(identity)))
        {
            errors.Add($"{where}: '{text}' is not a row of this panel. Use a row's identity, a circuit's element id or an empty row's uniqueId.");
            return null;
        }
        return identity;
    }

    private static string? ResolveLink(Document doc, string text, string where, List<string> errors)
    {
        text = text.Trim();
        Element? element = null;
        try
        {
            element = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? doc.GetElement(new ElementId(id))
                : text.Length > 0 ? doc.GetElement(text) : null;
        }
        catch
        {
            // Not a UniqueId Revit understands.
        }
        if (element == null)
        {
            errors.Add($"{where}: linked element '{text}' was not found (give an element id or UniqueId).");
            return null;
        }
        return element.UniqueId;
    }
}
