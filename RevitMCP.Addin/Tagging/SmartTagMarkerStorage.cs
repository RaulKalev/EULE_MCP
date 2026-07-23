#nullable disable

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace RevitMCP.Addin.Tagging
{
    public static class SmartTagMarkerStorage
    {
        // Keep the original SmartTags schema so tags made by either add-in remain discoverable.
        private static readonly Guid SchemaGuid =
            new Guid("A7B3C4D5-E6F7-8901-2345-6789ABCDEF01");

        private const string SchemaName = "SmartTagsMarker";
        private const string PluginNameField = "PluginName";
        private const string PluginVersionField = "PluginVersion";
        private const string CreationTimestampField = "CreationTimestamp";
        private const string ReferencedElementIdField = "ReferencedElementId";
        private const string ManagedField = "Managed";

        public static Schema EnsureSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null)
                return existing;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetDocumentation(
                "Marks tags created and managed by SmartTags-compatible tools.");
            builder.AddSimpleField(PluginNameField, typeof(string));
            builder.AddSimpleField(PluginVersionField, typeof(string));
            builder.AddSimpleField(CreationTimestampField, typeof(string));
            builder.AddSimpleField(ReferencedElementIdField, typeof(long));
            builder.AddSimpleField(ManagedField, typeof(bool));
            return builder.Finish();
        }

        public static void SetManagedTag(
            IndependentTag tag,
            ElementId referencedElementId)
        {
            if (tag == null)
                return;

            var entity = new Entity(EnsureSchema());
            entity.Set(PluginNameField, "RevitMCP");
            entity.Set(PluginVersionField, "1.0.0");
            entity.Set(CreationTimestampField, DateTime.UtcNow.ToString("o"));
            entity.Set(ManagedField, true);
            entity.Set(
                ReferencedElementIdField,
                referencedElementId == null ||
                referencedElementId == ElementId.InvalidElementId
                    ? -1L
                    : referencedElementId.Value);
            tag.SetEntity(entity);
        }

        public static bool IsManagedTag(IndependentTag tag)
        {
            if (tag == null)
                return false;

            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null)
                return false;

            var entity = tag.GetEntity(schema);
            if (!entity.IsValid())
                return false;

            try { return entity.Get<bool>(ManagedField); }
            catch { return false; }
        }

        public static bool TryGetMetadata(
            IndependentTag tag,
            out SmartTagMetadata metadata)
        {
            metadata = null;
            if (tag == null)
                return false;

            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null)
                return false;

            var entity = tag.GetEntity(schema);
            if (!entity.IsValid())
                return false;

            try
            {
                var referenceValue = entity.Get<long>(ReferencedElementIdField);
                metadata = new SmartTagMetadata
                {
                    PluginName = entity.Get<string>(PluginNameField),
                    PluginVersion = entity.Get<string>(PluginVersionField),
                    CreationTimestamp = entity.Get<string>(CreationTimestampField),
                    ReferencedElementId = referenceValue >= 0
                        ? new ElementId(referenceValue)
                        : ElementId.InvalidElementId,
                    Managed = entity.Get<bool>(ManagedField)
                };
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class SmartTagMetadata
    {
        public string PluginName { get; set; }
        public string PluginVersion { get; set; }
        public string CreationTimestamp { get; set; }
        public ElementId ReferencedElementId { get; set; }
        public bool Managed { get; set; }
    }
}
