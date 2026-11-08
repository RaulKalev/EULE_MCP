using RevitMCP.Addin.Export.Sheets;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Approval summaries of revit_export and revit_export_and_check (#105), e.g. "Export sheets matching discipline EL to
/// PDF, DWG into G:\…\TP (merged drawings per the EliteSheets rule; existing files get a timestamp suffix)."
/// ApprovalSummaryBuilder chains <see cref="TryBuild"/> into its default case. Built from the arguments only.
/// </summary>
public static class ExportApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request) => request.ToolName switch
    {
        "revit_export" => Build(request, check: false),
        "revit_export_and_check" => Build(request, check: true),
        _ => null
    };

    private static string Build(McpToolRequest request, bool check)
    {
        var args = request.Arguments;
        var formats = ExportFormats.Parse(ToolArguments.GetStringArray(args, "formats"), out _);
        var what = DescribeSelection(args, formats);

        var folder = ToolArguments.GetString(args, "folder").Trim();
        var pdfFolder = ToolArguments.GetString(args, "pdfFolder").Trim();
        var cadFolder = ToolArguments.GetString(args, "cadFolder").Trim();
        var where = new List<string>();
        if (pdfFolder.Length > 0) where.Add($"PDF into {pdfFolder}");
        if (cadFolder.Length > 0) where.Add($"DWG/DXF into {cadFolder}");
        if (folder.Length > 0) where.Add($"{(where.Count > 0 ? "the rest" : "files")} into {folder}");
        var whereText = where.Count > 0 ? string.Join(", ", where) : "the project's EliteSheets export folders";

        var details = new List<string>();
        if (formats.Any(f => f != ExportFormats.Ifc))
            details.Add(ToolArguments.GetBool(args, "merge", true) ? "merged drawings per the EliteSheets rule" : "no merging");
        var naming = ToolArguments.GetString(args, "naming").Trim();
        details.Add($"names: {(naming.Length > 0 ? naming : "sheet number")}{(ToolArguments.GetBool(args, "includeRevision") ? " + revision" : string.Empty)}");
        details.Add(ToolArguments.GetBool(args, "overwrite") ? "existing files are OVERWRITTEN" : "existing files are kept, new ones get a timestamp suffix");

        var document = ToolArguments.GetString(args, "document").Trim();
        return $"Export {what} to {string.Join(", ", formats.Select(f => f.ToUpperInvariant()))}" +
               (document.Length > 0 ? $" from '{document}'" : string.Empty) +
               $", {whereText} ({string.Join("; ", details)})" +
               (check ? ", then run the delivery check on those folders" +
                        (ToolArguments.GetString(args, "registerPath").Trim() is { Length: > 0 } reg ? $" and against the register {reg}" : string.Empty)
                      : string.Empty) + ".";
    }

    private static string DescribeSelection(Dictionary<string, object?> args, List<string> formats)
    {
        if (formats.All(f => f == ExportFormats.Ifc))
            return ToolArguments.GetLong(args, "viewId") > 0 ? $"the model (view {ToolArguments.GetLong(args, "viewId")})" : "the model";

        var parts = new List<string>();
        var sheets = ToolArguments.GetStringArray(args, "sheets").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (sheets.Count > 0)
            parts.Add($"{sheets.Count} sheet(s) ({string.Join(", ", sheets.Take(5))}{(sheets.Count > 5 ? ", …" : string.Empty)})");
        var set = ToolArguments.GetString(args, "sheetSet").Trim();
        if (set.Length > 0) parts.Add($"sheet set '{set}'");
        if (ToolArguments.GetBool(args, "allSheets")) parts.Add("all sheets");
        var filters = new List<string>();
        if (ToolArguments.GetString(args, "discipline").Trim() is { Length: > 0 } d) filters.Add($"discipline {d}");
        if (ToolArguments.GetString(args, "numberContains").Trim() is { Length: > 0 } n) filters.Add($"number contains '{n}'");
        if (ToolArguments.GetString(args, "nameContains").Trim() is { Length: > 0 } m) filters.Add($"name contains '{m}'");
        var text = parts.Count > 0 ? string.Join(" + ", parts) : "sheets";
        if (filters.Count > 0) text += (parts.Count > 0 ? " with " : " matching ") + string.Join(", ", filters);
        return text;
    }
}
