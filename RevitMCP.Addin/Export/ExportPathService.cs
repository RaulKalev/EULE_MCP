using System.IO;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Export;

/// <summary>
/// Where an Excel export is written. A plain name goes to Documents\RKTools\RevitMCP\Exports; a full path is
/// honoured (its folder is created). An existing file is never overwritten: the name gets a timestamp to the second,
/// then a counter. X5: a path's separators used to become "_" (so every export landed in the default folder), and
/// the minute timestamp let two exports in the same minute overwrite each other.
/// </summary>
public class ExportPathService
{
    private static readonly string _baseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "RKTools", "RevitMCP", "Exports");

    private static readonly Regex _invalid = new(@"[\\/:*?""<>|]", RegexOptions.Compiled);

    /// <summary>Clock for the timestamp (tests).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public string ExportDirectory
    {
        get
        {
            Directory.CreateDirectory(_baseDir);
            return _baseDir;
        }
    }

    public string ResolveFilePath(string requestedName)
    {
        var requested = (requestedName ?? string.Empty).Trim().Trim('"');
        var folder = _baseDir;
        var name = requested;
        if (Path.IsPathRooted(requested) && !string.IsNullOrEmpty(Path.GetFileName(requested)))
        {
            folder = Path.GetDirectoryName(requested) ?? _baseDir;
            name = Path.GetFileName(requested);
        }
        Directory.CreateDirectory(folder);

        var sanitized = _invalid.Replace(name, "_");
        if (sanitized.Length == 0) sanitized = "Export";
        if (!sanitized.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            sanitized += ".xlsx";

        var path = Path.Combine(folder, sanitized);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(sanitized);
        var stamped = Path.Combine(folder, $"{stem}_{Now():yyyy-MM-dd_HHmmss}.xlsx");
        for (var n = 2; File.Exists(stamped); n++)
            stamped = Path.Combine(folder, $"{stem}_{Now():yyyy-MM-dd_HHmmss}_{n}.xlsx");
        return stamped;
    }
}
