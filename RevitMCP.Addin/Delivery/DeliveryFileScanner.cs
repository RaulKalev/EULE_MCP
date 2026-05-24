using System.IO;
using RevitMCP.Addin.FileSystem;

namespace RevitMCP.Addin.Delivery;

/// <summary>
/// Scans a delivery folder and returns structured file inventory with parsed drawing names.
/// Uses FilePathPolicy to enforce allowed read paths.
/// </summary>
public class DeliveryFileScanner
{
    private readonly FilePathPolicy _policy;

    public DeliveryFileScanner(FilePathPolicy policy)
    {
        _policy = policy;
    }

    public DeliveryScanResult Scan(
        string folderPath,
        bool recursive,
        IEnumerable<string>? includeExtensions,
        int maxResults)
    {
        var policyError = _policy.ValidateRead(folderPath);
        if (policyError != null)
            return DeliveryScanResult.Fail(folderPath, policyError);

        if (!Directory.Exists(folderPath))
            return DeliveryScanResult.Fail(folderPath, $"Folder not found: {folderPath}");

        var allowedExts = BuildAllowedExtensions(includeExtensions);

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> allFiles;
        try
        {
            allFiles = Directory.EnumerateFiles(folderPath, "*", option);
        }
        catch (Exception ex)
        {
            return DeliveryScanResult.Fail(folderPath, $"Failed to enumerate folder: {ex.Message}");
        }

        var files = new List<DeliveryFileEntry>();
        var warnings = new List<string>();
        int total = 0;

        foreach (var fullPath in allFiles)
        {
            if (maxResults > 0 && total >= maxResults)
            {
                warnings.Add($"maxResults limit ({maxResults}) reached — scan is truncated.");
                break;
            }

            var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
            if (allowedExts != null && !allowedExts.Contains(ext)) continue;

            total++;
            FileInfo fi;
            try { fi = new FileInfo(fullPath); }
            catch { warnings.Add($"Could not stat file: {fullPath}"); continue; }

            var fileName = fi.Name;
            var parsed = DeliveryFileNameParser.Parse(fileName);

            if (parsed == null && DeliveryFileNameParser.LooksLikeEuleDrawing(fileName))
                warnings.Add($"Partial parse failure: {fileName}");

            if (fi.Length == 0)
                warnings.Add($"Zero-byte file: {fileName}");

            files.Add(new DeliveryFileEntry
            {
                FileName = fileName,
                Extension = ext,
                FullPath = fullPath,
                SizeBytes = fi.Length,
                ModifiedAt = fi.LastWriteTimeOffset(),
                Parsed = parsed
            });
        }

        return new DeliveryScanResult
        {
            Success = true,
            FolderPath = folderPath,
            Files = files,
            Warnings = warnings,
            SubFolders = Directory.Exists(folderPath)
                ? Directory.GetDirectories(folderPath)
                    .Select(Path.GetFileName)
                    .Where(n => n != null)
                    .Select(n => n!)
                    .ToList()
                : []
        };
    }

    /// <summary>
    /// Converts an optional extension list into a filter set.
    /// Returns null (= scan all) when the input is null, empty, or contains "*" or "all".
    /// Strips leading dots and normalises to lower-case.
    /// </summary>
    private static HashSet<string>? BuildAllowedExtensions(IEnumerable<string>? includeExtensions)
    {
        if (includeExtensions == null)
            return null;

        var normalized = includeExtensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
            .ToList();

        if (normalized.Count == 0)
            return null;

        if (normalized.Contains("*") || normalized.Contains("all"))
            return null;

        return normalized.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class DeliveryScanResult
{
    public bool Success { get; init; }
    public string FolderPath { get; init; } = string.Empty;
    public string? Error { get; init; }
    public List<DeliveryFileEntry> Files { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<string> SubFolders { get; init; } = new();

    public static DeliveryScanResult Fail(string folder, string error) =>
        new() { Success = false, FolderPath = folder, Error = error };
}

public sealed class DeliveryFileEntry
{
    public string FileName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>Null if the file name could not be parsed.</summary>
    public ParsedDeliveryFileName? Parsed { get; init; }
}

/// <summary>Helper to get <see cref="DateTimeOffset"/> from <see cref="FileInfo"/>.</summary>
internal static class FileInfoExtensions
{
    internal static DateTimeOffset LastWriteTimeOffset(this FileInfo fi)
        => new DateTimeOffset(fi.LastWriteTime, TimeZoneInfo.Local.GetUtcOffset(fi.LastWriteTime));
}
