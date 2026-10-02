using RevitMCP.Addin.Families;

namespace RevitMCP.Addin.Electrical;

/// <summary>One requested cable type: the new name plus optional type parameter values.</summary>
public sealed class CableTypeCreationItem
{
    public string NewName { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What the create tool will do for one requested name.</summary>
public enum CableTypeCreationAction
{
    /// <summary>Duplicate the source type under the new name.</summary>
    Create,
    /// <summary>A type with this name already exists and ifExists=skip — return its id.</summary>
    SkipExisting,
    /// <summary>The entry cannot be created; see <see cref="PlannedCableType.Reason"/>.</summary>
    Blocked
}

public sealed class PlannedCableType
{
    public CableTypeCreationItem Item { get; set; } = new();
    public CableTypeCreationAction Action { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>The name an existing type already uses, when it matches case-insensitively.</summary>
    public string? ExistingName { get; set; }
}

/// <summary>
/// Pure planning for revit_create_cable_type: validates names, applies the ifExists policy and
/// catches duplicate names inside one batch. No Revit API dependency — unit tested in RevitMCP.Tests.
/// The preview and write tools both execute this plan, so they cannot drift apart.
/// </summary>
public static class CableTypeCreationPlanner
{
    public const int MaxItems = 50;

    /// <summary>Normalizes the ifExists argument. Returns null for an unknown value.</summary>
    public static string? NormalizeIfExists(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (v.Length == 0) return "skip";
        return v is "skip" or "error" ? v : null;
    }

    /// <summary>
    /// Plans every requested name against the names already used by cable types in the model.
    /// Revit compares type names case-insensitively, so the plan does too.
    /// </summary>
    public static List<PlannedCableType> Plan(
        IReadOnlyList<CableTypeCreationItem> items,
        IEnumerable<string> existingNames,
        string ifExists)
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in existingNames)
        {
            if (!string.IsNullOrEmpty(name) && !existing.ContainsKey(name))
                existing[name] = name;
        }

        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<PlannedCableType>();

        foreach (var item in items)
        {
            var entry = new PlannedCableType { Item = item };
            plan.Add(entry);

            if (!FamilyTypeNamePlanner.IsValidTypeName(item.NewName, out var nameError))
            {
                entry.Action = CableTypeCreationAction.Blocked;
                entry.Reason = nameError;
                continue;
            }

            if (!seenInBatch.Add(item.NewName))
            {
                entry.Action = CableTypeCreationAction.Blocked;
                entry.Reason = $"'{item.NewName}' appears more than once in this request.";
                continue;
            }

            if (existing.TryGetValue(item.NewName, out var existingName))
            {
                entry.ExistingName = existingName;
                if (ifExists == "error")
                {
                    entry.Action = CableTypeCreationAction.Blocked;
                    entry.Reason = $"A cable type named '{existingName}' already exists (ifExists=error).";
                }
                else
                {
                    entry.Action = CableTypeCreationAction.SkipExisting;
                    entry.Reason = $"A cable type named '{existingName}' already exists — its id is returned.";
                }
                continue;
            }

            entry.Action = CableTypeCreationAction.Create;
            entry.Reason = "Will be created.";
        }

        return plan;
    }
}
