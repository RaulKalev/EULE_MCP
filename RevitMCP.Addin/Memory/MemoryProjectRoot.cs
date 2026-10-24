using System.IO;

namespace RevitMCP.Addin.Memory;

/// <summary>
/// Finds the project root that holds the project memory tier: the explicit argument, else the nearest folder
/// above the (central) model file that has a <c>.rktools</c> folder — the same rule as the project config — else
/// the model file's own folder. Cloud and server models have no folder, so they get no project tier.
/// Pure — no Revit API.
/// </summary>
public static class MemoryProjectRoot
{
    public static (string? Root, string? Problem) Find(string? modelPath, string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            try
            {
                var full = Path.GetFullPath(explicitRoot!.Trim().Trim('"'));
                return Directory.Exists(full) ? (full, null) : (null, $"Project root not found: {full}");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
            {
                return (null, "projectRoot is not a valid folder path: " + ex.Message);
            }
        }

        var path = (modelPath ?? string.Empty).Trim();
        if (path.Length == 0)
            return (null, "The model has not been saved to a folder, so it has no project memory. Save it, pass projectRoot, or use scope=personal.");
        if (path.Contains("://"))
            return (null, "Cloud and server models have no project folder for memory. Pass projectRoot (a folder everyone on the project can reach) or use scope=personal.");

        try
        {
            var modelDir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(modelDir)) return (null, "The model's folder could not be determined. Pass projectRoot.");
            for (var dir = modelDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(Path.Combine(dir, ".rktools"))) return (dir, null);
            return (modelDir, null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return (null, "The model's folder could not be determined: " + ex.Message);
        }
    }
}
