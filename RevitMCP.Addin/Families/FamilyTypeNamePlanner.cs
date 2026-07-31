namespace RevitMCP.Addin.Families;

/// <summary>
/// Pure name planning for family type duplication and renaming: builds the candidate name,
/// makes it unique against the names already taken inside the same family, and rejects names
/// Revit itself would refuse. No Revit API dependency — unit tested in RevitMCP.Tests.
/// </summary>
public static class FamilyTypeNamePlanner
{
    /// <summary>Characters Revit rejects in element and type names.</summary>
    public const string InvalidNameCharacters = @"\:{}[]|;<>?`~";

    /// <summary>
    /// Builds the raw copy name from the source name. A "{index}" placeholder in the prefix or
    /// suffix is replaced with the copy number; without one, the copy number is appended only
    /// when more than one copy is requested.
    /// </summary>
    public static string ComposeCopyName(
        string sourceName,
        string namePrefix,
        string nameSuffix,
        int copyIndex,
        int totalCopies)
    {
        var prefix = namePrefix ?? string.Empty;
        var suffix = nameSuffix ?? string.Empty;
        var hasPlaceholder = ContainsPlaceholder(prefix) || ContainsPlaceholder(suffix);

        prefix = ReplacePlaceholder(prefix, copyIndex);
        suffix = ReplacePlaceholder(suffix, copyIndex);

        var composed = prefix + (sourceName ?? string.Empty) + suffix;
        if (!hasPlaceholder && totalCopies > 1)
            composed += " " + copyIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return composed;
    }

    /// <summary>
    /// Returns <paramref name="candidate"/> if it is free, otherwise appends " 2", " 3", … until
    /// it is. Numbering starts at 2 because the unsuffixed name is the one already in the model.
    /// </summary>
    public static string ResolveUniqueName(string candidate, ISet<string> takenNames)
    {
        var name = candidate ?? string.Empty;
        if (takenNames == null || !takenNames.Contains(name))
            return name;

        for (var index = 2; ; index++)
        {
            var resolved = name + " " + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!takenNames.Contains(resolved))
                return resolved;
        }
    }

    /// <summary>
    /// Validates a type name against Revit's naming rules so the caller can report a clear reason
    /// instead of surfacing a raw API exception.
    /// </summary>
    public static bool IsValidTypeName(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Type name is empty.";
            return false;
        }

        var trimmed = name!;
        if (trimmed.Length != trimmed.Trim().Length)
        {
            error = "Type name has leading or trailing whitespace.";
            return false;
        }

        foreach (var c in trimmed)
        {
            if (InvalidNameCharacters.IndexOf(c) >= 0)
            {
                error = $"Type name contains the character '{c}', which Revit does not allow in names.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool ContainsPlaceholder(string value) =>
        value.IndexOf("{index}", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string ReplacePlaceholder(string value, int copyIndex)
    {
        var start = value.IndexOf("{index}", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return value;

        var replacement = copyIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = value;
        while (start >= 0)
        {
            result = result.Substring(0, start) + replacement + result.Substring(start + "{index}".Length);
            start = result.IndexOf("{index}", start + replacement.Length, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }
}
