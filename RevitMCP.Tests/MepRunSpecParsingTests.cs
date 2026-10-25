using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Mep;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#106: the arguments of revit_create_mep_run as they arrive through the pipe.</summary>
public class MepRunSpecParsingTests
{
    private static Dictionary<string, object?> Args(string json) =>
        JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);

    private static MepRunSpec Parse(string json)
    {
        var errors = new List<string>();
        var spec = MepRunSpec.Parse(Args(json), errors, out var status);
        Assert.True(spec != null, string.Join("; ", errors) + " " + status);
        return spec!;
    }

    private static (List<string> Errors, string? Status) Fail(string json)
    {
        var errors = new List<string>();
        var spec = MepRunSpec.Parse(Args(json), errors, out var status);
        Assert.Null(spec);
        return (errors, status);
    }

    [Fact]
    public void FullTrayRun_IsRead()
    {
        var spec = Parse("""
            {"kind":"cableTray","type":"KL-300x60","points":[{"x":0,"y":0,"z":2800},{"x":12000,"y":0,"z":2800}],
             "widthMm":300,"heightMm":60,"fittings":"auto","connectStart":"auto","connectEnd":{"elementId":123456},
             "snapMm":80,"minSegmentMm":150,"workset":"EL Containment","atomic":false,"level":"1. korrus"}
            """);

        Assert.Equal(MepKind.CableTray, spec.Kind);
        Assert.Equal("KL-300x60", spec.Type);
        Assert.Equal(2, spec.Points.Count);
        Assert.Equal(2800, spec.Points[1].Z);
        Assert.Equal(300, spec.WidthMm);
        Assert.Equal(60, spec.HeightMm);
        Assert.True(spec.AutoFittings);
        Assert.Equal(MepEndMode.Auto, spec.ConnectStart.Mode);
        Assert.Equal(MepEndMode.Element, spec.ConnectEnd.Mode);
        Assert.Equal(123456, spec.ConnectEnd.ElementId);
        Assert.Equal(80, spec.SnapMm);
        Assert.Equal(150, spec.MinSegmentMm);
        Assert.Equal("EL Containment", spec.Workset);
        Assert.False(spec.Atomic);
        Assert.Equal("1. korrus", spec.Level);
        Assert.Equal("300x60", spec.SizeLabel);
    }

    [Fact]
    public void Defaults_AreApplied()
    {
        var spec = Parse("""{"kind":"conduit","type":"PVC","points":[[0,0,3000],[5000,0,3000]],"diameterMm":25}""");

        Assert.Equal(MepKind.Conduit, spec.Kind);
        Assert.Equal(MepRunSpec.DefaultSnapMm, spec.SnapMm);
        Assert.Equal(MepRunSpec.DefaultMinSegmentMm, spec.MinSegmentMm);
        Assert.Equal("auto", spec.Fittings);
        Assert.True(spec.Atomic);
        Assert.Equal(MepEndMode.None, spec.ConnectStart.Mode);
        Assert.Equal(MepEndMode.None, spec.ConnectEnd.Mode);
        Assert.Equal("Ø25", spec.SizeLabel);
    }

    [Theory]
    [InlineData("tray", "cableTray")]
    [InlineData("Cable Tray", "cableTray")]
    [InlineData("cable_tray", "cableTray")]
    [InlineData("CONDUIT", "conduit")]
    public void KindSpellings_AreNormalized(string given, string expected) => Assert.Equal(expected, MepKind.Normalize(given));

    [Fact]
    public void PointsAsAJsonString_AreRead()
    {
        var args = new Dictionary<string, object?>
        {
            ["kind"] = "conduit", ["type"] = "PVC", ["points"] = "[{\"x\":0,\"y\":0,\"z\":1},{\"x\":10,\"y\":0,\"z\":1}]"
        };
        var errors = new List<string>();
        var spec = MepRunSpec.Parse(args, errors, out _);
        Assert.NotNull(spec);
        Assert.Equal(10, spec!.Points[1].X);
    }

    [Fact]
    public void MissingZ_NeedsLevelAndOffset()
    {
        var (errors, status) = Fail("""{"kind":"cableTray","type":"T","points":[{"x":0,"y":0},{"x":1000,"y":0}]}""");
        Assert.Equal(MepStatus.PolylineInvalid, status);
        Assert.Contains(errors, e => e.Contains("level and offsetMm"));

        var spec = Parse("""{"kind":"cableTray","type":"T","level":"Level 1","offsetMm":2800,"points":[{"x":0,"y":0},{"x":1000,"y":0}]}""");
        Assert.True(spec.NeedsLevelElevation);
        Assert.Equal(2800, spec.OffsetMm);
    }

    [Fact]
    public void TooFewOrBrokenPoints_ArePolylineErrors()
    {
        var (errors, status) = Fail("""{"kind":"cableTray","type":"T","points":[{"x":0,"y":0,"z":0}]}""");
        Assert.Equal(MepStatus.PolylineInvalid, status);
        Assert.Contains(errors, e => e.Contains("at least 2 points"));

        (errors, _) = Fail("""{"kind":"cableTray","type":"T","points":[{"x":0,"z":0},{"x":1,"y":1,"z":0}]}""");
        Assert.Contains(errors, e => e.StartsWith("points[0]"));
    }

    [Fact]
    public void MissingKindOrType_IsAValidationFailure()
    {
        var (errors, status) = Fail("""{"points":[[0,0,0],[1000,0,0]]}""");
        Assert.Equal(MepStatus.ValidationFailed, status);
        Assert.Contains(errors, e => e.StartsWith("kind is required"));
        Assert.Contains(errors, e => e.StartsWith("type is required"));
    }

    [Fact]
    public void SizesMustFitTheKind()
    {
        var (errors, _) = Fail("""{"kind":"cableTray","type":"T","points":[[0,0,0],[1000,0,0]],"diameterMm":25}""");
        Assert.Contains(errors, e => e.Contains("diameterMm is a conduit size"));

        (errors, _) = Fail("""{"kind":"conduit","type":"T","points":[[0,0,0],[1000,0,0]],"widthMm":300}""");
        Assert.Contains(errors, e => e.Contains("cable tray sizes"));

        (errors, _) = Fail("""{"kind":"cableTray","type":"T","points":[[0,0,0],[1000,0,0]],"widthMm":300}""");
        Assert.Contains(errors, e => e.Contains("both widthMm and heightMm"));

        (errors, _) = Fail("""{"kind":"conduit","type":"T","points":[[0,0,0],[1000,0,0]],"diameterMm":-5}""");
        Assert.Contains(errors, e => e.StartsWith("diameterMm must be"));
    }

    [Fact]
    public void NullOptionalSizes_FromTheBridge_CountAsMissing()
    {
        var spec = Parse("""{"kind":"conduit","type":"T","points":[[0,0,0],[1000,0,0]],"diameterMm":null,"widthMm":null,"offsetMm":null}""");
        Assert.Null(spec.DiameterMm);
        Assert.Null(spec.WidthMm);
        Assert.Null(spec.OffsetMm);
    }

    [Theory]
    [InlineData("\"auto\"", MepEndMode.Auto, 0)]
    [InlineData("\"none\"", MepEndMode.None, 0)]
    [InlineData("\"\"", MepEndMode.None, 0)]
    [InlineData("123", MepEndMode.Element, 123)]
    [InlineData("\"456\"", MepEndMode.Element, 456)]
    [InlineData("{\"elementId\":789}", MepEndMode.Element, 789)]
    [InlineData("\"{\\\"elementId\\\":42}\"", MepEndMode.Element, 42)]
    [InlineData("{\"elementId\":\"auto\"}", MepEndMode.Auto, 0)]
    public void EndConnections_AcceptEveryForm(string json, MepEndMode mode, long id)
    {
        var spec = Parse($$"""{"kind":"conduit","type":"T","points":[[0,0,0],[1000,0,0]],"connectEnd":{{json}}}""");
        Assert.Equal(mode, spec.ConnectEnd.Mode);
        Assert.Equal(id, spec.ConnectEnd.ElementId);
    }

    [Fact]
    public void UnknownEndConnection_IsRejected()
    {
        var (errors, _) = Fail("""{"kind":"conduit","type":"T","points":[[0,0,0],[1000,0,0]],"connectStart":"nearest"}""");
        Assert.Contains(errors, e => e.StartsWith("connectStart 'nearest'"));
    }

    [Fact]
    public void BadFittingsAndSnap_AreRejected()
    {
        var (errors, _) = Fail("""{"kind":"conduit","type":"T","points":[[0,0,0],[1000,0,0]],"fittings":"tees","snapMm":0}""");
        Assert.Contains(errors, e => e.StartsWith("fittings must be"));
        Assert.Contains(errors, e => e.StartsWith("snapMm must be"));
    }
}
