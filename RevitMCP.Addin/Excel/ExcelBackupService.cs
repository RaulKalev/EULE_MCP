using System.IO;

namespace RevitMCP.Addin.Excel;

/// <summary>Creates timestamped backup copies of Excel files before modification.</summary>
public static class ExcelBackupService
{
    /// <summary>
    /// Copies the file to a backup path with a timestamp suffix.
    /// Returns the backup path.
    /// Throws on failure — caller should handle.
    /// </summary>
    public static string CreateBackup(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var backupPath = RevitMCP.Addin.FileSystem.BackupPathNaming.Unique(dir, stem, "backup", DateTime.Now, ext);
        File.Copy(filePath, backupPath, overwrite: false);
        return backupPath;
    }
}
