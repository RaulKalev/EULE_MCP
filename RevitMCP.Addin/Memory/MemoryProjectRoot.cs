using System.IO;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Memory;

/// <summary>
/// Finds the project root that holds the project memory tier: the explicit argument, else the nearest folder
/// above the (central) model file that has a <c>.rktools</c> folder — the same rule as the project config — else
/// the project folder guessed by walking up past stage, discipline and Revit folders (<c>02_PP</c>, <c>4_EN</c>,
/// <c>Revit</c>), so EP, PP and TP models of one project share a memory. The guess comes with a warning that says
/// where memory went and how to pin it. A drive or share root is never a project root. Cloud and server models have
/// no folder, so they get no project tier. Pure — no Revit API.
/// </summary>
public static class MemoryProjectRoot
{
    /// <summary>Folder names skipped when guessing the project folder; <c>memory.projectRootSkipFolders</c> replaces it.</summary>
    public const string DefaultSkipFolders = MemorySettings.DefaultProjectRootSkipFolders;

    public static (string? Root, string? Problem, string? Warning) Find(string? modelPath, string? explicitRoot, string? skipFolders = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            try
            {
                var full = Path.GetFullPath(explicitRoot!.Trim().Trim('"'));
                return Directory.Exists(full) ? (full, null, null) : (null, $"Project root not found: {full}", null);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
            {
                return (null, "projectRoot is not a valid folder path: " + ex.Message, null);
            }
        }

        var path = (modelPath ?? string.Empty).Trim();
        if (path.Length == 0)
            return (null, "The model has not been saved to a folder, so it has no project memory. Save it, pass projectRoot, or use scope=personal.", null);
        if (path.Contains("://"))
            return (null, "Cloud and server models have no project folder for memory. Pass projectRoot (a folder everyone on the project can reach) or use scope=personal.", null);

        try
        {
            var modelDir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(modelDir)) return (null, "The model's folder could not be determined. Pass projectRoot.", null);

            // A .rktools at a drive or share root would merge every project's memory: it is not a project root.
            for (var dir = modelDir; !IsRoot(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(Path.Combine(dir!, ".rktools"))) return (dir, null, null);

            var guess = GuessProjectFolder(modelDir!, skipFolders);
            return (guess, null,
                $"No .rktools folder above the model, so project memory is kept in {Path.Combine(guess, ".rktools")} " +
                "(stage, discipline and Revit folders skipped). If that is not the project's top folder, create a .rktools " +
                "folder there or pass projectRoot, so every stage and discipline of the project shares one memory.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return (null, "The model's folder could not be determined: " + ex.Message, null);
        }
    }

    /// <summary>The model folder, or the first folder above it whose name is not a stage, discipline or Revit folder.</summary>
    public static string GuessProjectFolder(string modelDir, string? skipFolders = null)
    {
        if (IsRoot(modelDir)) return modelDir;
        var skip = SkipPattern(skipFolders);
        var dir = modelDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (skip.IsMatch(Path.GetFileName(dir)))
        {
            var parent = Path.GetDirectoryName(dir);
            if (IsRoot(parent)) break;
            dir = parent!;
        }
        return dir;
    }

    private static Regex SkipPattern(string? skipFolders)
    {
        if (!string.IsNullOrWhiteSpace(skipFolders))
        {
            try { return new Regex(skipFolders!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { /* an invalid pattern in the config falls back to the default */ }
        }
        return new Regex(DefaultSkipFolders, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>A drive root (C:\) or a share root (\\server\share) — or nothing above.</summary>
    private static bool IsRoot(string? dir) => string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(Path.GetDirectoryName(dir));
}
