using System.IO;
using System.Text;

namespace RevitMCP.Addin.Documents;

/// <summary>
/// The file name of the agent's own local copy of a central model (#101): <c>&lt;central&gt;_&lt;user&gt;_mcp.rvt</c>, so it
/// never collides with the user's own local (Revit names that <c>&lt;central&gt;_&lt;user&gt;.rvt</c>). Pure.
/// </summary>
public static class LocalFileNaming
{
    public const string Suffix = "_mcp";
    public const int MaxStemLength = 100;

    public static string FileName(string centralPath, string? username)
    {
        var stem = Sanitize(Path.GetFileNameWithoutExtension(centralPath ?? string.Empty));
        if (stem.Length == 0) stem = "model";
        var user = Sanitize(username ?? string.Empty);
        var name = user.Length > 0 ? $"{stem}_{user}{Suffix}" : stem + Suffix;
        if (name.Length > MaxStemLength) name = name.Substring(0, MaxStemLength);
        return name + ".rvt";
    }

    /// <summary>
    /// The path in <paramref name="folder"/>; when a file of that name exists (an earlier local the connector still
    /// holds open or could not replace) a timestamp keeps the new one apart.
    /// </summary>
    public static string PathIn(string folder, string centralPath, string? username, Func<string, bool> exists, DateTime now)
    {
        var path = Path.Combine(folder, FileName(centralPath, username));
        if (!exists(path)) return path;
        var stamped = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "_" + now.ToString("yyyyMMdd-HHmmss") + ".rvt");
        return stamped;
    }

    /// <summary>Letters, digits, '-', '_' and '.'; anything else (including path separators) becomes '_'.</summary>
    public static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
        return sb.ToString().Trim('.', '_');
    }
}
