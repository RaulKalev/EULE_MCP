using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin.Families;

// ── Request / Result DTOs ────────────────────────────────────────────────────

public class DwgFamilyGenerateRequest
{
    public string DwgPath         { get; set; } = string.Empty;
    public string UserDefinedName { get; set; } = string.Empty;
    public string PresetName      { get; set; } = "DefaultPanelSchematicSymbol";
    /// <summary>Override output folder; if empty the preset's folder is used.</summary>
    public string OutputFolder    { get; set; } = string.Empty;
}

public class DwgFamilyGenerateResult
{
    public bool          Success    { get; set; }
    public string?       FamilyName { get; set; }
    public string?       FileName   { get; set; }
    public string?       RfaPath    { get; set; }
    public string        PresetUsed { get; set; } = string.Empty;
    public List<string>  Warnings   { get; set; } = new();
    public List<string>  Errors     { get; set; } = new();
    public long          DurationMs { get; set; }
}

// ── Generator ────────────────────────────────────────────────────────────────

/// <summary>
/// Creates a Detail Item .rfa by copying a preset family and importing a DWG into it.
/// Must be called from the Revit API thread (inside an ExternalEvent execution).
/// </summary>
public static class DwgDetailFamilyGenerator
{
    public static DwgFamilyGenerateResult Generate(
        UIApplication uiapp,
        DwgFamilyGenerateRequest request)
    {
        var sw       = Stopwatch.StartNew();
        var warnings = new List<string>();

        // 1. Load preset
        var (preset, presetError) = DwgDetailItemPresetConfig.GetPreset(request.PresetName);
        if (preset == null)
            return Fail(request.PresetName, presetError!, sw);

        // 2. Validate preset family
        if (!File.Exists(preset.PresetFamilyPath))
            return Fail(request.PresetName,
                $"Preset family file not found: {preset.PresetFamilyPath}", sw);

        // 3. Validate DWG input
        if (string.IsNullOrWhiteSpace(request.DwgPath))
            return Fail(request.PresetName, "dwgPath is required.", sw);
        if (!File.Exists(request.DwgPath))
            return Fail(request.PresetName,
                $"DWG file was not found: {request.DwgPath}", sw);
        if (!request.DwgPath.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            return Fail(request.PresetName, "dwgPath must point to a .dwg file.", sw);

        // 4. Build family name
        string familyName;
        try
        {
            familyName = FamilyNameService.BuildFamilyName(
                preset.FamilyPrefix, request.UserDefinedName);
        }
        catch (ArgumentException ex)
        {
            return Fail(request.PresetName, ex.Message, sw);
        }

        // 5. Resolve output path (with versioning)
        var outputFolder = string.IsNullOrWhiteSpace(request.OutputFolder)
            ? preset.OutputFolder
            : request.OutputFolder;

        string finalPath;
        string? versionWarning;
        try
        {
            (finalPath, versionWarning) =
                FileVersioningService.GetAvailableFamilyPath(outputFolder, familyName);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(request.PresetName, ex.Message, sw);
        }

        if (versionWarning != null) warnings.Add(versionWarning);
        var finalFamilyName = Path.GetFileNameWithoutExtension(finalPath);

        // 6. Copy preset to output path
        try { File.Copy(preset.PresetFamilyPath, finalPath); }
        catch (Exception ex)
        {
            return Fail(request.PresetName,
                $"Failed to copy preset family to output path: {ex.Message}", sw);
        }

        // 7. Open, import, save, close — all on Revit API thread
        Document? familyDoc = null;
        try
        {
            familyDoc = uiapp.Application.OpenDocumentFile(finalPath);
            if (familyDoc == null)
                return FailAndCleanup(request.PresetName,
                    "Revit returned null when opening the family document.", finalPath, null, sw);

            // 8. Find target view
            var targetView = FindTargetFamilyView(familyDoc, preset.DefaultViewName);
            if (targetView == null)
                return FailAndCleanup(request.PresetName,
                    "No suitable family view found for DWG import. " +
                    "Ensure the preset family contains at least one non-template plan or drafting view.",
                    finalPath, familyDoc, sw);

            // 9. Import DWG
            ElementId importedId;
            using (var tx = new Transaction(familyDoc, "Import DWG into detail family"))
            {
                tx.Start();

                var importOptions = new DWGImportOptions
                {
                    Unit         = ImportUnit.Millimeter,
                    ThisViewOnly = true,
                    OrientToView = true,
                    Placement    = ImportPlacement.Origin,
                    ColorMode    = ImportColorMode.BlackAndWhite
                };

                bool imported = familyDoc.Import(
                    request.DwgPath, importOptions, targetView, out importedId);

                if (!imported)
                {
                    tx.RollBack();
                    return FailAndCleanup(request.PresetName,
                        "Revit DWG import operation returned false — the DWG may be empty or corrupted.",
                        finalPath, familyDoc, sw);
                }

                tx.Commit();
            }

            // 10. Center import at reference plane origin
            using (var txCenter = new Transaction(familyDoc, "Center DWG at origin"))
            {
                txCenter.Start();
                try
                {
                    var importInst = familyDoc.GetElement(importedId) as ImportInstance;
                    if (importInst != null)
                    {
                        var bb = importInst.get_BoundingBox(null)
                              ?? importInst.get_BoundingBox(targetView);
                        if (bb != null)
                        {
                            var cx = (bb.Min.X + bb.Max.X) / 2.0;
                            var cy = (bb.Min.Y + bb.Max.Y) / 2.0;
                            var translation = new XYZ(-cx, -cy, 0.0);
                            if (translation.GetLength() > 1e-9)
                                ElementTransformUtils.MoveElement(
                                    familyDoc, importedId, translation);
                        }
                        else
                        {
                            warnings.Add("Bounding box unavailable — DWG not centered.");
                        }
                    }
                    txCenter.Commit();
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not center DWG: {ex.Message}");
                    txCenter.RollBack();
                }
            }

            // 11. Full explode is not available in the Revit 2026 public API —
            // ImportInstance.Explode() was removed. The DWG remains as an import instance.
            // The user can explode manually in the Family Editor if needed.
            warnings.Add(
                "Full Explode is not available via the Revit 2026 API. " +
                "The DWG has been imported and centered but remains as an import instance. " +
                "To convert it to native detail elements, open the .rfa in the Family Editor " +
                "and use Modify → Import Instance → Explode → Full Explode.");

            // 12. Save and close
            familyDoc.Save();
            familyDoc.Close(false);

            sw.Stop();
            return new DwgFamilyGenerateResult
            {
                Success    = true,
                FamilyName = finalFamilyName,
                FileName   = Path.GetFileName(finalPath),
                RfaPath    = finalPath,
                PresetUsed = request.PresetName,
                Warnings   = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            try { familyDoc?.Close(false); } catch { }
            try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
            return new DwgFamilyGenerateResult
            {
                Success    = false,
                PresetUsed = request.PresetName,
                Errors     = [$"Unexpected error: {ex.Message}"],
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static View? FindTargetFamilyView(Document familyDoc, string? defaultViewName)
    {
        var views = new FilteredElementCollector(familyDoc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate)
            .ToList();

        // 1. Configured view name (exact, case-insensitive)
        if (!string.IsNullOrWhiteSpace(defaultViewName))
        {
            var named = views.FirstOrDefault(v =>
                v.Name.Equals(defaultViewName, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
        }

        // 2. FloorPlan (typical family reference level view)
        // 3. DraftingView
        // 4. Any non-template view
        return views.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
            ?? views.FirstOrDefault(v => v.ViewType == ViewType.DraftingView)
            ?? views.FirstOrDefault();
    }

    private static DwgFamilyGenerateResult Fail(
        string presetName, string error, Stopwatch sw) =>
        new()
        {
            Success    = false,
            PresetUsed = presetName,
            Errors     = [error],
            DurationMs = sw.ElapsedMilliseconds
        };

    private static DwgFamilyGenerateResult FailAndCleanup(
        string presetName, string error, string filePath, Document? doc, Stopwatch sw)
    {
        try { doc?.Close(false); } catch { }
        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
        return Fail(presetName, error, sw);
    }
}
