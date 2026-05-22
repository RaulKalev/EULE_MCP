using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Documentation.Presets;

/// <summary>
/// Reads PlaceViews/SheetManager preset JSON files from the well-known RK Tools preset folder.
/// All path operations are validated to prevent directory traversal.
/// </summary>
public static class PlaceViewsPresetService
{
    public const string DefaultPresetFolderPath =
        @"C:\ProgramData\RK Tools\PlaceViews\SheetManagerSettings";

    // -------------------------------------------------------------------------
    // List
    // -------------------------------------------------------------------------

    public static (bool success, string folderPath, List<PresetFileInfo> presets, string? warning)
        ListPresets(string? overrideFolderPath = null)
    {
        var folder = Coalesce(overrideFolderPath, DefaultPresetFolderPath);

        if (!Directory.Exists(folder))
            return (false, folder, [], $"Preset folder not found: {folder}");

        var files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
        var presets = files
            .Select(f => new FileInfo(f))
            .Select(fi => new PresetFileInfo
            {
                FileName     = fi.Name,
                FullPath     = fi.FullName,
                SizeBytes    = fi.Length,
                ModifiedUtc  = fi.LastWriteTimeUtc,
                DetectedType = DetectPresetType(fi.Name)
            })
            .ToList();

        return (true, folder, presets, null);
    }

    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    public static (bool success, string? content, JObject? parsed, string? error)
        ReadPreset(string presetNameOrFileName, string? overrideFolderPath = null)
    {
        var folder      = Coalesce(overrideFolderPath, DefaultPresetFolderPath);
        var resolvedPath = ResolvePresetPath(folder, presetNameOrFileName);
        if (resolvedPath == null)
            return (false, null, null, $"Preset '{presetNameOrFileName}' not found in folder: {folder}");

        try
        {
            var content = File.ReadAllText(resolvedPath);
            JObject? parsed = null;
            try { parsed = JObject.Parse(content); }
            catch { /* return raw content even if JSON is malformed */ }
            return (true, content, parsed, null);
        }
        catch (Exception ex)
        {
            return (false, null, null, $"Failed to read preset '{presetNameOrFileName}': {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Classify
    // -------------------------------------------------------------------------

    public static string ClassifyWorkflow(JObject preset)
    {
        // Heuristic classification based on top-level keys
        var keys = preset.Properties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (keys.Any(k => k.Contains("Sheet") && k.Contains("Duplic")))    return "DuplicateSheets";
        if (keys.Any(k => k.Contains("CreateSheet") || k.Contains("NewSheet"))) return "CreateSheets";
        if (keys.Any(k => k.Contains("PlaceView") || k.Contains("ViewPlacement"))) return "PlaceViews";
        if (keys.Any(k => k.Contains("Rename")))                            return "RenameWorkflow";
        if (keys.Any(k => k.Contains("Parameter") || k.Contains("Mapping"))) return "ParameterMapping";
        if (keys.Any(k => k.Contains("Template")))                          return "ViewTemplate";

        // Fallback: inspect string values for hints
        var allStringValues = preset.DescendantsAndSelf()
            .OfType<JProperty>()
            .Select(p => p.Value.ToString())
            .ToList();

        if (allStringValues.Any(v => v.IndexOf("sheet", StringComparison.OrdinalIgnoreCase) >= 0))
            return "SheetWorkflow";

        return "Unknown";
    }

    // -------------------------------------------------------------------------
    // Validate
    // -------------------------------------------------------------------------

    public static List<string> ValidatePresetStructure(JObject preset, out List<string> suggestions)
    {
        suggestions = new List<string>();
        var errors = new List<string>();

        if (!preset.HasValues)
        {
            errors.Add("Preset is empty.");
            return errors;
        }

        // Soft warnings for common expected fields
        var keys = preset.Properties().Select(p => p.Name).ToList();
        if (keys.Count == 0)
            errors.Add("Preset has no top-level properties.");

        // Type-specific expectations
        var workflowType = ClassifyWorkflow(preset);
        switch (workflowType)
        {
            case "DuplicateSheets":
                if (!keys.Any(k => k.Contains("Source", StringComparison.OrdinalIgnoreCase)))
                    suggestions.Add("DuplicateSheets preset typically has a SourceSheetNumbers or similar field.");
                break;
            case "CreateSheets":
                if (!keys.Any(k => k.Contains("TitleBlock", StringComparison.OrdinalIgnoreCase)))
                    suggestions.Add("CreateSheets preset typically references a TitleBlock.");
                break;
        }

        return errors;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves a preset file path within <paramref name="folder"/>, enforcing that the
    /// canonical result stays within the folder (prevents directory traversal).
    /// </summary>
    private static string? ResolvePresetPath(string folder, string fileNameOrPath)
    {
        // Accept only simple filenames — strip any directory component supplied by the caller
        var fileName = Path.GetFileName(fileNameOrPath);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            fileName += ".json";

        // Canonicalise and verify containment
        var folderFull = Path.GetFullPath(folder);
        var fullPath   = Path.GetFullPath(Path.Combine(folderFull, fileName));

        if (!fullPath.StartsWith(folderFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(folderFull, StringComparison.OrdinalIgnoreCase))
            return null; // path traversal attempt

        return File.Exists(fullPath) ? fullPath : null;
    }

    private static string DetectPresetType(string fileName)
    {
        if (fileName.Contains("SheetManager", StringComparison.OrdinalIgnoreCase))
            return "SheetManagerSettings";
        if (fileName.Contains("PlaceViews", StringComparison.OrdinalIgnoreCase))
            return "PlaceViewsSettings";
        return "Unknown";
    }

    private static string Coalesce(string? overrideValue, string defaultValue) =>
        string.IsNullOrWhiteSpace(overrideValue) ? defaultValue : overrideValue;
}

public class PresetFileInfo
{
    public string   FileName     { get; set; } = string.Empty;
    public string   FullPath     { get; set; } = string.Empty;
    public long     SizeBytes    { get; set; }
    public DateTime ModifiedUtc  { get; set; }
    public string   DetectedType { get; set; } = string.Empty;
}
