using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Sheets;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>Reads title block values per sheet and checks them against the real sheet size and each other (#142).</summary>
public class GetTitleBlockValuesTool : IRevitMcpTool
{
    private const double FeetToMm = 304.8;

    public string Name => "revit_get_titleblock_values";
    public string Description =>
        "Reads the title block values of sheets. Required: sheetIds or sheetNumbers. Per sheet: the title block family and " +
        "type, the sheet's and the title block instance's parameter values, the real size in mm and the matching ISO size " +
        "(A4...A0 and extended formats like A3x3). Warnings: the written format does not match the real size, dates or " +
        "versions differ between the sheets. The format, date and version parameters are found by common English and " +
        "Estonian names; formatParameter, dateParameter and versionParameter name them explicitly. The Revit API does not " +
        "say which parameter a label shows or how wide it is, so all values are returned and text overflow is not checked: " +
        "use revit_export_view_image for that. Write values with revit_set_parameters_bulk entity=sheets " +
        "includeTitleBlockInstance=true. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var sheetIds = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNumbers = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers")
            .Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();
        if (sheetIds.Length == 0 && sheetNumbers.Length == 0)
            return Task.FromResult(Fail(request, "Provide sheetIds or sheetNumbers."));

        var formatOverride = ToolArguments.GetString(request.Arguments, "formatParameter");
        var dateOverride = ToolArguments.GetString(request.Arguments, "dateParameter");
        var versionOverride = ToolArguments.GetString(request.Arguments, "versionParameter");

        var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        var byId = new HashSet<long>(sheetIds);
        var byNumber = new HashSet<string>(sheetNumbers, StringComparer.OrdinalIgnoreCase);
        var sheets = all.Where(s => byId.Contains(s.Id.Value) || byNumber.Contains(s.SheetNumber))
            .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var notFound = sheetIds.Where(id => all.All(s => s.Id.Value != id)).Select(id => id.ToString())
            .Concat(sheetNumbers.Where(n => all.All(s => !string.Equals(s.SheetNumber, n, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (sheets.Count == 0)
            return Task.FromResult(Fail(request, $"No sheet matched: {string.Join(", ", notFound)}."));

        var warnings = new List<string>();
        var rows = new List<object>();
        var checks = new List<TitleBlockSheetValues>();
        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var titleBlocks = TitleBlocksOf(doc, sheet);
            if (titleBlocks.Count > 1)
                warnings.Add($"Sheet {sheet.SheetNumber} has {titleBlocks.Count} title blocks: the first one is read.");
            var titleBlock = titleBlocks.FirstOrDefault();

            var sheetValues = ReadValues(sheet);
            var instanceValues = titleBlock == null ? new Dictionary<string, string>() : ReadValues(titleBlock);
            var (widthMm, heightMm) = titleBlock == null ? ((double?)null, (double?)null) : SizeOf(titleBlock, sheet);

            var check = new TitleBlockSheetValues
            {
                SheetNumber = sheet.SheetNumber,
                WidthMm = widthMm,
                HeightMm = heightMm,
                Format = Role(sheetValues, instanceValues, "format", formatOverride),
                Date = Role(sheetValues, instanceValues, "date", dateOverride),
                Version = Role(sheetValues, instanceValues, "version", versionOverride)
            };
            checks.Add(check);

            rows.Add(new
            {
                sheetId = sheet.Id.Value,
                sheetNumber = sheet.SheetNumber,
                sheetName = sheet.Name,
                titleBlockId = titleBlock?.Id.Value,
                family = titleBlock?.Symbol?.FamilyName,
                type = titleBlock?.Symbol?.Name,
                widthMm = widthMm == null ? (double?)null : Math.Round(widthMm.Value, 1),
                heightMm = heightMm == null ? (double?)null : Math.Round(heightMm.Value, 1),
                isoSize = widthMm == null || heightMm == null ? null : TitleBlockChecks.MatchIsoSize(widthMm.Value, heightMm.Value),
                format = check.Format,
                date = check.Date,
                version = check.Version,
                sheetParameters = sheetValues,
                titleBlockParameters = instanceValues
            });
        }

        var findings = TitleBlockChecks.Check(checks);
        warnings.AddRange(findings.Select(f => f.Message));
        if (notFound.Count > 0)
            warnings.Add($"Not found: {string.Join(", ", notFound)}.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Read the title blocks of {rows.Count} sheet(s); {findings.Count} finding(s).",
            Data = new
            {
                count = rows.Count,
                sheets = rows,
                findings = findings.Select(f => new { code = f.Code, message = f.Message, sheetNumbers = f.SheetNumbers }).ToList(),
                notChecked = "Text overflowing a label: the Revit API does not expose which parameter a label shows or its width."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    internal static List<FamilyInstance> TitleBlocksOf(Document doc, ViewSheet sheet) =>
        new FilteredElementCollector(doc)
            .OwnedByView(sheet.Id)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .OfType<FamilyInstance>()
            .ToList();

    private static string? Role(Dictionary<string, string> sheet, Dictionary<string, string> instance, string role, string overrideName)
    {
        // The instance first: its own value is what the label on this sheet prints when both have the name.
        var name = TitleBlockChecks.FindParameter(instance.Keys, role, overrideName);
        if (name != null) return instance[name];
        name = TitleBlockChecks.FindParameter(sheet.Keys, role, overrideName);
        return name != null ? sheet[name] : null;
    }

    private static (double? Width, double? Height) SizeOf(FamilyInstance titleBlock, ViewSheet sheet)
    {
        var width = titleBlock.get_Parameter(BuiltInParameter.SHEET_WIDTH);
        var height = titleBlock.get_Parameter(BuiltInParameter.SHEET_HEIGHT);
        if (width is { HasValue: true } && height is { HasValue: true } && width.AsDouble() > 0 && height.AsDouble() > 0)
            return (width.AsDouble() * FeetToMm, height.AsDouble() * FeetToMm);

        var box = titleBlock.get_BoundingBox(sheet);
        return box == null
            ? (null, null)
            : ((box.Max.X - box.Min.X) * FeetToMm, (box.Max.Y - box.Min.Y) * FeetToMm);
    }

    /// <summary>Text, number and yes/no values that have content. ElementId values are links, not label text.</summary>
    private static Dictionary<string, string> ReadValues(Element element)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Parameter parameter in element.Parameters)
        {
            if (parameter.StorageType == StorageType.None || parameter.StorageType == StorageType.ElementId) continue;
            string? value;
            try
            {
                value = parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                continue;
            }
            var name = parameter.Definition?.Name;
            if (string.IsNullOrEmpty(name) || string.IsNullOrWhiteSpace(value)) continue;
            values[name!] = value!;
        }
        return values;
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
