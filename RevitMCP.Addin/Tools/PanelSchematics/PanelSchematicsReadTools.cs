using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.PanelSchematics;
using RevitMCP.Core.Models;
using Contract = RevitMCP.Addin.PanelSchematics.PanelSchematicsContract;
using Support = RevitMCP.Addin.Tools.PanelSchematics.PanelSchematicsToolSupport;

namespace RevitMCP.Addin.Tools.PanelSchematics;

/// <summary>revit_panelschematics_guide: how the PanelSchematics plugin stores its data and which parameter gets which value.</summary>
public sealed class PanelSchematicsGuideTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_guide";
    public string Description =>
        "How the PanelSchematics plugin (Kilbiskeemid) works: row fields, the circuit / connected-element / detail-item parameter " +
        "each field is written to, the Extensible Storage stores, panel metadata keys, and the rules for changing them. Read this first.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var symbols = PanelSchematicsSymbols.Load();
        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = "PanelSchematics data model (" + Contract.PluginSourceRevision + ").",
            Data = new
            {
                pluginSource = Contract.PluginSourceRevision,
                rules = Contract.Rules,
                rowIdentity = new
                {
                    circuit = "Row id = the circuit's 'id' text parameter; group identity = 'C:' + circuit UniqueId.",
                    emptyRow = "Row id = the row's UniqueId (a GUID in the stored JSON); group identity = 'E:' + UniqueId.",
                    detailItems = "Detail items of a row carry the row id in their 'id' parameter."
                },
                rowFields = Contract.RowFields.Select(f => new
                {
                    name = f.Name,
                    label = f.Label,
                    type = f.Type.ToString().ToLowerInvariant(),
                    circuitParameter = f.CircuitParameter,
                    connectedElementParameter = f.ConnectedElementParameter,
                    detailItemParameter = f.DetailItemParameter,
                    onCircuitRows = f.CircuitWritable,
                    note = f.Note
                }),
                protectedRowProperties = Contract.ProtectedRowProperties,
                symbols = new
                {
                    file = symbols.Path,
                    loaded = symbols.Loaded,
                    problem = symbols.Problem,
                    note = "Valid values of the Symbol field, by category. Use the name exactly as written; the plugin maps it to a detail family and type.",
                    byCategory = symbols.Symbols.GroupBy(s => s.Category, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Select(s => s.Name).ToList())
                },
                stores = Contract.Stores.Select(s => new
                {
                    key = s.Key,
                    schemaName = s.SchemaName,
                    schemaGuid = s.SchemaGuid,
                    storedOn = s.Host.ToString(),
                    writable = s.Writable,
                    purpose = s.Purpose,
                    fields = s.Fields.Select(f => new { name = f.Name, kind = f.Kind.ToString(), note = f.Note })
                }),
                panelMetadata = new
                {
                    sections = Contract.MetadataSections,
                    fields = Contract.MetadataFields.Select(f => new
                    {
                        key = f.Key,
                        label = f.Label,
                        defaultValue = f.Default,
                        unit = f.Unit,
                        titlePageParameter = f.DetailItemParameter
                    }),
                    note = "Only the number is stored for fields with a unit. An empty value removes the stored value (the plugin then shows the default)."
                },
                tools = new[]
                {
                    "revit_panelschematics_get_panel_table: a panel's rows (circuits and empty rows) in Rea nr order.",
                    "revit_panelschematics_get_data: everything else the plugin stores, per panel and for the project.",
                    "revit_panelschematics_preview_edit_empty_rows / revit_panelschematics_edit_empty_rows: add, change, remove empty rows.",
                    "revit_panelschematics_preview_set_circuit_rows / revit_panelschematics_set_circuit_rows: row values of circuits.",
                    "revit_panelschematics_preview_set_panel_data / revit_panelschematics_set_panel_data: metadata, factors, cable lengths, colours, main panel, links, schematic view.",
                    "revit_panelschematics_preview_set_row_groups / revit_panelschematics_set_row_groups: row groups of a panel."
                }
            }
        };
        Support.AddPluginWindowWarnings(result.Warnings);
        return Task.FromResult(result);
    }
}

/// <summary>revit_panelschematics_get_panel_table: a panel's rows as the plugin's table shows them.</summary>
public sealed class PanelSchematicsGetPanelTableTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_get_panel_table";
    public string Description =>
        "A panel's PanelSchematics table: its circuits and its empty rows merged in Rea nr order, with every row's values, " +
        "identity and detail-item count. Without 'panel' it lists the panels that have rows. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = Support.ActiveDocument(uiapp, request, out var failure);
        if (doc == null) return Task.FromResult(failure!);

        var warnings = new List<string>();
        Support.AddPluginWindowWarnings(warnings);
        var selector = ToolArguments.GetString(request.Arguments, "panel");
        var emptyStore = Contract.GetStore(Contract.EmptyRowsKey);

        if (selector.Trim().Length == 0)
        {
            var circuitCounts = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Select(s => { try { return s.BaseEquipment?.Id.Value ?? -1L; } catch { return -1L; } })
                .Where(id => id > 0).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            var panels = new List<object>();
            foreach (var panel in PanelSchematicsModel.Panels(doc).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var state = PanelSchematicsStorage.Read(panel, emptyStore);
                var emptyCount = EmptyRowsEditor.TryReadRows(state.Text("EmptyRowsJson"), out var rows, out _) ? rows.Count : -1;
                circuitCounts.TryGetValue(panel.Id.Value, out var circuits);
                if (circuits == 0 && emptyCount == 0) continue;
                panels.Add(new { id = panel.Id.Value, name = panel.Name, circuits, emptyRows = emptyCount });
            }
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"{panels.Count} panel(s) with rows. Pass 'panel' (id or name) for a panel's table.",
                Data = new { panels, note = "emptyRows -1 means the stored rows could not be read." },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var target = PanelSchematicsModel.ResolvePanel(doc, selector, out var error);
        if (target == null) return Task.FromResult(Support.Fail(request, error!));

        var table = PanelTable.Read(doc, target);
        if (table.EmptyRowsProblem != null) warnings.Add(table.EmptyRowsProblem + " Empty rows cannot be changed until that is fixed in the plugin.");
        foreach (var number in table.Rows.Where(r => r.ReaNr > 0).GroupBy(r => r.ReaNr).Where(g => g.Count() > 1).Select(g => g.Key))
            warnings.Add($"Rea nr {number} is used by more than one row.");
        if (table.Rows.Any(r => r.ReaNr <= 0))
            warnings.Add("Some rows have no Rea nr (0); the plugin numbers them when its table is written.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Panel '{target.Name}': {table.Rows.Count(r => r.IsCircuit)} circuit row(s), {table.Rows.Count(r => !r.IsCircuit)} empty row(s).",
            Data = new
            {
                panel = Support.DescribePanel(target),
                rows = table.Rows.Select(r => new
                {
                    kind = r.IsCircuit ? "circuit" : "empty",
                    reaNr = r.ReaNr,
                    identity = r.Identity,
                    circuitId = r.CircuitId,
                    uniqueId = r.UniqueId,
                    rowId = r.RowId,
                    detailItems = r.DetailItemCount,
                    values = r.Values
                }),
                emptyRowsStateToken = table.EmptyRowsToken,
                note = "Circuit values are live parameters; empty-row values are the stored JSON. rowId ties a row to its detail items ('id' parameter)."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_panelschematics_read_drawing: the schematic symbols drawn in a view, as rows.</summary>
public sealed class PanelSchematicsReadDrawingTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_read_drawing";
    public string Description =>
        "Reads the panel-schematic symbols drawn in one or more views: every detail item the view owns with its family, type, " +
        "position in mm, the symbol names that family and type stand for, and the row values on it (Rea Nr, Group Nr, Group Name, " +
        "Sümbol, Circuit Breaker, kW, …). Use it to copy the layout of an existing schematic into a panel's table. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = Support.ActiveDocument(uiapp, request, out var failure);
        if (doc == null) return Task.FromResult(failure!);

        var selectors = ToolArguments.GetStringArray(request.Arguments, "views").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (selectors.Count == 0)
            return Task.FromResult(Support.Fail(request, "views is required: view names or element ids."));

        var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        var views = new List<View>();
        var errors = new List<string>();
        foreach (var selector in selectors)
        {
            var matches = long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? allViews.Where(v => v.Id.Value == id).ToList()
                : allViews.Where(v => string.Equals(v.Name, selector, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) views.Add(matches[0]);
            else errors.Add(matches.Count == 0 ? $"No view with id or name '{selector}'." : $"{matches.Count} views are named '{selector}'; pass the element id.");
        }
        if (errors.Count > 0)
            return Task.FromResult(Support.Fail(request, errors[0], errors: errors));

        var includeOther = ToolArguments.GetBool(request.Arguments, "includeNonRowItems");
        var library = PanelSchematicsSymbols.Load();
        var warnings = new List<string>();
        if (!library.Loaded) warnings.Add($"Symbol names could not be matched: {library.Problem}");

        var result = new List<object>();
        int rowItems = 0;
        foreach (var view in views)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Everything the view owns, so a schematic drawn with lines and text (an exploded DWG) is recognisable as such
            var owned = new FilteredElementCollector(doc).WherePasses(new ElementOwnerViewFilter(view.Id)).WhereElementIsNotElementType().ToList();
            var ownedByCategory = owned.GroupBy(e => e.Category?.Name ?? e.GetType().Name)
                .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count());

            // Symbols are family instances: detail items, and generic annotations in older drawings
            var items = owned.OfType<FamilyInstance>().ToList();
            var described = new List<(double X, double Y, bool RowLike, object Item)>();
            foreach (var item in items)
            {
                var family = item.Symbol?.FamilyName ?? string.Empty;
                var type = item.Symbol?.Name ?? string.Empty;
                var names = library.Loaded ? library.NamesFor(family, type) : new List<string>();

                var values = new JObject();
                foreach (var field in Contract.RowFields.Where(f => f.DetailItemParameter != null))
                {
                    var parameter = item.LookupParameter(field.DetailItemParameter);
                    if (parameter == null || !parameter.HasValue) continue;
                    var text = parameter.StorageType switch
                    {
                        StorageType.String => parameter.AsString(),
                        StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
                        StorageType.Double => parameter.AsDouble().ToString("0.###", CultureInfo.InvariantCulture),
                        _ => parameter.AsValueString()
                    };
                    if (!string.IsNullOrWhiteSpace(text)) values[field.Name] = text;
                }

                var point = (item.Location as LocationPoint)?.Point;
                double x = point == null ? 0 : Math.Round(UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters), 1);
                double y = point == null ? 0 : Math.Round(UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters), 1);
                var rowLike = values["GroupNr"] != null || values["GroupName"] != null || names.Count > 0;
                described.Add((x, y, rowLike, new
                {
                    elementId = item.Id.Value,
                    category = item.Category?.Name ?? string.Empty,
                    family,
                    type,
                    xMm = x,
                    yMm = y,
                    symbolNames = names,
                    rowId = item.LookupParameter(PanelSchematicsModel.RowIdParameter)?.AsString() ?? string.Empty,
                    rowLike,
                    values
                }));
            }

            rowItems += described.Count(d => d.RowLike);
            result.Add(new
            {
                view = new { id = view.Id.Value, name = view.Name, type = view.ViewType.ToString() },
                ownedElements = ownedByCategory,
                familyInstances = described.Count,
                rowItems = described.Count(d => d.RowLike),
                items = described.Where(d => includeOther || d.RowLike)
                    .OrderByDescending(d => d.Y).ThenBy(d => d.X).Select(d => d.Item)
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{rowItems} row symbol(s) in {views.Count} view(s).",
            Data = new
            {
                views = result,
                note = "Items are ordered top to bottom, then left to right. rowLike: carries a group number or name, or its family and type are a " +
                       "symbol of the library; frames, headers and title pages are left out unless includeNonRowItems=true. A row drawn by the plugin " +
                       "can be several items with the same rowId."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>A panel's rows, read once for the table tool and for the write tools' checks.</summary>
internal sealed class PanelTable
{
    public sealed class Row
    {
        public bool IsCircuit;
        public int ReaNr;
        public string Identity = string.Empty;
        public long? CircuitId;
        public string UniqueId = string.Empty;
        public string RowId = string.Empty;
        public int DetailItemCount;
        public object Values = new();
    }

    public List<Row> Rows { get; } = new();
    public List<ElectricalSystem> Circuits { get; private set; } = new();
    public PanelSchematicsStorage.StoreState EmptyRowsState { get; private set; } = new();
    public string? EmptyRowsJson { get; private set; }
    public string? EmptyRowsProblem { get; private set; }
    public string EmptyRowsToken { get; private set; } = string.Empty;
    public Dictionary<string, List<FamilyInstance>> DetailItems { get; private set; } = new();

    public static PanelTable Read(Document doc, FamilyInstance panel)
    {
        var table = new PanelTable
        {
            Circuits = PanelSchematicsModel.Circuits(doc, panel.Id),
            DetailItems = PanelSchematicsModel.DetailItemsByRowId(doc),
            EmptyRowsState = PanelSchematicsStorage.Read(panel, Contract.GetStore(Contract.EmptyRowsKey))
        };

        foreach (var circuit in table.Circuits)
        {
            var rowId = PanelSchematicsModel.RowIdOf(circuit);
            table.Rows.Add(new Row
            {
                IsCircuit = true,
                ReaNr = PanelSchematicsModel.ReaNrOf(circuit),
                Identity = RowGroupsPayload.CircuitIdentity(circuit.UniqueId),
                CircuitId = circuit.Id.Value,
                UniqueId = circuit.UniqueId,
                RowId = rowId,
                DetailItemCount = rowId.Length > 0 && table.DetailItems.TryGetValue(rowId, out var items) ? items.Count : 0,
                Values = PanelSchematicsModel.ReadCircuitRow(doc, circuit)
            });
        }

        var json = table.EmptyRowsState.Text("EmptyRowsJson");
        table.EmptyRowsJson = string.IsNullOrWhiteSpace(json) ? null : json;
        if (!table.EmptyRowsState.Readable)
            table.EmptyRowsProblem = table.EmptyRowsState.Problem;
        else if (!EmptyRowsEditor.TryReadRows(table.EmptyRowsJson, out var stored, out var readError))
            table.EmptyRowsProblem = readError;
        else
        {
            foreach (var row in stored)
            {
                var uniqueId = EmptyRowsEditor.UniqueIdOf(row) ?? string.Empty;
                var values = new JObject();
                foreach (var field in Contract.RowFields)
                    if (row.ContainsKey(field.Name))
                        values[field.Name] = Support.ToToken(row[field.Name]);
                table.Rows.Add(new Row
                {
                    ReaNr = EmptyRowsEditor.ReaNrOf(row),
                    Identity = RowGroupsPayload.EmptyRowIdentity(uniqueId),
                    UniqueId = uniqueId,
                    RowId = uniqueId,
                    DetailItemCount = uniqueId.Length > 0 && table.DetailItems.TryGetValue(uniqueId, out var items) ? items.Count : 0,
                    Values = values
                });
            }
        }

        table.Rows.Sort((a, b) => a.ReaNr != b.ReaNr ? a.ReaNr.CompareTo(b.ReaNr) : b.IsCircuit.CompareTo(a.IsCircuit));
        table.EmptyRowsToken = EmptyRowsEditor.StateToken(
            table.EmptyRowsJson,
            string.Join(",", table.Circuits.OrderBy(c => c.Id.Value).Select(c => c.Id.Value + ":" + PanelSchematicsModel.ReaNrOf(c))));
        return table;
    }
}

/// <summary>revit_panelschematics_get_data: the plugin's stored data besides the rows.</summary>
public sealed class PanelSchematicsGetDataTool : IRevitMcpTool
{
    public string Name => "revit_panelschematics_get_data";
    public string Description =>
        "Everything the PanelSchematics plugin stores besides the rows: per panel the title-page metadata, Üheaegsustegur, Pingelang, " +
        "cable-length override, orientation, colour label, schematic view record, row groups and info-item link; for the project the main " +
        "panel, colour labels and picker histories. 'panel' (id or name) limits it to one panel. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = Support.ActiveDocument(uiapp, request, out var failure);
        if (doc == null) return Task.FromResult(failure!);

        var warnings = new List<string>();
        Support.AddPluginWindowWarnings(warnings);

        var selector = ToolArguments.GetString(request.Arguments, "panel");
        List<FamilyInstance> panels;
        if (selector.Trim().Length > 0)
        {
            var one = PanelSchematicsModel.ResolvePanel(doc, selector, out var error);
            if (one == null) return Task.FromResult(Support.Fail(request, error!));
            panels = new List<FamilyInstance> { one };
        }
        else
        {
            panels = PanelSchematicsModel.Panels(doc).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var project = PanelSchematicsProjectData.Read(doc);
        warnings.AddRange(project.Problems);

        var includeEmpty = ToolArguments.GetBool(request.Arguments, "includePanelsWithoutData", selector.Trim().Length > 0);
        var panelData = new List<object>();
        foreach (var panel in panels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = DescribePanel(doc, panel, project, out var hasData);
            if (hasData || includeEmpty) panelData.Add(data);
        }

        PanelColorsPayload.TryRead(project.Colors.Text(PanelSchematicsProjectData.ColorsField), out var colors, out var colorError);
        if (colorError != null) warnings.Add(colorError);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"PanelSchematics data of {panelData.Count} panel(s) in '{doc.Title}'.",
            Data = new
            {
                project = new
                {
                    mainPanelId = long.TryParse(project.MainPanelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var main) ? main : (long?)null,
                    colorLabels = Support.ToToken(colors["ColorLabels"]),
                    overviewValuesSource = project.Overview.Exists ? "PanelSchematicsOverviewValuesV2" : project.OverviewLegacy.Exists ? "PanelOverviewValues (legacy; copied to V2 on the first write)" : "none",
                    infoLinksSource = project.InfoLinks.Exists ? "PanelSchematicsPanelInfoLinksV2" : project.InfoLinksLegacy.Exists ? "PanelInfoLinks (legacy; copied to V2 on the first write)" : "none",
                    recentCables = History(doc, Contract.RecentCablesKey),
                    recentSymbols = History(doc, Contract.RecentSymbolsKey),
                    stateToken = project.Token()
                },
                panels = panelData
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    internal static object DescribePanel(Document doc, FamilyInstance panel, PanelSchematicsProjectData project, out bool hasData)
    {
        var key = PanelSchematicsProjectData.Key(panel.Id);

        var metadata = new JObject();
        var metadataMap = project.Metadata.TextMap(PanelSchematicsProjectData.MetadataField);
        foreach (var field in Contract.MetadataFields)
            if (metadataMap.TryGetValue(Contract.MetadataMapKey(panel.Id.Value, field.Key), out var value))
                metadata[field.Key] = value;

        var emptyState = PanelSchematicsStorage.Read(panel, Contract.GetStore(Contract.EmptyRowsKey));
        var emptyCount = EmptyRowsEditor.TryReadRows(emptyState.Text("EmptyRowsJson"), out var rows, out _) ? rows.Count : -1;

        var groupsState = PanelSchematicsStorage.Read(panel, Contract.GetStore(Contract.RowGroupsKey));
        var groupsJson = groupsState.Text("RowGroupsJson");
        object rowGroups;
        if (!groupsState.Exists)
            rowGroups = new { status = "none" };
        else if (RowGroupsPayload.TryParse(groupsJson, out var groups) is var groupStatus && groupStatus != RowGroupsPayload.ParseStatus.Ok)
            rowGroups = new { status = groupStatus.ToString() };
        else
            rowGroups = new
            {
                status = "ok",
                groups = groups.Select(g => new { id = g.Id, parent = g.Parent, dependents = g.Dependents, links = g.Links })
            };

        var recordState = PanelSchematicsStorage.Read(panel, Contract.GetStore(Contract.PanelRecordKey));
        object schematicView;
        if (!recordState.Exists)
            schematicView = new { status = "none" };
        else if (PanelRecordPayload.TryParse(recordState.Text("PanelRecordJson"), out var record) is var recordStatus
                 && recordStatus != RowGroupsPayload.ParseStatus.Ok)
            schematicView = new { status = recordStatus.ToString() };
        else
        {
            var view = doc.GetElement(record!.ViewUniqueId) as View;
            schematicView = new
            {
                status = "ok",
                viewUniqueId = record.ViewUniqueId,
                viewId = view?.Id.Value,
                viewName = view?.Name ?? record.ViewName,
                viewExists = view != null,
                recordedForThisPanel = string.Equals(record.PanelUniqueId, panel.UniqueId, StringComparison.Ordinal)
            };
        }

        project.Factors.TryGetValue(key, out var factor);
        var hasDrop = project.Drops.TryGetValue(key, out var drop);
        var hasOverride = project.CableLengths.NumberMap(PanelSchematicsProjectData.OverridesField).TryGetValue(key, out var lengthOverride);
        var hasOriginal = project.CableLengths.NumberMap(PanelSchematicsProjectData.OriginalsField).TryGetValue(key, out var lengthOriginal);
        var hasOrientation = project.Orientations.NumberMap(PanelSchematicsProjectData.OrientationsField).TryGetValue(key, out var orientation);
        project.Links.TryGetValue(key, out var infoItem);

        string? colorLabel = null;
        if (PanelColorsPayload.TryRead(project.Colors.Text(PanelSchematicsProjectData.ColorsField), out var colors, out _)
            && colors["PanelColors"] is System.Text.Json.Nodes.JsonObject panelColors && panelColors[key] != null)
            colorLabel = EmptyRowsEditor.Shown(panelColors[key]);

        var isMain = project.MainPanelId == key;
        hasData = metadata.Count > 0 || emptyCount != 0 || groupsState.Exists || recordState.Exists || factor > 0 || hasDrop
                  || hasOverride || hasOrientation || infoItem != null || colorLabel != null || isMain;

        return new
        {
            panel = Support.DescribePanel(panel),
            isMainPanel = isMain,
            emptyRows = emptyCount,
            metadata,
            uheaegsustegur = factor > 0 ? factor : (double?)null,
            pingelang = hasDrop ? drop : (double?)null,
            cableLengthOverrideM = hasOverride ? lengthOverride : (double?)null,
            cableLengthOriginalM = hasOriginal ? lengthOriginal : (double?)null,
            horizontal = hasOrientation ? orientation > 0.5 : (bool?)null,
            colorLabelId = colorLabel,
            infoDetailItemId = long.TryParse(infoItem, NumberStyles.Integer, CultureInfo.InvariantCulture, out var infoId) ? infoId : (long?)null,
            schematicView,
            rowGroups
        };
    }

    private static object History(Document doc, string storeKey)
    {
        var store = Contract.GetStore(storeKey);
        var entries = new List<JToken>();
        foreach (var carrier in PanelSchematicsStorage.Carriers(doc, store))
        {
            var json = PanelSchematicsStorage.Read(carrier, store).Text("HistoryJson");
            if (string.IsNullOrWhiteSpace(json)) continue;
            try { entries.Add(JToken.Parse(json!)); }
            catch { entries.Add(json); }
        }
        return entries;
    }
}
