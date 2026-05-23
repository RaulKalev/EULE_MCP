using System.IO;

namespace RevitMCP.Addin.Families;

/// <summary>
/// Resolves an available output path for a generated .rfa family file.
/// If the base name is taken, appends _01, _02, ... _99.
/// </summary>
public static class FileVersioningService
{
    /// <summary>
    /// Returns an available file path and an optional warning if a version suffix was applied.
    /// Creates <paramref name="outputFolder"/> if it does not exist.
    /// </summary>
    public static (string filePath, string? warning) GetAvailableFamilyPath(
        string outputFolder, string baseName)
    {
        Directory.CreateDirectory(outputFolder);

        var candidate = Path.Combine(outputFolder, baseName + ".rfa");
        if (!File.Exists(candidate))
            return (candidate, null);

        for (int i = 1; i <= 99; i++)
        {
            var suffix    = i.ToString("D2");
            var versioned = Path.Combine(outputFolder, $"{baseName}_{suffix}.rfa");
            if (!File.Exists(versioned))
            {
                return (versioned,
                    $"{baseName}.rfa already existed. Created {baseName}_{suffix}.rfa instead.");
            }
        }

        throw new InvalidOperationException(
            $"Could not find an available path for '{baseName}' after 99 versions.");
    }
}
