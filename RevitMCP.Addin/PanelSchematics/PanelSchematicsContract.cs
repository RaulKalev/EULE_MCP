namespace RevitMCP.Addin.PanelSchematics;

/// <summary>
/// What the PanelSchematics plugin (RaulKalev/PanelSchematics, branch Version2) stores and where: row fields,
/// the Revit parameters each row field is written to, and its Extensible Storage stores. The connector reads and
/// writes the plugin's data through this contract, so it must follow the plugin's source — change it only
/// together with the plugin. No Revit types, so it is unit tested and served by revit_panelschematics_guide.
/// </summary>
public static class PanelSchematicsContract
{
    /// <summary>Plugin commit this contract was read from.</summary>
    public const string PluginSourceRevision = "Version2 @ 16d5ac7 (2026-09-29)";

    public enum FieldType { Text, Whole, Number, YesNo }

    /// <summary>One column of a panel table row.</summary>
    public sealed class RowField
    {
        public RowField(string name, FieldType type, string label, string? circuitParameter, string? detailItemParameter,
            string? connectedElementParameter, string note, bool circuitWritable = true, params string[] aliases)
        {
            Name = name;
            Type = type;
            Label = label;
            CircuitParameter = circuitParameter;
            DetailItemParameter = detailItemParameter;
            ConnectedElementParameter = connectedElementParameter;
            Note = note;
            CircuitWritable = circuitWritable && circuitParameter != null;
            Aliases = aliases;
        }

        /// <summary>Property name in the stored empty-row JSON (the plugin's ElectricalSystemData).</summary>
        public string Name { get; }
        public FieldType Type { get; }
        /// <summary>Column header in the plugin's table.</summary>
        public string Label { get; }
        /// <summary>Parameter on the electrical circuit, or null when the field exists on empty rows only.</summary>
        public string? CircuitParameter { get; }
        /// <summary>Parameter on the schematic detail items of the row (matched by the "id" parameter).</summary>
        public string? DetailItemParameter { get; }
        /// <summary>Parameter mirrored to the elements connected to the circuit.</summary>
        public string? ConnectedElementParameter { get; }
        public string Note { get; }
        public bool CircuitWritable { get; }
        public IReadOnlyList<string> Aliases { get; }
    }

    public const string DefaultSymbol = "Kaitselüliti";
    public const string Yes = "Jah";
    public const string No = "Ei";

    /// <summary>Row fields the plugin shows and edits. Order is the order of a row in the stored JSON.</summary>
    public static readonly IReadOnlyList<RowField> RowFields = new[]
    {
        new RowField("WireTypeName", FieldType.Text, "Kaabli tüüp", "Wire Type", "Wire Type", "Kaabel",
            "Cable/wire type name. On a circuit the type itself (Cable Type in Revit 2026, Wire Type before) is set by name and the " +
            "text parameter 'Kaabel' is filled on the circuit and its connected elements; detail items get the name as text.",
            true, "Wire Type", "Cable Type", "Kaabel", "Kaabli tüüp"),
        new RowField("ReaNr", FieldType.Whole, "Rea nr", "Rea Nr", "Rea Nr", "Rea Nr",
            "Position of the row in the panel. One number per row: circuits and empty rows share the numbering.",
            true, "Rea Nr", "Row"),
        new RowField("CircuitPhase", FieldType.Text, "Faas", "Phase", "Phase", null,
            "Number of phases as text (\"1\" or \"3\"). Read from the circuit's '# of Hot Conductors', written to the integer parameter 'Phase'.",
            true, "Phase", "Faas"),
        new RowField("GroupName", FieldType.Text, "Grupi nimi", "Group Name", "Group Name", "Group Name", "Name of the group (load).",
            true, "Group Name"),
        new RowField("GroupNr", FieldType.Text, "Grupi nr", "Group Nr", "Group Nr", "Group Nr", "Group designation, e.g. F12.",
            true, "Group Nr"),
        new RowField("KlemmiNr", FieldType.Text, "Klemmi nr", "Klemmi Nr", "Klemmi Nr", null, "Terminal number.",
            true, "Klemmi Nr"),
        new RowField("ApparentLoad", FieldType.Number, "kW", "kW", "kW", null,
            "Load in kW. On a circuit it is read from Revit's apparent load; the plugin writes it as text with one decimal to 'kW'.",
            true, "kW", "Load"),
        new RowField("CircuitBreaker", FieldType.Text, "Kaitselüliti", "Circuit Breaker", "Circuit Breaker", "Circuit Breaker",
            "Breaker designation, e.g. C16.", true, "Circuit Breaker", "Breaker"),
        new RowField("Samm", FieldType.Whole, "Samm", "Samm", "Samm", null, "Spacing step of the symbol on the schematic.",
            true),
        new RowField("Symbol", FieldType.Text, "Sümbol", "Sümbol", "Sümbol", null,
            "Schematic symbol name. An empty value on a circuit is written as '" + DefaultSymbol + "'.",
            true, "Sümbol", "Sumbol"),
        new RowField("Rikkevooluga", FieldType.Text, "Rikkevooluga", "Rikkevooluga", "Rikkevooluga", null,
            "Residual-current protection: '" + Yes + "' or '" + No + "' (empty means '" + No + "'). A yes/no detail-item parameter gets 1/0.",
            true, "RCD"),
        new RowField("LisaKW", FieldType.Number, "Lisa kW", "Lisa kW", "Lisa kW", null, "Extra load in kW added to the row's kW.",
            true, "Lisa kW"),
        new RowField("IsChecked", FieldType.YesNo, "Joonista", null, null, null,
            "Empty rows only: whether the row is ticked for drawing.", false, "Checked", "Draw"),
        new RowField("CircuitLength", FieldType.Number, "Pikkus (m)", null, null, null, "Empty rows only: stored cable length in metres.", false, "Length"),
        new RowField("CircuitVoltage", FieldType.Number, "Pinge (V)", null, null, null, "Empty rows only: stored voltage in volts.", false, "Voltage"),
        new RowField("ApparentCurrent", FieldType.Number, "Vool (A)", null, null, null, "Empty rows only: stored current in amperes.", false, "Current"),
        new RowField("LoadClassification", FieldType.Text, "Koormuse liik", null, null, null, "Empty rows only: stored load classification.", false),
        new RowField("ConductorSize", FieldType.Number, "Ristlõige", null, null, null, "Empty rows only: stored conductor size.", false),
        new RowField("ConductorMaterial", FieldType.Text, "Juhi materjal", null, null, null, "Empty rows only: stored conductor material.", false),
        new RowField("AmbientTemperature", FieldType.Number, "Ümbritsev temp.", null, null, null, "Empty rows only: ambient temperature, 30 by default.", false),
        new RowField("Calculation", FieldType.Text, "Arvutus", null, null, null, "Empty rows only: stored calculation text.", false),
        new RowField("Calculation2", FieldType.Text, "Arvutus 2", null, null, null, "Empty rows only: stored calculation text.", false),
    };

    /// <summary>Stored row properties the connector never edits.</summary>
    public static readonly IReadOnlyList<string> ProtectedRowProperties = new[]
    {
        "UniqueId", "PanelId", "CircuitId", "IsSpacerRow", "RowBackground", "TotalKW"
    };

    private static readonly Dictionary<string, RowField> FieldLookup = BuildFieldLookup();

    private static Dictionary<string, RowField> BuildFieldLookup()
    {
        var lookup = new Dictionary<string, RowField>(StringComparer.Ordinal);
        foreach (var field in RowFields)
        {
            lookup[Normalize(field.Name)] = field;
            foreach (var alias in field.Aliases)
                lookup[Normalize(alias)] = field;
        }
        return lookup;
    }

    /// <summary>Finds a row field by its JSON name, a parameter name or a label alias (spaces, case and diacritics-free forms ignored).</summary>
    public static RowField? FindRowField(string? name)
        => name != null && FieldLookup.TryGetValue(Normalize(name), out var field) ? field : null;

    private static string Normalize(string value)
    {
        var chars = value.Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-').Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    // ------------------------------------------------------------------ Extensible Storage stores

    public enum StoreHost { Panel, ProjectInformation, DataStorage }

    public enum StoreFieldKind { Json, Text, TextMap, NumberMap, LengthMap, LegacyIntMap, LegacyIntNumberMap }

    public sealed class StoreField
    {
        public StoreField(string name, StoreFieldKind kind, string note)
        {
            Name = name;
            Kind = kind;
            Note = note;
        }

        public string Name { get; }
        public StoreFieldKind Kind { get; }
        public string Note { get; }
    }

    public sealed class Store
    {
        public Store(string key, string schemaGuid, string schemaName, string? vendorId, StoreHost host, bool writable,
            string purpose, params StoreField[] fields)
        {
            Key = key;
            SchemaGuid = schemaGuid;
            SchemaName = schemaName;
            VendorId = vendorId;
            Host = host;
            Writable = writable;
            Purpose = purpose;
            Fields = fields;
        }

        public string Key { get; }
        public string SchemaGuid { get; }
        public string SchemaName { get; }
        /// <summary>Vendor id the plugin builds the schema with; null when it sets none.</summary>
        public string? VendorId { get; }
        public StoreHost Host { get; }
        /// <summary>False for stores the connector only reads (legacy copies, picker histories).</summary>
        public bool Writable { get; }
        public string Purpose { get; }
        public IReadOnlyList<StoreField> Fields { get; }
    }

    public const string EmptyRowsKey = "emptyRows";
    public const string RowGroupsKey = "rowGroups";
    public const string PanelRecordKey = "panelRecord";
    public const string MetadataKey = "panelMetadata";
    public const string OverviewValuesKey = "overviewValues";
    public const string OverviewValuesLegacyKey = "overviewValuesLegacy";
    public const string CableLengthKey = "cableLengths";
    public const string OrientationsKey = "orientations";
    public const string ColorsKey = "panelColors";
    public const string MainPanelKey = "mainPanel";
    public const string InfoLinksKey = "panelInfoLinks";
    public const string InfoLinksLegacyKey = "panelInfoLinksLegacy";
    public const string RecentCablesKey = "recentCables";
    public const string RecentSymbolsKey = "recentSymbols";

    public static readonly IReadOnlyList<Store> Stores = new[]
    {
        new Store(EmptyRowsKey, "a1b2c3d4-e5f6-7890-abcd-ef1234567890", "PanelSchematicsEmptyRows", "RKTools", StoreHost.Panel, true,
            "Rows of a panel that are not backed by a Revit circuit (reserve and hand-made rows). The entity is deleted when the last row goes.",
            new StoreField("EmptyRowsJson", StoreFieldKind.Json, "JSON list of rows; each row has the row fields plus UniqueId (a GUID).")),
        new Store(RowGroupsKey, "63780606-3e4b-4114-89bc-757517a023af", "PanelSchematicsRowGroupsV1", "RKTools", StoreHost.Panel, true,
            "Row groups: a parent row with dependent rows that move together.",
            new StoreField("RowGroupsJson", StoreFieldKind.Json,
                "{\"v\":1,\"groups\":[{\"id\",\"parent\",\"deps\":[...],\"links\":[...]}]}. Rows are 'C:<circuit UniqueId>' or 'E:<empty row UniqueId>'.")),
        new Store(PanelRecordKey, "8bc99ca7-01dd-4a3a-aaa0-e171bb241ec6", "PanelSchematicsPanelRecordV1", "RKTools", StoreHost.Panel, true,
            "Which floor plan view holds the panel's schematic, by UniqueId.",
            new StoreField("PanelRecordJson", StoreFieldKind.Json, "{\"v\":1,\"panel\":\"<panel UniqueId>\",\"view\":\"<view UniqueId>\",\"panelName\",\"viewName\"}.")),
        new Store(MetadataKey, "b3e87173-c374-4fa0-ac47-742707aaf588", "PanelSchematicsPanelMetadataV1", "RKTOOLS", StoreHost.ProjectInformation, true,
            "Title-page data of every panel (Kilbi andmed).",
            new StoreField("PanelMetadata", StoreFieldKind.TextMap, "Key '<panelId>_<fieldKey>', e.g. '1234567_EA_2'. An empty value removes the key.")),
        new Store(OverviewValuesKey, "001f3776-66cc-4fd0-8d69-c16301c62f62", "PanelSchematicsOverviewValuesV2", null, StoreHost.ProjectInformation, true,
            "Per-panel Üheaegsustegur (coincidence factor 0..1) and Pingelang (voltage drop %).",
            new StoreField("Uheaegsustegurid", StoreFieldKind.NumberMap, "Panel id (text) to factor 0..1."),
            new StoreField("Pingelangud", StoreFieldKind.NumberMap, "Panel id (text) to percent.")),
        new Store(OverviewValuesLegacyKey, "c7d8e9f0-a1b2-4c3d-8e5f-6a7b8c9d0e1f", "PanelOverviewValues", null, StoreHost.ProjectInformation, false,
            "Original overview values keyed by 32-bit ids. Read only while the V2 store does not exist; never written.",
            new StoreField("Uheaegsustegurid", StoreFieldKind.LegacyIntNumberMap, "Panel id (int) to factor."),
            new StoreField("Pingelangud", StoreFieldKind.LegacyIntNumberMap, "Panel id (int) to percent.")),
        new Store(CableLengthKey, "f1e2d3c4-b5a6-7890-fedc-ba9876543210", "PanelCableLengthOverrides", null, StoreHost.ProjectInformation, true,
            "Feeding-cable length overrides of panels, in metres.",
            new StoreField("CableLengthOverrides", StoreFieldKind.LengthMap, "Panel id (text) to the user's length."),
            new StoreField("CableLengthOriginals", StoreFieldKind.LengthMap, "Panel id (text) to Revit's length when the override was set.")),
        new Store(OrientationsKey, "b3c4d5e6-f7a8-9012-bcde-f01234567892", "PanelOrientations", null, StoreHost.ProjectInformation, true,
            "Box orientation of panels in Kilpide ülevaade.",
            new StoreField("Orientations", StoreFieldKind.NumberMap, "Panel id (text) to 1 (horizontal) or 0 (vertical).")),
        new Store(ColorsKey, "a9a28760-5b0c-4097-8109-abd46f14bf35", "PanelSchematicsPanelColorsV1", "RKTOOLS", StoreHost.ProjectInformation, true,
            "Colour labels of Kilpide ülevaade and the label of each panel.",
            new StoreField("PanelColorConfigJson", StoreFieldKind.Json,
                "{\"ColorLabels\":[{\"Id\",\"Hex\",\"Label\"}],\"PanelColors\":{\"<panelId>\":\"<label Id>\"}}.")),
        new Store(MainPanelKey, "b3c4d5e6-f7a8-9012-bcde-f01234567891", "PanelSchematicsMainPanel", "RKTools", StoreHost.ProjectInformation, true,
            "The main panel of the project.",
            new StoreField("MainPanelId", StoreFieldKind.Text, "Panel element id as text; empty when none.")),
        new Store(InfoLinksKey, "49f2606c-a3aa-4c1c-8890-b3e56bdaa0d3", "PanelSchematicsPanelInfoLinksV2", null, StoreHost.ProjectInformation, true,
            "Which title-page detail item shows a panel's data.",
            new StoreField("Links", StoreFieldKind.TextMap, "Panel id (text) to detail item id (text).")),
        new Store(InfoLinksLegacyKey, "f9c2c7e2-2a7f-4d10-8d7e-0a8a8c40c2a1", "PanelInfoLinks", null, StoreHost.ProjectInformation, false,
            "Original panel-info links keyed by 32-bit ids. Read only while the V2 store does not exist; never written.",
            new StoreField("Links", StoreFieldKind.LegacyIntMap, "Panel id (int) to detail item id (int).")),
        new Store(RecentCablesKey, "b8eb834a-38f8-4621-97cf-86ef3d0d575a", "PanelSchematicsRecentCablesV1", "RKTools", StoreHost.DataStorage, false,
            "Recently used cable types of the cable picker. Maintained by the plugin as cables are assigned.",
            new StoreField("HistoryJson", StoreFieldKind.Json, "Picker history.")),
        new Store(RecentSymbolsKey, "135a0743-3eaf-4159-a51a-f01ddd7f7312", "PanelSchematicsRecentSymbolsV1", "RKTools", StoreHost.DataStorage, false,
            "Recently used symbols of the symbol picker. Maintained by the plugin as symbols are assigned.",
            new StoreField("HistoryJson", StoreFieldKind.Json, "Picker history.")),
    };

    public static Store GetStore(string key) => Stores.First(s => s.Key == key);

    // ------------------------------------------------------------------ Panel metadata (Kilbi andmed)

    public sealed class MetadataField
    {
        public MetadataField(string key, string label, string defaultValue, string? unit)
        {
            Key = key;
            Label = label;
            Default = defaultValue;
            Unit = unit;
        }

        public string Key { get; }
        public string Label { get; }
        public string Default { get; }
        /// <summary>Unit the plugin adds on the title page; only the number is stored.</summary>
        public string? Unit { get; }

        /// <summary>Text parameter on the title-page detail item (EL_Kilbiskeem_Tiitelleht), e.g. "2.1 Keskuse tüüp".</summary>
        public string DetailItemParameter => SectionNumber(Key) + "." + Key.Substring(Key.IndexOf('_') + 1) + " " + Label;
    }

    public static readonly IReadOnlyList<MetadataField> MetadataFields = new[]
    {
        new MetadataField("ET_1", "Nimipinge", "230/400", "V"),
        new MetadataField("ET_2", "Nimivool", "", "A"),
        new MetadataField("ET_3", "Installeeritud võimsus", "", "kW"),
        new MetadataField("ET_4", "Arvutuslik võimsus", "", "kW"),
        new MetadataField("ET_5", "Arvutuslik vool", "", "A"),
        new MetadataField("ET_6", "Latistiku süsteem", "L1, L2, L3, N, PE", null),
        new MetadataField("ET_7", "Lühisvoolutaluvus", "", "kA"),
        new MetadataField("EA_1", "Keskuse tüüp", "Metallkestaga", null),
        new MetadataField("EA_2", "Kaitseaste", "IP 21", null),
        new MetadataField("EA_3", "Paigaldusviis", "Pinnapealne", null),
        new MetadataField("EA_4", "Kinnitusviis", "Seinal", null),
        new MetadataField("EA_5", "Aparaatide liik", "Kohtkindel", null),
        new MetadataField("EA_6", "Ukse tüüp", "Riiv", null),
        new MetadataField("EA_7", "Teenindusviis", "1 - poolne", null),
        new MetadataField("EA_8", "Pinnakate", "Pulberkate", null),
        new MetadataField("KT_1", "Toide", "Ülalt", null),
        new MetadataField("KT_2", "Väljuvad kaablid", "Ülalt", null),
        new MetadataField("KT_3", "Juhtimiskaablid", "Ülalt", null),
        new MetadataField("TT_1", "Tunnussilt", "Vastavalt standardile", null),
        new MetadataField("TT_2", "Tähistused", "Vastavalt standardile", null),
    };

    public static readonly IReadOnlyDictionary<string, string> MetadataSections = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ET"] = "1. Elektrotehnilised andmed",
        ["EA"] = "2. Ehituslikud andmed",
        ["KT"] = "3. Kaabliühenduste teostus",
        ["TT"] = "4. Tunnussilt ja tähistused",
    };

    public static MetadataField? FindMetadataField(string? key)
        => MetadataFields.FirstOrDefault(f => string.Equals(f.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string MetadataMapKey(long panelId, string fieldKey)
        => panelId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + fieldKey;

    private static string SectionNumber(string key)
        => key.StartsWith("ET_", StringComparison.Ordinal) ? "1"
            : key.StartsWith("EA_", StringComparison.Ordinal) ? "2"
            : key.StartsWith("KT_", StringComparison.Ordinal) ? "3"
            : "4";

    // ------------------------------------------------------------------ Rules (served by the guide tool)

    public static readonly IReadOnlyList<string> Rules = new[]
    {
        "A panel table is the panel's circuits plus its empty rows, ordered by Rea nr. Rea nr is shared: no two rows of a panel may have the same number.",
        "A circuit row's values live in the circuit's parameters; an empty row's values live in the panel's emptyRows store. Never write a circuit's values into emptyRows or the other way round.",
        "A row is tied to its schematic detail items by the 'id' text parameter: the circuit's 'id' for a circuit row, the row's UniqueId for an empty row. Never change 'id' or UniqueId.",
        "Per-panel stores on Project Information are keyed by the panel's element id as text.",
        "The connector edits stored JSON in place, so fields it does not know stay as they are, and it refuses to write when stored data cannot be read.",
        "Every write tool has a preview. Run the preview, show the user what changes, then run the write (it needs approval).",
        "Writes are refused while the plugin's Kilbiskeemid window is open: it keeps the table in memory and its next save would overwrite the change. Ask the user to close it.",
        "The drawing is not redrawn by the connector. After changing rows the user runs Kirjuta + Joonista in the plugin to update the schematic; values of existing detail items are updated directly.",
        "Legacy stores (PanelOverviewValues, PanelInfoLinks) and the picker histories are read-only here.",
    };
}
