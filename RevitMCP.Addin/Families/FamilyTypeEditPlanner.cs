using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Families;

/// <summary>
/// Turns the edit-family-types arguments into one planned edit per target type, shared by the
/// preview and write tools.
/// </summary>
internal static class FamilyTypeEditPlanner
{
    /// <summary>
    /// Builds the plan. Returns null with <paramref name="error"/> set when the request is invalid;
    /// per-type problems are reported on the individual entries.
    /// </summary>
    public static List<PlannedFamilyTypeEdit>? Build(
        Document doc,
        Dictionary<string, object?> arguments,
        List<string> warnings,
        out string? error)
    {
        error = null;

        var edits = ParseEdits(arguments);
        var plan = new List<PlannedFamilyTypeEdit>();

        if (edits.Count > 0)
        {
            foreach (var edit in edits)
            {
                if (doc.GetElement(new ElementId(edit.TypeId)) is not ElementType type)
                {
                    warnings.Add($"Type {edit.TypeId} was not found or is not a family type — skipped.");
                    continue;
                }
                plan.Add(new PlannedFamilyTypeEdit
                {
                    Type = type,
                    NewName = edit.NewName,
                    Parameters = edit.Parameters,
                    BlockedReason = edit.NewName == null && edit.Parameters.Count == 0
                        ? "Nothing to change — the entry has neither newName nor parameters."
                        : null
                });
            }

            if (plan.Count == 0)
            {
                error = "None of the supplied edits resolved to a family type. " +
                        "Use revit_list_family_types to find valid type ids.";
                return null;
            }
        }
        else
        {
            var typeIds = ToolArguments.GetLongArray(arguments, "typeIds");
            var familyName = ToolArguments.GetString(arguments, "familyName").Trim();
            var typeName = ToolArguments.GetString(arguments, "typeName").Trim();
            var newName = ToolArguments.GetString(arguments, "newName").Trim();
            var parameters = ViewManagerToolSupport.GetStringDictionary(arguments, "parameters");

            if (newName.Length == 0 && parameters.Count == 0)
            {
                error = "Nothing to do — provide edits, or newName and/or parameters.";
                return null;
            }

            var types = FamilyTypeSupport.ResolveTypes(doc, typeIds, familyName, typeName, warnings, out error);
            if (types == null)
                return null;

            if (newName.Length > 0 && types.Count > 1)
            {
                error = $"newName renames a single type but {types.Count} types were resolved. " +
                        "Use the edits array to give each type its own name.";
                return null;
            }

            foreach (var type in types)
            {
                plan.Add(new PlannedFamilyTypeEdit
                {
                    Type = type,
                    NewName = newName.Length > 0 ? newName : null,
                    Parameters = parameters
                });
            }
        }

        ValidateNames(doc, plan);
        return plan;
    }

    /// <summary>Flags renames Revit would reject: invalid characters, or a name already in the family.</summary>
    private static void ValidateNames(Document doc, List<PlannedFamilyTypeEdit> plan)
    {
        var takenNamesByFamily = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entry in plan)
        {
            if (entry.NewName == null)
                continue;

            var currentName = FamilyTypeSupport.SafeName(entry.Type);
            if (string.Equals(entry.NewName, currentName, StringComparison.Ordinal))
            {
                entry.NewName = null;
                entry.RenameNote = "New name matches the current name — no rename needed.";
                continue;
            }

            if (!FamilyTypeNamePlanner.IsValidTypeName(entry.NewName, out var nameError))
            {
                entry.BlockedReason = nameError;
                continue;
            }

            var key = FamilyScopeKey(entry.Type);
            if (!takenNamesByFamily.TryGetValue(key, out var taken))
            {
                taken = FamilyTypeSupport.CollectSiblingNames(doc, entry.Type);
                takenNamesByFamily[key] = taken;
            }

            if (taken.Contains(entry.NewName))
            {
                entry.BlockedReason =
                    $"A type named '{entry.NewName}' already exists in this family. " +
                    "Type names must be unique — pick another name.";
                continue;
            }

            // Later entries in the same family must not claim this name too.
            taken.Remove(currentName);
            taken.Add(entry.NewName);
        }
    }

    private static string FamilyScopeKey(ElementType type)
    {
        try
        {
            if (type is FamilySymbol symbol && symbol.Family != null)
                return "family:" + symbol.Family.Id.Value;
        }
        catch { }

        return "class:" + type.GetType().FullName + "|category:" + (type.Category?.Id.Value.ToString() ?? "none");
    }

    private static List<ParsedFamilyTypeEdit> ParseEdits(Dictionary<string, object?> arguments)
    {
        var edits = new List<ParsedFamilyTypeEdit>();
        if (!arguments.TryGetValue("edits", out var raw) || raw == null)
            return edits;

        JArray? array = raw switch
        {
            JArray ja => ja,
            string text => ToolArguments.TryParseJArray(text),
            _ => TryFromObject(raw)
        };

        if (array == null)
            return edits;

        foreach (var token in array)
        {
            if (token is not JObject obj)
                continue;

            var typeId = obj["typeId"]?.Value<long?>() ?? 0L;
            if (typeId <= 0)
                continue;

            var newName = obj["newName"]?.Value<string>()?.Trim();
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (obj["parameters"] is JObject parameterObject)
            {
                foreach (var property in parameterObject.Properties())
                {
                    parameters[property.Name] = property.Value.Type == JTokenType.Null
                        ? string.Empty
                        : property.Value.ToString();
                }
            }

            edits.Add(new ParsedFamilyTypeEdit
            {
                TypeId = typeId,
                NewName = string.IsNullOrEmpty(newName) ? null : newName,
                Parameters = parameters
            });
        }

        return edits;
    }

    private static JArray? TryFromObject(object value)
    {
        try { return JArray.FromObject(value); }
        catch { return null; }
    }

    private sealed class ParsedFamilyTypeEdit
    {
        public long TypeId { get; set; }
        public string? NewName { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class PlannedFamilyTypeEdit
{
    public ElementType Type { get; set; } = null!;
    public string? NewName { get; set; }
    public string? RenameNote { get; set; }
    public string? BlockedReason { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool CanEdit => BlockedReason == null;
}
