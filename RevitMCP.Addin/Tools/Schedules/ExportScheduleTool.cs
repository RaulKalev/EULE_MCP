using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Schedules;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Schedules;

/// <summary>revit_export_schedule (#103): Revit's own CSV/TXT export, or an Excel workbook of the table as displayed.</summary>
public sealed class ExportScheduleTool : IRevitMcpTool
{
    public string Name => "revit_export_schedule";

    public string Description =>
        "Exports a schedule to a file. Requires approval (writes a file). schedule (name or id); format = csv (default), " +
        "txt (tab-separated) — both Revit's own ViewSchedule.Export — or xlsx (the table as displayed, title and column " +
        "headings bold, headings frozen). folder (default Documents\\RKTools\\RevitMCP\\Exports; must be allowed by the " +
        "file access policy), fileName (default the schedule name; an existing file is never overwritten). csv: " +
        "delimiter (default \",\", or \";\"). title and headersFootersBlanks (default true) include the title row and " +
        "group headers, footers and blank lines. Returns filePath, rows and columns.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Schedules;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var args = request.Arguments;

        var schedule = ScheduleLookup.Resolve(doc, request, ToolArguments.GetString(args, "schedule"), 0, forEdit: true, out var failure);
        if (schedule == null) return Task.FromResult(failure!);

        if (!ScheduleExportNaming.TryNormalizeFormat(ToolArguments.GetString(args, "format", "csv"), out var format))
            return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", $"format '{format}' is not csv, txt or xlsx.", sw: sw));

        var folder = ToolArguments.GetString(args, "folder").Trim().Trim('"');
        if (folder.Length == 0) folder = new ExportPathService().ExportDirectory;
        var policy = new FilePathPolicy();
        var folderError = policy.ValidateWrite(folder);
        if (folderError != null)
            return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", $"Cannot write to '{folder}': {folderError}", sw: sw));
        folder = FilePathPolicy.NormalizePath(folder).Path ?? folder;

        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            return Task.FromResult(ScheduleToolSupport.Fail(request, "tool_execution_failed", $"Cannot create the folder '{folder}': {ex.Message}", sw: sw));
        }

        var filePath = ScheduleExportNaming.Resolve(folder, ToolArguments.GetString(args, "fileName"), schedule.Name, format, File.Exists, DateTime.Now);
        var pathError = policy.ValidateWrite(filePath);
        if (pathError != null)
            return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", $"Cannot write '{filePath}': {pathError}", sw: sw));

        var includeTitle = ToolArguments.GetBool(args, "title", true);
        var headersFootersBlanks = ToolArguments.GetBool(args, "headersFootersBlanks", true);
        var warnings = new List<string>();

        ScheduleTable table;
        try { table = ScheduleTableReader.Read(schedule); }
        catch (Exception ex)
        {
            return Task.FromResult(ScheduleToolSupport.Fail(request, "tool_execution_failed", $"Revit could not read the table of '{schedule.Name}': {ex.Message}", sw: sw));
        }

        try
        {
            if (format == "xlsx")
                WriteWorkbook(filePath, schedule.Name, table, includeTitle, headersFootersBlanks, warnings);
            else
            {
                var delimiter = ToolArguments.GetString(args, "delimiter", ",");
                if (format == "txt") delimiter = "\t";
                else if (delimiter is not ("," or ";"))
                {
                    warnings.Add($"delimiter '{delimiter}' is not ',' or ';'; ',' is used.");
                    delimiter = ",";
                }
                var options = new ViewScheduleExportOptions
                {
                    FieldDelimiter = delimiter,
                    TextQualifier = ExportTextQualifier.DoubleQuote,
                    Title = includeTitle,
                    HeadersFootersBlanks = headersFootersBlanks,
                    ColumnHeaders = ExportColumnHeaders.MultipleRows
                };
                schedule.Export(folder, Path.GetFileName(filePath), options);
                if (!File.Exists(filePath))
                    return Task.FromResult(ScheduleToolSupport.Fail(request, "tool_execution_failed", $"Revit reported no error but wrote no file at '{filePath}'.", sw: sw));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(ScheduleToolSupport.Fail(request, "tool_execution_failed", $"Export of '{schedule.Name}' failed: {ex.Message}", warnings: warnings, sw: sw));
        }

        return Task.FromResult(ScheduleToolSupport.Ok(request, sw,
            $"Exported '{schedule.Name}' ({table.DataRows.Count} row(s), {table.Columns.Count} column(s)) to {filePath}.",
            new
            {
                filePath,
                format,
                scheduleId = schedule.Id.Value,
                scheduleName = schedule.Name,
                rows = table.DataRows.Count,
                columns = table.Columns.Count,
                columnNames = table.Columns
            }, warnings));
    }

    private static void WriteWorkbook(string path, string scheduleName, ScheduleTable table, bool includeTitle, bool headersFootersBlanks, List<string> warnings)
    {
        using var workbook = new XLWorkbook();
        var sheetName = ScheduleExportNaming.Sanitize(scheduleName).Replace("[", "(").Replace("]", ")");
        var worksheet = workbook.Worksheets.Add(sheetName.Length > 31 ? sheetName.Substring(0, 31) : sheetName);

        var row = 1;
        if (includeTitle)
        {
            var title = string.Join(" ", table.TitleRows.SelectMany(r => r).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct());
            if (title.Length == 0) title = scheduleName;
            worksheet.Cell(row, 1).Value = title;
            worksheet.Cell(row, 1).Style.Font.Bold = true;
            worksheet.Cell(row, 1).Style.Font.FontSize = 13;
            row += 2;
        }

        var headingRows = table.HeadingRows.Count > 0 ? table.HeadingRows : new List<string[]> { table.Columns.ToArray() };
        foreach (var heading in headingRows)
        {
            for (var c = 0; c < heading.Length; c++)
            {
                var cell = worksheet.Cell(row, c + 1);
                cell.Value = heading[c];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8E8E8");
            }
            row++;
        }
        worksheet.SheetView.FreezeRows(row - 1);

        var skipped = 0;
        foreach (var data in table.DataRows)
        {
            if (!headersFootersBlanks && ScheduleTableMath.IsBlank(data)) { skipped++; continue; }
            for (var c = 0; c < data.Length; c++)
                worksheet.Cell(row, c + 1).Value = data[c];
            row++;
        }
        if (skipped > 0) warnings.Add($"{skipped} blank row(s) left out (headersFootersBlanks=false); group headers and footers stay in the workbook, since the displayed table does not mark them.");

        worksheet.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }
}
