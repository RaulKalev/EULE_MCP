using System.IO;

namespace RevitMCP.Addin.Export.Sheets;

/// <summary>Export formats the export tools know.</summary>
public static class ExportFormats
{
    public const string Pdf = "pdf";
    public const string Dwg = "dwg";
    public const string Dxf = "dxf";
    public const string Ifc = "ifc";
    public const string Nwc = "nwc";

    public static readonly string[] SheetFormats = { Pdf, Dwg, Dxf };
    public static readonly string[] ModelFormats = { Ifc, Nwc };
    public static readonly string[] All = { Pdf, Dwg, Dxf, Ifc, Nwc };

    /// <summary>Lower-cased, de-duplicated formats in the canonical order; unknown names go to <paramref name="unknown"/>.</summary>
    public static List<string> Parse(IEnumerable<string> raw, out List<string> unknown)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        unknown = new List<string>();
        foreach (var r in raw)
        {
            var f = (r ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
            if (f.Length == 0) continue;
            if (All.Contains(f)) wanted.Add(f); else unknown.Add(r!);
        }
        return All.Where(wanted.Contains).ToList();
    }
}

/// <summary>What the planner needs besides the sheets.</summary>
public sealed class ExportPlanOptions
{
    public List<string> Formats { get; init; } = new();
    /// <summary>Apply the EliteSheets merge rule (<see cref="SheetMergeGrouping"/>). Default true.</summary>
    public bool Merge { get; init; } = true;
    public ExportNaming Naming { get; init; } = new();
    public bool Overwrite { get; init; }
    public bool IncludeUnlisted { get; init; }
    public string PdfFolder { get; init; } = string.Empty;
    /// <summary>DWG and DXF go here (EliteSheets' "DWG folder").</summary>
    public string CadFolder { get; init; } = string.Empty;
    /// <summary>IFC and NWC go here.</summary>
    public string ModelFolder { get; init; } = string.Empty;
    /// <summary>Extension of a merged drawing requested as DWG: "dwg" when the ODA converter will convert it, else "dxf".</summary>
    public string MergedDwgExtension { get; init; } = ExportFormats.Dwg;
    /// <summary>File name stem of the IFC/NWC model export.</summary>
    public string ModelFileStem { get; init; } = "Model";
    public int MaxSheets { get; init; } = ExportPlanner.DefaultMaxSheets;
}

/// <summary>One file the export will write.</summary>
public sealed class PlannedExportFile
{
    /// <summary>The requested format: pdf, dwg, dxf, ifc, nwc.</summary>
    public string Format { get; init; } = string.Empty;
    /// <summary>single (one sheet), merged (an EliteSheets group), model (IFC/NWC).</summary>
    public string Kind { get; init; } = "single";
    /// <summary>The file's extension without the dot; differs from Format for a merged DWG delivered as DXF.</summary>
    public string Extension { get; init; } = string.Empty;
    public List<long> SheetIds { get; init; } = new();
    public List<string> SheetNumbers { get; init; } = new();
    /// <summary>The merged group id ("05"), empty for singles.</summary>
    public string GroupKey { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    /// <summary>new | overwrite | timestamped (an existing file is kept and this one gets a timestamp suffix).</summary>
    public string Disposition { get; set; } = "new";
    /// <summary>Delivery round-trip warning, when the name will not parse in the delivery check.</summary>
    public string? NameWarning { get; set; }
}

public sealed class SkippedSheet
{
    public long SheetId { get; init; }
    public string SheetNumber { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class ExportPlan
{
    public List<PlannedExportFile> Files { get; } = new();
    public List<SkippedSheet> Skipped { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>Sheets that will be exported (after skipping).</summary>
    public List<ExportSheetInfo> Sheets { get; } = new();
    public List<SheetMergeGroup> Groups { get; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Turns the selected sheets and options into the exact list of files to write: EliteSheets merge groups, file names,
/// collisions inside the batch (<c>_2</c>, <c>_3</c>) and the overwrite policy for files already on disk (overwrite, or
/// a timestamp suffix like <see cref="ExportPathService"/>). The preview and the export call this with the same input,
/// so what the preview shows is what the export writes. Pure.
/// </summary>
public static class ExportPlanner
{
    public const int DefaultMaxSheets = 500;

    public static ExportPlan Plan(
        IReadOnlyList<ExportSheetInfo> sheets,
        ExportPlanOptions options,
        Func<string, bool> fileExists,
        Func<DateTime> now)
    {
        var plan = new ExportPlan();
        var sheetFormats = options.Formats.Where(f => ExportFormats.SheetFormats.Contains(f)).ToList();
        var modelFormats = options.Formats.Where(f => ExportFormats.ModelFormats.Contains(f)).ToList();

        if (sheetFormats.Count > 0)
        {
            foreach (var sheet in sheets)
            {
                if (!sheet.HasTitleBlock && !options.IncludeUnlisted)
                    plan.Skipped.Add(new SkippedSheet { SheetId = sheet.Id, SheetNumber = sheet.SheetNumber, Reason = "no title block (includeUnlisted=true exports it)" });
                else if (!sheet.AppearsInSheetList && !options.IncludeUnlisted)
                    plan.Skipped.Add(new SkippedSheet { SheetId = sheet.Id, SheetNumber = sheet.SheetNumber, Reason = "Appears In Sheet List is off (includeUnlisted=true exports it)" });
                else
                    plan.Sheets.Add(sheet);
            }

            if (plan.Sheets.Count > options.MaxSheets)
            {
                plan.Error = $"{plan.Sheets.Count} sheets selected; at most {options.MaxSheets} per call. Narrow the selection or export in batches.";
                return plan;
            }

            var partition = SheetMergeGrouping.Partition(plan.Sheets, options.Merge);
            plan.Groups.AddRange(partition.Groups);

            foreach (var format in sheetFormats)
            {
                var folder = format == ExportFormats.Pdf ? options.PdfFolder : options.CadFolder;
                foreach (var sheet in partition.Singles)
                {
                    var stem = ExportFileNamer.BuildStem(options.Naming, sheet.SheetNumber, sheet.SheetName, sheet.Revision);
                    plan.Files.Add(new PlannedExportFile
                    {
                        Format = format,
                        Kind = "single",
                        Extension = format,
                        SheetIds = { sheet.Id },
                        SheetNumbers = { sheet.SheetNumber },
                        Folder = folder,
                        FileName = stem + "." + format,
                        NameWarning = ExportFileNamer.CheckDeliveryRoundTrip(stem + "." + format, sheet.SheetNumber)
                    });
                }

                foreach (var group in partition.Groups)
                {
                    var first = group.Sheets[0];
                    var stem = ExportFileNamer.BuildStem(options.Naming, group.BaseName, first.SheetName, first.Revision);
                    var ext = format == ExportFormats.Dwg ? options.MergedDwgExtension : format;
                    var file = new PlannedExportFile
                    {
                        Format = format,
                        Kind = "merged",
                        Extension = ext,
                        SheetIds = group.Sheets.Select(s => s.Id).ToList(),
                        SheetNumbers = group.Sheets.Select(s => s.SheetNumber).ToList(),
                        GroupKey = group.GroupKey,
                        Folder = folder,
                        FileName = stem + "." + ext,
                        NameWarning = ExportFileNamer.CheckDeliveryRoundTrip(stem + "." + ext, first.SheetNumber)
                    };
                    // dwg without the converter and dxf both produce the same merged .dxf: write it once.
                    if (plan.Files.Any(f => f.Kind == "merged" && f.GroupKey == group.GroupKey &&
                                            string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(f.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    plan.Files.Add(file);
                }
            }

            if (partition.Groups.Count > 0 && sheetFormats.Contains(ExportFormats.Dwg) &&
                !string.Equals(options.MergedDwgExtension, ExportFormats.Dwg, StringComparison.OrdinalIgnoreCase))
                plan.Warnings.Add($"{partition.Groups.Count} merged drawing(s) will be delivered as DXF: the ODA File Converter is not installed " +
                                  "or convertMergedToDwg is off (EliteSheets does the same). The delivery check then reports their DWG as missing.");
        }

        foreach (var format in modelFormats)
        {
            var stem = ExportFileNamer.Sanitize(options.ModelFileStem);
            plan.Files.Add(new PlannedExportFile
            {
                Format = format,
                Kind = "model",
                Extension = format,
                Folder = options.ModelFolder,
                FileName = stem + "." + format
            });
        }

        ResolvePaths(plan.Files, options.Overwrite, fileExists, now);

        var unparsed = plan.Files.Where(f => f.NameWarning != null).ToList();
        if (unparsed.Count > 0)
            plan.Warnings.Add($"sheet_number_not_company_pattern: {unparsed.Count} file name(s) will not parse in the delivery check, e.g. {unparsed[0].FileName}.");
        return plan;
    }

    /// <summary>Batch collisions get _2, _3; an existing file is overwritten or the new one gets a timestamp suffix.</summary>
    private static void ResolvePaths(List<PlannedExportFile> files, bool overwrite, Func<string, bool> fileExists, Func<DateTime> now)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(file.FileName);
            var ext = System.IO.Path.GetExtension(file.FileName);
            var name = file.FileName;
            for (var n = 2; used.Contains(Combine(file.Folder, name)); n++)
                name = $"{stem}_{n}{ext}";

            var path = Combine(file.Folder, name);
            if (fileExists(path))
            {
                if (overwrite)
                    file.Disposition = "overwrite";
                else
                {
                    var stampedStem = $"{System.IO.Path.GetFileNameWithoutExtension(name)}_{now():yyyy-MM-dd_HHmmss}";
                    name = stampedStem + ext;
                    for (var n = 2; fileExists(Combine(file.Folder, name)) || used.Contains(Combine(file.Folder, name)); n++)
                        name = $"{stampedStem}_{n}{ext}";
                    file.Disposition = "timestamped";
                    path = Combine(file.Folder, name);
                }
            }
            file.FileName = name;
            file.Path = path;
            used.Add(path);
        }
    }

    private static string Combine(string folder, string name) =>
        string.IsNullOrEmpty(folder) ? name : System.IO.Path.Combine(folder, name);
}
