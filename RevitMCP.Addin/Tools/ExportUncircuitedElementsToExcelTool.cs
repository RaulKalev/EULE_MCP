using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ExportUncircuitedElementsToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_uncircuited_elements_to_excel";
    public string Description => "Exports elements not assigned to any electrical circuit to a formatted .xlsx file with Summary and Uncircuited Elements sheets. Accepts: categories (string[]), filters (JSON array), returnParameters (string[]), fileName (default Uncircuited_Elements.xlsx), limit. Returns the file path.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly string[] DefaultCategories =
    [
        "Electrical Fixtures", "Lighting Fixtures", "Electrical Equipment",
        "Data Devices", "Fire Alarm Devices", "Security Devices", "Communication Devices"
    ];

    private readonly CategoryResolver _categoryResolver = new();
    private readonly ParameterReader _paramReader = new();
    private readonly ExportPathService _pathService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var categories = ToolArguments.GetStringArray(request.Arguments, "categories");
        if (categories.Length == 0) categories = DefaultCategories;
        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var returnParams = ToolArguments.GetStringArray(request.Arguments, "returnParameters");
        var fileName = ToolArguments.GetString(request.Arguments, "fileName", "Uncircuited_Elements.xlsx");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 2000);

        var records = new List<(long Id, string Category, string Family, string Type, string Level, Dictionary<string, string> Params)>();
        var warnings = new List<string>(filtersParsed.Warnings);

        var circuitedElemIds = new HashSet<long>(
            new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(c => c.Elements != null)
                .SelectMany(c => c.Elements.Cast<Element>().Select(e => e.Id.Value))
        );

        foreach (var catName in categories)
        {
            if (records.Count >= limit) break;
            var resolve = _categoryResolver.Resolve(doc, catName);
            if (resolve.Category == null) { warnings.Add($"Category '{catName}' not found — skipped."); continue; }

            foreach (var eid in new FilteredElementCollector(doc)
                .WhereElementIsNotElementType().OfCategoryId(resolve.Category.Id).ToElementIds())
            {
                if (records.Count >= limit) break;
                var element = doc.GetElement(eid);
                if (element is not FamilyInstance fi || fi.MEPModel == null) continue;
                if (circuitedElemIds.Contains(fi.Id.Value)) continue;

                if (filtersParsed.Items.Count > 0)
                {
                    var allP = _paramReader.ReadParameters(doc, element,
                        new ParameterReadOptions { IncludeInstanceParameters = true, IncludeTypeParameters = true });
                    if (!PassesFilters(allP, filtersParsed.Items)) continue;
                }

                var typeId = element.GetTypeId();
                var typeElem = typeId != null && typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                string level = "";
                try
                {
                    var lvl = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                           ?? element.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                    if (lvl != null) level = doc.GetElement(lvl.AsElementId())?.Name ?? "";
                }
                catch { }

                var paramDict = new Dictionary<string, string>();
                if (returnParams.Length > 0)
                {
                    var readOpts = new ParameterReadOptions { IncludeInstanceParameters = true, IncludeTypeParameters = true };
                    foreach (var p in _paramReader.ReadParameters(doc, element, readOpts))
                        if (returnParams.Any(rp => ParameterMatcher.Matches(p.Name, rp, "ContainsNormalized")))
                            paramDict[p.Name] = p.Value;
                }
                records.Add((eid.Value, element.Category?.Name ?? "", fi.Symbol?.Family?.Name ?? "", typeElem?.Name ?? element.Name, level, paramDict));
            }
        }

        var filePath = _pathService.ResolveFilePath(fileName);
        using (var wb = new XLWorkbook())
        {
            var ws0 = wb.Worksheets.Add("Summary");
            int r0 = 1;
            void AddS(string l, string v) { ws0.Cell(r0, 1).Value = l; ws0.Cell(r0, 1).Style.Font.Bold = true; ws0.Cell(r0, 2).Value = v; r0++; }
            AddS("Export Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AddS("Total Uncircuited", records.Count.ToString());
            AddS("Categories Checked", string.Join(", ", categories));
            ws0.Column(1).Width = 22; ws0.Column(2).AdjustToContents();

            var extraCols = returnParams.Length > 0 ? records.SelectMany(r => r.Params.Keys).Distinct().ToList() : new List<string>();
            var ws1 = wb.Worksheets.Add("Uncircuited Elements");
            var baseHdrs = new[] { "Element Id", "Category", "Family", "Type", "Level" };
            var allHdrs = baseHdrs.Concat(extraCols).ToArray();
            for (var c = 0; c < allHdrs.Length; c++) { ws1.Cell(1, c + 1).Value = allHdrs[c]; ws1.Cell(1, c + 1).Style.Font.Bold = true; }
            for (var row = 0; row < records.Count; row++)
            {
                var rec = records[row];
                ws1.Cell(row + 2, 1).Value = rec.Id;
                ws1.Cell(row + 2, 2).Value = rec.Category;
                ws1.Cell(row + 2, 3).Value = rec.Family;
                ws1.Cell(row + 2, 4).Value = rec.Type;
                ws1.Cell(row + 2, 5).Value = rec.Level;
                for (var c = 0; c < extraCols.Count; c++)
                    ws1.Cell(row + 2, 6 + c).Value = rec.Params.TryGetValue(extraCols[c], out var pv) ? pv : "";
            }
            if (records.Count > 0)
            {
                ws1.Range(1, 1, records.Count + 1, allHdrs.Length).CreateTable("UncircuitedTable").ShowAutoFilter = true;
                ws1.SheetView.FreezeRows(1);
                for (var c = 1; c <= allHdrs.Length; c++) ws1.Column(c).AdjustToContents(1, Math.Min(records.Count + 1, 200));
            }
            wb.SaveAs(filePath);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Exported {records.Count} uncircuited element(s) to {System.IO.Path.GetFileName(filePath)}",
            Data = new { filePath, uncircuitedCount = records.Count },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static bool PassesFilters(IReadOnlyList<ParameterValueDto> parameters, List<ParameterFilterDto> filters)
    {
        foreach (var filter in filters)
        {
            var matches = parameters.Where(p => ParameterMatcher.Matches(p.Name, filter.ParameterName, filter.MatchMode)).ToList();
            if (matches.Count == 0) { if (filter.Operator == "isEmpty") continue; return false; }
            if (!matches.Any(p => EvalOp(p.Value, filter.Operator, filter.Value))) return false;
        }
        return true;
    }

    private static bool EvalOp(string v, string op, string fv) => op switch
    {
        "equals" => string.Equals(v, fv, StringComparison.OrdinalIgnoreCase),
        "contains" => v.Contains(fv, StringComparison.OrdinalIgnoreCase),
        "isEmpty" => string.IsNullOrEmpty(v),
        "isNotEmpty" => !string.IsNullOrEmpty(v),
        _ => false
    };

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
