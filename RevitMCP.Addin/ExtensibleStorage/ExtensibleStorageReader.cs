using System.Collections;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace RevitMCP.Addin.ExtensibleStorage;

/// <summary>
/// Read-only introspection of Revit Extensible Storage: schema/field metadata and
/// decoded entity values for data written by other add-ins.
///
/// Nothing here writes — no SchemaBuilder, no Entity.Set, no Element.SetEntity or
/// DeleteEntity. Reads need no transaction, so the calling tools stay ReadOnly.
///
/// The Revit 2024 and 2026 Extensible Storage surfaces are identical, so this file
/// needs no REVIT2024/REVIT2026 conditionals.
/// </summary>
internal static class ExtensibleStorageReader
{
    /// <summary>Entity.Get&lt;T&gt;(Field) — the closed generic is built per field from ValueType/ContainerType.</summary>
    private static readonly MethodInfo EntityGet = typeof(Entity).GetMethods()
        .Single(m => m.Name == nameof(Entity.Get)
                     && m.IsGenericMethodDefinition
                     && m.GetParameters().Length == 1
                     && m.GetParameters()[0].ParameterType == typeof(Field));

    /// <summary>Entity.Get&lt;T&gt;(Field, ForgeTypeId) — required for fields with a measurable spec.</summary>
    private static readonly MethodInfo EntityGetWithUnit = typeof(Entity).GetMethods()
        .Single(m => m.Name == nameof(Entity.Get)
                     && m.IsGenericMethodDefinition
                     && m.GetParameters().Length == 2
                     && m.GetParameters()[0].ParameterType == typeof(Field)
                     && m.GetParameters()[1].ParameterType == typeof(ForgeTypeId));

    // ---------------------------------------------------------------- schemas

    /// <summary>
    /// Schemas known to the running Revit session: those registered by loaded add-ins plus
    /// those Revit read from open documents. Being listed here is not proof that the active
    /// model uses a schema — use <see cref="CountElementsWithSchema"/> for that.
    /// </summary>
    public static IList<Schema> ListSchemas() =>
        Safe(() => Schema.ListSchemas(), new List<Schema>());

    public static Schema? LookupSchema(Guid guid) => SafeRef(() => Schema.Lookup(guid));

    /// <summary>Number of elements in <paramref name="doc"/> carrying data for the schema, or -1 if the scan failed.</summary>
    public static int CountElementsWithSchema(Document doc, Guid schemaGuid) =>
        Safe(() => new FilteredElementCollector(doc)
            .WherePasses(new ExtensibleStorageFilter(schemaGuid))
            .GetElementCount(), -1);

    /// <summary>Elements in <paramref name="doc"/> carrying data for the schema — instances and types alike.</summary>
    public static IList<Element> CollectElementsWithSchema(Document doc, Guid schemaGuid) =>
        Safe(() => new FilteredElementCollector(doc)
            .WherePasses(new ExtensibleStorageFilter(schemaGuid))
            .ToElements(), new List<Element>());

    /// <summary>Schema metadata, plus field definitions when <paramref name="includeFields"/> is set.</summary>
    public static object DescribeSchema(Schema schema, bool includeFields)
    {
        var fields = SafeListFields(schema);
        return new
        {
            schemaGuid = Safe(() => schema.GUID.ToString(), string.Empty),
            schemaName = SafeSchemaName(schema),
            documentation = Safe(() => schema.Documentation, string.Empty),
            vendorId = Safe(() => schema.VendorId, string.Empty),
            applicationGuid = Safe(() => schema.ApplicationGUID.ToString(), string.Empty),
            readAccessLevel = Safe(() => schema.ReadAccessLevel.ToString(), "Unknown"),
            writeAccessLevel = Safe(() => schema.WriteAccessLevel.ToString(), "Unknown"),
            // Vendor/Application read levels only grant access to the owning add-in. When this is
            // false the values cannot be read from here, whatever the model contains.
            readAccessGranted = Safe(() => schema.ReadAccessGranted(), false),
            fieldCount = fields.Count,
            fields = includeFields ? fields.Select(DescribeField).ToList() : null
        };
    }

    /// <summary>Field definition: name, value/key types, container shape, sub-schema, and unit spec.</summary>
    public static object DescribeField(Field field)
    {
        var spec = SafeRef(() => field.GetSpecTypeId());
        var measurable = spec != null && !spec.Empty();
        return new
        {
            fieldName = Safe(() => field.FieldName, string.Empty),
            documentation = Safe(() => field.Documentation, string.Empty),
            containerType = Safe(() => field.ContainerType.ToString(), "Simple"),
            valueType = SafeRef(() => field.ValueType?.Name) ?? "Unknown",
            keyType = SafeRef(() => field.ContainerType == ContainerType.Map ? field.KeyType?.Name : null),
            subSchemaGuid = SafeRef(() => field.SubSchemaGUID == Guid.Empty ? null : field.SubSchemaGUID.ToString()),
            subSchemaName = SafeRef(() => field.SubSchema == null ? null : SafeSchemaName(field.SubSchema)),
            isMeasurable = measurable,
            specTypeId = measurable ? spec!.TypeId : null
        };
    }

    // --------------------------------------------------------------- entities

    /// <summary>
    /// Reads every field of <paramref name="element"/>'s entity for <paramref name="schema"/>.
    /// Returns null when the element carries no data for that schema. A field that fails to
    /// decode is reported in <c>errors</c> rather than aborting the whole entity.
    /// </summary>
    public static EntityReadResult? ReadEntity(Document doc, Element element, Schema schema, int maxDepth)
    {
        var result = new EntityReadResult
        {
            SchemaGuid = Safe(() => schema.GUID.ToString(), string.Empty),
            SchemaName = SafeSchemaName(schema)
        };

        if (!Safe(() => schema.ReadAccessGranted(), false))
        {
            result.ReadAccessGranted = false;
            result.Errors.Add(
                $"Read access is not granted for schema '{result.SchemaName}' " +
                $"(read access level {Safe(() => schema.ReadAccessLevel.ToString(), "Unknown")}). " +
                "Only the add-in that owns the schema can read its values.");
            return result;
        }

        Entity entity;
        try { entity = element.GetEntity(schema); }
        catch (Exception ex)
        {
            result.Errors.Add($"GetEntity failed: {Unwrap(ex).Message}");
            return result;
        }

        if (entity == null || !entity.IsValid())
            return null;

        foreach (var field in SafeListFields(schema))
        {
            var name = Safe(() => field.FieldName, string.Empty);
            if (name.Length == 0)
                continue;

            var unit = ResolveUnit(doc, field);
            if (unit != null)
                result.Units[name] = unit.TypeId;

            try
            {
                result.Fields[name] = ReadFieldValue(doc, entity, field, unit, maxDepth, 0);
            }
            catch (Exception ex)
            {
                result.Fields[name] = null;
                result.Errors.Add($"Field '{name}': {Unwrap(ex).Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Reads one field. The closed generic handed to Entity.Get is derived from the field's
    /// container shape: T for Simple, IList&lt;T&gt; for Array, IDictionary&lt;K,V&gt; for Map.
    /// </summary>
    private static object? ReadFieldValue(
        Document doc,
        Entity entity,
        Field field,
        ForgeTypeId? unit,
        int maxDepth,
        int depth)
    {
        var valueType = field.ValueType
                        ?? throw new InvalidOperationException("Field has no value type.");

        var closedType = field.ContainerType switch
        {
            ContainerType.Array => typeof(IList<>).MakeGenericType(valueType),
            ContainerType.Map => typeof(IDictionary<,>).MakeGenericType(
                field.KeyType ?? throw new InvalidOperationException("Map field has no key type."),
                valueType),
            _ => valueType
        };

        object? raw;
        try
        {
            raw = unit == null
                ? EntityGet.MakeGenericMethod(closedType).Invoke(entity, new object[] { field })
                : EntityGetWithUnit.MakeGenericMethod(closedType).Invoke(entity, new object[] { field, unit });
        }
        catch (TargetInvocationException ex)
        {
            throw Unwrap(ex);
        }

        return Describe(doc, raw, maxDepth, depth);
    }

    /// <summary>
    /// Picks a unit for a measurable field, or null when the field carries no spec.
    /// Prefers the document's display unit so returned numbers match what the user sees in Revit.
    /// </summary>
    private static ForgeTypeId? ResolveUnit(Document doc, Field field)
    {
        var spec = SafeRef(() => field.GetSpecTypeId());
        if (spec == null || spec.Empty())
            return null;

        var displayUnit = SafeRef(() => doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId());
        if (displayUnit != null && !displayUnit.Empty() && Safe(() => field.CompatibleUnit(displayUnit), false))
            return displayUnit;

        var valid = Safe(() => UnitUtils.GetValidUnits(spec), new List<ForgeTypeId>());
        return valid.FirstOrDefault(u => Safe(() => field.CompatibleUnit(u), false))
               ?? valid.FirstOrDefault();
    }

    /// <summary>Converts a decoded Revit value into a JSON-friendly shape.</summary>
    private static object? Describe(Document doc, object? value, int maxDepth, int depth)
    {
        switch (value)
        {
            case null:
                return null;

            // Sub-entity: recurse into the sub-schema, bounded by maxDepth.
            case Entity sub:
            {
                if (depth >= maxDepth)
                    return new { truncated = true, reason = $"maxDepth {maxDepth} reached." };
                if (!sub.IsValid())
                    return null;

                var subSchema = SafeRef(() => sub.Schema);
                if (subSchema == null)
                    return new { unreadable = true, reason = "Sub-entity schema is unavailable." };
                if (!Safe(() => subSchema.ReadAccessGranted(), false))
                    return new { unreadable = true, reason = "Read access is not granted for the sub-schema." };

                var nested = new Dictionary<string, object?>();
                foreach (var subField in SafeListFields(subSchema))
                {
                    var subName = Safe(() => subField.FieldName, string.Empty);
                    if (subName.Length == 0)
                        continue;
                    try
                    {
                        nested[subName] = ReadFieldValue(
                            doc, sub, subField, ResolveUnit(doc, subField), maxDepth, depth + 1);
                    }
                    catch (Exception ex)
                    {
                        nested[subName] = $"<unreadable: {Unwrap(ex).Message}>";
                    }
                }
                return new
                {
                    subSchemaGuid = Safe(() => subSchema.GUID.ToString(), string.Empty),
                    subSchemaName = SafeSchemaName(subSchema),
                    fields = nested
                };
            }

            case ElementId id:
                return new
                {
                    elementId = id.Value,
                    name = SafeRef(() => doc.GetElement(id)?.Name)
                };

            case XYZ p:
                return new { x = p.X, y = p.Y, z = p.Z };

            case UV uv:
                return new { u = uv.U, v = uv.V };

            case Guid g:
                return g.ToString();

            case string s:
                return s;

            // Map — checked before IEnumerable, which dictionaries also satisfy.
            case IDictionary map:
            {
                var items = new List<object?>();
                foreach (DictionaryEntry pair in map)
                {
                    items.Add(new
                    {
                        key = Describe(doc, pair.Key, maxDepth, depth),
                        value = Describe(doc, pair.Value, maxDepth, depth)
                    });
                }
                return items;
            }

            case IEnumerable list:
                return list.Cast<object?>().Select(item => Describe(doc, item, maxDepth, depth)).ToList();

            default:
            {
                // A map surfaced as a KeyValuePair sequence rather than a non-generic IDictionary.
                var type = value.GetType();
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                {
                    return new
                    {
                        key = Describe(doc, type.GetProperty("Key")?.GetValue(value), maxDepth, depth),
                        value = Describe(doc, type.GetProperty("Value")?.GetValue(value), maxDepth, depth)
                    };
                }
                return value;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    public static IList<Field> SafeListFields(Schema schema) =>
        Safe(() => schema.ListFields(), new List<Field>());

    public static string SafeSchemaName(Schema schema) =>
        Safe(() => schema.SchemaName, string.Empty);

    /// <summary>Element name — the property throws for some element kinds.</summary>
    public static string SafeElementName(Element element) =>
        Safe(() => element.Name, string.Empty);

    /// <summary>
    /// Revit API reads on stale or access-restricted objects throw rather than return null,
    /// and this metadata is never worth failing a read-only query over.
    /// </summary>
    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    private static T? SafeRef<T>(Func<T?> getter) where T : class
    {
        try { return getter(); }
        catch { return null; }
    }

    private static Exception Unwrap(Exception ex) =>
        (ex as TargetInvocationException)?.InnerException ?? ex;
}

internal sealed class EntityReadResult
{
    public string SchemaGuid { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public bool ReadAccessGranted { get; set; } = true;

    public Dictionary<string, object?> Fields { get; } = new();

    /// <summary>Field name to the unit its value was converted to, for measurable fields only.</summary>
    public Dictionary<string, string> Units { get; } = new();

    public List<string> Errors { get; } = new();

    public object ToPayload() => new
    {
        schemaGuid = SchemaGuid,
        schemaName = SchemaName,
        readAccessGranted = ReadAccessGranted,
        fieldCount = Fields.Count,
        fields = Fields,
        fieldUnits = Units.Count > 0 ? Units : null,
        errors = Errors
    };
}
