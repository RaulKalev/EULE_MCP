using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Sheet and model export (#105, docs/export.md): PDF/DWG/DXF as the EliteSheets plugin makes them, IFC, NWC, delivery check.</summary>
internal sealed partial class RevitMcpTools
{
    private const string ExportArgsSummary =
        "formats [pdf, dwg, dxf, ifc, nwc]. Sheets: sheets (ids, sheet numbers or EL-5-01 cores), sheetSet, allSheets, " +
        "narrowed by discipline / numberContains / nameContains (max 500). merge (default true) applies the EliteSheets rule: " +
        "sheets …-7-{group}_{title}--{order} become one combined PDF and one DXF with the sheets side by side in the " +
        "EliteSheets DXF template (DWG via the ODA File Converter when installed). naming sheetNumber (default) | " +
        "sheetNumberAndName | pattern:{SheetNumber}_{SheetName}_{Revision}; includeRevision appends _r02. folder, or " +
        "pdfFolder + cadFolder; default the project's EliteSheets folders. overwrite=false gives a new file a timestamp " +
        "suffix when the name exists.";

    [McpServerTool(Name = "revit_list_export_setups", ReadOnly = true),
     Description("Lists what revit_export can use: DWG/DXF export setups (and the active one), saved sheet sets, which exporters this Revit has (pdf, dwg, dxf, ifc, nwc), the EliteSheets settings the export follows (DXF template for merged drawings, convert merged drawings to DWG, this project's PDF and DWG folders), the ODA File Converter, printable sheet count and the merged drawings the sheet numbers form.")]
    public async Task<string> ListExportSetups(
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["document"] = document ?? string.Empty };
        return FormatResult(await pipeClient.SendAsync("revit_list_export_setups", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_export", ReadOnly = true),
     Description("Previews revit_export WITHOUT writing: every file name and path, merged drawings, existing files (overwrite or timestamp suffix), skipped sheets (no title block, not in the sheet list), names the delivery check would not parse, exporters and options. " + ExportArgsSummary)]
    public Task<string> PreviewExport(
        [Description("pdf, dwg, dxf, ifc, nwc.")] string[] formats,
        [Description("Sheet ids, sheet numbers, or discipline-group-sequence cores (EL-5-01).")] string[]? sheets = null,
        [Description("A saved Print/Export sheet set by name.")] string? sheetSet = null,
        [Description("Every sheet (then narrowed by the filters).")] bool allSheets = false,
        [Description("Only sheets of this discipline (EL, EN, EA…), from the sheet number.")] string? discipline = null,
        [Description("Only sheets whose number contains this.")] string? numberContains = null,
        [Description("Only sheets whose name contains this.")] string? nameContains = null,
        [Description("Folder for every format (unless pdfFolder / cadFolder are given).")] string? folder = null,
        [Description("PDF folder.")] string? pdfFolder = null,
        [Description("DWG/DXF folder.")] string? cadFolder = null,
        [Description("sheetNumber (default) | sheetNumberAndName | pattern:{SheetNumber}_{SheetName}_{Revision}.")] string? naming = null,
        [Description("Append _r{revision} from the sheet's current revision.")] bool includeRevision = false,
        [Description("Apply the EliteSheets merge rule (default true).")] bool merge = true,
        [Description("Replace existing files; false gives the new file a timestamp suffix.")] bool overwrite = false,
        [Description("Also sheets without a title block or not in the sheet list.")] bool includeUnlisted = false,
        [Description("DWG/DXF export setup; default the active one.")] string? dwgSetup = null,
        [Description("Override the setup: true = one DWG per sheet without view xrefs.")] bool? mergedViews = null,
        [Description("DXF template for merged drawings; default EliteSheets' TemplateDxfPath.")] string? dxfTemplatePath = null,
        [Description("Convert merged drawings to DWG with the ODA File Converter; default the EliteSheets setting.")] bool? convertMergedToDwg = null,
        [Description("Spacing of merged sheets in the template, mm (default 220).")] double mergeSpacingMm = 220,
        [Description("IFC/NWC file name without extension; default the project file name.")] string? fileName = null,
        [Description("IFC filter view / NWC view (3D); empty = whole model.")] long viewId = 0,
        [Description("IFC2x3CV2 (default) | IFC4RV | IFC4DTV.")] string? ifcVersion = null,
        [Description("IFC space boundaries 0-2.")] int ifcSpaceBoundaries = 0,
        [Description("IFC base quantities.")] bool ifcBaseQuantities = false,
        [Description("NWC: include linked models.")] bool nwcExportLinks = false,
        [Description("NWC: shared (default) | internal coordinates.")] string? nwcCoordinates = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendExport("revit_preview_export", formats, sheets, sheetSet, allSheets, discipline, numberContains, nameContains, folder, pdfFolder,
            cadFolder, naming, includeRevision, merge, overwrite, includeUnlisted, dwgSetup, mergedViews, dxfTemplatePath, convertMergedToDwg,
            mergeSpacingMm, fileName, viewId, ifcVersion, ifcSpaceBoundaries, ifcBaseQuantities, nwcExportLinks, nwcCoordinates,
            null, null, document, cancellationToken);

    [McpServerTool(Name = "revit_export"),
     Description("Exports sheets to PDF, DWG and DXF the way the EliteSheets plugin does, and the model to IFC or NWC. Requires approval. Long-running; a cancel stops between files and reports what was written. Returns per file {fileName, path, bytes, status: written | overwritten | renamed | failed | cancelled, reason}. Run revit_preview_export first. " + ExportArgsSummary)]
    public Task<string> Export(
        [Description("pdf, dwg, dxf, ifc, nwc.")] string[] formats,
        [Description("Sheet ids, sheet numbers, or discipline-group-sequence cores (EL-5-01).")] string[]? sheets = null,
        [Description("A saved Print/Export sheet set by name.")] string? sheetSet = null,
        [Description("Every sheet (then narrowed by the filters).")] bool allSheets = false,
        [Description("Only sheets of this discipline (EL, EN, EA…), from the sheet number.")] string? discipline = null,
        [Description("Only sheets whose number contains this.")] string? numberContains = null,
        [Description("Only sheets whose name contains this.")] string? nameContains = null,
        [Description("Folder for every format (unless pdfFolder / cadFolder are given).")] string? folder = null,
        [Description("PDF folder.")] string? pdfFolder = null,
        [Description("DWG/DXF folder.")] string? cadFolder = null,
        [Description("sheetNumber (default) | sheetNumberAndName | pattern:{SheetNumber}_{SheetName}_{Revision}.")] string? naming = null,
        [Description("Append _r{revision} from the sheet's current revision.")] bool includeRevision = false,
        [Description("Apply the EliteSheets merge rule (default true).")] bool merge = true,
        [Description("Replace existing files; false gives the new file a timestamp suffix.")] bool overwrite = false,
        [Description("Also sheets without a title block or not in the sheet list.")] bool includeUnlisted = false,
        [Description("DWG/DXF export setup; default the active one.")] string? dwgSetup = null,
        [Description("Override the setup: true = one DWG per sheet without view xrefs.")] bool? mergedViews = null,
        [Description("DXF template for merged drawings; default EliteSheets' TemplateDxfPath.")] string? dxfTemplatePath = null,
        [Description("Convert merged drawings to DWG with the ODA File Converter; default the EliteSheets setting.")] bool? convertMergedToDwg = null,
        [Description("Spacing of merged sheets in the template, mm (default 220).")] double mergeSpacingMm = 220,
        [Description("IFC/NWC file name without extension; default the project file name.")] string? fileName = null,
        [Description("IFC filter view / NWC view (3D); empty = whole model.")] long viewId = 0,
        [Description("IFC2x3CV2 (default) | IFC4RV | IFC4DTV.")] string? ifcVersion = null,
        [Description("IFC space boundaries 0-2.")] int ifcSpaceBoundaries = 0,
        [Description("IFC base quantities.")] bool ifcBaseQuantities = false,
        [Description("NWC: include linked models.")] bool nwcExportLinks = false,
        [Description("NWC: shared (default) | internal coordinates.")] string? nwcCoordinates = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendExport("revit_export", formats, sheets, sheetSet, allSheets, discipline, numberContains, nameContains, folder, pdfFolder,
            cadFolder, naming, includeRevision, merge, overwrite, includeUnlisted, dwgSetup, mergedViews, dxfTemplatePath, convertMergedToDwg,
            mergeSpacingMm, fileName, viewId, ifcVersion, ifcSpaceBoundaries, ifcBaseQuantities, nwcExportLinks, nwcCoordinates,
            null, null, document, cancellationToken);

    [McpServerTool(Name = "revit_export_and_check"),
     Description("revit_export, then the delivery check on the folders it wrote to: every exported sheet has its files (missing), once (duplicates such as an older timestamped copy), and every company-named file belongs to a sheet (orphans). registerPath (.xlsx document register) also checks the folder against the register. Requires approval. Returns the export result and deliveryCheck per folder. " + ExportArgsSummary)]
    public Task<string> ExportAndCheck(
        [Description("pdf, dwg, dxf, ifc, nwc.")] string[] formats,
        [Description("Sheet ids, sheet numbers, or discipline-group-sequence cores (EL-5-01).")] string[]? sheets = null,
        [Description("A saved Print/Export sheet set by name.")] string? sheetSet = null,
        [Description("Every sheet (then narrowed by the filters).")] bool allSheets = false,
        [Description("Only sheets of this discipline (EL, EN, EA…), from the sheet number.")] string? discipline = null,
        [Description("Only sheets whose number contains this.")] string? numberContains = null,
        [Description("Only sheets whose name contains this.")] string? nameContains = null,
        [Description("Folder for every format (unless pdfFolder / cadFolder are given).")] string? folder = null,
        [Description("PDF folder.")] string? pdfFolder = null,
        [Description("DWG/DXF folder.")] string? cadFolder = null,
        [Description("sheetNumber (default) | sheetNumberAndName | pattern:{SheetNumber}_{SheetName}_{Revision}.")] string? naming = null,
        [Description("Append _r{revision} from the sheet's current revision.")] bool includeRevision = false,
        [Description("Apply the EliteSheets merge rule (default true).")] bool merge = true,
        [Description("Replace existing files; false gives the new file a timestamp suffix.")] bool overwrite = false,
        [Description("Also sheets without a title block or not in the sheet list.")] bool includeUnlisted = false,
        [Description("DWG/DXF export setup; default the active one.")] string? dwgSetup = null,
        [Description("Override the setup: true = one DWG per sheet without view xrefs.")] bool? mergedViews = null,
        [Description("DXF template for merged drawings; default EliteSheets' TemplateDxfPath.")] string? dxfTemplatePath = null,
        [Description("Convert merged drawings to DWG with the ODA File Converter; default the EliteSheets setting.")] bool? convertMergedToDwg = null,
        [Description("Spacing of merged sheets in the template, mm (default 220).")] double mergeSpacingMm = 220,
        [Description("IFC/NWC file name without extension; default the project file name.")] string? fileName = null,
        [Description("IFC filter view / NWC view (3D); empty = whole model.")] long viewId = 0,
        [Description("IFC2x3CV2 (default) | IFC4RV | IFC4DTV.")] string? ifcVersion = null,
        [Description("IFC space boundaries 0-2.")] int ifcSpaceBoundaries = 0,
        [Description("IFC base quantities.")] bool ifcBaseQuantities = false,
        [Description("NWC: include linked models.")] bool nwcExportLinks = false,
        [Description("NWC: shared (default) | internal coordinates.")] string? nwcCoordinates = null,
        [Description("Document register (.xlsx) to check the folder against as well.")] string? registerPath = null,
        [Description("Worksheet of the register; default the first.")] string? registerWorksheet = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendExport("revit_export_and_check", formats, sheets, sheetSet, allSheets, discipline, numberContains, nameContains, folder, pdfFolder,
            cadFolder, naming, includeRevision, merge, overwrite, includeUnlisted, dwgSetup, mergedViews, dxfTemplatePath, convertMergedToDwg,
            mergeSpacingMm, fileName, viewId, ifcVersion, ifcSpaceBoundaries, ifcBaseQuantities, nwcExportLinks, nwcCoordinates,
            registerPath, registerWorksheet, document, cancellationToken);

    private async Task<string> SendExport(string toolName, string[] formats, string[]? sheets, string? sheetSet, bool allSheets,
        string? discipline, string? numberContains, string? nameContains, string? folder, string? pdfFolder, string? cadFolder,
        string? naming, bool includeRevision, bool merge, bool overwrite, bool includeUnlisted, string? dwgSetup, bool? mergedViews,
        string? dxfTemplatePath, bool? convertMergedToDwg, double mergeSpacingMm, string? fileName, long viewId, string? ifcVersion,
        int ifcSpaceBoundaries, bool ifcBaseQuantities, bool nwcExportLinks, string? nwcCoordinates, string? registerPath,
        string? registerWorksheet, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["formats"] = formats ?? [],
            ["sheets"] = sheets ?? [],
            ["sheetSet"] = sheetSet ?? string.Empty,
            ["allSheets"] = allSheets,
            ["discipline"] = discipline ?? string.Empty,
            ["numberContains"] = numberContains ?? string.Empty,
            ["nameContains"] = nameContains ?? string.Empty,
            ["folder"] = folder ?? string.Empty,
            ["pdfFolder"] = pdfFolder ?? string.Empty,
            ["cadFolder"] = cadFolder ?? string.Empty,
            ["naming"] = naming ?? string.Empty,
            ["includeRevision"] = includeRevision,
            ["merge"] = merge,
            ["overwrite"] = overwrite,
            ["includeUnlisted"] = includeUnlisted,
            ["dwgSetup"] = dwgSetup ?? string.Empty,
            ["mergedViews"] = mergedViews?.ToString() ?? string.Empty,
            ["dxfTemplatePath"] = dxfTemplatePath ?? string.Empty,
            ["convertMergedToDwg"] = convertMergedToDwg?.ToString() ?? string.Empty,
            ["mergeSpacingMm"] = mergeSpacingMm,
            ["fileName"] = fileName ?? string.Empty,
            ["viewId"] = viewId,
            ["ifcVersion"] = ifcVersion ?? string.Empty,
            ["ifcSpaceBoundaries"] = ifcSpaceBoundaries,
            ["ifcBaseQuantities"] = ifcBaseQuantities,
            ["nwcExportLinks"] = nwcExportLinks,
            ["nwcCoordinates"] = nwcCoordinates ?? string.Empty,
            ["registerPath"] = registerPath ?? string.Empty,
            ["registerWorksheet"] = registerWorksheet ?? string.Empty,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}
