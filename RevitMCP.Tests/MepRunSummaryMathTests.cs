using RevitMCP.Addin.Mep;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#106: summary grouping and metre totals of revit_get_mep_run_summary.</summary>
public class MepRunSummaryMathTests
{
    private static MepSegmentInfo Tray(long id, double lengthMm, string level = "Level 1", string size = "300x60", int open = 0,
        string type = "KL-300x60", string workset = "EL") => new()
    {
        ElementId = id, Kind = MepKind.CableTray, TypeName = type, Size = size, Level = level, Workset = workset,
        LengthMm = lengthMm, EndConnectors = 2, OpenEnds = open
    };

    private static MepSegmentInfo Conduit(long id, double lengthMm, string size = "Ø25", int open = 0) => new()
    {
        ElementId = id, Kind = MepKind.Conduit, TypeName = "PVC", Size = size, Level = "Level 1", Workset = "EL",
        LengthMm = lengthMm, EndConnectors = 2, OpenEnds = open
    };

    [Fact]
    public void Totals_AreSummedInMetres()
    {
        var summary = MepRunSummaryMath.Summarize(
            new[] { Tray(1, 12000.4), Tray(2, 8000.2), Conduit(3, 2500) }, Array.Empty<MepFittingInfo>(), Array.Empty<MepOpenEndInfo>());

        Assert.Equal(3, summary.Segments);
        Assert.Equal(22500.6, summary.TotalLengthMm, 6);
        Assert.Equal(22.501, summary.TotalLengthM, 6);
        Assert.Equal(20.001, summary.TotalByKindM[MepKind.CableTray], 6);
        Assert.Equal(2.5, summary.TotalByKindM[MepKind.Conduit], 6);
    }

    [Fact]
    public void Segments_AreGroupedByTypeSizeLevelAndWorkset()
    {
        var summary = MepRunSummaryMath.Summarize(
            new[]
            {
                Tray(1, 10000), Tray(2, 5000, level: "Level 2"), Tray(3, 3000, size: "200x60", type: "KL-200x60"),
                Conduit(4, 2000), Conduit(5, 1000, size: "Ø32")
            },
            Array.Empty<MepFittingInfo>(), Array.Empty<MepOpenEndInfo>());

        var byType = summary.ByType.Single(g => g.Key == "KL-300x60");
        Assert.Equal(2, byType.Count);
        Assert.Equal(15, byType.LengthM, 6);

        var bySize = summary.BySize.Where(g => g.Kind == MepKind.Conduit).ToList();
        Assert.Equal(new[] { "Ø25", "Ø32" }, bySize.Select(g => g.Key));

        var level2 = summary.ByLevel.Single(g => g.Kind == MepKind.CableTray && g.Key == "Level 2");
        Assert.Equal(5, level2.LengthM, 6);

        Assert.Equal(2, summary.ByWorkset.Count); // one per kind
        Assert.Equal(5, summary.Groups.Count);
        // Longest first within a kind.
        Assert.Equal("KL-300x60", summary.Groups.First(g => g.Kind == MepKind.CableTray).Type);
    }

    [Fact]
    public void Fittings_AreCountedByType()
    {
        var fittings = new[]
        {
            new MepFittingInfo { ElementId = 10, Kind = MepKind.CableTray, TypeName = "Bend: Standard" },
            new MepFittingInfo { ElementId = 11, Kind = MepKind.CableTray, TypeName = "Bend: Standard" },
            new MepFittingInfo { ElementId = 12, Kind = MepKind.Conduit, TypeName = "Elbow: PVC" }
        };
        var summary = MepRunSummaryMath.Summarize(Array.Empty<MepSegmentInfo>(), fittings, Array.Empty<MepOpenEndInfo>());

        Assert.Equal(3, summary.Fittings);
        Assert.Equal(2, summary.FittingsByType.Single(g => g.Key == "Bend: Standard").Count);
    }

    [Fact]
    public void OpenEnds_AreCappedAndRounded()
    {
        var ends = Enumerable.Range(1, 520)
            .Select(i => new MepOpenEndInfo { ElementId = i, Kind = MepKind.CableTray, Position = new MepPoint(i + 0.04, 0.06, 2800.123) })
            .ToList();
        var summary = MepRunSummaryMath.Summarize(Array.Empty<MepSegmentInfo>(), Array.Empty<MepFittingInfo>(), ends);

        Assert.Equal(520, summary.OpenEndCount);
        Assert.Equal(MepRunSummaryMath.MaxListed, summary.OpenEnds.Count);
        Assert.True(summary.OpenEndsTruncated);
        Assert.Equal(1.0, summary.OpenEnds[0].Position.X, 6);
        Assert.Equal(0.1, summary.OpenEnds[0].Position.Y, 6);
        Assert.Equal(2800.1, summary.OpenEnds[0].Position.Z, 6);
    }

    [Fact]
    public void SegmentsWithEveryEndOpen_AreDisconnected()
    {
        var summary = MepRunSummaryMath.Summarize(
            new[] { Tray(1, 1000, open: 2), Tray(2, 1000, open: 1), Tray(3, 1000) },
            Array.Empty<MepFittingInfo>(), Array.Empty<MepOpenEndInfo>());

        Assert.Equal(1, summary.DisconnectedCount);
        Assert.Equal(new long[] { 1 }, summary.DisconnectedSegmentIds);
        Assert.False(summary.DisconnectedTruncated);
    }

    [Fact]
    public void SizeLabels_AreInMillimetres()
    {
        Assert.Equal("300x60", MepRunSummaryMath.SizeLabel(MepKind.CableTray, 300.0001, 60, null));
        Assert.Equal("Ø20", MepRunSummaryMath.SizeLabel(MepKind.Conduit, null, null, 20));
        Assert.Equal("Ø62.5", MepRunSummaryMath.SizeLabel(MepKind.Conduit, null, null, 62.5));
    }
}
