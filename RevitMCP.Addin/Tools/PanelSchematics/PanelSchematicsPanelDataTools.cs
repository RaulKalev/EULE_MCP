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
using Data = RevitMCP.Addin.PanelSchematics.PanelSchematicsProjectData;
using StoreState = RevitMCP.Addin.PanelSchematics.PanelSchematicsStorage.StoreState;
using Support = RevitMCP.Addin.Tools.PanelSchematics.PanelSchematicsToolSupport;

namespace RevitMCP.Addin.Tools.PanelSchematics;

/// <summary>revit_panelschematics_preview_set_panel_data: what a panel-data write would change, no changes.</summary>
public sealed class PanelSchematicsPreviewSetPanelDataTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_preview_set_panel_data";
    public string Description =>
        "Previews changing the PanelSchematics data kept per panel and for the project WITHOUT changing anything: title-page metadata, " +
        "Üheaegsustegur, Pingelang, cable-length override, orientation, colour label, info detail item, schematic view, main panel and " +
        "colour labels. Returns every value before and after and a state token for the write.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(PanelDataExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_panelschematics_set_panel_data: writes the plugin's per-panel and project data to Extensible Storage.</summary>
public sealed class PanelSchematicsSetPanelDataTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_set_panel_data";
    public string Description =>
        "Writes the PanelSchematics data kept per panel and for the project to Extensible Storage (requires approval). Only the given " +
        "values change; everything else in the stores is kept; a backup is written first; refused while the plugin's table window is open. " +
        "Run the preview first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(PanelDataExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class PanelDataExecutor
{
    private sealed class Change
    {
        public string Scope = string.Empty;
        public string Item = string.Empty;
        public string? Before;
        public string? After;
    }

    private sealed class RecordWrite
    {
        public FamilyInstance Panel = null!;
        public string Json = string.Empty;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = Support.ActiveDocument(uiapp, request, out var failure);
        if (doc == null) return failure!;

        if (apply && Support.WriteGuard(doc, request) is { } blocked) return blocked;

        var errors = new List<string>();
        var args = request.Arguments;
        var panelItems = Support.GetArray(args, "panels", errors);
        var labelItems = Support.GetArray(args, "colorLabels", errors);
        var removeLabels = ToolArguments.GetStringArray(args, "removeColorLabelIds");
        var mainPanelArg = ToolArguments.GetString(args, "mainPanel").Trim();
        if (errors.Count > 0)
            return Support.Fail(request, "Nothing was written. " + errors[0], errors: errors);
        if ((panelItems == null || panelItems.Count == 0) && (labelItems == null || labelItems.Count == 0)
            && removeLabels.Length == 0 && mainPanelArg.Length == 0)
            return Support.Fail(request, "Nothing to do: give panels, mainPanel, colorLabels or removeColorLabelIds. " + Support.GuideHint);

        var project = Data.Read(doc);
        var changes = new List<Change>();
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var recordWrites = new List<RecordWrite>();
        var recordStates = new List<string?>();

        // Working copies; a store is written only when it is in 'touched'
        var metadata = project.Metadata.TextMap(Data.MetadataField).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var factors = new Dictionary<string, double>(project.Factors, StringComparer.Ordinal);
        var drops = new Dictionary<string, double>(project.Drops, StringComparer.Ordinal);
        var overrides = new Dictionary<string, double>(project.CableLengths.NumberMap(Data.OverridesField), StringComparer.Ordinal);
        var originals = new Dictionary<string, double>(project.CableLengths.NumberMap(Data.OriginalsField), StringComparer.Ordinal);
        var orientations = new Dictionary<string, double>(project.Orientations.NumberMap(Data.OrientationsField), StringComparer.Ordinal);
        var links = new Dictionary<string, string>(project.Links, StringComparer.Ordinal);
        var mainPanel = project.MainPanelId;
        var colorAssignments = new Dictionary<string, string?>(StringComparer.Ordinal);
        var seenPanels = new HashSet<long>();

        for (int i = 0; panelItems != null && i < panelItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (panelItems[i] is not JObject item)
            {
                errors.Add($"Panel entry {i + 1} must be an object.");
                continue;
            }
            var panel = PanelSchematicsModel.ResolvePanel(doc, Support.Text(item["panel"]), out var panelError);
            if (panel == null)
            {
                errors.Add($"Panel entry {i + 1}: {panelError}");
                continue;
            }
            if (!seenPanels.Add(panel.Id.Value))
            {
                errors.Add($"Panel '{panel.Name}' is given twice.");
                continue;
            }
            var key = Data.Key(panel.Id);
            var scope = $"{panel.Name} ({key})";

            foreach (var property in item.Properties())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "panel":
                        break;

                    case "metadata":
                        if (value is not JObject fields)
                        {
                            errors.Add($"{scope}: metadata must be an object of key: value (keys such as EA_2).");
                            break;
                        }
                        foreach (var entry in fields.Properties())
                        {
                            var field = Contract.FindMetadataField(entry.Name);
                            if (field == null)
                            {
                                errors.Add($"{scope}: '{entry.Name}' is not a metadata key. Keys: {string.Join(", ", Contract.MetadataFields.Select(f => f.Key))}.");
                                continue;
                            }
                            var mapKey = Contract.MetadataMapKey(panel.Id.Value, field.Key);
                            metadata.TryGetValue(mapKey, out var before);
                            var after = Support.Text(entry.Value).Trim();
                            if (after.Length == 0) metadata.Remove(mapKey); else metadata[mapKey] = after;
                            Note(changes, touched, Contract.MetadataKey, scope, $"{field.Key} {field.Label}", before, after.Length == 0 ? null : after);
                        }
                        break;

                    case "uheaegsustegur":
                        if (!TryNumber(value, out var factor) || factor < 0 || factor > 1)
                        {
                            errors.Add($"{scope}: uheaegsustegur must be a factor from 0 to 1 (0.8, not 80).");
                            break;
                        }
                        Note(changes, touched, Contract.OverviewValuesKey, scope, "Üheaegsustegur", Shown(factors, key), Shown(factor));
                        factors[key] = factor;
                        break;

                    case "pingelang":
                        if (!TryNumber(value, out var drop) || drop < 0 || drop > 100)
                        {
                            errors.Add($"{scope}: pingelang must be a percent from 0 to 100.");
                            break;
                        }
                        Note(changes, touched, Contract.OverviewValuesKey, scope, "Pingelang %", Shown(drops, key), Shown(drop));
                        drops[key] = drop;
                        break;

                    case "cableLengthOverrideM":
                        if (!TryNumber(value, out var length) || length <= 0)
                        {
                            errors.Add($"{scope}: cableLengthOverrideM must be a length in metres above 0. Use clearCableLengthOverride to remove it.");
                            break;
                        }
                        Note(changes, touched, Contract.CableLengthKey, scope, "Cable length override (m)", Shown(overrides, key), Shown(length));
                        overrides[key] = length;
                        break;

                    case "cableLengthOriginalM":
                        if (!TryNumber(value, out var original) || original < 0)
                        {
                            errors.Add($"{scope}: cableLengthOriginalM must be a length in metres.");
                            break;
                        }
                        Note(changes, touched, Contract.CableLengthKey, scope, "Revit cable length at override time (m)", Shown(originals, key), Shown(original));
                        originals[key] = original;
                        break;

                    case "clearCableLengthOverride":
                        if (!IsTrue(value)) break;
                        Note(changes, touched, Contract.CableLengthKey, scope, "Cable length override (m)", Shown(overrides, key), null);
                        overrides.Remove(key);
                        originals.Remove(key);
                        break;

                    case "horizontal":
                        if (value.Type != JTokenType.Boolean)
                        {
                            errors.Add($"{scope}: horizontal must be true or false.");
                            break;
                        }
                        var horizontal = value.Value<bool>();
                        Note(changes, touched, Contract.OrientationsKey, scope, "Horizontal box",
                            orientations.TryGetValue(key, out var was) ? (was > 0.5 ? "true" : "false") : null, horizontal ? "true" : "false");
                        orientations[key] = horizontal ? 1.0 : 0.0;
                        break;

                    case "colorLabelId":
                        colorAssignments[key] = Support.Text(value).Trim();
                        break;

                    case "infoDetailItemId":
                        if (!long.TryParse(Support.Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var detailId)
                            || doc.GetElement(new ElementId(detailId)) is not FamilyInstance detail
                            || detail.Category == null || detail.Category.Id.Value != (long)BuiltInCategory.OST_DetailComponents)
                        {
                            errors.Add($"{scope}: infoDetailItemId must be the element id of a detail item (the title-page family instance).");
                            break;
                        }
                        links.TryGetValue(key, out var linkedBefore);
                        Note(changes, touched, Contract.InfoLinksKey, scope, "Info detail item", linkedBefore, detailId.ToString(CultureInfo.InvariantCulture));
                        links[key] = detailId.ToString(CultureInfo.InvariantCulture);
                        break;

                    case "clearInfoLink":
                        if (!IsTrue(value)) break;
                        links.TryGetValue(key, out var linked);
                        Note(changes, touched, Contract.InfoLinksKey, scope, "Info detail item", linked, null);
                        links.Remove(key);
                        break;

                    case "schematicViewId":
                        PlanRecord(doc, panel, scope, value, changes, recordWrites, recordStates, errors);
                        break;

                    default:
                        errors.Add($"{scope}: '{property.Name}' is not a panel value. Values: metadata, uheaegsustegur, pingelang, cableLengthOverrideM, " +
                                   "cableLengthOriginalM, clearCableLengthOverride, horizontal, colorLabelId, infoDetailItemId, clearInfoLink, schematicViewId.");
                        break;
                }
            }
        }

        if (mainPanelArg.Length > 0)
        {
            string after;
            string? afterShown;
            if (mainPanelArg.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                after = string.Empty;
                afterShown = null;
            }
            else
            {
                var main = PanelSchematicsModel.ResolvePanel(doc, mainPanelArg, out var mainError);
                if (main == null)
                    errors.Add("mainPanel: " + mainError + " Pass 'none' to clear it.");
                after = main == null ? mainPanel : Data.Key(main.Id);
                afterShown = main == null ? null : $"{main.Name} ({after})";
            }
            Note(changes, touched, Contract.MainPanelKey, "project", "Main panel", mainPanel.Length == 0 ? null : mainPanel, afterShown);
            mainPanel = after;
        }

        // Colours: labels first, so a panel can be given a label made in the same call
        string? colorsJson = null;
        if ((labelItems != null && labelItems.Count > 0) || removeLabels.Length > 0 || colorAssignments.Count > 0)
        {
            var edits = new List<PanelColorsPayload.LabelEdit>();
            foreach (var labelItem in labelItems ?? new JArray())
            {
                if (labelItem is not JObject label)
                {
                    errors.Add("colorLabels entries must be objects of { id?, hex?, label? }.");
                    continue;
                }
                edits.Add(new PanelColorsPayload.LabelEdit
                {
                    Id = label["id"] == null ? null : Support.Text(label["id"]),
                    Hex = label["hex"] == null ? null : Support.Text(label["hex"]),
                    Label = label["label"] == null ? null : Support.Text(label["label"])
                });
            }
            var colors = PanelColorsPayload.Apply(project.Colors.Text(Data.ColorsField), edits, removeLabels, colorAssignments,
                apply ? null : NewPreviewId());
            errors.AddRange(colors.Errors);
            if (colors.Errors.Count == 0 && colors.Changed)
            {
                colorsJson = colors.Json;
                touched.Add(Contract.ColorsKey);
                changes.AddRange(colors.Changes.Select(c => new Change { Scope = "colours", Item = c }));
            }
        }

        // Every store that is written must have been readable, and so must its legacy copy
        foreach (var storeKey in touched)
        {
            var problem = project.All.Where(s => s.Key == storeKey || s.Key == storeKey + "Legacy").Select(s => s.State)
                .FirstOrDefault(s => !s.Readable);
            if (problem != null)
                errors.Add((problem.Problem ?? storeKey + " cannot be read.") + " It is not overwritten.");
        }

        if (errors.Count > 0)
            return Support.Fail(request, "Nothing was written. " + errors[0] + " " + Support.GuideHint, errors: errors);

        changes.RemoveAll(c => c.Item.Length > 0 && c.Before == c.After && c.Scope != "colours");
        var warnings = new List<string>();
        if (touched.Contains(Contract.OverviewValuesKey) && !project.Overview.Exists && project.OverviewLegacy.Exists)
            warnings.Add("The first write copies the legacy PanelOverviewValues entries into PanelSchematicsOverviewValuesV2, as the plugin does.");
        if (touched.Contains(Contract.InfoLinksKey) && !project.InfoLinks.Exists && project.InfoLinksLegacy.Exists)
            warnings.Add("The first write copies the legacy PanelInfoLinks entries into PanelSchematicsPanelInfoLinksV2, as the plugin does.");
        if (touched.Contains(Contract.MetadataKey))
            warnings.Add("Metadata is stored for the plugin; the title-page detail item is updated when the user refreshes Kilbi andmed in the plugin.");

        var token = EmptyRowsEditor.StateToken(new[] { project.Token() }.Concat(recordStates).ToArray());
        object Summary() => new
        {
            changes = changes.Select(c => new { scope = c.Scope, item = c.Item, before = c.Before, after = c.After }),
            storesWritten = touched.OrderBy(k => k, StringComparer.Ordinal).Concat(recordWrites.Count > 0 ? new[] { Contract.PanelRecordKey } : Array.Empty<string>()),
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
                Message = changes.Count == 0 ? "Nothing would change." : $"Would change {changes.Count} PanelSchematics value(s).",
                Data = Summary(),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (changes.Count == 0)
            return new McpToolResult
            {
                RequestId = request.RequestId, Success = true, Message = "Nothing to change: every value is already stored.",
                Data = Summary(), DurationMs = sw.ElapsedMilliseconds
            };

        if (Support.CheckToken(request, token) is { } stale)
            return Support.Fail(request, stale, "state_changed");

        var notEditable = new List<string>();
        if (touched.Count > 0 && PanelSchematicsStorage.WhyNotEditable(doc, doc.ProjectInformation) is { } projectBlocked)
            notEditable.Add(projectBlocked);
        foreach (var record in recordWrites)
            if (PanelSchematicsStorage.WhyNotEditable(doc, record.Panel) is { } panelBlocked)
                notEditable.Add(panelBlocked);
        if (notEditable.Count > 0)
            return Support.Fail(request, "Nothing was written. " + notEditable[0], "not_editable", errors: notEditable);

        // New states: the whole store is replaced, so each starts from what was read
        var writes = new List<(Contract.Store Store, StoreState State)>();
        if (touched.Contains(Contract.MetadataKey))
            writes.Add((Contract.GetStore(Contract.MetadataKey), TextMapState(Data.MetadataField, metadata)));
        if (touched.Contains(Contract.OverviewValuesKey))
        {
            var state = new StoreState();
            state.NumberMaps[Data.FactorsField] = factors;
            state.NumberMaps[Data.DropsField] = drops;
            writes.Add((Contract.GetStore(Contract.OverviewValuesKey), state));
        }
        if (touched.Contains(Contract.CableLengthKey))
        {
            var state = new StoreState();
            state.NumberMaps[Data.OverridesField] = overrides;
            state.NumberMaps[Data.OriginalsField] = originals;
            writes.Add((Contract.GetStore(Contract.CableLengthKey), state));
        }
        if (touched.Contains(Contract.OrientationsKey))
        {
            var state = new StoreState();
            state.NumberMaps[Data.OrientationsField] = orientations;
            writes.Add((Contract.GetStore(Contract.OrientationsKey), state));
        }
        if (touched.Contains(Contract.InfoLinksKey))
            writes.Add((Contract.GetStore(Contract.InfoLinksKey), TextMapState(Data.LinksField, links)));
        if (touched.Contains(Contract.MainPanelKey))
            writes.Add((Contract.GetStore(Contract.MainPanelKey), TextState(Data.MainPanelField, mainPanel)));
        if (touched.Contains(Contract.ColorsKey))
            writes.Add((Contract.GetStore(Contract.ColorsKey), TextState(Data.ColorsField, colorsJson!)));

        string backupPath;
        try
        {
            backupPath = PanelSchematicsModel.WriteBackup(doc, "panel-data", Backup(project, recordWrites));
        }
        catch (Exception ex)
        {
            return Support.Fail(request, $"The backup could not be written ({ex.Message}), so nothing was changed.", "backup_failed");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var recordStore = Contract.GetStore(Contract.PanelRecordKey);
        var transaction = RevitTransactionRunner.Run(doc, "Revit MCP - PanelSchematics panel data", () =>
        {
            var host = doc.ProjectInformation;
            foreach (var (store, state) in writes)
            {
                PanelSchematicsStorage.Write(host, store, state);
                if (!Same(PanelSchematicsStorage.Read(host, store), state, store))
                    throw new InvalidOperationException($"{store.SchemaName} does not hold what was written.");
            }
            foreach (var record in recordWrites)
            {
                PanelSchematicsStorage.Write(record.Panel, recordStore, TextState("PanelRecordJson", record.Json));
                if (PanelSchematicsStorage.Read(record.Panel, recordStore).Text("PanelRecordJson") != record.Json)
                    throw new InvalidOperationException($"The schematic view record of '{record.Panel.Name}' does not hold what was written.");
            }
        });

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = transaction.Success,
            Status = transaction.Success ? null : "transaction_failed",
            Message = transaction.Success
                ? $"Wrote {changes.Count} PanelSchematics value(s)."
                : $"Writing the panel data failed and was rolled back: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}",
            Data = new { result = Summary(), backupFile = backupPath, transaction = transaction.Diagnostics },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    // ------------------------------------------------------------------ schematic view record

    private static void PlanRecord(Document doc, FamilyInstance panel, string scope, JToken value, List<Change> changes,
        List<RecordWrite> recordWrites, List<string?> recordStates, List<string> errors)
    {
        if (!long.TryParse(Support.Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var viewId)
            || doc.GetElement(new ElementId(viewId)) is not ViewPlan view || view.IsTemplate)
        {
            errors.Add($"{scope}: schematicViewId must be the element id of a plan view (not a template); the plugin draws schematics on floor plans.");
            return;
        }

        var state = PanelSchematicsStorage.Read(panel, Contract.GetStore(Contract.PanelRecordKey));
        var stored = state.Text("PanelRecordJson");
        recordStates.Add(panel.Id.Value + ":" + stored);
        PanelRecordPayload.Record? current = null;
        if (!state.Readable || (state.Exists && PanelRecordPayload.TryParse(stored, out current) != RowGroupsPayload.ParseStatus.Ok))
        {
            errors.Add($"{scope}: the stored schematic view record cannot be read (damaged or from a newer plugin). It is not overwritten.");
            return;
        }

        var record = new PanelRecordPayload.Record
        {
            PanelUniqueId = panel.UniqueId,
            ViewUniqueId = view.UniqueId,
            PanelName = panel.Name ?? string.Empty,
            ViewName = view.Name ?? string.Empty
        };
        var before = current == null ? null : (doc.GetElement(current.ViewUniqueId) as View)?.Name ?? current.ViewName;
        if (current != null && current.PanelUniqueId == record.PanelUniqueId && current.ViewUniqueId == record.ViewUniqueId)
            return;

        changes.Add(new Change { Scope = scope, Item = "Schematic view", Before = before, After = $"{view.Name} ({viewId})" });
        recordWrites.Add(new RecordWrite { Panel = panel, Json = PanelRecordPayload.Serialize(record) });
    }

    // ------------------------------------------------------------------ helpers

    private static void Note(List<Change> changes, HashSet<string> touched, string storeKey, string scope, string item, string? before, string? after)
    {
        changes.Add(new Change { Scope = scope, Item = item, Before = before, After = after });
        if (before != after) touched.Add(storeKey);
    }

    private static Func<string> NewPreviewId()
    {
        int n = 0;
        return () => "(new label " + ++n + ")";
    }

    private static bool TryNumber(JToken token, out double number)
    {
        number = 0;
        return (token.Type is JTokenType.Integer or JTokenType.Float or JTokenType.String)
               && double.TryParse(Support.Text(token).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
               && !double.IsNaN(number) && !double.IsInfinity(number);
    }

    private static bool IsTrue(JToken token) => token.Type == JTokenType.Boolean && token.Value<bool>();

    private static string Shown(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string? Shown(Dictionary<string, double> map, string key) => map.TryGetValue(key, out var value) ? Shown(value) : null;

    private static StoreState TextState(string field, string value)
    {
        var state = new StoreState();
        state.Texts[field] = value;
        return state;
    }

    private static StoreState TextMapState(string field, Dictionary<string, string> map)
    {
        var state = new StoreState();
        state.TextMaps[field] = map;
        return state;
    }

    private static bool Same(StoreState stored, StoreState wanted, Contract.Store store)
    {
        if (!stored.Readable || !stored.Exists) return false;
        foreach (var field in store.Fields)
        {
            switch (field.Kind)
            {
                case Contract.StoreFieldKind.Json:
                case Contract.StoreFieldKind.Text:
                    if ((stored.Text(field.Name) ?? string.Empty) != (wanted.Text(field.Name) ?? string.Empty)) return false;
                    break;
                case Contract.StoreFieldKind.TextMap:
                    var a = stored.TextMap(field.Name);
                    var b = wanted.TextMap(field.Name);
                    if (a.Count != b.Count || a.Any(p => !b.TryGetValue(p.Key, out var other) || other != p.Value)) return false;
                    break;
                default:
                    var x = stored.NumberMap(field.Name);
                    var y = wanted.NumberMap(field.Name);
                    if (x.Count != y.Count || x.Any(p => !y.TryGetValue(p.Key, out var other) || Math.Abs(other - p.Value) > 1e-9)) return false;
                    break;
            }
        }
        return true;
    }

    private static JObject Backup(Data project, List<RecordWrite> recordWrites)
    {
        var stores = new JObject();
        foreach (var (key, state) in project.All)
        {
            stores[key] = new JObject
            {
                ["exists"] = state.Exists,
                ["texts"] = JObject.FromObject(state.Texts),
                ["textMaps"] = JObject.FromObject(state.TextMaps),
                ["numberMaps"] = JObject.FromObject(state.NumberMaps)
            };
        }
        var records = new JObject();
        foreach (var record in recordWrites)
            records[record.Panel.Id.Value.ToString(CultureInfo.InvariantCulture)] =
                PanelSchematicsStorage.Read(record.Panel, Contract.GetStore(Contract.PanelRecordKey)).Text("PanelRecordJson");
        return new JObject { ["projectInformation"] = stores, ["panelRecords"] = records };
    }
}
