using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMCP.Addin.Configuration;

/// <summary>
/// Reads and writes JSON configuration files with atomic-write semantics and optional backup.
/// All write operations go through a temp-file → validate → move/replace cycle so a crash
/// during the write never corrupts the existing config file.
/// </summary>
public class JsonConfigService
{
    private static readonly JsonSerializerOptions _prettyOptions = new()
    {
        WriteIndented = true
    };

    // ─── Read ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a JSON config file.
    /// When <paramref name="createIfMissing"/> is true and the file does not exist,
    /// it is created with an empty object {} and that object is returned.
    /// Returns (null, error) on failure.
    /// </summary>
    public (JsonObject? Config, string? Error) Read(string filePath, bool createIfMissing = false)
    {
        if (!File.Exists(filePath))
        {
            if (!createIfMissing)
                return (null, $"Config file not found: {filePath}");

            var empty = new JsonObject();
            var writeErr = AtomicWrite(filePath, empty.ToJsonString(_prettyOptions));
            if (writeErr != null)
                return (null, $"Failed to create config file: {writeErr}");

            return (empty, null);
        }

        try
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj)
                return (null, $"Config file does not contain a JSON object: {filePath}");
            return (obj, null);
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid JSON in config file: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"Failed to read config file: {ex.Message}");
        }
    }

    // ─── Write ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the entire contents of a config file with a new JSON object.
    /// Validates the JSON before writing. Creates parent directories if needed.
    /// Optionally creates a timestamped backup of the existing file first.
    /// </summary>
    public (bool Success, string? Error, string? BackupPath) Write(
        string filePath, string jsonContent, bool backupBeforeOverwrite)
    {
        // Validate JSON first — fail fast before touching any files
        JsonObject? parsedObj;
        try
        {
            var node = JsonNode.Parse(jsonContent);
            if (node is not JsonObject obj)
                return (false, "Content must be a JSON object (starts with {}).", null);
            parsedObj = obj;
        }
        catch (JsonException ex)
        {
            return (false, $"Invalid JSON: {ex.Message}", null);
        }

        string? backupPath = null;
        if (backupBeforeOverwrite && File.Exists(filePath))
        {
            var (bp, err) = CreateBackup(filePath);
            if (err != null) return (false, $"Backup failed: {err}", null);
            backupPath = bp;
        }

        var normalised = parsedObj!.ToJsonString(_prettyOptions);
        var writeErr = AtomicWrite(filePath, normalised);
        if (writeErr != null)
            return (false, writeErr, backupPath);

        return (true, null, backupPath);
    }

    // ─── Update (JSONPath-style dot-path) ────────────────────────────────────

    /// <summary>
    /// Updates one or more properties inside an existing JSON config file using dot-path keys
    /// (e.g. "excel.defaultBackupBeforeSave" or "$.excel.defaultBackupBeforeSave").
    /// Missing intermediate objects are created automatically.
    /// When <paramref name="createIfMissing"/> is true the file is created if it does not exist.
    /// </summary>
    public (bool Success, string? Error, string? BackupPath, List<string> Applied) Update(
        string filePath,
        Dictionary<string, object?> updates,
        bool backupBeforeOverwrite,
        bool createIfMissing = false)
    {
        var applied = new List<string>();

        var (existing, readErr) = Read(filePath, createIfMissing);
        if (readErr != null) return (false, readErr, null, applied);

        string? backupPath = null;
        if (backupBeforeOverwrite && File.Exists(filePath))
        {
            var (bp, err) = CreateBackup(filePath);
            if (err != null) return (false, $"Backup failed: {err}", null, applied);
            backupPath = bp;
        }

        foreach (var (dotPath, value) in updates)
        {
            var cleanPath = dotPath.TrimStart('$').TrimStart('.');
            var parts = cleanPath.Split('.');
            SetByPath(existing!, parts, value);
            applied.Add(dotPath);
        }

        var writeErr = AtomicWrite(filePath, existing!.ToJsonString(_prettyOptions));
        if (writeErr != null) return (false, writeErr, backupPath, applied);

        return (true, null, backupPath, applied);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static void SetByPath(JsonObject root, string[] parts, object? value)
    {
        JsonObject current = root;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var key = parts[i];
            if (current[key] is not JsonObject nested)
            {
                nested = new JsonObject();
                current[key] = nested;
            }
            current = nested;
        }

        var lastKey = parts[^1];
        current[lastKey] = value is null
            ? null
            : JsonValue.Create(value.ToString());
    }

    /// <summary>
    /// Writes content atomically: write to .tmp → validate JSON → rename to target.
    /// Returns null on success, or an error string.
    /// </summary>
    private static string? AtomicWrite(string filePath, string jsonContent)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tmpPath = filePath + ".tmp";
        try
        {
            File.WriteAllText(tmpPath, jsonContent, Encoding.UTF8);

            // Validate the written file is parseable before making it canonical
            var verify = JsonNode.Parse(File.ReadAllText(tmpPath, Encoding.UTF8));
            if (verify == null) throw new InvalidDataException("Written file could not be parsed.");

            if (File.Exists(filePath)) File.Delete(filePath);
            File.Move(tmpPath, filePath);
            return null;
        }
        catch (Exception ex)
        {
            // Clean up temp file if it exists
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* ignore */ }
            return $"Atomic write failed: {ex.Message}";
        }
    }

    private static (string? BackupPath, string? Error) CreateBackup(string filePath)
    {
        try
        {
            var dir      = Path.GetDirectoryName(filePath) ?? string.Empty;
            var stem     = Path.GetFileNameWithoutExtension(filePath);
            var ext      = Path.GetExtension(filePath);
            var ts       = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
            var bakPath  = Path.Combine(dir, $"{stem}_backup_{ts}{ext}");
            File.Copy(filePath, bakPath, overwrite: false);
            return (bakPath, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
