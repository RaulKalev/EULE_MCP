using RevitMCP.Addin.Delivery;
using Xunit;

namespace RevitMCP.Tests;

public class DeliveryFileNameParserTests
{
    [Theory]
    [InlineData("1626_TP_EL-5-01_Valgustuse_plaan.pdf", "1626", "TP", "EL-5-01", "EL", "5", "01", "Valgustuse_plaan", null, "pdf")]
    [InlineData("12345_EP_EN-2-003_Ventilatsioon.dwg", "12345", "EP", "EN-2-003", "EN", "2", "003", "Ventilatsioon", null, "dwg")]
    [InlineData("100_PP_PP-1-01.pdf", "100", "PP", "PP-1-01", "PP", "1", "01", "", null, "pdf")]
    public void Parse_ValidName_ReturnsExpected(
        string fileName,
        string projectNum, string stage, string sheetNum,
        string discipline, string group, string seq,
        string? description, string? revision, string ext)
    {
        var result = DeliveryFileNameParser.Parse(fileName);
        Assert.NotNull(result);
        Assert.Equal(projectNum, result!.ProjectNumber);
        Assert.Equal(stage, result.Stage);
        Assert.Equal(sheetNum, result.SheetNumber);
        Assert.Equal(discipline, result.Discipline);
        Assert.Equal(group, result.Group);
        Assert.Equal(seq, result.Sequence);
        Assert.Equal(description, result.Description);
        Assert.Equal(revision, result.Revision);
        Assert.Equal(ext, result.Extension);
    }

    [Theory]
    [InlineData("1626_TP_EL-5-01_Nimetus_r01.pdf", "R01")]
    [InlineData("1626_TP_EL-5-01_Nimetus_rev2.pdf", "REV2")]
    [InlineData("1626_TP_EL-5-01_Nimetus_R01.pdf", "R01")]
    public void Parse_WithRevision_ExtractsRevision(string fileName, string expectedRevision)
    {
        var result = DeliveryFileNameParser.Parse(fileName);
        Assert.NotNull(result);
        Assert.Equal(expectedRevision, result!.Revision);
    }

    [Theory]
    [InlineData("randomfile.pdf")]
    [InlineData("no_stage.pdf")]
    [InlineData("")]
    [InlineData("abc.pdf")]
    public void Parse_InvalidName_ReturnsNull(string fileName)
    {
        var result = DeliveryFileNameParser.Parse(fileName);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("1626_TP_EL-5-01_Valgustuse_plaan.pdf", true)]
    [InlineData("document.pdf", false)]
    [InlineData("1626_TP_EL-5-01.pdf", true)]
    [InlineData("randomfile.docx", false)]
    public void LooksLikeEuleDrawing_CorrectlyClassifies(string fileName, bool expected)
    {
        Assert.Equal(expected, DeliveryFileNameParser.LooksLikeEuleDrawing(fileName));
    }
}
