using System.Globalization;
using System.IO;

namespace RevitMCP.Addin.FileSystem;

/// <summary>
/// Timestamped backup file names (config, Excel and file backups). The timestamp has one-second
/// resolution, so two writes to the same file within a second used to collide and the second write
/// failed with "Backup failed: file already exists". A free name now gets a _2, _3, … suffix.
/// </summary>
public static class BackupPathNaming
{
    public static string Unique(string directory, string stem, string label, DateTime timestamp, string extension)
    {
        var stamp = timestamp.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{stem}_{label}_{stamp}{extension}");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{stem}_{label}_{stamp}_{n.ToString(CultureInfo.InvariantCulture)}{extension}");
        return path;
    }
}
