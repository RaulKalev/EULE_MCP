using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.Export.Sheets;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.Services;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#105: the EliteSheets merge rule, export file names and the export plan.</summary>
public class SheetMergeGroupingTests
{
    private static ExportSheetInfo S(long id, string number, string name = "Plaan", string rev = "") =>
        new() { Id = id, SheetNumber = number, SheetName = name, Revision = rev };

    [Fact]
    public void GroupsSheetsByTheSevenGroupMarkerAndOrdersByMergeOrder()
    {
        var p = SheetMergeGrouping.Partition(new[]
        {
            S(1, "1626_TP_EL-5-01_Valgustus"),
            S(3, "1626_TP_EL-7-05_Kilbi skeem--2"),
            S(2, "1626_TP_EL-7-05_Kilbi skeem--1"),
            S(4, "1626_TP_EL-7-06_Peakilp--1")
        });

        Assert.Equal(new long[] { 1 }, p.Singles.Select(s => s.Id));
        Assert.Equal(2, p.Groups.Count);
        var g = p.Groups[0];
        Assert.Equal("05", g.GroupKey);
        Assert.Equal("1626_TP_EL-7-05_Kilbi skeem", g.BaseName);
        Assert.Equal(new long[] { 2, 3 }, g.Sheets.Select(s => s.Id));
        Assert.Equal("1626_TP_EL-7-06_Peakilp", p.Groups[1].BaseName);
    }

    [Theory]
    [InlineData("1626_TP_EL-7-05_Skeem", false)]          // no merge order
    [InlineData("1626_TP_EL-5-05_Plaan--1", false)]       // merge order but not group 7
    [InlineData("1626_TP_EL-7-05_Skeem--1", true)]
    [InlineData("1626_TP_EL–7–05_Skeem-- 2", true)]       // en dashes and a space, as EliteSheets accepts
    public void MergePartDetection(string number, bool expected) =>
        Assert.Equal(expected, SheetMergeGrouping.IsMergePart(number));

    [Fact]
    public void MergeOffExportsEverySheetAlone()
    {
        var p = SheetMergeGrouping.Partition(new[] { S(1, "1626_TP_EL-7-05_A--1"), S(2, "1626_TP_EL-7-05_A--2") }, merge: false);
        Assert.Empty(p.Groups);
        Assert.Equal(2, p.Singles.Count);
    }

    [Fact]
    public void CombinedNameDropsInvalidCharactersAndFallsBack()
    {
        Assert.Equal("1626_TP_EL-7-05_KilpAB", SheetMergeGrouping.BuildCombinedFileName("1626_TP_EL-7-05_Kilp/A:B--1", "05"));
        Assert.Equal("Group-09", SheetMergeGrouping.BuildCombinedFileName("", "09"));
    }
}

public class ExportFileNamerTests
{
    [Fact]
    public void DefaultNameIsTheSheetNumberAsInEliteSheets()
    {
        var naming = ExportNaming.Parse(null, false, out _)!;
        Assert.Equal("1626_TP_EL-5-01_Valgustus", ExportFileNamer.BuildStem(naming, "1626_TP_EL-5-01_Valgustus", "Valgustuse plaan", "02"));
    }

    [Theory]
    [InlineData("02", "r02")]
    [InlineData("2", "r02")]
    [InlineData("R03", "r03")]
    [InlineData("rev4", "r04")]
    [InlineData("A", "rA")]
    [InlineData("", "")]
    [InlineData("-", "")]
    public void RevisionToken(string revision, string expected) => Assert.Equal(expected, ExportFileNamer.FormatRevision(revision));

    [Fact]
    public void IncludeRevisionAppendsTheTokenAndRoundTripsThroughTheDeliveryParser()
    {
        var naming = ExportNaming.Parse("sheetNumber", includeRevision: true, out _)!;
        var stem = ExportFileNamer.BuildStem(naming, "1626_TP_EL-5-01_Valgustus", "x", "2");
        Assert.Equal("1626_TP_EL-5-01_Valgustus_r02", stem);
        var parsed = DeliveryFileNameParser.Parse(stem + ".pdf")!;
        Assert.Equal("EL-5-01", parsed.SheetNumber);
        Assert.Equal("R02", parsed.Revision);
        Assert.Null(ExportFileNamer.CheckDeliveryRoundTrip(stem + ".pdf", "1626_TP_EL-5-01_Valgustus"));
    }

    [Fact]
    public void SheetNumberAndNameAndPattern()
    {
        var both = ExportNaming.Parse("sheetNumberAndName", false, out _)!;
        Assert.Equal("1626_TP_EL-5-01_Valgustuse plaan", ExportFileNamer.BuildStem(both, "1626_TP_EL-5-01", "Valgustuse plaan", ""));

        var pattern = ExportNaming.Parse("pattern:{SheetNumber}_{SheetName}_{Revision}", false, out _)!;
        Assert.Equal("1626_TP_EL-5-01_Plaan_r01", ExportFileNamer.BuildStem(pattern, "1626_TP_EL-5-01", "Plaan", "1"));
        // An empty revision leaves no dangling underscore.
        Assert.Equal("1626_TP_EL-5-01_Plaan", ExportFileNamer.BuildStem(pattern, "1626_TP_EL-5-01", "Plaan", ""));
    }

    [Theory]
    [InlineData("pattern:{Sheet}", "unknown")]
    [InlineData("pattern:", "tokens")]
    [InlineData("company2", "Unknown naming")]
    public void BadNamingIsRefused(string raw, string fragment)
    {
        Assert.Null(ExportNaming.Parse(raw, false, out var error));
        Assert.Contains(fragment, error);
    }

    [Fact]
    public void SanitizeKeepsEstonianLettersAndReplacesWindowsInvalidCharacters()
    {
        Assert.Equal("Õueala_Ülevaade_ plaan", ExportFileNamer.Sanitize("Õueala/Ülevaade: plaan."));
        Assert.True(ExportFileNamer.Sanitize(new string('x', 300)).Length <= ExportFileNamer.MaxFileNameLength - 5);
        Assert.Equal("Export", ExportFileNamer.Sanitize("  ..  "));
    }

    [Fact]
    public void NonCompanyNumberGetsTheWarning()
    {
        Assert.NotNull(ExportFileNamer.CheckDeliveryRoundTrip("A101.pdf", "A101"));
    }
}

public class ExportPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 14, 30, 15);

    private static ExportSheetInfo S(long id, string number, bool listed = true, bool titleBlock = true) =>
        new() { Id = id, SheetNumber = number, SheetName = "Nimi", AppearsInSheetList = listed, HasTitleBlock = titleBlock };

    private static ExportPlanOptions Options(params string[] formats) => new()
    {
        Formats = formats.ToList(),
        PdfFolder = "pdf",
        CadFolder = "cad",
        ModelFolder = "cad",
        Naming = ExportNaming.Parse(null, false, out _)!
    };

    private static ExportPlan Plan(IReadOnlyList<ExportSheetInfo> sheets, ExportPlanOptions o, params string[] existing) =>
        ExportPlanner.Plan(sheets, o, p => existing.Contains(p.Replace('\\', '/')), () => Now);

    [Fact]
    public void SinglesAndMergedGroupsPerFormat()
    {
        var sheets = new[] { S(1, "1626_TP_EL-5-01_Plaan"), S(2, "1626_TP_EL-7-05_Skeem--1"), S(3, "1626_TP_EL-7-05_Skeem--2") };
        var plan = Plan(sheets, Options("pdf", "dwg"));

        Assert.Null(plan.Error);
        Assert.Equal(new[]
        {
            "pdf/1626_TP_EL-5-01_Plaan.pdf", "pdf/1626_TP_EL-7-05_Skeem.pdf",
            "cad/1626_TP_EL-5-01_Plaan.dwg", "cad/1626_TP_EL-7-05_Skeem.dwg"
        }, plan.Files.Select(f => f.Path.Replace('\\', '/')));
        var merged = plan.Files.Single(f => f.Format == "pdf" && f.Kind == "merged");
        Assert.Equal(new long[] { 2, 3 }, merged.SheetIds);
        Assert.All(plan.Files, f => Assert.Null(f.NameWarning));
    }

    [Fact]
    public void MergedDwgWithoutConverterIsDeliveredAsDxfOnceEvenWhenDxfIsAlsoRequested()
    {
        var sheets = new[] { S(2, "1626_TP_EL-7-05_Skeem--1"), S(3, "1626_TP_EL-7-05_Skeem--2") };
        var o = new ExportPlanOptions
        {
            Formats = new List<string> { "dwg", "dxf" }, CadFolder = "cad", PdfFolder = "pdf",
            MergedDwgExtension = "dxf", Naming = ExportNaming.Parse(null, false, out _)!
        };
        var plan = Plan(sheets, o);
        var file = Assert.Single(plan.Files);
        Assert.Equal("1626_TP_EL-7-05_Skeem.dxf", file.FileName);
        Assert.Contains(plan.Warnings, w => w.Contains("delivered as DXF"));
    }

    [Fact]
    public void ExistingFileGetsATimestampUnlessOverwrite()
    {
        var sheets = new[] { S(1, "1626_TP_EL-5-01_Plaan") };
        var kept = Plan(sheets, Options("pdf"), "pdf/1626_TP_EL-5-01_Plaan.pdf");
        Assert.Equal("1626_TP_EL-5-01_Plaan_2026-11-05_143015.pdf", kept.Files[0].FileName);
        Assert.Equal("timestamped", kept.Files[0].Disposition);

        var o = Options("pdf");
        var overwrite = Plan(sheets, new ExportPlanOptions { Formats = o.Formats, PdfFolder = "pdf", Overwrite = true, Naming = o.Naming },
            "pdf/1626_TP_EL-5-01_Plaan.pdf");
        Assert.Equal("1626_TP_EL-5-01_Plaan.pdf", overwrite.Files[0].FileName);
        Assert.Equal("overwrite", overwrite.Files[0].Disposition);
    }

    [Fact]
    public void BatchCollisionsGetACounter()
    {
        var sheets = new[] { S(1, "EL-5-01"), S(2, "EL-5-01") };
        var plan = Plan(sheets, Options("pdf"));
        Assert.Equal(new[] { "EL-5-01.pdf", "EL-5-01_2.pdf" }, plan.Files.Select(f => f.FileName));
    }

    [Fact]
    public void UnlistedAndTitleBlockLessSheetsAreSkippedUnlessIncluded()
    {
        var sheets = new[] { S(1, "1626_TP_EL-5-01_A"), S(2, "1626_TP_EL-5-02_B", listed: false), S(3, "1626_TP_EL-5-03_C", titleBlock: false) };
        var plan = Plan(sheets, Options("pdf"));
        Assert.Single(plan.Files);
        Assert.Equal(new long[] { 2, 3 }, plan.Skipped.Select(s => s.SheetId));

        var o = Options("pdf");
        var all = Plan(sheets, new ExportPlanOptions { Formats = o.Formats, PdfFolder = "pdf", IncludeUnlisted = true, Naming = o.Naming });
        Assert.Equal(3, all.Files.Count);
    }

    [Fact]
    public void TooManySheetsIsAnError()
    {
        var sheets = Enumerable.Range(1, 3).Select(i => S(i, $"EL-5-0{i}")).ToList();
        var o = Options("pdf");
        var plan = Plan(sheets, new ExportPlanOptions { Formats = o.Formats, PdfFolder = "pdf", MaxSheets = 2, Naming = o.Naming });
        Assert.NotNull(plan.Error);
    }

    [Fact]
    public void IfcNeedsNoSheets()
    {
        var o = Options("ifc");
        var plan = Plan(Array.Empty<ExportSheetInfo>(), new ExportPlanOptions
        {
            Formats = o.Formats, ModelFolder = "cad", ModelFileStem = "1626_EL_mudel", Naming = o.Naming
        });
        Assert.Equal(new[] { "1626_EL_mudel.ifc" }, plan.Files.Select(f => f.FileName));
        Assert.All(plan.Files, f => Assert.Equal("model", f.Kind));
    }

    [Fact]
    public void NonCompanyNumbersAreFlagged()
    {
        var plan = Plan(new[] { S(1, "A101") }, Options("pdf"));
        Assert.NotNull(plan.Files[0].NameWarning);
        Assert.Contains(plan.Warnings, w => w.StartsWith("sheet_number_not_company_pattern"));
    }

    [Fact]
    public void ExportedSetPassesTheDeliveryComparer()
    {
        var sheets = new[] { S(1, "1626_TP_EL-5-01_Plaan"), S(2, "1626_TP_EL-7-05_Skeem--1"), S(3, "1626_TP_EL-7-05_Skeem--2") };
        var plan = Plan(sheets, Options("pdf", "dwg"));
        var scan = new DeliveryScanResult
        {
            Success = true,
            Files = plan.Files.Select(f => new DeliveryFileEntry
            {
                FileName = f.FileName, Extension = f.Extension, FullPath = f.Path, SizeBytes = 100_000,
                Parsed = DeliveryFileNameParser.Parse(f.FileName)
            }).ToList()
        };
        var descriptors = sheets.Select(s => RevitSheetDescriptor.FromSheet(s.SheetNumber, s.SheetName)).ToList();
        var issues = DeliveryRevitSheetComparer.Compare(scan, descriptors, new[] { "pdf", "dwg" }, Array.Empty<string>(), Array.Empty<string>(), "T");
        Assert.Empty(issues);
    }

    [Fact]
    public void FormatsParseCanonicallyAndReportUnknown()
    {
        Assert.Equal(new[] { "pdf", "dwg", "ifc" }, ExportFormats.Parse(new[] { "IFC", ".dwg", "pdf", "pdf" }, out var unknown));
        Assert.Empty(unknown);
        ExportFormats.Parse(new[] { "dgn", "nwc" }, out unknown);
        Assert.Equal(new[] { "dgn", "nwc" }, unknown);
    }
}

public class EliteSheetsSettingsTests
{
    private const string Json = """
    {
      "IsDarkMode": true,
      "TemplateDxfPath": "C:\\Templates\\EULE.dxf",
      "ConvertMergedToDwg": true,
      "ExportPaths": { "1626_EL.rvt": "C:\\Projects\\1626\\Valja" },
      "PdfExportPaths": { "1626_EL.rvt": "C:\\Projects\\1626\\PDF" },
      "DwgExportPaths": { "1626_EL.rvt": "C:\\Projects\\1626\\DWG" },
      "SeparateExportFolders": { "1626_EL.rvt": "true" }
    }
    """;

    [Fact]
    public void ReadsTemplateConversionAndProjectFolders()
    {
        var s = EliteSheetsSettings.Parse(Json, "1626_el.RVT");
        Assert.True(s.Found);
        Assert.Equal(@"C:\Templates\EULE.dxf", s.TemplateDxfPath);
        Assert.True(s.ConvertMergedToDwg);
        Assert.Equal(@"C:\Projects\1626\PDF", s.PdfFolder);
        Assert.Equal(@"C:\Projects\1626\DWG", s.CadFolder);
    }

    [Fact]
    public void SharedFolderWhenNotSeparateAndNothingForOtherProjects()
    {
        var shared = EliteSheetsSettings.Parse(Json.Replace("\"true\"", "\"false\""), "1626_EL.rvt");
        Assert.Equal(@"C:\Projects\1626\Valja", shared.PdfFolder);
        Assert.Equal(@"C:\Projects\1626\Valja", shared.CadFolder);

        var other = EliteSheetsSettings.Parse(Json, "9999.rvt");
        Assert.Equal(string.Empty, other.PdfFolder);
    }

    [Fact]
    public void BrokenJsonIsReportedNotThrown()
    {
        var s = EliteSheetsSettings.Parse("{ not json", "x.rvt");
        Assert.NotNull(s.Error);
    }
}

public class DeliveryParserLetterGroupTests
{
    [Fact]
    public void LetterGroupsParseInFileNamesAndSheetNumbers()
    {
        var file = DeliveryFileNameParser.Parse("1626_TP_EL-A-001_Plaan.pdf")!;
        Assert.Equal("EL-A-001", file.SheetNumber);
        Assert.Equal("A", file.Group);

        Assert.Equal("EL-A-001", RevitSheetNumberParser.Parse("1626_TP_EL-A-001")!.SheetNumberCore);
        Assert.Equal("EL-A-001", RevitSheetNumberParser.Parse("EL-A-001_Plaan")!.SheetNumberCore);
    }
}

public class SheetExportSurfaceTests
{
    private static readonly string[] Tools = { "revit_list_export_setups", "revit_preview_export", "revit_export", "revit_export_and_check" };

    [Fact]
    public void EveryToolIsInTheOutputGroup()
    {
        foreach (var tool in Tools) Assert.Equal("output", BridgeToolGroups.GroupOf(tool));
    }

    [Theory]
    [InlineData("revit_list_export_setups", "ReadOnly")]
    [InlineData("revit_preview_export", "ReadOnly")]
    [InlineData("revit_export", "RequiresApproval")]
    [InlineData("revit_export_and_check", "RequiresApproval")]
    public void Permissions(string tool, string permission)
    {
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Fact]
    public void ExportsAreLongRunningAndModifyNothingInAPlan()
    {
        Assert.True(LongRunningTools.IsLongRunning("revit_export"));
        Assert.True(LongRunningTools.IsLongRunning("revit_export_and_check"));
        Assert.False(LongRunningTools.IsLongRunning("revit_preview_export"));
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_export"));
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_export_and_check"));
    }
}
