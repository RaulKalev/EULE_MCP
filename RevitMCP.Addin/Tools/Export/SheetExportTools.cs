using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Export.Sheets;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Tools.Export;

/// <summary>Registers the export tools (#105). Called once from App.OnStartup.</summary>
public static class SheetExportToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListExportSetupsTool());
        handler.RegisterTool(new PreviewExportTool());
        handler.RegisterTool(new ExportTool());
        handler.RegisterTool(new ExportAndCheckTool());
    }
}

/// <summary>revit_list_export_setups (#105): what the export tools can use in this document and on this machine.</summary>
public sealed class ListExportSetupsTool : IRevitMcpTool
{
    public string Name => "revit_list_export_setups";

    public string Description =>
        "Lists what revit_export can use: the document's DWG/DXF export setups (and the active one), its saved sheet sets, " +
        "which exporters this Revit has (pdf, dwg, dxf, ifc, nwc), the EliteSheets plugin settings the export follows " +
        "(DXF template for merged drawings, convert merged drawings to DWG, this project's PDF and DWG folders), whether " +
        "the ODA File Converter is installed, and how many sheets are printable and how many form merged drawings. Read-only.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Delivery;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var setups = new FilteredElementCollector(doc).OfClass(typeof(ExportDWGSettings)).Cast<ExportDWGSettings>()
            .Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var sheetSets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
            .Select(s => new { name = s.Name, sheetCount = s.Views.Cast<View>().OfType<ViewSheet>().Count() })
            .OrderBy(s => s.name, StringComparer.OrdinalIgnoreCase).ToList();
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
        var infos = SheetExportSelection.Describe(doc, sheets);
        var partition = SheetMergeGrouping.Partition(infos);
        var elite = EliteSheetsSettings.Load(ProjectFileName(doc));
        var oda = OdaFileConverter.Find();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{setups.Count} DWG/DXF setup(s), {sheetSets.Count} sheet set(s), {infos.Count} sheet(s) " +
                      $"({partition.Groups.Count} merged drawing(s) from {partition.Groups.Sum(g => g.Sheets.Count)} sheets).",
            Data = new
            {
                documentTitle = doc.Title,
                dwgSetups = setups,
                activeDwgSetup = SheetExportArgs.ActiveDwgSetup(doc),
                sheetSets,
                exporters = ExporterAvailability.All(),
                odaConverter = oda,
                eliteSheets = new
                {
                    configPath = elite.ConfigPath,
                    found = elite.Found,
                    error = elite.Error,
                    templateDxfPath = elite.TemplateDxfPath,
                    templateExists = elite.TemplateDxfPath.Length > 0 && File.Exists(elite.TemplateDxfPath),
                    convertMergedToDwg = elite.ConvertMergedToDwg,
                    pdfFolder = elite.PdfFolder,
                    cadFolder = elite.CadFolder
                },
                sheets = new
                {
                    printable = infos.Count(i => i.HasTitleBlock && i.AppearsInSheetList),
                    withoutTitleBlock = infos.Count(i => !i.HasTitleBlock),
                    notInSheetList = infos.Count(i => !i.AppearsInSheetList),
                    mergedDrawings = partition.Groups.Select(g => new { group = g.GroupKey, fileName = g.BaseName, sheets = g.Sheets.Select(s => s.SheetNumber) })
                },
                mergeRule = "Sheets numbered …-7-{group}_{title}--{order} (EliteSheets) become one file per group: one combined PDF, " +
                            "one DXF in the template (DWG with the ODA converter), named {prefix}-7-{group}_{title}."
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    internal static string ProjectFileName(Document doc) =>
        string.IsNullOrEmpty(doc.PathName) ? string.Empty : Path.GetFileName(doc.PathName);
}

/// <summary>revit_preview_export (#105): the exact file list revit_export would write.</summary>
public sealed class PreviewExportTool : IRevitMcpTool
{
    public string Name => "revit_preview_export";

    public string Description =>
        "Previews revit_export without writing anything: resolves the sheets, applies the EliteSheets merge rule (sheets " +
        "numbered …-7-{group}_{title}--{order} become one combined PDF and one merged DXF/DWG), computes every file name and " +
        "path, reports existing files (overwritten with overwrite=true, else the new file gets a timestamp suffix), batch " +
        "collisions, skipped sheets (no title block, not in the sheet list), names the delivery check would not parse, the " +
        "exporters and the options. Same arguments as revit_export.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Delivery;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SheetExportExecutor.Run(uiapp, request, write: false, check: false, cancellationToken));
}

/// <summary>revit_export (#105): exports sheets to PDF/DWG/DXF and the model to IFC/NWC.</summary>
public sealed class ExportTool : IRevitMcpTool
{
    public string Name => "revit_export";

    public string Description =>
        "Exports sheets to PDF, DWG and DXF the way the EliteSheets plugin does, and the model to IFC or NWC. Requires " +
        "approval. formats [pdf, dwg, dxf, ifc, nwc]. Sheets: sheets (ids, sheet numbers or EL-5-01 cores), sheetSet, " +
        "allSheets, narrowed by discipline / numberContains / nameContains; at most 500. Merge rule (merge=true, default): " +
        "sheets …-7-{group}_{title}--{order} go into one file per group — one combined PDF in merge order, and one DXF with " +
        "the sheets side by side in the EliteSheets DXF template (converted to DWG with the ODA File Converter when installed " +
        "and convertMergedToDwg, else delivered as DXF). Singles: PDF at the sheet's paper size, 100 %, crop/scope/reference " +
        "hidden; DWG with the named setup (dwgSetup, default the active one); DXF promoted from paper to model space. Names: " +
        "naming=sheetNumber (default, as EliteSheets) | sheetNumberAndName | pattern:{SheetNumber}_{SheetName}_{Revision}; " +
        "includeRevision appends _r02. Folders: folder, or pdfFolder + cadFolder; default the project's EliteSheets folders; " +
        "the file access policy decides. Existing files: overwrite=true replaces them, else the new file gets a timestamp " +
        "suffix. Long-running; cancel stops between files and reports what was written. Returns per file {fileName, path, " +
        "bytes, status: written | overwritten | renamed | failed | cancelled, reason}. Run revit_preview_export first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Delivery;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SheetExportExecutor.Run(uiapp, request, write: true, check: false, cancellationToken));
}

/// <summary>revit_export_and_check (#105): revit_export, then the delivery check on the folders it wrote to.</summary>
public sealed class ExportAndCheckTool : IRevitMcpTool
{
    public string Name => "revit_export_and_check";

    public string Description =>
        "revit_export followed by the delivery check on the folders it wrote to: the exported sheets' files must all be " +
        "there (missing), once (duplicates, e.g. an older timestamped copy), and every company-named file must belong to a " +
        "sheet of the document (orphans). registerPath (an .xlsx document register, registerWorksheet optional) also checks " +
        "the folder against the register. Requires approval. Same arguments as revit_export. Returns the export result and " +
        "deliveryCheck {issues, summary} per folder.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Delivery;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SheetExportExecutor.Run(uiapp, request, write: true, check: true, cancellationToken));
}

/// <summary>The arguments of the export tools, parsed and validated.</summary>
internal sealed class SheetExportArgs
{
    public List<string> Formats { get; private init; } = new();
    public SheetSelectionSpec Selection { get; private init; } = new();
    public ExportNaming Naming { get; private init; } = new();
    public bool Merge { get; private init; } = true;
    public bool Overwrite { get; private init; }
    public bool IncludeUnlisted { get; private init; }
    public string Folder { get; private init; } = string.Empty;
    public string PdfFolder { get; private init; } = string.Empty;
    public string CadFolder { get; private init; } = string.Empty;
    public string DwgSetup { get; private init; } = string.Empty;
    public bool? MergedViews { get; private init; }
    public string DxfTemplatePath { get; private init; } = string.Empty;
    public bool? ConvertMergedToDwg { get; private init; }
    public double MergeSpacingMm { get; private init; }
    public string FileName { get; private init; } = string.Empty;
    public long ViewId { get; private init; }
    public string IfcVersion { get; private init; } = string.Empty;
    public int IfcSpaceBoundaries { get; private init; }
    public bool IfcBaseQuantities { get; private init; }
    public bool NwcExportLinks { get; private init; }
    public bool NwcSharedCoordinates { get; private init; } = true;
    public string RegisterPath { get; private init; } = string.Empty;
    public string RegisterWorksheet { get; private init; } = string.Empty;

    public static SheetExportArgs? Parse(Dictionary<string, object?> args, out string? error)
    {
        error = null;
        var formats = ExportFormats.Parse(ToolArguments.GetStringArray(args, "formats"), out var unknown);
        if (unknown.Count > 0)
        {
            error = $"Unknown format(s): {string.Join(", ", unknown)}. Use pdf, dwg, dxf, ifc, nwc.";
            return null;
        }
        if (formats.Count == 0)
        {
            error = "formats is required: one or more of pdf, dwg, dxf, ifc, nwc.";
            return null;
        }
        var naming = ExportNaming.Parse(ToolArguments.GetString(args, "naming"), ToolArguments.GetBool(args, "includeRevision"), out error);
        if (naming == null) return null;

        var nwcCoordinates = ToolArguments.GetString(args, "nwcCoordinates").Trim();
        if (nwcCoordinates.Length > 0 && !nwcCoordinates.Equals("shared", StringComparison.OrdinalIgnoreCase) &&
            !nwcCoordinates.Equals("internal", StringComparison.OrdinalIgnoreCase))
        {
            error = "nwcCoordinates must be shared (default) or internal.";
            return null;
        }
        var spacing = ToolArguments.GetDouble(args, "mergeSpacingMm", DxfSheetProcessing.DefaultSheetSpacingMm);
        if (spacing <= 0) spacing = DxfSheetProcessing.DefaultSheetSpacingMm;

        return new SheetExportArgs
        {
            Formats = formats,
            Selection = new SheetSelectionSpec
            {
                Sheets = ToolArguments.GetStringArray(args, "sheets").ToList(),
                SheetSet = ToolArguments.GetString(args, "sheetSet").Trim(),
                AllSheets = ToolArguments.GetBool(args, "allSheets"),
                Discipline = ToolArguments.GetString(args, "discipline").Trim(),
                NumberContains = ToolArguments.GetString(args, "numberContains").Trim(),
                NameContains = ToolArguments.GetString(args, "nameContains").Trim()
            },
            Naming = naming,
            Merge = ToolArguments.GetBool(args, "merge", true),
            Overwrite = ToolArguments.GetBool(args, "overwrite"),
            IncludeUnlisted = ToolArguments.GetBool(args, "includeUnlisted"),
            Folder = ToolArguments.GetString(args, "folder").Trim().Trim('"'),
            PdfFolder = ToolArguments.GetString(args, "pdfFolder").Trim().Trim('"'),
            CadFolder = ToolArguments.GetString(args, "cadFolder").Trim().Trim('"'),
            DwgSetup = ToolArguments.GetString(args, "dwgSetup").Trim(),
            MergedViews = OptionalBool(args, "mergedViews"),
            DxfTemplatePath = ToolArguments.GetString(args, "dxfTemplatePath").Trim().Trim('"'),
            ConvertMergedToDwg = OptionalBool(args, "convertMergedToDwg"),
            MergeSpacingMm = spacing,
            FileName = ToolArguments.GetString(args, "fileName").Trim(),
            ViewId = ToolArguments.GetLong(args, "viewId"),
            IfcVersion = ToolArguments.GetString(args, "ifcVersion").Trim(),
            IfcSpaceBoundaries = Math.Max(0, Math.Min(2, ToolArguments.GetInt(args, "ifcSpaceBoundaries"))),
            IfcBaseQuantities = ToolArguments.GetBool(args, "ifcBaseQuantities"),
            NwcExportLinks = ToolArguments.GetBool(args, "nwcExportLinks"),
            NwcSharedCoordinates = !nwcCoordinates.Equals("internal", StringComparison.OrdinalIgnoreCase),
            RegisterPath = ToolArguments.GetString(args, "registerPath").Trim().Trim('"'),
            RegisterWorksheet = ToolArguments.GetString(args, "registerWorksheet").Trim()
        };
    }

    /// <summary>null when the argument is absent or empty, so the setup or the EliteSheets setting decides.</summary>
    private static bool? OptionalBool(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return null;
        var text = raw.ToString()?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;
        return bool.TryParse(text, out var b) ? b : null;
    }

    public static string ActiveDwgSetup(Document doc)
    {
        try
        {
            var active = ExportDWGSettings.GetActivePredefinedSettings(doc);
            if (active != null) return active.Name;
        }
        catch { /* no active setup */ }
        return new FilteredElementCollector(doc).OfClass(typeof(ExportDWGSettings)).Cast<ExportDWGSettings>()
            .Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty;
    }
}

internal static class SheetExportExecutor
{
    public static McpToolResult Run(UIApplication uiapp, McpToolRequest request, bool write, bool check, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var a = SheetExportArgs.Parse(request.Arguments, out var argError);
        if (a == null) return Fail(request, argError!);

        var warnings = new List<string>();
        var notes = new List<string>();

        // Exporters
        var missing = a.Formats.Where(f => !ExporterAvailability.IsAvailable(f)).ToList();
        if (missing.Count > 0)
            return Fail(request, $"Exporter not available: {string.Join(", ", missing)}. {ExporterAvailability.Hint(missing[0])}",
                "exporter_missing", new { formats = missing, hint = ExporterAvailability.Hint(missing[0]) });

        var sheetFormats = a.Formats.Where(f => ExportFormats.SheetFormats.Contains(f)).ToList();
        var modelFormats = a.Formats.Where(f => ExportFormats.ModelFormats.Contains(f)).ToList();

        // Sheets
        var sheets = new List<ViewSheet>();
        if (sheetFormats.Count > 0)
        {
            if (a.Selection.IsEmpty)
                return Fail(request, "Say which sheets: sheets, sheetSet, allSheets=true, or discipline / numberContains / nameContains.", "nothing_to_export");
            sheets = SheetExportSelection.Resolve(doc, a.Selection, notes, out var selError);
            if (selError != null) return Fail(request, selError, "nothing_to_export");
            if (sheets.Count == 0)
                return Fail(request, "No sheet matches the selection." + (notes.Count > 0 ? " " + string.Join(" ", notes) : string.Empty), "nothing_to_export");
        }

        // View for IFC (filter) / NWC (scope)
        if (modelFormats.Count > 0 && a.ViewId > 0 && doc.GetElement(new ElementId(a.ViewId)) is not View)
            return Fail(request, $"viewId {a.ViewId} is not a view in '{doc.Title}'.");

        // Settings: the arguments, then the EliteSheets plugin's settings for this project.
        var elite = EliteSheetsSettings.Load(ListExportSetupsTool.ProjectFileName(doc));
        if (elite.Error != null) warnings.Add($"EliteSheets settings not read ({elite.ConfigPath}): {elite.Error}");
        var pdfFolder = FirstNonEmpty(a.PdfFolder, a.Folder, elite.PdfFolder);
        var cadFolder = FirstNonEmpty(a.CadFolder, a.Folder, elite.CadFolder);
        var modelFolder = FirstNonEmpty(a.CadFolder, a.Folder, elite.CadFolder, a.PdfFolder);

        var folders = new List<(string Role, string Path)>();
        if (sheetFormats.Contains(ExportFormats.Pdf)) folders.Add(("pdfFolder", pdfFolder));
        if (sheetFormats.Contains(ExportFormats.Dwg) || sheetFormats.Contains(ExportFormats.Dxf)) folders.Add(("cadFolder", cadFolder));
        if (modelFormats.Count > 0) folders.Add(("folder", modelFolder));
        foreach (var (role, path) in folders)
        {
            if (path.Length == 0)
                return Fail(request, $"No {role}: pass folder (or pdfFolder / cadFolder). The project has no EliteSheets folder saved either.", "export_folder_denied");
            var denied = new FilePathPolicy().ValidateWrite(path);
            if (denied != null)
                return Fail(request, $"{role} '{path}': {denied}" +
                                     (denied.Contains("Network") ? " Set \"AllowNetworkPaths\": true in FileAccessPolicy.json to export to a share." : string.Empty),
                    "export_folder_denied", new { folder = path, reason = denied });
        }

        var oda = OdaFileConverter.Find();
        var convert = a.ConvertMergedToDwg ?? (elite.Found ? elite.ConvertMergedToDwg : true);
        var mergedDwgExt = convert && oda != null ? ExportFormats.Dwg : ExportFormats.Dxf;
        if (convert && oda == null && a.ConvertMergedToDwg == true)
            warnings.Add("convertMergedToDwg=true but the ODA File Converter is not installed (Program Files\\ODA\\ODAFileConverter*).");
        var template = FirstNonEmpty(a.DxfTemplatePath, elite.TemplateDxfPath);
        var dwgSetup = a.DwgSetup.Length > 0 ? a.DwgSetup : SheetExportArgs.ActiveDwgSetup(doc);
        if (a.DwgSetup.Length > 0 && !new FilteredElementCollector(doc).OfClass(typeof(ExportDWGSettings)).Cast<ExportDWGSettings>()
                .Any(s => string.Equals(s.Name, a.DwgSetup, StringComparison.OrdinalIgnoreCase)))
            return Fail(request, $"No DWG/DXF export setup named '{a.DwgSetup}'. revit_list_export_setups lists them.");

        // Plan
        var infos = SheetExportSelection.Describe(doc, sheets);
        var plan = ExportPlanner.Plan(infos, new ExportPlanOptions
        {
            Formats = a.Formats,
            Merge = a.Merge,
            Naming = a.Naming,
            Overwrite = a.Overwrite,
            IncludeUnlisted = a.IncludeUnlisted,
            PdfFolder = pdfFolder,
            CadFolder = cadFolder,
            ModelFolder = modelFolder,
            MergedDwgExtension = mergedDwgExt,
            ModelFileStem = a.FileName.Length > 0 ? a.FileName : DefaultModelStem(doc)
        }, File.Exists, () => DateTime.Now);
        if (plan.Error != null) return Fail(request, plan.Error);
        warnings.AddRange(plan.Warnings);
        warnings.AddRange(notes);
        if (plan.Files.Count == 0)
            return Fail(request, "Nothing to export: every selected sheet was skipped.", "nothing_to_export",
                new { skipped = plan.Skipped });

        var needsTemplate = plan.Files.Any(f => f.Kind == "merged" && f.Format != ExportFormats.Pdf);
        if (needsTemplate && (template.Length == 0 || !File.Exists(template)))
            warnings.Add(template.Length == 0
                ? "Merged DWG/DXF drawings need the DXF template: set TemplateDxfPath in EliteSheets or pass dxfTemplatePath; those files will fail."
                : $"DXF template not found: {template}; merged DWG/DXF files will fail.");

        var options = new
        {
            pdf = sheetFormats.Contains(ExportFormats.Pdf) ? "sheet paper size, zoom 100 %, centred, crop boundaries / scope boxes / reference planes / unreferenced view tags hidden, vector" : null,
            dwgSetup = sheetFormats.Any(f => f != ExportFormats.Pdf) ? (dwgSetup.Length > 0 ? dwgSetup : "Revit defaults (no setup in the document)") : null,
            mergedViews = a.MergedViews,
            merge = a.Merge,
            dxfTemplatePath = needsTemplate ? template : null,
            mergedDrawingsAs = plan.Groups.Count > 0 && sheetFormats.Contains(ExportFormats.Dwg) ? mergedDwgExt : null,
            odaConverter = oda,
            naming = a.Naming.Describe(),
            overwrite = a.Overwrite,
            ifc = modelFormats.Contains(ExportFormats.Ifc) ? new { version = a.IfcVersion.Length > 0 ? a.IfcVersion : "IFC2x3CV2", spaceBoundaries = a.IfcSpaceBoundaries, baseQuantities = a.IfcBaseQuantities, filterViewId = a.ViewId > 0 ? a.ViewId : (long?)null } : null,
            nwc = modelFormats.Contains(ExportFormats.Nwc) ? new { scope = a.ViewId > 0 ? "view" : "model", viewId = a.ViewId > 0 ? a.ViewId : (long?)null, exportLinks = a.NwcExportLinks, coordinates = a.NwcSharedCoordinates ? "shared" : "internal" } : null,
            eliteSheetsConfig = elite.Found ? elite.ConfigPath : null
        };
        if (modelFormats.Contains(ExportFormats.Ifc))
        {
            try { SheetExportRunner.ParseIfcVersion(a.IfcVersion); }
            catch (ArgumentException ex) { return Fail(request, ex.Message); }
        }

        var groups = plan.Groups.Select(g => new { group = g.GroupKey, fileStem = g.BaseName, sheets = g.Sheets.Select(s => s.SheetNumber) }).ToList();
        var sizes = infos.Where(i => plan.Sheets.Any(s => s.Id == i.Id))
            .GroupBy(i => SheetExportSelection.PaperLabel(i.WidthMm, i.HeightMm))
            .ToDictionary(g => g.Key, g => g.Count());

        if (!write)
        {
            sw.Stop();
            var existing = plan.Files.Count(f => f.Disposition != "new");
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preview: {plan.Files.Count} file(s) from {plan.Sheets.Count} sheet(s)" +
                          (plan.Groups.Count > 0 ? $" ({plan.Groups.Count} merged drawing(s))" : string.Empty) +
                          (plan.Skipped.Count > 0 ? $", {plan.Skipped.Count} sheet(s) skipped" : string.Empty) +
                          (existing > 0 ? $"; {existing} name(s) already exist ({(a.Overwrite ? "will be overwritten" : "new files get a timestamp suffix")})" : string.Empty) + ".",
                Data = new
                {
                    documentTitle = doc.Title,
                    folders = folders.Select(f => new { role = f.Role, path = f.Path }),
                    files = plan.Files.Select(f => new
                    {
                        format = f.Format, kind = f.Kind, fileName = f.FileName, path = f.Path, disposition = f.Disposition,
                        sheetIds = f.SheetIds, sheetNumbers = f.SheetNumbers, nameWarning = f.NameWarning
                    }),
                    mergedDrawings = groups,
                    skipped = plan.Skipped,
                    paperSizes = sizes,
                    exporters = a.Formats.ToDictionary(f => f, _ => true),
                    options
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var open = CheckNoOpenTransaction(doc);
        if (open != null) return Fail(request, open);

        var runner = new SheetExportRunner(doc, new SheetExportSettings
        {
            DwgSetup = dwgSetup,
            MergedViews = a.MergedViews,
            DxfTemplatePath = template,
            MergeSpacingMm = a.MergeSpacingMm,
            OdaConverterPath = mergedDwgExt == ExportFormats.Dwg ? oda : null,
            IfcVersion = a.IfcVersion,
            IfcSpaceBoundaries = a.IfcSpaceBoundaries,
            IfcBaseQuantities = a.IfcBaseQuantities,
            ViewId = a.ViewId,
            NwcExportLinks = a.NwcExportLinks,
            NwcSharedCoordinates = a.NwcSharedCoordinates
        });
        var results = runner.Run(plan, cancellationToken);

        var ok = results.Count(r => r.Status is "written" or "overwritten" or "renamed");
        var failed = results.Count(r => r.Status == "failed");
        var cancelled = results.Count(r => r.Status == "cancelled");

        object? deliveryCheck = null;
        if (check && ok > 0)
            deliveryCheck = DeliveryCheck(doc, a, plan, results, pdfFolder, cadFolder, warnings);

        sw.Stop();
        var message = $"Exported {ok} of {results.Count} file(s)" +
                      (failed > 0 ? $", {failed} failed" : string.Empty) +
                      (cancelled > 0 ? $", {cancelled} not started (cancelled)" : string.Empty) +
                      (plan.Skipped.Count > 0 ? $"; {plan.Skipped.Count} sheet(s) skipped" : string.Empty) + ".";
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = ok > 0 || results.Count == 0,
            Status = cancelled > 0 ? "cancelled" : failed > 0 && ok == 0 ? "export_failed" : null,
            Message = message,
            Data = new
            {
                documentTitle = doc.Title,
                totals = new
                {
                    files = results.Count, written = results.Count(r => r.Status == "written"),
                    overwritten = results.Count(r => r.Status == "overwritten"), renamed = results.Count(r => r.Status == "renamed"),
                    failed, cancelled, bytes = results.Sum(r => r.Bytes), elapsedMs = sw.ElapsedMilliseconds
                },
                files = results.Select(r => new
                {
                    format = r.Format, kind = r.Kind, sheetIds = r.SheetIds, sheetNumbers = r.SheetNumbers, fileName = Path.GetFileName(r.Path),
                    path = r.Path, bytes = r.Bytes, status = r.Status, reason = r.Reason,
                    extraFiles = r.ExtraFiles.Count > 0 ? r.ExtraFiles : null
                }),
                mergedDrawings = groups,
                skipped = plan.Skipped,
                options,
                deliveryCheck
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// The delivery check on each folder the export wrote to. Sheets checked for missing files: the exported ones, plus
    /// every sheet that already has a file in the folder (so a stale or extra file is reported, not hidden).
    /// </summary>
    private static object DeliveryCheck(Document doc, SheetExportArgs a, ExportPlan plan, List<ExportFileResult> results,
        string pdfFolder, string cadFolder, List<string> warnings)
    {
        var allSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder)
            .Select(s => RevitSheetDescriptor.FromSheet(s.SheetNumber, s.Name))
            .ToList();
        var exportedCores = new HashSet<string>(plan.Sheets.Select(s => RevitSheetDescriptor.FromSheet(s.SheetNumber, s.SheetName).SheetNumber),
            StringComparer.OrdinalIgnoreCase);

        var byFolder = new List<(string Folder, List<string> Extensions)>();
        void Add(string folder, string ext)
        {
            var entry = byFolder.FirstOrDefault(f => string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase));
            if (entry.Folder == null) byFolder.Add((folder, new List<string> { ext }));
            else if (!entry.Extensions.Contains(ext)) entry.Extensions.Add(ext);
        }
        if (a.Formats.Contains(ExportFormats.Pdf)) Add(pdfFolder, "pdf");
        if (a.Formats.Contains(ExportFormats.Dwg)) Add(cadFolder, "dwg");
        if (a.Formats.Contains(ExportFormats.Dxf)) Add(cadFolder, "dxf");

        var policy = new FilePathPolicy();
        var scanner = new DeliveryFileScanner(policy);
        var reports = new List<object>();
        foreach (var (folder, extensions) in byFolder)
        {
            var scan = scanner.Scan(folder, false, extensions, 5000);
            if (!scan.Success)
            {
                reports.Add(new { folder, error = scan.Error });
                continue;
            }
            var withFiles = new HashSet<string>(scan.Files.Where(f => f.Parsed != null).Select(f => f.Parsed!.SheetNumber), StringComparer.OrdinalIgnoreCase);
            var sheets = allSheets.Where(s => exportedCores.Contains(s.SheetNumber) || withFiles.Contains(s.SheetNumber)).ToList();
            var runId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            var comparison = DeliveryRevitSheetComparer.CompareSheets(scan, sheets, extensions, new List<string>(), new List<string>(), runId);
            var issues = comparison.Issues;

            List<IssueDto>? registerIssues = null;
            string? registerError = null;
            if (a.RegisterPath.Length > 0)
            {
                var readError = policy.ValidateRead(a.RegisterPath);
                if (readError != null) registerError = readError;
                else (registerIssues, registerError) = DeliveryExcelRegisterComparer.Compare(scan, a.RegisterPath, a.RegisterWorksheet, extensions, runId);
            }

            var report = DeliveryIssueBuilder.BuildReport(runId, $"Export check — {Path.GetFileName(folder.TrimEnd('\\', '/'))}",
                registerIssues == null ? issues : issues.Concat(registerIssues).ToList());
            reports.Add(new
            {
                folder,
                extensions,
                summary = new
                {
                    files = scan.Files.Count,
                    sheetsChecked = comparison.ComparedSheetCount,
                    issues = report.TotalCount,
                    errors = report.ErrorCount,
                    warnings = report.WarningCount
                },
                registerError,
                issueReport = report
            });
            if (report.TotalCount > 0)
                warnings.Add($"Delivery check of {folder}: {report.TotalCount} issue(s).");
            warnings.AddRange(scan.Warnings);
        }
        return reports;
    }

    private static string? CheckNoOpenTransaction(Document doc)
    {
        try
        {
            return doc.IsModifiable
                ? $"'{doc.Title}' has an open transaction (an edit is in progress). Finish or cancel it in Revit and retry."
                : null;
        }
        catch (Exception ex)
        {
            return $"Could not read the state of '{doc.Title}': {ex.Message}";
        }
    }

    private static string DefaultModelStem(Document doc)
    {
        var name = string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileNameWithoutExtension(doc.PathName);
        return string.IsNullOrWhiteSpace(name) ? "Model" : name;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static McpToolResult Fail(McpToolRequest request, string message, string? status = null, object? data = null)
    {
        var result = new McpToolResult { RequestId = request.RequestId, Success = false, Message = message, Data = data };
        if (status != null) result.Status = status;
        return result;
    }
}
