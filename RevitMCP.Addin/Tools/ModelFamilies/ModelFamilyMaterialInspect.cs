using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>The inspection sections of the material feature (#173): the materials and the form assignments. Read only.</summary>
internal static class ModelFamilyMaterialInspect
{
    /// <summary>Each material of the family with the elements that use it directly and the parameters that default to it.</summary>
    public static JToken Materials(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;

        // Element ids whose material is set directly (not driven by a parameter), by material id.
        var usedBy = new Dictionary<long, List<long>>();
        foreach (var form in ModelFamilyMaterialRevit.FormsWithMaterial(doc))
        {
            var parameter = ModelFamilyMaterialRevit.MaterialParameter(form)!;
            if (ModelFamilyAssociations.Associated(doc, parameter) != null) continue;
            var id = ModelFamilyMaterialRevit.SafeId(parameter);
            if (id <= 0) continue;
            if (!usedBy.TryGetValue(id, out var list)) usedBy[id] = list = new List<long>();
            list.Add(form.Id.Value);
        }

        // Material parameters with their value in each type, by material id.
        var defaultOf = new Dictionary<long, Dictionary<string, List<string>>>();
        var types = doc.FamilyManager.Types.Cast<FamilyType>().ToList();
        foreach (var parameter in ModelFamilyLookup.Parameters(doc).Where(ModelFamilyMaterialRevit.IsMaterialParameter))
        {
            foreach (var type in types)
            {
                long id;
                try { id = type.HasValue(parameter) ? type.AsElementId(parameter).Value : -1; }
                catch { continue; }
                if (id <= 0) continue;
                if (!defaultOf.TryGetValue(id, out var byParameter)) defaultOf[id] = byParameter = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (!byParameter.TryGetValue(parameter.Definition.Name, out var names)) byParameter[parameter.Definition.Name] = names = new List<string>();
                names.Add(type.Name);
            }
        }

        var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new JArray();
        foreach (var material in materials.Take(limit))
        {
            var row = ModelFamilyMaterialRevit.Snapshot(material);
            var ordered = new JObject
            {
                ["id"] = material.Id.Value,
                ["name"] = material.Name,
                ["color"] = row["color"],
                ["transparency"] = row["transparency"],
                ["class"] = row["materialClass"],
                ["usedBy"] = new JArray(usedBy.TryGetValue(material.Id.Value, out var users) ? users : new List<long>()),
                ["defaultOf"] = new JArray((defaultOf.TryGetValue(material.Id.Value, out var parameters)
                    ? parameters.Select(p => new JObject { ["parameter"] = p.Key, ["types"] = new JArray(p.Value) })
                    : Enumerable.Empty<JObject>()))
            };
            rows.Add(ordered);
        }
        if (materials.Count > limit)
            context.Warnings.Add($"materials: {materials.Count - limit} more materials not listed (limit {limit}).");
        return rows;
    }

    /// <summary>Each form with a Material property: where its material comes from and which it is.</summary>
    public static JToken Assignments(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var forms = ModelFamilyMaterialRevit.FormsWithMaterial(doc);
        var rows = new JArray();
        foreach (var form in forms.Take(limit))
        {
            if (ModelFamilyMaterialRevit.State(doc, form) is not { } state) continue;
            state["kind"] = ModelFamilyMaterialState.KindOf(form.GetType().Name);
            if (form is GenericForm generic) state["isSolid"] = generic.IsSolid;
            rows.Add(state);
        }
        if (forms.Count > limit)
            context.Warnings.Add($"materialAssignments: {forms.Count - limit} more forms not listed (limit {limit}).");
        return rows;
    }
}
