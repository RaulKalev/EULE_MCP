using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace RevitMCP.Addin.Mep;

/// <summary>
/// Extensible Storage marker for the elements of a run drawn by revit_create_mep_run (#106), like the circuit-arcs
/// marker: every segment and fitting of a run carries the run's id, so revit_get_mep_run_summary runId=… finds the run
/// again. Public read and write access, plain string fields, so revit_read_extensible_storage decodes it. The schema
/// name is RevitMCP_MepRun: Revit schema names allow letters, digits and underscores only, so "RevitMCP.MepRun" from
/// the issue is spelled with an underscore.
/// </summary>
internal static class MepRunSchema
{
    public static readonly Guid SchemaGuid = new("b8f0c3a2-6d4e-4c71-9e25-3f1a7d6b9c04");
    public const string SchemaName = "RevitMCP_MepRun";
    public const string RunIdField = "RunId";
    public const string KindField = "Kind";
    public const string RoleField = "Role";
    public const string CreatedField = "CreatedUtc";

    public const string RoleSegment = "segment";
    public const string RoleFitting = "fitting";

    /// <summary>The schema, built the first time it is needed in this Revit session.</summary>
    public static Schema GetOrCreate()
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(SchemaGuid);
        builder.SetSchemaName(SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.SetDocumentation("Marks a cable tray or conduit segment or fitting drawn by Revit MCP with the id of its run.");
        builder.AddSimpleField(RunIdField, typeof(string));
        builder.AddSimpleField(KindField, typeof(string));
        builder.AddSimpleField(RoleField, typeof(string));
        builder.AddSimpleField(CreatedField, typeof(string));
        return builder.Finish();
    }

    /// <summary>Stores the run id on an element. Inside a transaction.</summary>
    public static void Mark(Element element, string runId, string kind, string role)
    {
        var entity = new Entity(GetOrCreate());
        entity.Set(RunIdField, runId);
        entity.Set(KindField, kind);
        entity.Set(RoleField, role);
        entity.Set(CreatedField, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        element.SetEntity(entity);
    }

    /// <summary>The run id stored on the element, or null.</summary>
    public static string? RunIdOf(Element element)
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema == null) return null;
        try
        {
            var entity = element.GetEntity(schema);
            if (!entity.IsValid()) return null;
            var value = entity.Get<string>(RunIdField);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A filter for every element carrying the marker; null when the schema is not in this session (no runs drawn).</summary>
    public static ElementFilter? Filter() => Schema.Lookup(SchemaGuid) == null ? null : new ExtensibleStorageFilter(SchemaGuid);
}
