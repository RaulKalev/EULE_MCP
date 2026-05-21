using System.IO;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Export;

public class ExportPathService
{
    private static readonly string _baseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "RKTools", "RevitMCP", "Exports");

    private static readonly Regex _invalid = new(@"[\\/:*?""<>|]", RegexOptions.Compiled);

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
        Directory.CreateDirectory(_baseDir);

        var sanitized = _invalid.Replace(requestedName, "_");
        if (!sanitized.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            sanitized += ".xlsx";

        var path = Path.Combine(_baseDir, sanitized);
        if (!File.Exists(path)) return path;

        // Add timestamp to avoid overwrites
        var stem = Path.GetFileNameWithoutExtension(sanitized);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        return Path.Combine(_baseDir, $"{stem}_{stamp}.xlsx");
    }
}
