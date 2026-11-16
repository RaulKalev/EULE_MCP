using System.IO;
using ClosedXML.Excel;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Parameters;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.Services;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#165: revit_set_parameter_values — parsing rows, validation, duplicates and the tool surface.</summary>
public class ParameterValueMappingTests
{
    [Fact]
    public void ParsesInlineRowsByIdOrUniqueId()
    {
        var rows = ParameterValueMapping.ParseInline(JArray.Parse(
            "[{\"elementId\":1037257,\"value\":\"ED-SEN-B01-0001\"}," +
            " {\"uniqueId\":\"abc-123\",\"value\":\"X\",\"parameterName\":\"Mark\"}," +
            " {\"ElementId\":\"42\",\"Value\":7}]"), out var error);

        Assert.Null(error);
        Assert.Equal(3, rows.Count);
        Assert.Equal(1037257, rows[0].ElementId);
        Assert.Equal("ED-SEN-B01-0001", rows[0].Value);
        Assert.Null(rows[1].ElementId);
        Assert.Equal("abc-123", rows[1].UniqueId);
        Assert.Equal("Mark", rows[1].ParameterName);
        Assert.Equal(42, rows[2].ElementId);
        Assert.Equal("7", rows[2].Value);
    }

    [Fact]
    public void ParsesAnIdToValueObjectAndAJsonString()
    {
        var map = ParameterValueMapping.ParseInline("{\"10\":\"A\",\"11\":\"B\"}", out var error);
        Assert.Null(error);
        Assert.Equal(new long?[] { 10, 11 }, map.Select(r => r.ElementId));
        Assert.Equal(new[] { "A", "B" }, map.Select(r => r.Value));
    }

    [Fact]
    public void RowWithoutValueIsAnError()
    {
        ParameterValueMapping.ParseInline(JArray.Parse("[{\"elementId\":1}]"), out var error);
        Assert.Contains("no value", error);
    }

    [Fact]
    public void ReadsCsvWithSemicolonsAndQuotes()
    {
        var table = ParameterValueMapping.ReadCsv("ElementId;Object_ID uus\r\n1;\"A;1\"\r\n2;\"say \"\"hi\"\"\"\r\n\r\n");
        Assert.Equal(3, table.Count);
        Assert.Equal("A;1", table[1][1]);
        Assert.Equal("say \"hi\"", table[2][1]);

        string? error = null;
        var rows = ParameterValueMapping.FromTable(table, new ParameterValueSource { ValueColumn = "Object_ID uus" }, ref error);
        Assert.Null(error);
        Assert.Equal(new long?[] { 1, 2 }, rows.Select(r => r.ElementId));
        Assert.Equal(2, rows[0].Row);   // file row number, header is row 1
    }

    [Fact]
    public void MissingColumnsAreNamed()
    {
        string? error = null;
        ParameterValueMapping.FromTable(new List<string[]> { new[] { "Foo", "Value" }, new[] { "1", "x" } }, new ParameterValueSource(), ref error);
        Assert.Contains("No element id column", error);
        Assert.Contains("Foo", error);

        error = null;
        ParameterValueMapping.FromTable(new List<string[]> { new[] { "ElementId", "Value" } }, new ParameterValueSource { ValueColumn = "Uus" }, ref error);
        Assert.Contains("Column 'Uus'", error);
    }

    [Fact]
    public void ReadsAnExcelSheetEvenWhileItIsOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pv-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var wb = new XLWorkbook())
            {
                wb.AddWorksheet("Muu");
                var ws = wb.AddWorksheet("Object_ID eelvaade");
                ws.Cell(1, 1).Value = "ElementId";
                ws.Cell(1, 2).Value = "Object_ID uus";
                ws.Cell(2, 1).Value = 1037257;
                ws.Cell(2, 2).Value = "ED-SEN-B01-0001";
                ws.Cell(3, 1).Value = 1037258;
                ws.Cell(3, 2).Value = "0007";
                wb.SaveAs(path);
            }

            // Excel keeps the file open with a write lock share; the reader must still get in.
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var rows = ParameterValueMapping.ReadFile(new ParameterValueSource
            {
                Path = path,
                Sheet = "Object_ID eelvaade",
                ValueColumn = "Object_ID uus"
            }, out var error);

            Assert.Null(error);
            Assert.Equal(new long?[] { 1037257, 1037258 }, rows.Select(r => r.ElementId));
            Assert.Equal(new[] { "ED-SEN-B01-0001", "0007" }, rows.Select(r => r.Value));

            ParameterValueMapping.ReadFile(new ParameterValueSource { Path = path, Sheet = "Puudub" }, out var sheetError);
            Assert.Contains("Sheet 'Puudub' not found", sheetError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsAJsonFileWithAValuesProperty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pv-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"values\":[{\"elementId\":5,\"value\":\"A\"}]}");
            var rows = ParameterValueMapping.ReadFile(new ParameterValueSource { Path = path }, out var error);
            Assert.Null(error);
            Assert.Equal(5, Assert.Single(rows).ElementId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnsupportedAndMissingFilesFail()
    {
        ParameterValueMapping.ReadFile(new ParameterValueSource { Path = @"C:\nope\missing.xlsx" }, out var missing);
        Assert.Contains("File not found", missing);
    }

    [Fact]
    public void NormalizeCollapsesRepeatsAndRefusesConflicts()
    {
        var rows = new List<ParameterValueRow>
        {
            new() { Row = 1, ElementId = 1, Value = "A" },
            new() { Row = 2, ElementId = 1, Value = "A" },          // repeat: collapsed
            new() { Row = 3, ElementId = 2, Value = "B" },
            new() { Row = 4, ElementId = 2, Value = "C" },          // conflict with row 3
            new() { Row = 5, ElementId = 2, ParameterName = "Comments", Value = "C" },   // other parameter: fine
            new() { Row = 6, Value = "D" },                         // no element
            new() { Row = 7, ElementId = -5, Value = "E" }
        };
        var problems = new List<ParameterValueProblem>();
        var usable = ParameterValueMapping.Normalize(rows, "Mark", problems, out var collapsed);

        Assert.Equal(new[] { 1, 5 }, usable.Select(r => r.Row));
        Assert.Equal(1, collapsed);
        var reasons = ParameterValueMapping.CountByReason(problems);
        Assert.Equal(2, reasons["conflict"]);
        Assert.Equal(2, reasons["invalidElementId"]);
    }

    [Fact]
    public void NormalizeNeedsAParameter()
    {
        var problems = new List<ParameterValueProblem>();
        var usable = ParameterValueMapping.Normalize(new List<ParameterValueRow> { new() { Row = 1, ElementId = 1, Value = "A" } }, "", problems, out _);
        Assert.Empty(usable);
        Assert.StartsWith("noParameter", Assert.Single(problems).Reason);
    }

    [Fact]
    public void FindsDuplicatesPerParameterAndIgnoresEmptyValues()
    {
        var duplicates = ParameterValueMapping.FindDuplicates(new (string, string, string?)[]
        {
            ("RBR-Object_ID", "1", "FI-SMD-L01-0007"),
            ("RBR-Object_ID", "2", "FI-SMD-L01-0007 "),   // trailing space still the same value
            ("RBR-Object_ID", "3", "FI-SMD-L01-0008"),
            ("Mark", "3", "FI-SMD-L01-0007"),              // another parameter: not a duplicate
            ("RBR-Object_ID", "4", ""),
            ("RBR-Object_ID", "5", null)
        });

        var d = Assert.Single(duplicates);
        Assert.Equal("FI-SMD-L01-0007", d.Value);
        Assert.Equal(new[] { "1", "2" }, d.Elements);
    }

    [Fact]
    public void ManyRowsStayFast()
    {
        var rows = Enumerable.Range(1, 6736).Select(i => new ParameterValueRow { Row = i, ElementId = 1_000_000 + i, Value = $"FI-SMD-L01-{i:0000}" }).ToList();
        var problems = new List<ParameterValueProblem>();
        var usable = ParameterValueMapping.Normalize(rows, "RBR-Object_ID", problems, out _);
        Assert.Equal(6736, usable.Count);
        Assert.Empty(ParameterValueMapping.FindDuplicates(usable.Select(r => ("RBR-Object_ID", r.ElementKey, (string?)r.Value))));
    }

    [Theory]
    [InlineData(0, ParameterValueMapping.DefaultSampleSize)]
    [InlineData(5, 5)]
    [InlineData(5000, ParameterValueMapping.MaxSampleSize)]
    public void SampleSizeIsClamped(int requested, int expected) => Assert.Equal(expected, ParameterValueMapping.ClampSample(requested));

    // ── Surface ──

    [Theory]
    [InlineData("revit_preview_set_parameter_values", "ReadOnly")]
    [InlineData("revit_set_parameter_values", "RequiresApproval")]
    public void ToolsAreInTheEditGroupWithTheRightPermissions(string tool, string permission)
    {
        Assert.Equal("edit", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.True(LongRunningTools.IsLongRunning(tool));
    }

    [Fact]
    public void TheWriteModifiesForPlansAndItsIdsAreInScope()
    {
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_set_parameter_values"));
        var args = JObject.Parse("{\"parameterName\":\"Mark\",\"values\":[{\"elementId\":6,\"value\":\"x\"},{\"elementId\":7,\"value\":\"y\"}]}")
            .Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        Assert.Equal(new long[] { 6, 7 }, PlanArgumentScope.Extract(args).ElementIds.OrderBy(i => i));
    }
}
