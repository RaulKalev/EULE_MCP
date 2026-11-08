using System.IO;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Export.Sheets;

/// <summary>Format options the runner needs besides the plan.</summary>
public sealed class SheetExportSettings
{
    /// <summary>DWG/DXF export setup name; empty uses Revit's default options.</summary>
    public string DwgSetup { get; init; } = string.Empty;
    /// <summary>Overrides the setup's "export views on sheets as external references" choice when set.</summary>
    public bool? MergedViews { get; init; }
    public string DxfTemplatePath { get; init; } = string.Empty;
    public double MergeSpacingMm { get; init; } = DxfSheetProcessing.DefaultSheetSpacingMm;
    /// <summary>ODA File Converter path, null when merged drawings stay DXF.</summary>
    public string? OdaConverterPath { get; init; }
    // IFC
    public string IfcVersion { get; init; } = "IFC2x3CV2";
    public int IfcSpaceBoundaries { get; init; }
    public bool IfcBaseQuantities { get; init; }
    // IFC filter view
    public long ViewId { get; init; }
}

/// <summary>What happened to one planned file.</summary>
public sealed class ExportFileResult
{
    public string Format { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public List<long> SheetIds { get; init; } = new();
    public List<string> SheetNumbers { get; init; } = new();
    public string FileName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long Bytes { get; set; }
    /// <summary>written | overwritten | renamed (timestamp suffix, an existing file kept) | failed | cancelled.</summary>
    public string Status { get; set; } = "failed";
    public string? Reason { get; set; }
    /// <summary>Other files Revit wrote with it (view xrefs of a DWG exported without merged views).</summary>
    public List<string> ExtraFiles { get; } = new();
}

/// <summary>
/// Writes the files of an <see cref="ExportPlan"/>, the way EliteSheets does: PDF through Revit's PDF export (one call per
/// file, a merged drawing as one combined PDF in merge order), DWG with the named setup, DXF promoted from paper to model
/// space, merged drawings as DXF sheets placed side by side in the DXF template (DWG through the ODA converter). Every file
/// is exported into a work folder first and then moved to its planned path, so a failed export never destroys an existing
/// file and Revit's own name decoration never reaches the delivery folder. Must run outside a transaction; a file that
/// fails is recorded and the next one continues. Cancellation is checked between files.
/// </summary>
public sealed class SheetExportRunner
{
    private readonly Document _doc;
    private readonly SheetExportSettings _settings;
    private readonly string _work;
    private readonly Dictionary<string, string> _mergedDxfByGroup = new(StringComparer.OrdinalIgnoreCase);

    public SheetExportRunner(Document doc, SheetExportSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevitMCP", "export", Guid.NewGuid().ToString("N"));
    }

    public List<ExportFileResult> Run(ExportPlan plan, CancellationToken cancellationToken)
    {
        var results = new List<ExportFileResult>();
        Directory.CreateDirectory(_work);
        try
        {
            foreach (var file in plan.Files)
            {
                var result = new ExportFileResult
                {
                    Format = file.Format,
                    Kind = file.Kind,
                    SheetIds = file.SheetIds,
                    SheetNumbers = file.SheetNumbers,
                    FileName = file.FileName,
                    Path = file.Path
                };
                results.Add(result);
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Status = "cancelled";
                    result.Reason = "The call was cancelled before this file.";
                    continue;
                }
                try
                {
                    var produced = Produce(file, result);
                    if (produced == null) continue; // Reason set by Produce
                    Deliver(produced, file, result);
                }
                catch (Exception ex)
                {
                    result.Status = "failed";
                    result.Reason = ex.Message;
                }
            }
        }
        finally
        {
            try { Directory.Delete(_work, true); } catch { /* temp folder, left for the OS */ }
        }
        return results;
    }

    /// <summary>Exports into the work folder and returns the produced file, or null with the reason on the result.</summary>
    private string? Produce(PlannedExportFile file, ExportFileResult result)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(file.FileName);
        var ids = file.SheetIds.Select(id => new ElementId(id)).ToList();
        var dir = NewWorkDir();

        switch (file.Format)
        {
            case ExportFormats.Pdf:
                return ExportPdf(ids, dir, stem, result);
            case ExportFormats.Dwg when file.Kind == "single":
                return ExportDwg(ids, dir, stem, result);
            case ExportFormats.Dxf when file.Kind == "single":
                return ExportDxf(ids, dir, stem, promote: true, result);
            case ExportFormats.Dwg or ExportFormats.Dxf:
                return ExportMerged(file, stem, result);
            case ExportFormats.Ifc:
                return ExportIfc(dir, stem, result);
            default:
                result.Reason = $"Unknown format '{file.Format}'.";
                return null;
        }
    }

    private string? ExportPdf(List<ElementId> ids, string dir, string stem, ExportFileResult result)
    {
        using var options = new PDFExportOptions
        {
            Combine = true,
            FileName = stem,
            // EliteSheets' settings: the sheet's own paper size, 100 %, centred, no crop/scope/reference clutter.
            ZoomType = ZoomType.Zoom,
            ZoomPercentage = 100,
            PaperPlacement = PaperPlacementType.Center,
            HideCropBoundaries = true,
            HideReferencePlane = true,
            HideScopeBoxes = true,
            HideUnreferencedViewTags = true,
            StopOnError = false,
            AlwaysUseRaster = false
        };
        if (!_doc.Export(dir, ids, options))
        {
            result.Reason = "Revit reported that the PDF export failed.";
            return null;
        }
        return Expect(dir, stem, ".pdf", result);
    }

    private string? ExportDwg(List<ElementId> ids, string dir, string stem, ExportFileResult result)
    {
        var options = DwgOptions();
        if (!_doc.Export(dir, stem, ids, options))
        {
            result.Reason = "Revit reported that the DWG export failed. Check the export setup.";
            return null;
        }
        var main = Expect(dir, stem, ".dwg", result);
        if (main != null)
            // View xrefs of a setup without merged views; Revit's .pcp/log side files stay behind.
            foreach (var extra in Directory.GetFiles(dir, "*.dwg").Where(f => !string.Equals(f, main, StringComparison.OrdinalIgnoreCase)))
                result.ExtraFiles.Add(extra);
        return main;
    }

    private string? ExportDxf(List<ElementId> ids, string dir, string stem, bool promote, ExportFileResult result)
    {
        var options = DxfOptions();
        if (!_doc.Export(dir, stem, ids, options))
        {
            result.Reason = "Revit reported that the DXF export failed. Check the export setup.";
            return null;
        }
        var path = Expect(dir, stem, ".dxf", result);
        if (path != null && promote)
            DxfSheetProcessing.PromotePaperToModel(path);
        return path;
    }

    /// <summary>EliteSheets' merged drawing: each sheet to DXF, promoted, placed side by side in the template; DWG via ODA.</summary>
    private string? ExportMerged(PlannedExportFile file, string stem, ExportFileResult result)
    {
        if (!_mergedDxfByGroup.TryGetValue(file.GroupKey, out var mergedDxf))
        {
            if (string.IsNullOrWhiteSpace(_settings.DxfTemplatePath) || !File.Exists(_settings.DxfTemplatePath))
            {
                result.Reason = string.IsNullOrWhiteSpace(_settings.DxfTemplatePath)
                    ? "No DXF template for merged drawings: set TemplateDxfPath in the EliteSheets settings or pass dxfTemplatePath."
                    : $"DXF template not found: {_settings.DxfTemplatePath}";
                return null;
            }

            var parts = new List<string>();
            for (var i = 0; i < file.SheetIds.Count; i++)
            {
                var partResult = new ExportFileResult();
                var part = ExportDxf(new List<ElementId> { new(file.SheetIds[i]) }, NewWorkDir(), $"part_{i + 1:D2}", promote: true, partResult);
                if (part == null)
                {
                    result.Reason = $"Sheet {file.SheetNumbers[i]}: {partResult.Reason}";
                    return null;
                }
                parts.Add(part);
            }

            mergedDxf = System.IO.Path.Combine(NewWorkDir(), stem + ".dxf");
            DxfSheetProcessing.MergeIntoTemplate(parts, _settings.DxfTemplatePath, mergedDxf, _settings.MergeSpacingMm);
            _mergedDxfByGroup[file.GroupKey] = mergedDxf;
        }

        if (!string.Equals(file.Extension, ExportFormats.Dwg, StringComparison.OrdinalIgnoreCase))
            return mergedDxf;

        if (_settings.OdaConverterPath == null)
        {
            result.Reason = "The ODA File Converter is not installed; the merged drawing exists only as DXF.";
            return null;
        }
        var inDir = NewWorkDir();
        var source = System.IO.Path.Combine(inDir, stem + ".dxf");
        File.Copy(mergedDxf, source, true);
        var outDir = NewWorkDir();
        var error = OdaFileConverter.ConvertFolder(_settings.OdaConverterPath, inDir, outDir);
        if (error != null)
        {
            result.Reason = "DWG conversion failed: " + error;
            return null;
        }
        return Expect(outDir, stem, ".dwg", result);
    }

    private string? ExportIfc(string dir, string stem, ExportFileResult result)
    {
        var options = new IFCExportOptions
        {
            FileVersion = ParseIfcVersion(_settings.IfcVersion),
            SpaceBoundaryLevel = _settings.IfcSpaceBoundaries,
            ExportBaseQuantities = _settings.IfcBaseQuantities
        };
        if (_settings.ViewId > 0) options.FilterViewId = new ElementId(_settings.ViewId);

        bool ok;
        // The IFC exporter needs an open transaction; nothing it does there is kept.
        using (var t = new Transaction(_doc, "Revit MCP - IFC export"))
        {
            t.Start();
            ok = _doc.Export(dir, stem, options);
            t.RollBack();
        }
        if (!ok)
        {
            result.Reason = "Revit reported that the IFC export failed.";
            return null;
        }
        return Expect(dir, stem, ".ifc", result);
    }

    public static IFCVersion ParseIfcVersion(string? raw) => (raw ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "IFC4RV" or "IFC4REFERENCEVIEW" => IFCVersion.IFC4RV,
        "IFC4DTV" or "IFC4DESIGNTRANSFERVIEW" => IFCVersion.IFC4DTV,
        "IFC2X3" or "IFC2X3CV2" or "" => IFCVersion.IFC2x3CV2,
        _ => throw new ArgumentException($"Unknown ifcVersion '{raw}'. Use IFC2x3CV2, IFC4RV or IFC4DTV.")
    };

    private DWGExportOptions DwgOptions()
    {
        DWGExportOptions? options = null;
        if (!string.IsNullOrWhiteSpace(_settings.DwgSetup))
            options = DWGExportOptions.GetPredefinedOptions(_doc, _settings.DwgSetup);
        options ??= new DWGExportOptions();
        if (_settings.MergedViews.HasValue) options.MergedViews = _settings.MergedViews.Value;
        return options;
    }

    private DXFExportOptions DxfOptions()
    {
        DXFExportOptions? options = null;
        if (!string.IsNullOrWhiteSpace(_settings.DwgSetup))
        {
            try { options = DXFExportOptions.GetPredefinedOptions(_doc, _settings.DwgSetup); }
            catch { options = null; } // EliteSheets falls back to defaults as well
        }
        return options ?? new DXFExportOptions();
    }

    /// <summary>The file Revit wrote: "{stem}{ext}", else the only new file with that extension (Revit decorates names).</summary>
    private static string? Expect(string dir, string stem, string ext, ExportFileResult result)
    {
        var exact = System.IO.Path.Combine(dir, stem + ext);
        if (File.Exists(exact)) return exact;
        var candidates = Directory.GetFiles(dir, "*" + ext);
        var decorated = candidates.Where(f => System.IO.Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase)).ToList();
        if (decorated.Count == 1) return decorated[0];
        if (candidates.Length == 1) return candidates[0];
        result.Reason = candidates.Length == 0
            ? $"Revit reported success but wrote no {ext} file."
            : $"Revit wrote {candidates.Length} {ext} files; cannot tell which is the sheet.";
        return null;
    }

    /// <summary>Moves the produced file (and any xrefs) to the planned path.</summary>
    private static void Deliver(string produced, PlannedExportFile file, ExportFileResult result)
    {
        Directory.CreateDirectory(file.Folder);
        var existed = File.Exists(file.Path);
        File.Copy(produced, file.Path, true);
        result.Bytes = new FileInfo(file.Path).Length;
        result.Status = file.Disposition == "timestamped" ? "renamed" : existed ? "overwritten" : "written";
        if (file.Disposition == "timestamped")
            result.Reason = "A file with the planned name exists; it was kept and this file got a timestamp suffix (overwrite=true replaces it).";

        var moved = new List<string>();
        foreach (var extra in result.ExtraFiles)
        {
            var target = System.IO.Path.Combine(file.Folder, System.IO.Path.GetFileName(extra));
            File.Copy(extra, target, true);
            moved.Add(target);
        }
        result.ExtraFiles.Clear();
        result.ExtraFiles.AddRange(moved);
    }

    private string NewWorkDir()
    {
        var dir = System.IO.Path.Combine(_work, Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
