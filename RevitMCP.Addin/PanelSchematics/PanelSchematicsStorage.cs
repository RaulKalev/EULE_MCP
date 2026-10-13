using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Contract = RevitMCP.Addin.PanelSchematics.PanelSchematicsContract;

namespace RevitMCP.Addin.PanelSchematics;

/// <summary>
/// Reads and writes the PanelSchematics plugin's Extensible Storage entities, following
/// <see cref="PanelSchematicsContract"/>. A schema that is loaded but does not look like the plugin's is never
/// used; a schema that is not loaded yet is created exactly as the plugin would create it, so whichever add-in
/// runs first, both end up with the same schema. Writers must be called inside a transaction.
/// </summary>
internal static class PanelSchematicsStorage
{
    /// <summary>The loaded schema of a store, or null when it is not loaded. <paramref name="conflict"/> is set when it does not match.</summary>
    public static Schema? Lookup(Contract.Store store, out string? conflict)
    {
        conflict = null;
        Schema? schema;
        try
        {
            schema = Schema.Lookup(new Guid(store.SchemaGuid));
        }
        catch (Exception ex)
        {
            conflict = $"Schema {store.SchemaName} could not be looked up: {ex.Message}";
            return null;
        }
        if (schema == null) return null;

        try
        {
            if (schema.SchemaName != store.SchemaName)
            {
                conflict = $"Schema {store.SchemaGuid} is named '{schema.SchemaName}', expected '{store.SchemaName}'.";
                return null;
            }
            foreach (var expected in store.Fields)
            {
                var field = schema.GetField(expected.Name);
                if (field == null || !Matches(field, expected.Kind))
                {
                    conflict = $"Schema {store.SchemaName} has no field '{expected.Name}' of the expected type.";
                    return null;
                }
            }
            if (!schema.ReadAccessGranted())
            {
                conflict = $"Schema {store.SchemaName} does not grant read access.";
                return null;
            }
        }
        catch (Exception ex)
        {
            conflict = $"Schema {store.SchemaName} could not be checked: {ex.Message}";
            return null;
        }
        return schema;
    }

    /// <summary>The store's schema, created when it is not loaded. Throws when a loaded schema does not match.</summary>
    public static Schema GetOrCreate(Contract.Store store)
    {
        if (!store.Writable)
            throw new InvalidOperationException($"{store.SchemaName} is read-only for the connector.");

        var schema = Lookup(store, out var conflict);
        if (conflict != null) throw new InvalidOperationException(conflict);
        if (schema != null)
        {
            if (!schema.WriteAccessGranted())
                throw new InvalidOperationException($"Schema {store.SchemaName} does not grant write access.");
            return schema;
        }

        var builder = new SchemaBuilder(new Guid(store.SchemaGuid));
        builder.SetSchemaName(store.SchemaName);
        if (store.VendorId != null) builder.SetVendorId(store.VendorId);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        foreach (var field in store.Fields)
        {
            switch (field.Kind)
            {
                case Contract.StoreFieldKind.Json:
                case Contract.StoreFieldKind.Text:
                    builder.AddSimpleField(field.Name, typeof(string));
                    break;
                case Contract.StoreFieldKind.TextMap:
                    builder.AddMapField(field.Name, typeof(string), typeof(string));
                    break;
                case Contract.StoreFieldKind.NumberMap:
                    builder.AddMapField(field.Name, typeof(string), typeof(double)).SetSpec(SpecTypeId.Number);
                    break;
                case Contract.StoreFieldKind.LengthMap:
                    builder.AddMapField(field.Name, typeof(string), typeof(double)).SetSpec(SpecTypeId.Length);
                    break;
                default:
                    throw new InvalidOperationException($"{store.SchemaName} is a legacy store and is never created.");
            }
        }
        return builder.Finish();
    }

    private static bool Matches(Field field, Contract.StoreFieldKind kind) => kind switch
    {
        Contract.StoreFieldKind.Json or Contract.StoreFieldKind.Text
            => field.ContainerType == ContainerType.Simple && field.ValueType == typeof(string),
        Contract.StoreFieldKind.TextMap
            => field.ContainerType == ContainerType.Map && field.KeyType == typeof(string) && field.ValueType == typeof(string),
        Contract.StoreFieldKind.NumberMap or Contract.StoreFieldKind.LengthMap
            => field.ContainerType == ContainerType.Map && field.KeyType == typeof(string) && field.ValueType == typeof(double),
        Contract.StoreFieldKind.LegacyIntMap
            => field.ContainerType == ContainerType.Map && field.KeyType == typeof(int) && field.ValueType == typeof(int),
        Contract.StoreFieldKind.LegacyIntNumberMap
            => field.ContainerType == ContainerType.Map && field.KeyType == typeof(int) && field.ValueType == typeof(double),
        _ => false
    };

    private static ForgeTypeId UnitOf(Contract.StoreFieldKind kind)
        => kind == Contract.StoreFieldKind.LengthMap ? UnitTypeId.Meters : UnitTypeId.General;

    // ------------------------------------------------------------------ reading

    /// <summary>What one store holds on one element.</summary>
    public sealed class StoreState
    {
        /// <summary>False when the schema conflicts or a value could not be read: such a store is never written.</summary>
        public bool Readable = true;
        public string? Problem;
        /// <summary>True when the element carries an entity of the store.</summary>
        public bool Exists;
        public Dictionary<string, string?> Texts = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, string>> TextMaps = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, double>> NumberMaps = new(StringComparer.Ordinal);

        public string? Text(string field) => Texts.TryGetValue(field, out var value) ? value : null;

        public Dictionary<string, string> TextMap(string field)
            => TextMaps.TryGetValue(field, out var map) ? map : new Dictionary<string, string>(StringComparer.Ordinal);

        public Dictionary<string, double> NumberMap(string field)
            => NumberMaps.TryGetValue(field, out var map) ? map : new Dictionary<string, double>(StringComparer.Ordinal);
    }

    /// <summary>Reads a store from <paramref name="host"/>. Never throws, never creates a schema.</summary>
    public static StoreState Read(Element? host, Contract.Store store)
    {
        var state = new StoreState();
        if (host == null) return state;

        var schema = Lookup(store, out var conflict);
        if (conflict != null)
        {
            state.Readable = false;
            state.Problem = conflict;
            return state;
        }
        if (schema == null) return state;

        try
        {
            var entity = host.GetEntity(schema);
            if (entity == null || !entity.IsValid()) return state;
            state.Exists = true;

            foreach (var field in store.Fields)
            {
                var schemaField = schema.GetField(field.Name);
                switch (field.Kind)
                {
                    case Contract.StoreFieldKind.Json:
                    case Contract.StoreFieldKind.Text:
                        state.Texts[field.Name] = entity.Get<string>(schemaField);
                        break;
                    case Contract.StoreFieldKind.TextMap:
                        state.TextMaps[field.Name] = new Dictionary<string, string>(
                            entity.Get<IDictionary<string, string>>(schemaField) ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                        break;
                    case Contract.StoreFieldKind.NumberMap:
                    case Contract.StoreFieldKind.LengthMap:
                        state.NumberMaps[field.Name] = new Dictionary<string, double>(
                            entity.Get<IDictionary<string, double>>(schemaField, UnitOf(field.Kind)) ?? new Dictionary<string, double>(), StringComparer.Ordinal);
                        break;
                    case Contract.StoreFieldKind.LegacyIntMap:
                        state.TextMaps[field.Name] = (entity.Get<IDictionary<int, int>>(schemaField) ?? new Dictionary<int, int>())
                            .ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
                        break;
                    case Contract.StoreFieldKind.LegacyIntNumberMap:
                        state.NumberMaps[field.Name] = (entity.Get<IDictionary<int, double>>(schemaField, UnitTypeId.General) ?? new Dictionary<int, double>())
                            .ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value, StringComparer.Ordinal);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            state.Readable = false;
            state.Problem = $"{store.SchemaName} on element {host.Id.Value} could not be read: {ex.Message}";
        }
        return state;
    }

    /// <summary>Elements of the document that carry an entity of the store (panels, DataStorage elements).</summary>
    public static List<Element> Carriers(Document doc, Contract.Store store)
    {
        var schema = Lookup(store, out _);
        if (schema == null) return new List<Element>();
        try
        {
            return new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(schema.GUID)).ToList();
        }
        catch
        {
            return new List<Element>();
        }
    }

    // ------------------------------------------------------------------ writing (inside a transaction)

    /// <summary>Replaces the store's entity on <paramref name="host"/> with these values. Fields not given are stored empty.</summary>
    public static void Write(Element host, Contract.Store store, StoreState values)
    {
        var schema = GetOrCreate(store);
        var entity = new Entity(schema);
        foreach (var field in store.Fields)
        {
            var schemaField = schema.GetField(field.Name);
            switch (field.Kind)
            {
                case Contract.StoreFieldKind.Json:
                case Contract.StoreFieldKind.Text:
                    entity.Set(schemaField, values.Text(field.Name) ?? string.Empty);
                    break;
                case Contract.StoreFieldKind.TextMap:
                    entity.Set<IDictionary<string, string>>(schemaField, values.TextMap(field.Name));
                    break;
                case Contract.StoreFieldKind.NumberMap:
                case Contract.StoreFieldKind.LengthMap:
                    entity.Set<IDictionary<string, double>>(schemaField, values.NumberMap(field.Name), UnitOf(field.Kind));
                    break;
            }
        }
        host.SetEntity(entity);
    }

    /// <summary>Removes the store's entity from <paramref name="host"/>; other entities on the element are untouched.</summary>
    public static void Delete(Element host, Contract.Store store)
    {
        var schema = Lookup(store, out var conflict);
        if (conflict != null) throw new InvalidOperationException(conflict);
        if (schema == null) return;
        if (host.GetEntity(schema).IsValid())
            host.DeleteEntity(schema);
    }

    /// <summary>Null when the element may be edited; else why not (owned by someone else, changed in central).</summary>
    public static string? WhyNotEditable(Document doc, Element element)
    {
        try
        {
            if (!doc.IsWorkshared) return null;
            var status = WorksharingUtils.GetCheckoutStatus(doc, element.Id, out var owner);
            if (status == CheckoutStatus.OwnedByOtherUser)
                return $"{Describe(element)} is owned by {owner}.";
            var updates = WorksharingUtils.GetModelUpdatesStatus(doc, element.Id);
            if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral)
                return $"{Describe(element)} was changed in the central model; synchronise first.";
            return null;
        }
        catch (Exception ex)
        {
            return $"The worksharing state of {Describe(element)} could not be read: {ex.Message}";
        }
    }

    public static string Describe(Element element)
    {
        string name;
        try { name = element.Name; }
        catch { name = string.Empty; }
        return element is ProjectInfo ? "Project Information" : $"'{name}' ({element.Id.Value})";
    }
}
