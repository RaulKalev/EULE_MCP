using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Families;

/// <summary>
/// Builds and sanitizes family names for DWG-generated detail item families.
/// </summary>
public static class FamilyNameService
{
    private static readonly char[] _invalidChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// Sanitizes a user-supplied name for use as part of a Windows filename.
    /// - Trims leading/trailing whitespace
    /// - Replaces spaces with underscores
    /// - Replaces invalid filename characters with underscores
    /// - Collapses consecutive underscores to one
    /// - Strips leading/trailing underscores
    /// </summary>
    public static string SanitizeName(string userDefinedName)
    {
        var s = userDefinedName.Trim();
        foreach (var c in _invalidChars)
            s = s.Replace(c, '_');
        s = s.Replace(' ', '_');
        s = Regex.Replace(s, "_+", "_");
        s = s.Trim('_');
        return s;
    }

    /// <summary>
    /// Returns <paramref name="prefix"/> + sanitized <paramref name="userDefinedName"/>.
    /// Throws <see cref="ArgumentException"/> if the sanitized name is empty.
    /// </summary>
    public static string BuildFamilyName(string prefix, string userDefinedName)
    {
        var sanitized = SanitizeName(userDefinedName);
        if (string.IsNullOrEmpty(sanitized))
            throw new ArgumentException(
                "User-defined name is empty after sanitizing invalid characters.",
                nameof(userDefinedName));
        return prefix + sanitized;
    }
}
