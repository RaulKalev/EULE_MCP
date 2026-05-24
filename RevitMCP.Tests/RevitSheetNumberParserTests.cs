using RevitMCP.Addin.Delivery;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Tests for RevitSheetNumberParser — Fix 1 validation.
/// </summary>
public class RevitSheetNumberParserTests
{
    // ── Full format (with project number and stage) ────────────────────────────

    [Fact]
    public void Parse_FullFormat_ReturnsAllParts()
    {
        var result = RevitSheetNumberParser.Parse("1626_TP_EL-5-01");

        Assert.NotNull(result);
        Assert.Equal("1626", result.ProjectNumber);
        Assert.Equal("TP", result.Stage);
        Assert.Equal("EL", result.Discipline);
        Assert.Equal("5", result.Group);
        Assert.Equal("01", result.Sequence);
        Assert.Equal("EL-5-01", result.SheetNumberCore);
    }

    [Fact]
    public void Parse_FullFormat_LongStage_ReturnsAllParts()
    {
        var result = RevitSheetNumberParser.Parse("2530_PP_EN-1-001");

        Assert.NotNull(result);
        Assert.Equal("2530", result.ProjectNumber);
        Assert.Equal("PP", result.Stage);
        Assert.Equal("EN", result.Discipline);
        Assert.Equal("1", result.Group);
        Assert.Equal("001", result.Sequence);
        Assert.Equal("EN-1-001", result.SheetNumberCore);
    }

    [Fact]
    public void Parse_FullFormat_LongProjectNumber_ReturnsAllParts()
    {
        var result = RevitSheetNumberParser.Parse("12345678_TP_EA-3-05");

        Assert.NotNull(result);
        Assert.Equal("12345678", result.ProjectNumber);
        Assert.Equal("TP", result.Stage);
        Assert.Equal("EA", result.Discipline);
        Assert.Equal("EA-3-05", result.SheetNumberCore);
    }

    // ── Short format (discipline-group-sequence only) ──────────────────────────

    [Fact]
    public void Parse_ShortFormat_ReturnsCoreParts()
    {
        var result = RevitSheetNumberParser.Parse("EL-5-01");

        Assert.NotNull(result);
        Assert.Null(result.ProjectNumber);
        Assert.Null(result.Stage);
        Assert.Equal("EL", result.Discipline);
        Assert.Equal("5", result.Group);
        Assert.Equal("01", result.Sequence);
        Assert.Equal("EL-5-01", result.SheetNumberCore);
    }

    [Fact]
    public void Parse_ShortFormat_ThreeLetterDiscipline_ReturnsCoreParts()
    {
        var result = RevitSheetNumberParser.Parse("ARC-1-001");

        Assert.NotNull(result);
        Assert.Null(result.ProjectNumber);
        Assert.Equal("ARC", result.Discipline);
        Assert.Equal("ARC-1-001", result.SheetNumberCore);
    }

    // ── Core is extracted correctly for full format ────────────────────────────

    [Fact]
    public void Parse_FullFormat_SheetNumberCore_MatchesDeliveryFileNameSheetNumber()
    {
        // This is the key fix — the core matches what DeliveryFileNameParser returns
        // for the same drawing, enabling correct comparison
        var revit = RevitSheetNumberParser.Parse("1626_TP_EL-5-01");
        var delivery = DeliveryFileNameParser.Parse("1626_TP_EL-5-01_Valgustuse_plaan.pdf");

        Assert.NotNull(revit);
        Assert.NotNull(delivery);
        Assert.Equal(revit.SheetNumberCore, delivery.SheetNumber);
    }

    // ── Unrecognised formats return null ──────────────────────────────────────

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
        => Assert.Null(RevitSheetNumberParser.Parse(string.Empty));

    [Fact]
    public void Parse_Null_ReturnsNull()
        => Assert.Null(RevitSheetNumberParser.Parse(null!));

    [Fact]
    public void Parse_RandomString_ReturnsNull()
        => Assert.Null(RevitSheetNumberParser.Parse("SHEET-001"));

    [Fact]
    public void Parse_PartialFullFormat_ReturnsNull()
        => Assert.Null(RevitSheetNumberParser.Parse("1626_TP_EL"));

    [Fact]
    public void Parse_ExtraUnderscoreSegment_ReturnsNull()
        => Assert.Null(RevitSheetNumberParser.Parse("1626_TP_EL-5-01_extra_segment"));

    // ── Discipline filter now works correctly ──────────────────────────────────

    [Fact]
    public void Parse_FullFormat_DisciplineIsTwoLetterCode_NotPrefixed()
    {
        // Bug was: 1626_TP_EL-5-01 → discipline = "1626_TP_EL"  (wrong)
        //        Should be:                discipline = "EL"        (correct)
        var result = RevitSheetNumberParser.Parse("1626_TP_EL-5-01");

        Assert.Equal("EL", result?.Discipline);
        Assert.NotEqual("1626_TP_EL", result?.Discipline);
    }
}
