using System.IO;
using System.Text.Json;

namespace RevitMCP.Addin.FileSystem;

/// <summary>
/// Validates file paths against a configurable access policy.
/// Config: %ProgramData%\RKTools\MCP\Config\FileAccessPolicy.json
/// Falls back to permissive defaults when config is missing.
/// </summary>
public class FilePathPolicy
{
    private readonly FileAccessPolicyConfig _config;

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RKTools", "MCP", "Config", "FileAccessPolicy.json");

    public FilePathPolicy() => _config = LoadConfig();

    public FilePathPolicy(FileAccessPolicyConfig config) => _config = config;

    public int MaxReadBytes => _config.MaxReadBytesDefault;

    private static FileAccessPolicyConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<FileAccessPolicyConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null) return loaded;
            }
        }
        catch { /* fall through to defaults */ }

        return DefaultConfig();
    }

    private static FileAccessPolicyConfig DefaultConfig() => new()
    {
        AllowedReadRoots =
        [
            "%USERPROFILE%",
            "C:\\Projects",
            "%TEMP%",
            "%TMP%"
        ],
        AllowedWriteRoots =
        [
            "%USERPROFILE%\\Documents",
            "%USERPROFILE%\\Desktop",
            "C:\\Projects",
            "%TEMP%",
            "%TMP%"
        ],
        AllowNetworkPaths = true,
        AllowProgramDataWrites = true,
        MaxReadBytesDefault = 1_048_576
    };

    /// <summary>Validates a path for read access. Returns null on success, error message on failure.</summary>
    public string? ValidateRead(string rawPath)
    {
        var (path, error) = NormalizePath(rawPath);
        if (error != null) return error;
        return CheckAllowedRoots(path!, _config.AllowedReadRoots, out var msg) ? null : msg;
    }

    /// <summary>Validates a path for write access. Returns null on success, error message on failure.</summary>
    public string? ValidateWrite(string rawPath)
    {
        var (path, error) = NormalizePath(rawPath);
        if (error != null) return error;
        return CheckAllowedRoots(path!, _config.AllowedWriteRoots, out var msg) ? null : msg;
    }

    /// <summary>Expands environment variables, resolves to full path, and blocks traversal.</summary>
    public static (string? Path, string? Error) NormalizePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return (null, "Path cannot be empty.");

        var expanded = Environment.ExpandEnvironmentVariables(rawPath);

        if (expanded.Contains(".."))
            return (null, "Path traversal sequences ('..') are not allowed.");

        try
        {
            var full = Path.GetFullPath(expanded);
            return (full, null);
        }
        catch (Exception ex)
        {
            return (null, $"Invalid path: {ex.Message}");
        }
    }

    private static bool CheckAllowedRoots(string normalizedPath, IEnumerable<string> roots, out string errorMessage)
    {
        foreach (var rawRoot in roots)
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(rawRoot);
                var fullRoot = Path.GetFullPath(expanded)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                var fullPath = normalizedPath
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = string.Empty;
                    return true;
                }
            }
            catch { /* skip invalid root */ }
        }

        errorMessage = $"Path '{normalizedPath}' is outside all allowed roots. " +
                       "Add the folder to FileAccessPolicy.json to permit access.";
        return false;
    }
}

public class FileAccessPolicyConfig
{
    public List<string> AllowedReadRoots { get; set; } = new();
    public List<string> AllowedWriteRoots { get; set; } = new();
    public bool AllowNetworkPaths { get; set; } = true;
    public bool AllowProgramDataWrites { get; set; } = true;
    public int MaxReadBytesDefault { get; set; } = 1_048_576;
}
