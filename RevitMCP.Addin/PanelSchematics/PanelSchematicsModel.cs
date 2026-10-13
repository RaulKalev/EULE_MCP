using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Newtonsoft.Json.Linq;
using Contract = RevitMCP.Addin.PanelSchematics.PanelSchematicsContract;

namespace RevitMCP.Addin.PanelSchematics;

/// <summary>
/// The Revit side of the PanelSchematics plugin's row model: which elements are a panel's rows, how a circuit row
/// is read from parameters, and how row values are written back to the circuit, its connected elements and its
/// schematic detail items. Mirrors the plugin's RevitDataService and UpdateCircuitParameterHandler.
/// </summary>
internal static class PanelSchematicsModel
{
    public const string RowIdParameter = "id";

    // ------------------------------------------------------------------ panels and circuits

    /// <summary>Resolves a panel by element id or exact name; null with <paramref name="error"/> set otherwise.</summary>
    public static FamilyInstance? ResolvePanel(Document doc, string? selector, out string? error)
    {
        error = null;
        selector = (selector ?? string.Empty).Trim();
        if (selector.Length == 0)
        {
            error = "panel is required: the panel's element id or name.";
            return null;
        }

        if (long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            if (doc.GetElement(new ElementId(id)) is FamilyInstance byId && IsPanel(byId))
                return byId;
        }

        var matches = Panels(doc).Where(p => string.Equals(p.Name, selector, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        error = matches.Count == 0
            ? $"No electrical equipment with id or name '{selector}'."
            : $"{matches.Count} panels are named '{selector}' (ids {string.Join(", ", matches.Select(m => m.Id.Value))}); pass the element id.";
        return null;
    }

    public static List<FamilyInstance> Panels(Document doc)
        => new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).OfCategory(BuiltInCategory.OST_ElectricalEquipment)
            .Cast<FamilyInstance>().ToList();

    private static bool IsPanel(FamilyInstance instance)
        => instance.Category != null && instance.Category.Id.Value == (long)BuiltInCategory.OST_ElectricalEquipment;

    /// <summary>The circuits fed from a panel: the plugin's rows that are backed by the model.</summary>
    public static List<ElectricalSystem> Circuits(Document doc, ElementId panelId)
        => new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
            .Where(s =>
            {
                try { return s.BaseEquipment != null && s.BaseEquipment.Id == panelId; }
                catch { return false; }
            })
            .ToList();

    /// <summary>Schematic detail items grouped by their "id" parameter (the row they draw).</summary>
    public static Dictionary<string, List<FamilyInstance>> DetailItemsByRowId(Document doc)
        => new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).OfCategory(BuiltInCategory.OST_DetailComponents)
            .Cast<FamilyInstance>()
            .Select(item => new { Item = item, Id = item.LookupParameter(RowIdParameter)?.AsString() })
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Item).ToList(), StringComparer.Ordinal);

    // ------------------------------------------------------------------ reading a circuit row

    public static string RowIdOf(ElectricalSystem circuit) => circuit.LookupParameter(RowIdParameter)?.AsString() ?? string.Empty;

    public static int ReaNrOf(ElectricalSystem circuit) => WholeOf(circuit.LookupParameter("Rea Nr"));

    /// <summary>A circuit's row values as the plugin shows them, keyed by row field name.</summary>
    public static Dictionary<string, object?> ReadCircuitRow(Document doc, ElectricalSystem circuit)
    {
        var lisaKw = circuit.LookupParameter("Lisa kW");
        double lisa = 0;
        if (lisaKw != null && lisaKw.StorageType == StorageType.String)
            double.TryParse(lisaKw.AsString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out lisa);
        else if (lisaKw != null && lisaKw.StorageType == StorageType.Double)
            lisa = lisaKw.AsDouble();

        var load = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
        var symbol = circuit.LookupParameter("Sümbol")?.AsString();
        var rikke = circuit.LookupParameter("Rikkevooluga")?.AsString();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["WireTypeName"] = WireTypeName(doc, circuit),
            ["ReaNr"] = ReaNrOf(circuit),
            ["CircuitPhase"] = PhaseOf(circuit),
            ["GroupName"] = circuit.LookupParameter("Group Name")?.AsString() ?? string.Empty,
            ["GroupNr"] = circuit.LookupParameter("Group Nr")?.AsString() ?? string.Empty,
            ["KlemmiNr"] = circuit.LookupParameter("Klemmi Nr")?.AsString() ?? string.Empty,
            ["ApparentLoad"] = load != null && load.HasValue
                ? Math.Round(UnitUtils.ConvertFromInternalUnits(load.AsDouble(), UnitTypeId.Watts) / 1000.0, 3)
                : 0.0,
            ["CircuitBreaker"] = circuit.LookupParameter("Circuit Breaker")?.AsString() ?? string.Empty,
            ["Samm"] = WholeOf(circuit.LookupParameter("Samm")),
            ["Symbol"] = string.IsNullOrWhiteSpace(symbol) ? Contract.DefaultSymbol : symbol,
            ["Rikkevooluga"] = string.IsNullOrWhiteSpace(rikke) ? Contract.No : rikke,
            ["LisaKW"] = lisa,
        };
    }

    private static string PhaseOf(ElectricalSystem circuit)
    {
        var hot = circuit.LookupParameter("# of Hot Conductors");
        if (hot != null && hot.HasValue && hot.StorageType == StorageType.Integer)
            return hot.AsInteger().ToString(CultureInfo.InvariantCulture);
        try { return circuit.PolesNumber.ToString(CultureInfo.InvariantCulture); }
        catch { return string.Empty; }
    }

    private static int WholeOf(Parameter? parameter)
    {
        if (parameter == null || !parameter.HasValue) return 0;
        if (parameter.StorageType == StorageType.Integer) return parameter.AsInteger();
        return parameter.StorageType == StorageType.String && int.TryParse(parameter.AsString(), out var parsed) ? parsed : 0;
    }

    /// <summary>Cable type (Revit 2026) or wire type name of a circuit; empty when none.</summary>
    public static string WireTypeName(Document doc, ElectricalSystem circuit)
    {
        try
        {
            var cableProperty = circuit.GetType().GetProperty("CableType", BindingFlags.Public | BindingFlags.Instance);
            if (cableProperty != null && cableProperty.PropertyType == typeof(ElementId)
                && cableProperty.GetValue(circuit) is ElementId cableId && cableId != ElementId.InvalidElementId
                && doc.GetElement(cableId) is ElementType cableType)
                return cableType.Name;
        }
        catch
        {
            // Not Revit 2026, or no cable type: fall through.
        }

        foreach (Parameter parameter in circuit.Parameters)
        {
            if (parameter == null || parameter.StorageType != StorageType.ElementId) continue;
            var candidate = parameter.AsElementId();
            if (candidate != null && candidate != ElementId.InvalidElementId
                && doc.GetElement(candidate) is ElementType type && type.GetType().Name == "CableType")
                return type.Name;
        }

        try
        {
            if (circuit.GetType().GetProperty("WireType")?.GetValue(circuit) is WireType wireType)
                return wireType.Name;
        }
        catch
        {
            // No wire type.
        }

        return (circuit.LookupParameter("Cable Type") ?? circuit.LookupParameter("Kaabli tüüp") ?? circuit.LookupParameter("Wire Type"))
            ?.AsString() ?? string.Empty;
    }

    // ------------------------------------------------------------------ writing a circuit row

    /// <summary>One parameter a row write sets (or would set), for previews and results.</summary>
    public sealed class ParameterWrite
    {
        public long ElementId { get; set; }
        /// <summary>circuit, connectedElement or detailItem.</summary>
        public string Target { get; set; } = string.Empty;
        public string Field { get; set; } = string.Empty;
        public string Parameter { get; set; } = string.Empty;
        public string? Before { get; set; }
        public string? After { get; set; }
        /// <summary>set, unchanged, missing (no such parameter), readOnly or invalid (value does not fit the parameter).</summary>
        public string Status { get; set; } = string.Empty;
    }

    public sealed class RowWriteOptions
    {
        public bool ConnectedElements { get; set; } = true;
        public bool DetailItems { get; set; } = true;
        public bool Apply { get; set; }
    }

    /// <summary>
    /// Writes (or, with Apply off, lists) row values on a circuit, its connected elements and its detail items,
    /// field by field as the plugin's "Kirjuta" does. Only the fields in <paramref name="values"/> are touched.
    /// </summary>
    public static List<ParameterWrite> WriteCircuitRow(Document doc, ElectricalSystem circuit, IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, List<FamilyInstance>> detailItemsByRowId, RowWriteOptions options, List<string> warnings)
    {
        var writes = new List<ParameterWrite>();
        var rowId = RowIdOf(circuit);
        var detailItems = options.DetailItems && rowId.Length > 0 && detailItemsByRowId.TryGetValue(rowId, out var found)
            ? found
            : new List<FamilyInstance>();
        var connected = new List<Element>();
        if (options.ConnectedElements)
        {
            try
            {
                foreach (Element element in circuit.Elements)
                    if (element != null) connected.Add(element);
            }
            catch
            {
                warnings.Add($"Circuit {circuit.Id.Value}: the connected elements could not be read; only the circuit and its detail items are written.");
            }
        }

        foreach (var pair in values)
        {
            var field = Contract.FindRowField(pair.Key)!;
            var value = pair.Value;

            if (field.Name == "WireTypeName")
            {
                WriteWireType(doc, circuit, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, connected, detailItems, options, writes, warnings);
                continue;
            }

            // Values in the form the plugin writes them
            object circuitValue = value ?? string.Empty;
            if (field.Name == "ApparentLoad")
                circuitValue = Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("F1", CultureInfo.InvariantCulture);
            if (field.Name == "Symbol" && string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
                circuitValue = Contract.DefaultSymbol;

            Set(circuit, "circuit", field, field.CircuitParameter!, circuitValue, options.Apply, writes);

            if (field.ConnectedElementParameter != null && MirrorsToConnected(field, circuitValue))
                foreach (var element in connected)
                    Set(element, "connectedElement", field, field.ConnectedElementParameter, circuitValue, options.Apply, writes, skipMissing: true);

            if (field.DetailItemParameter != null)
                foreach (var item in detailItems)
                    Set(item, "detailItem", field, field.DetailItemParameter, circuitValue, options.Apply, writes, skipMissing: true);
        }
        return writes;
    }

    /// <summary>
    /// Mirrors an empty row's values to its schematic detail items (those whose "id" is the row's UniqueId), with
    /// the same parameter per field as for a circuit row.
    /// </summary>
    public static List<ParameterWrite> WriteDetailItems(IEnumerable<FamilyInstance> detailItems, IReadOnlyDictionary<string, object?> values, bool apply)
    {
        var writes = new List<ParameterWrite>();
        foreach (var pair in values)
        {
            var field = Contract.FindRowField(pair.Key);
            if (field?.DetailItemParameter == null) continue;

            object value = pair.Value ?? string.Empty;
            if (field.Name == "ApparentLoad")
                value = Convert.ToDouble(pair.Value, CultureInfo.InvariantCulture).ToString("F1", CultureInfo.InvariantCulture);
            foreach (var item in detailItems)
                Set(item, "detailItem", field, field.DetailItemParameter, value, apply, writes, skipMissing: true);
        }
        return writes;
    }

    /// <summary>The plugin does not blank a connected element's Rea Nr or group values with an empty row value.</summary>
    private static bool MirrorsToConnected(Contract.RowField field, object value) => field.Name switch
    {
        "ReaNr" => Convert.ToInt32(value, CultureInfo.InvariantCulture) > 0,
        "GroupName" or "GroupNr" => !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)),
        _ => true
    };

    private static void WriteWireType(Document doc, ElectricalSystem circuit, string typeName, List<Element> connected,
        List<FamilyInstance> detailItems, RowWriteOptions options, List<ParameterWrite> writes, List<string> warnings)
    {
        var field = Contract.FindRowField("WireTypeName")!;
        var before = WireTypeName(doc, circuit);
        var write = new ParameterWrite
        {
            ElementId = circuit.Id.Value, Target = "circuit", Field = field.Name, Parameter = "Cable Type / Wire Type",
            Before = before, After = typeName
        };
        writes.Add(write);

        if (string.IsNullOrWhiteSpace(typeName))
        {
            write.Status = "unchanged";
            warnings.Add($"Circuit {circuit.Id.Value}: an empty cable type is not written (the plugin leaves the circuit's type as it is).");
            return;
        }

        var cableTypeId = new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
            .FirstOrDefault(t => t.GetType().Name == "CableType" && t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))?.Id
            ?? ElementId.InvalidElementId;
        var wireType = new FilteredElementCollector(doc).OfClass(typeof(WireType)).Cast<WireType>()
            .FirstOrDefault(w => w.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

        if (cableTypeId == ElementId.InvalidElementId && wireType == null)
        {
            write.Status = "missing";
            warnings.Add($"Circuit {circuit.Id.Value}: no cable or wire type named '{typeName}' in the project; only the text parameters are written. " +
                         "Create the type first (revit_create_cable_type) to set the circuit's type.");
        }
        else if (string.Equals(before, typeName, StringComparison.OrdinalIgnoreCase))
        {
            write.Status = "unchanged";
        }
        else if (!options.Apply)
        {
            write.Status = "set";
        }
        else
        {
            write.Status = SetWireOrCableType(doc, circuit, typeName, cableTypeId, wireType) ? "set" : "readOnly";
            if (write.Status != "set")
                warnings.Add($"Circuit {circuit.Id.Value}: the cable/wire type could not be set to '{typeName}'.");
        }

        Set(circuit, "circuit", field, "Kaabel", typeName, options.Apply, writes, skipMissing: true);
        foreach (var element in connected)
            Set(element, "connectedElement", field, "Kaabel", typeName, options.Apply, writes, skipMissing: true);
        foreach (var item in detailItems)
            Set(item, "detailItem", field, "Wire Type", typeName, options.Apply, writes, skipMissing: true);
    }

    /// <summary>The plugin's WireTypeHelper.SetWireOrCableTypeByName: cable type property, a cable-type parameter, then the legacy wire type.</summary>
    private static bool SetWireOrCableType(Document doc, ElectricalSystem circuit, string typeName, ElementId cableTypeId, WireType? wireType)
    {
        if (cableTypeId != ElementId.InvalidElementId)
        {
            try
            {
                var property = circuit.GetType().GetProperty("CableType", BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.PropertyType == typeof(ElementId) && property.CanWrite)
                {
                    property.SetValue(circuit, cableTypeId);
                    return true;
                }
            }
            catch
            {
                // Fall through to the parameter.
            }

            foreach (var owner in new Element?[] { circuit, doc.GetElement(circuit.GetTypeId()) })
            {
                if (owner == null) continue;
                foreach (Parameter parameter in owner.Parameters)
                {
                    if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.ElementId) continue;
                    var current = parameter.AsElementId();
                    var pointsToCableType = current != null && current != ElementId.InvalidElementId
                                            && doc.GetElement(current) is ElementType type && type.GetType().Name == "CableType";
                    var name = parameter.Definition?.Name ?? string.Empty;
                    if (pointsToCableType || name.Equals("Cable Type", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Kaabli tüüp", StringComparison.OrdinalIgnoreCase))
                        return parameter.Set(cableTypeId);
                }
            }
        }

        if (wireType != null)
        {
            try
            {
                var property = circuit.GetType().GetProperty("WireType");
                if (property != null && property.CanWrite)
                {
                    property.SetValue(circuit, wireType);
                    return true;
                }
            }
            catch
            {
                // Fall through to a text parameter.
            }
        }

        var text = circuit.LookupParameter("Cable Type") ?? circuit.LookupParameter("Kaabli tüüp") ?? circuit.LookupParameter("Wire Type");
        return text != null && !text.IsReadOnly && text.StorageType == StorageType.String && text.Set(typeName);
    }

    private static void Set(Element element, string target, Contract.RowField field, string parameterName, object value, bool apply,
        List<ParameterWrite> writes, bool skipMissing = false)
    {
        var parameter = element.LookupParameter(parameterName);
        if (parameter == null && skipMissing) return;

        var write = new ParameterWrite { ElementId = element.Id.Value, Target = target, Field = field.Name, Parameter = parameterName };
        writes.Add(write);
        if (parameter == null)
        {
            write.Status = "missing";
            return;
        }

        write.Before = Shown(parameter);
        if (!TryConvert(parameter, value, out var converted, out var shown))
        {
            write.After = Convert.ToString(value, CultureInfo.InvariantCulture);
            write.Status = "invalid";
            return;
        }
        write.After = shown;
        if (string.Equals(write.Before ?? string.Empty, shown, StringComparison.Ordinal))
        {
            write.Status = "unchanged";
            return;
        }
        if (parameter.IsReadOnly)
        {
            write.Status = "readOnly";
            return;
        }
        write.Status = "set";
        if (!apply) return;

        bool ok = converted switch
        {
            int whole => parameter.Set(whole),
            double number => parameter.Set(number),
            _ => parameter.Set((string)converted)
        };
        if (!ok)
            throw new InvalidOperationException($"Revit refused to set '{parameterName}' on element {element.Id.Value}.");
    }

    /// <summary>The value in the parameter's storage type, as the plugin's SetParameterValue converts it.</summary>
    private static bool TryConvert(Parameter parameter, object value, out object converted, out string shown)
    {
        var text = value is double d ? d.ToString("0.0###", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        switch (parameter.StorageType)
        {
            case StorageType.String:
                converted = text;
                shown = text;
                return true;
            case StorageType.Integer:
                int whole;
                if (IsYesNo(parameter))
                    whole = text.Equals(Contract.Yes, StringComparison.OrdinalIgnoreCase) || text.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                            || text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                else if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole))
                {
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var asNumber)) break;
                    whole = (int)Math.Round(asNumber);
                }
                converted = whole;
                shown = whole.ToString(CultureInfo.InvariantCulture);
                return true;
            case StorageType.Double:
                if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) break;
                converted = number;
                shown = number.ToString("0.0###", CultureInfo.InvariantCulture);
                return true;
        }
        converted = text;
        shown = text;
        return false;
    }

    private static bool IsYesNo(Parameter parameter)
    {
        try { return parameter.Definition != null && parameter.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; }
        catch { return false; }
    }

    private static string? Shown(Parameter parameter)
    {
        if (!parameter.HasValue) return parameter.StorageType == StorageType.String ? string.Empty : null;
        return parameter.StorageType switch
        {
            StorageType.String => parameter.AsString() ?? string.Empty,
            StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
            StorageType.Double => parameter.AsDouble().ToString("0.0###", CultureInfo.InvariantCulture),
            StorageType.ElementId => parameter.AsElementId()?.Value.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    // ------------------------------------------------------------------ the plugin's own windows

    /// <summary>Class names of the plugin's windows that are open in this Revit session.</summary>
    public static List<string> OpenPluginWindows()
    {
        var names = new List<string>();
        try
        {
            foreach (System.Windows.PresentationSource source in System.Windows.PresentationSource.CurrentSources)
            {
                try
                {
                    if (source?.RootVisual is System.Windows.Window window && window.IsVisible)
                    {
                        var type = window.GetType();
                        if ((type.Namespace ?? string.Empty).StartsWith("PanelSchematics", StringComparison.Ordinal))
                            names.Add(type.Name);
                    }
                }
                catch
                {
                    // A window of another thread: not one the plugin shows from Revit's UI thread.
                }
            }
        }
        catch
        {
            // No WPF sources to look at.
        }
        return names;
    }

    /// <summary>The plugin's main table window keeps rows in memory and saves them whole, overwriting other writers.</summary>
    public const string MainWindowName = "MainWindow";

    // ------------------------------------------------------------------ backups

    /// <summary>
    /// Saves what is about to be replaced to %LOCALAPPDATA%\RevitMCP\PanelSchematicsBackups. Throws when it cannot
    /// be written: no backup, no write.
    /// </summary>
    public static string WriteBackup(Document doc, string label, JObject before)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "PanelSchematicsBackups");
        Directory.CreateDirectory(folder);

        var content = new JObject
        {
            ["format"] = "RevitMcpPanelSchematicsBackup",
            ["createdAt"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
            ["document"] = doc.Title ?? string.Empty,
            ["documentPath"] = doc.PathName ?? string.Empty,
            ["operation"] = label,
            ["before"] = before
        };

        var name = Safe(doc.Title) + "_" + Safe(label) + "_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff", CultureInfo.InvariantCulture) + ".json";
        var path = Path.Combine(folder, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
        return path;
    }

    private static string Safe(string? text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((text ?? "project").Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        return clean.Length > 60 ? clean.Substring(0, 60) : clean;
    }
}
