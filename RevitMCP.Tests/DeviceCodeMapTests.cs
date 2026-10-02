using System.Text.Json.Nodes;
using RevitMCP.Addin.RoomDevices;
using Xunit;

namespace RevitMCP.Tests;

public class DeviceCodeMapTests
{
    private const string Sample = """
    {
      "ATS_SA":  { "family": "Suitsuandur", "type": "Optiline", "mount": "ceiling", "offsetFromCeilingMm": 0,
                   "avoidCategories": ["Lighting Fixtures", "Air Terminals"], "maxSpacingMm": 10600, "maxDistFromWallMm": 7500 },
      "ANDM_2x": { "family": "Andmesidepesa", "type": "2xRJ45", "mount": "wall", "heightMm": 300 },
      "LPS_LUG": { "family": "LPS lugeja", "type": "Standard", "mount": "wall", "heightMm": 1100, "doorSide": "lock", "doorOffsetMm": 150,
                   "sourceType": "LPS lugeja : Default", "typeParameters": { "Type Comments": "LPS_LUG" } }
    }
    """;

    [Fact]
    public void Parse_ReadsAllFields()
    {
        var errors = new List<string>();
        var map = DeviceCodeMap.Parse(JsonNode.Parse(Sample) as JsonObject, errors);

        Assert.Empty(errors);
        Assert.Equal(3, map.Count);

        var sa = map["ats_sa"];
        Assert.True(sa.IsCeiling);
        Assert.Equal(["Lighting Fixtures", "Air Terminals"], sa.AvoidCategories);
        Assert.Equal(7500, sa.MaxDistFromWallMm);

        var lps = map["LPS_LUG"];
        Assert.Equal(1100, lps.HeightMm);
        Assert.Equal("LPS_LUG", lps.TypeParameters["type comments"]);
        Assert.Equal(("LPS lugeja", "Default"), lps.SourceFamilyAndType());
    }

    [Fact]
    public void Parse_RejectsInvalidEntries_AndKeepsValidOnes()
    {
        var json = JsonNode.Parse("""
        {
          "OK":    { "family": "F", "type": "T", "mount": "ceiling" },
          "NOFAM": { "type": "T", "mount": "ceiling" },
          "WALL":  { "family": "F", "type": "T", "mount": "wall" },
          "MOUNT": { "family": "F", "type": "T", "mount": "roof" },
          "NEG":   { "family": "F", "type": "T", "mount": "floor", "heightMm": -5 },
          "SIDE":  { "family": "F", "type": "T", "mount": "ceiling", "doorSide": "left" }
        }
        """) as JsonObject;
        var errors = new List<string>();
        var map = DeviceCodeMap.Parse(json, errors);

        Assert.Equal(["OK"], map.Keys);
        Assert.Contains(errors, e => e.StartsWith("NOFAM") && e.Contains("family"));
        Assert.Contains(errors, e => e.StartsWith("WALL") && e.Contains("heightMm"));
        Assert.Contains(errors, e => e.StartsWith("MOUNT"));
        Assert.Contains(errors, e => e.StartsWith("NEG"));
        Assert.Contains(errors, e => e.StartsWith("SIDE"));
    }

    [Fact]
    public void Parse_ReadsCoverageFields_AndRejectsBadFov()
    {
        var json = JsonNode.Parse("""
        {
          "CAM": { "family": "Kaamera", "type": "Dome", "mount": "ceiling", "fovDeg": 90, "rangeM": 15 },
          "AP":  { "family": "WiFi AP", "type": "Lagi", "mount": "ceiling", "coverageRadiusMm": 12000 },
          "BAD": { "family": "Kaamera", "type": "Dome", "mount": "ceiling", "fovDeg": 400 }
        }
        """) as JsonObject;
        var errors = new List<string>();
        var map = DeviceCodeMap.Parse(json, errors);
        Assert.Equal(90, map["CAM"].FovDeg);
        Assert.Equal(15, map["CAM"].RangeM);
        Assert.Equal(12000, map["AP"].CoverageRadiusMm);
        Assert.False(map.ContainsKey("BAD"));
        Assert.Contains(errors, e => e.StartsWith("BAD") && e.Contains("fovDeg"));
    }

    [Fact]
    public void Parse_ReadsFireAlarmFields()
    {
        var json = JsonNode.Parse("""
        {
          "ATS_SA":  { "family": "F", "type": "T", "mount": "ceiling", "detectorType": "PointSmoke" },
          "ATS_TA":  { "family": "F", "type": "T", "mount": "ceiling", "detectorType": "pointHeat", "detectorClass": "A1" },
          "ATS_SIR": { "family": "F", "type": "T", "mount": "wall", "heightMm": 2400, "detectorType": "sounder", "soundLevelDb": 97, "tone": "EN54-3 slow whoop" },
          "NO_DB":   { "family": "F", "type": "T", "mount": "wall", "heightMm": 2400, "detectorType": "sounder" },
          "BADTYPE": { "family": "F", "type": "T", "mount": "ceiling", "detectorType": "laser" }
        }
        """) as JsonObject;
        var errors = new List<string>();
        var map = DeviceCodeMap.Parse(json, errors);

        Assert.Equal("pointSmoke", map["ATS_SA"].DetectorType);
        Assert.True(map["ATS_SA"].IsFireDetector);
        Assert.Equal("A1", map["ATS_TA"].DetectorClass);
        Assert.True(map["ATS_SIR"].IsSounder);
        Assert.Equal(97, map["ATS_SIR"].SoundLevelDb);
        Assert.Contains(errors, e => e.StartsWith("NO_DB") && e.Contains("soundLevelDb"));
        Assert.Contains(errors, e => e.StartsWith("BADTYPE") && e.Contains("detectorType"));
    }

    [Fact]
    public void SourceFamilyAndType_BareTypeUsesOwnFamily()
    {
        var code = new DeviceCode { Family = "Kaamera", SourceType = "Dome" };
        Assert.Equal(("Kaamera", "Dome"), code.SourceFamilyAndType());
    }

    [Fact]
    public void Merge_ReplacesEntriesCaseInsensitively_AndRemoves()
    {
        var existing = JsonNode.Parse("""{ "a": {"family":"F","type":"1"}, "B": {"family":"F","type":"2"} }""") as JsonObject;
        var updates = JsonNode.Parse("""{ "A": {"family":"F","type":"9"}, "C": {"family":"F","type":"3"} }""") as JsonObject;

        var merged = DeviceCodeMap.Merge(existing, updates!, ["b"], replace: false);

        Assert.Equal(["A", "C"], merged.Select(p => p.Key).OrderBy(k => k));
        Assert.Equal("9", merged["A"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_Replace_DiscardsExisting()
    {
        var existing = JsonNode.Parse("""{ "a": {"family":"F","type":"1"} }""") as JsonObject;
        var updates = JsonNode.Parse("""{ "C": {"family":"F","type":"3"} }""") as JsonObject;
        Assert.Equal(["C"], DeviceCodeMap.Merge(existing, updates!, [], replace: true).Select(p => p.Key));
    }
}
