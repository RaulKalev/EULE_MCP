using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Schedules;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#103: tool arguments (JSON after the pipe) become schedule specs; table paging, export names, approval text.</summary>
public class ScheduleSpecParsingTests
{
    /// <summary>The socket list of docs/schedules.md, as the pipe delivers it.</summary>
    private const string SocketListJson = """
        {
          "name": "EL - Socket list L2",
          "category": "Electrical Fixtures",
          "fields": [
            "Mark",
            { "parameter": "Type", "width": 40 },
            "Level",
            { "parameter": "Room: Name", "heading": "Room" },
            { "parameter": "Comments", "heading": "Notes", "width": 60 },
            { "count": true, "heading": "Qty" }
          ],
          "filters": [ { "field": "Level", "type": "equals", "value": "Level 2" } ],
          "sort": [ { "field": "Room", "header": true, "footer": true }, "Mark" ],
          "itemize": true,
          "grandTotal": "titleCountTotals"
        }
        """;

    private static Dictionary<string, object?> Args(string json) =>
        JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);

    [Fact]
    public void Create_ParsesTheDocsExample()
    {
        var errors = new List<string>();
        var spec = ScheduleSpecParser.ParseCreate(Args(SocketListJson), errors);

        Assert.Empty(errors);
        Assert.Equal("EL - Socket list L2", spec.Name);
        Assert.Equal("Electrical Fixtures", spec.Category);
        Assert.Equal(6, spec.Fields.Count);
        Assert.Equal("Mark", spec.Fields[0].Parameter);
        Assert.Equal(40, spec.Fields[1].WidthMm);
        Assert.Equal("Room", spec.Fields[3].Heading);
        Assert.Equal(ScheduleFieldKind.Count, spec.Fields[5].Kind);
        Assert.Equal("Qty", spec.Fields[5].Heading);
        Assert.Equal("Level 2", spec.Filters.Single().Value);
        Assert.Null(spec.Filters.Single().Number);
        Assert.Equal(2, spec.Sort.Count);
        Assert.True(spec.Sort[0].Header && spec.Sort[0].Footer && !spec.Sort[0].BlankLine);
        Assert.Equal("Mark", spec.Sort[1].Field);
        Assert.Equal("titleCountTotals", spec.GrandTotal);
        Assert.True(spec.Itemize);
    }

    [Fact]
    public void Create_Defaults()
    {
        var errors = new List<string>();
        var spec = ScheduleSpecParser.ParseCreate(new Dictionary<string, object?> { ["category"] = "Lighting Fixtures", ["fields"] = "[\"Mark\"]" }, errors);

        Assert.Empty(errors);
        Assert.True(spec.Itemize);
        Assert.Null(spec.GrandTotal);
        Assert.Null(spec.Template);
        Assert.False(spec.MultiCategory);
        Assert.False(spec.AllowMissingFields);
        Assert.Empty(spec.Filters);
        Assert.Empty(spec.Sort);
        Assert.Equal(string.Empty, spec.Name);
    }

    [Fact]
    public void Create_ReportsWhatIsMissing()
    {
        var errors = new List<string>();
        ScheduleSpecParser.ParseCreate(new Dictionary<string, object?> { ["fields"] = new JArray() }, errors);
        Assert.Contains(errors, e => e.Contains("category is required"));
        Assert.Contains(errors, e => e.Contains("fields is required"));

        errors.Clear();
        ScheduleSpecParser.ParseCreate(Args("""{ "multiCategory": true, "keySchedule": true, "fields": ["Mark", 5, {"heading": "x"}], "filters": {"field": "a"} }"""), errors);
        Assert.Contains(errors, e => e.Contains("cannot be combined"));
        Assert.Contains(errors, e => e.Contains("fields[1]"));
        Assert.Contains(errors, e => e.Contains("fields[2] names no field"));
        Assert.Contains(errors, e => e.Contains("filters must be an array"));
    }

    [Fact]
    public void Create_ComputedFieldsAndFormats()
    {
        var errors = new List<string>();
        var spec = ScheduleSpecParser.ParseCreate(Args("""
            {
              "category": "Electrical Equipment",
              "fields": [
                "count",
                { "parameter": "Apparent Load", "totals": true, "hidden": "false", "format": { "units": "kVA", "accuracy": 0.01, "suppressTrailingZeros": true } },
                { "percentage": { "of": "Apparent Load" }, "heading": "Share" },
                { "formula": { "name": "Load kW", "expression": "Apparent Load * 0.9" } },
                { "parameter": "Comments", "fieldType": "type" }
              ],
              "filters": [ { "field": "Apparent Load", "type": ">", "value": 1500 }, { "field": "Mark", "type": "hasValue" }, { "field": "Spare", "value": true } ]
            }
            """), errors);

        Assert.Empty(errors);
        Assert.Equal(ScheduleFieldKind.Count, spec.Fields[0].Kind);
        Assert.True(spec.Fields[1].Totals);
        Assert.False(spec.Fields[1].Hidden);
        Assert.Equal("kVA", spec.Fields[1].Format!.Units);
        Assert.Equal(0.01, spec.Fields[1].Format!.Accuracy);
        Assert.Equal(ScheduleFieldKind.Percentage, spec.Fields[2].Kind);
        Assert.Equal("Apparent Load", spec.Fields[2].PercentageOf);
        Assert.Equal(ScheduleFieldKind.Formula, spec.Fields[3].Kind);
        Assert.Equal("Load kW", spec.Fields[3].FormulaName);
        Assert.Equal("type", spec.Fields[4].FieldType);

        Assert.Equal(1500, spec.Filters[0].Number);
        Assert.Equal("1500", spec.Filters[0].Value);
        Assert.False(spec.Filters[1].HasValue);
        Assert.Equal("equals", spec.Filters[2].Type);
        Assert.Equal(1, spec.Filters[2].Number);
    }

    [Fact]
    public void Edit_ParsesEveryPart()
    {
        var errors = new List<string>();
        var edit = ScheduleSpecParser.ParseEdit(Args("""
            {
              "addFields": [ { "parameter": "Circuit Number", "insertAt": 1 } ],
              "removeFields": ["Comments", "3"],
              "order": ["Mark", "Circuit Number"],
              "rename": { "Mark": "Tag" },
              "hide": ["Level"],
              "show": "Type, Count",
              "format": { "Apparent Load": { "units": "kVA" } },
              "widths": { "Mark": 25 },
              "setFilters": [],
              "itemize": false,
              "grandTotal": "count"
            }
            """), errors);

        Assert.Empty(errors);
        Assert.Equal(1, edit.AddFields.Single().InsertAt);
        Assert.Equal(new[] { "Comments", "3" }, edit.RemoveFields);
        Assert.Equal(new[] { "Mark", "Circuit Number" }, edit.Order);
        Assert.Equal("Tag", edit.Rename.Single().Value);
        Assert.Equal(new[] { "Type", "Count" }, edit.Show);
        Assert.Equal("kVA", edit.Format.Single().Value.Units);
        Assert.Equal(25, edit.Widths.Single().Value);
        Assert.NotNull(edit.SetFilters);
        Assert.Empty(edit.SetFilters!);
        Assert.Null(edit.SetSort);   // absent keeps the sorting; [] would clear it
        Assert.False(edit.Itemize);
        Assert.True(edit.ChangesStructure);
        Assert.False(edit.IsEmpty);
    }

    [Fact]
    public void Edit_EmptyArgumentsAreEmpty()
    {
        var errors = new List<string>();
        var edit = ScheduleSpecParser.ParseEdit(new Dictionary<string, object?>
        {
            ["schedule"] = "x", ["removeFields"] = new JArray(), ["grandTotal"] = "", ["allowMissingFields"] = false
        }, errors);
        Assert.Empty(errors);
        Assert.True(edit.IsEmpty);
        Assert.Null(edit.Itemize);
    }

    // ── Table paging and headings ───────────────────────────────────────────

    [Theory]
    [InlineData(250, 1, 100, 0, 100, 3)]
    [InlineData(250, 3, 100, 200, 50, 3)]
    [InlineData(250, 4, 100, 250, 0, 3)]
    [InlineData(0, 1, 100, 0, 0, 0)]
    [InlineData(1200, 1, 0, 0, 100, 12)]
    [InlineData(1200, 2, 900, 500, 500, 3)]
    public void Paging(int total, int page, int size, int start, int count, int pages)
        => Assert.Equal((start, count, pages), ScheduleTableMath.Page(total, page, size));

    [Fact]
    public void DataStart_SkipsHeadingsAndTheBlankRow()
    {
        var headings = new[] { "Mark", "Type", "Room" };
        IReadOnlyList<string>[] rows =
        {
            new[] { "Mark", "Type", "Room" },
            new[] { "", "", "" },
            new[] { "S1", "Double socket", "101" }
        };
        Assert.Equal(2, ScheduleTableMath.DataStartRow(rows, headings));

        IReadOnlyList<string>[] grouped =
        {
            new[] { "Identity", "", "" },
            new[] { "mark", "Type", "Room" },
            new[] { "S1", "Double socket", "101" }
        };
        Assert.Equal(2, ScheduleTableMath.DataStartRow(grouped, headings));

        IReadOnlyList<string>[] noHeaders = { new[] { "S1", "Double socket", "101" } };
        Assert.Equal(0, ScheduleTableMath.DataStartRow(noHeaders, headings));
    }

    // ── Export names ────────────────────────────────────────────────────────

    [Fact]
    public void ExportNames_AreSanitizedAndNeverOverwrite()
    {
        var now = new DateTime(2026, 10, 25, 14, 30, 5);
        var folder = Path.Combine("C:", "Exports");
        Assert.Equal(Path.Combine(folder, "EL_ Socket list.csv"), ScheduleExportNaming.Resolve(folder, null, "EL: Socket list", "csv", _ => false, now));
        Assert.Equal(Path.Combine(folder, "list.xlsx"), ScheduleExportNaming.Resolve(folder, "list.csv", "x", "xlsx", _ => false, now));
        Assert.Equal(Path.Combine(folder, "list.txt"), ScheduleExportNaming.Resolve(folder, "../evil/list", "x", "txt", _ => false, now));

        var taken = new HashSet<string> { Path.Combine(folder, "a.csv"), Path.Combine(folder, "a_2026-10-25_143005.csv") };
        Assert.Equal(Path.Combine(folder, "a_2026-10-25_143005_2.csv"), ScheduleExportNaming.Resolve(folder, "a", "x", "csv", taken.Contains, now));
    }

    [Theory]
    [InlineData("CSV", "csv")]
    [InlineData(".xlsx", "xlsx")]
    [InlineData("excel", "xlsx")]
    [InlineData("tsv", "txt")]
    [InlineData("", "csv")]
    public void ExportFormats(string raw, string expected)
    {
        Assert.True(ScheduleExportNaming.TryNormalizeFormat(raw, out var format));
        Assert.Equal(expected, format);
    }

    [Fact]
    public void ExportFormat_RejectsPdf() => Assert.False(ScheduleExportNaming.TryNormalizeFormat("pdf", out _));

    // ── Approval summaries ──────────────────────────────────────────────────

    private static McpToolRequest Request(string tool, string json) => new() { ToolName = tool, Arguments = Args(json) };

    [Fact]
    public void Summary_Create()
    {
        var text = SchedulesApprovalSummaries.TryBuild(Request("revit_create_schedule", SocketListJson));
        Assert.NotNull(text);
        Assert.StartsWith("Create schedule 'EL - Socket list L2' (Electrical Fixtures): 6 field(s) (Mark, Type, Level, Room, Notes, Qty)", text);
        Assert.Contains("1 filter(s): Level equals 'Level 2'", text);
        Assert.Contains("sorted by Room with headers, Mark", text);
        Assert.Contains("grand total: titleCountTotals", text);
    }

    [Fact]
    public void Summary_EditPlaceExport()
    {
        var edit = SchedulesApprovalSummaries.TryBuild(Request("revit_edit_schedule",
            """{ "schedule": "Socket list", "addFields": ["Circuit Number"], "setFilters": [], "removeFields": [] }"""));
        Assert.Equal("Edit schedule 'Socket list': add 1 field(s) (Circuit Number), remove every filter.", edit);

        var next = SchedulesApprovalSummaries.TryBuild(Request("revit_place_schedule_on_sheet",
            """{ "schedule": "Socket list", "sheet": "EL-201", "nextTo": { "viewportId": 123456, "side": "below", "gapMm": 15 } }"""));
        Assert.Equal("Place schedule 'Socket list' on sheet EL-201 next to viewport 123456 (below, gap 15 mm).", next);

        var at = SchedulesApprovalSummaries.TryBuild(Request("revit_place_schedule_on_sheet",
            """{ "scheduleId": 991, "sheet": "EL-201", "xMm": 600, "yMm": 560, "allowDuplicate": true }"""));
        Assert.Equal("Place schedule id 991 on sheet EL-201 with its top-left corner at (600, 560) mm (a second copy allowed).", at);

        var export = SchedulesApprovalSummaries.TryBuild(Request("revit_export_schedule", """{ "schedule": "Socket list", "format": "xlsx" }"""));
        Assert.StartsWith("Export schedule 'Socket list' as XLSX to the default export folder", export);

        Assert.Null(SchedulesApprovalSummaries.TryBuild(Request("revit_get_schedule", "{}")));
    }
}
