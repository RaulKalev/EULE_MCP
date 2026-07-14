using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using Xunit;

namespace RevitMCP.Tests;

public class IfcSpaceConventionTests
{
    [Fact]
    public void ClassificationCode_IfcSpaceTypeRuum_IsConfirmed()
    {
        var result = IfcSpaceConventionResolver.Detect(P(
            ("ClassificationCode", "[IPI ÜBN]1.21 IfcSpaceType (ruum)")));

        Assert.Equal("Confirmed", result.Confidence);
        Assert.Equal("ClassificationCodeIfcSpaceType", result.Reason);
    }

    [Fact]
    public void ArRuumPropertySet_WithKnownProperty_IsConfirmed()
    {
        var result = IfcSpaceConventionResolver.Detect(P(
            ("IfcPropertySetList", "Other; AR_Ruum"),
            ("AR_Ruum.100_Nimi", "Büroohaldus")));

        Assert.Equal("Confirmed", result.Confidence);
        Assert.Equal("AR_RuumPropertySet", result.Reason);
    }

    [Fact]
    public void IfcGuidAlone_RemainsProbable()
    {
        var result = IfcSpaceConventionResolver.Detect(P(("IfcGUID", "abc123")));

        Assert.Equal("Probable", result.Confidence);
        Assert.Equal("ProbableIfcOrigin", result.Reason);
    }

    [Fact]
    public void ArRuumNameAndNumber_TakePrecedence_AndPreserveUnicode()
    {
        var meta = IfcSpaceConventionResolver.ResolveMetadata(P(
            ("IfcName", "1022"),
            ("IfcLongName", "Fallback office"),
            ("AR_Ruum.100_Nimi", "Büroohaldus"),
            ("AR_Ruum.105_Number", "1022"),
            ("IfcDecomposes", "10.korrus"),
            ("AR_Ruum.120_Pindala", "19.828 m²")));

        Assert.Equal("Büroohaldus", meta.Name);
        Assert.Equal("AR_Ruum.100_Nimi", meta.NameSource);
        Assert.Equal("1022", meta.Number);
        Assert.Equal("AR_Ruum.105_Number", meta.NumberSource);
        Assert.Equal("10.korrus", meta.StoreyName);
        Assert.Equal("IfcDecomposes", meta.StoreySource);
        Assert.Equal(19.828, meta.AreaM2);
    }

    [Fact]
    public void MissingArRuum_FallsBackToIfcLongNameAndIfcName()
    {
        var meta = IfcSpaceConventionResolver.ResolveMetadata(P(
            ("IfcLongName", "Meeting room"), ("IfcName", "204")));

        Assert.Equal("Meeting room", meta.Name);
        Assert.Equal("IfcLongName", meta.NameSource);
        Assert.Equal("204", meta.Number);
        Assert.Equal("IfcName", meta.NumberSource);
    }

    [Fact]
    public void BlankArRuumValues_FallBack()
    {
        var meta = IfcSpaceConventionResolver.ResolveMetadata(P(
            ("AR_Ruum.100_Nimi", "  "), ("AR_Ruum.105_Number", "\t"),
            ("IfcLongName", "Office"), ("IfcName", "305")));

        Assert.Equal("Office", meta.Name);
        Assert.Equal("305", meta.Number);
    }

    [Fact]
    public void ParameterLookup_IsCaseInsensitive_ButReportsActualName()
    {
        var meta = IfcSpaceConventionResolver.ResolveMetadata(P(
            ("ar_ruum.100_nimi", "Büroohaldus"), ("IFCNAME", "1022")));

        Assert.Equal("Büroohaldus", meta.Name);
        Assert.Equal("ar_ruum.100_nimi", meta.NameSource);
        Assert.Equal("1022", meta.Number);
    }

    [Fact]
    public void ExplicitParameters_OverrideArRuumDefaults()
    {
        var options = new IfcMetadataMappingOptions
        {
            RoomNameParameter = "CustomName",
            RoomNumberParameter = "CustomNumber"
        };
        var meta = IfcSpaceConventionResolver.ResolveMetadata(P(
            ("CustomName", "Custom office"), ("CustomNumber", "C-1"),
            ("AR_Ruum.100_Nimi", "Default office"), ("AR_Ruum.105_Number", "101")), options);

        Assert.Equal("Custom office", meta.Name);
        Assert.Equal("CustomName", meta.NameSource);
        Assert.Equal("C-1", meta.Number);
        Assert.Equal("CustomNumber", meta.NumberSource);
    }

    [Fact]
    public void DryRunPolicy_NeverPermitsWrites()
    {
        Assert.False(IfcConversionPolicy.ShouldWrite(dryRun: true));
        Assert.True(IfcConversionPolicy.ShouldWrite(dryRun: false));
    }

    [Fact]
    public void DuplicateIdentity_UsesResolvedNameAndNumber()
    {
        var meta = IfcSpaceConventionResolver.ResolveMetadata(P(
            ("IfcName", "wrong"),
            ("AR_Ruum.105_Number", "1022"),
            ("AR_Ruum.100_Nimi", "Büroohaldus")));

        Assert.True(IfcConversionPolicy.IsExactRoomIdentity(
            10, meta.Number, meta.Name, 10, "1022", "Büroohaldus"));
        Assert.False(IfcConversionPolicy.IsExactRoomIdentity(
            10, meta.Number, meta.Name, 10, "1022", "1022"));
    }

    private static IReadOnlyList<KeyValuePair<string, string?>> P(
        params (string Name, string? Value)[] values) =>
        values.Select(v => new KeyValuePair<string, string?>(v.Name, v.Value)).ToList();
}
