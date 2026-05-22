using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Geometry;
using RevitMCP.Addin.Coordination.Clash.Services;
using Xunit;

namespace RevitMCP.Tests;

public class ClashDetectionTests
{
    // ── ClashBoundingBoxMath ──────────────────────────────────────────────────

    [Fact]
    public void ClashBoundingBoxMath_Overlaps_DetectsOverlappingBoxes()
    {
        // Two overlapping boxes (1-3 range vs 2-4 range on all axes)
        var overlaps = ClashBoundingBoxMath.Overlaps(
            1, 1, 1, 3, 3, 3,
            2, 2, 2, 4, 4, 4);
        Assert.True(overlaps);
    }

    [Fact]
    public void ClashBoundingBoxMath_Overlaps_RejectsNonOverlapping()
    {
        // Boxes separated on X axis
        var overlaps = ClashBoundingBoxMath.Overlaps(
            0, 0, 0, 1, 1, 1,
            2, 0, 0, 3, 1, 1);
        Assert.False(overlaps);
    }

    [Fact]
    public void ClashBoundingBoxMath_Overlaps_TouchingEdgeIsOverlapping()
    {
        // Boxes touch exactly at x=1
        var overlaps = ClashBoundingBoxMath.Overlaps(
            0, 0, 0, 1, 1, 1,
            1, 0, 0, 2, 1, 1);
        Assert.True(overlaps);
    }

    [Fact]
    public void ClashBoundingBoxMath_Expand_IncreasesAllSides()
    {
        var (mnX, mnY, mnZ, mxX, mxY, mxZ) = ClashBoundingBoxMath.Expand(0, 0, 0, 1, 1, 1, 0.5);
        Assert.Equal(-0.5, mnX, 6);
        Assert.Equal(-0.5, mnY, 6);
        Assert.Equal(-0.5, mnZ, 6);
        Assert.Equal(1.5, mxX, 6);
        Assert.Equal(1.5, mxY, 6);
        Assert.Equal(1.5, mxZ, 6);
    }

    [Fact]
    public void ClashBoundingBoxMath_ApproximateDistance_GapAlongX()
    {
        // Box A ends at x=1, Box B starts at x=3 — gap of 2 units
        var dist = ClashBoundingBoxMath.ApproximateDistance(
            0, 0, 0, 1, 1, 1,
            3, 0, 0, 4, 1, 1);
        Assert.Equal(2.0, dist, 6);
    }

    [Fact]
    public void ClashBoundingBoxMath_ApproximateDistance_OverlappingBoxesIsZero()
    {
        var dist = ClashBoundingBoxMath.ApproximateDistance(
            0, 0, 0, 2, 2, 2,
            1, 1, 1, 3, 3, 3);
        Assert.Equal(0.0, dist, 6);
    }

    // ── ClashSummaryService ───────────────────────────────────────────────────

    private static List<ClashResultDto> MakeSampleClashes()
    {
        return
        [
            new ClashResultDto
            {
                ClashId = "CL-0001", RuleName = "Rule A", ClashType = "HardClash",
                Severity = "High", Status = "New", Level = "Level 1",
                Source = new ClashElementRefDto { Category = "Cable Trays", Model = "Host" },
                Target = new ClashElementRefDto { Category = "Ducts", Model = "Host" }
            },
            new ClashResultDto
            {
                ClashId = "CL-0002", RuleName = "Rule A", ClashType = "HardClash",
                Severity = "Medium", Status = "New", Level = "Level 2",
                Source = new ClashElementRefDto { Category = "Conduits", Model = "Host" },
                Target = new ClashElementRefDto { Category = "Pipes", Model = "LinkA", LinkName = "LinkA" }
            },
            new ClashResultDto
            {
                ClashId = "CL-0003", RuleName = "Rule B", ClashType = "Clearance",
                Severity = "High", Status = "Resolved", Level = "Level 1",
                Source = new ClashElementRefDto { Category = "Cable Trays", Model = "Host" },
                Target = new ClashElementRefDto { Category = "Pipes", Model = "Host" }
            }
        ];
    }

    [Fact]
    public void ClashSummaryService_GroupByRule_CorrectCounts()
    {
        var clashes = MakeSampleClashes();
        var summary = ClashSummaryService.BuildSummary(clashes, ["Rule"]);

        Assert.True(summary.ContainsKey("byRule"));
        var byRule = (Dictionary<string, object>)summary["byRule"];
        Assert.Equal(2, (int)(object)byRule["Rule A"]);
        Assert.Equal(1, (int)(object)byRule["Rule B"]);
    }

    [Fact]
    public void ClashSummaryService_GroupByLevel_CorrectCounts()
    {
        var clashes = MakeSampleClashes();
        var summary = ClashSummaryService.BuildSummary(clashes, ["Level"]);

        Assert.True(summary.ContainsKey("byLevel"));
        var byLevel = (Dictionary<string, object>)summary["byLevel"];
        Assert.Equal(2, (int)(object)byLevel["Level 1"]);
        Assert.Equal(1, (int)(object)byLevel["Level 2"]);
    }

    [Fact]
    public void ClashSummaryService_TotalClashes_IsCorrect()
    {
        var clashes = MakeSampleClashes();
        var summary = ClashSummaryService.BuildSummary(clashes);
        Assert.Equal(3, (int)(object)summary["totalClashes"]);
    }

    [Fact]
    public void ClashSummaryService_ByStatus_GroupsCorrectly()
    {
        var clashes = MakeSampleClashes();
        var summary = ClashSummaryService.BuildSummary(clashes);
        var byStatus = (Dictionary<string, object>)summary["byStatus"];
        Assert.Equal(2, (int)(object)byStatus["New"]);
        Assert.Equal(1, (int)(object)byStatus["Resolved"]);
    }

    // ── ClashPresetService ────────────────────────────────────────────────────

    [Fact]
    public void ClashPresetService_GetDefaults_ReturnsTwoPresets()
    {
        var defaults = ClashPresetService.GetDefaultPresets();
        Assert.Equal(2, defaults.Count);
    }

    [Fact]
    public void ClashPresetService_GetDefaults_DefaultsPassValidation()
    {
        var svc = new ClashPresetService();
        var defaults = ClashPresetService.GetDefaultPresets();
        foreach (var preset in defaults)
        {
            var (valid, errors) = svc.Validate(preset);
            Assert.True(valid, $"Preset '{preset.Name}' failed: {string.Join(", ", errors)}");
        }
    }

    [Fact]
    public void ClashPresetService_Validate_RejectsEmptyName()
    {
        var svc = new ClashPresetService();
        var preset = new ClashPresetDto { Name = "", Rules = [new ClashRuleDto { Name = "R", SourceCategories = ["Cable Trays"], TargetCategories = ["Ducts"], ClashType = "HardClash" }] };
        var (valid, errors) = svc.Validate(preset);
        Assert.False(valid);
        Assert.Contains(errors, e => e.Contains("name is required"));
    }

    [Fact]
    public void ClashPresetService_Validate_RejectsInvalidClashType()
    {
        var svc = new ClashPresetService();
        var preset = new ClashPresetDto
        {
            Name = "Test",
            Rules = [new ClashRuleDto { Name = "R", SourceCategories = ["Cable Trays"], TargetCategories = ["Ducts"], ClashType = "Invalid" }]
        };
        var (valid, errors) = svc.Validate(preset);
        Assert.False(valid);
        Assert.Contains(errors, e => e.Contains("ClashType must be"));
    }

    // ── ClashRunCacheService ──────────────────────────────────────────────────

    [Fact]
    public void ClashRunCacheService_GetNext_WrapsAround()
    {
        ClashRunCacheService.ResetCursor();
        var svc = new ClashRunCacheService();
        var run = new ClashRunResultDto
        {
            RunId = "TEST",
            Clashes =
            [
                new ClashResultDto { ClashId = "CL-0001" },
                new ClashResultDto { ClashId = "CL-0002" }
            ]
        };

        // Cursor starts at 0. GetNext advances to 1.
        var (c1, i1, t1) = svc.GetNext(run);
        Assert.Equal("CL-0002", c1!.ClashId);
        Assert.Equal(2, i1);
        Assert.Equal(2, t1);

        // GetNext again wraps around to 0.
        var (c2, i2, _) = svc.GetNext(run);
        Assert.Equal("CL-0001", c2!.ClashId);
        Assert.Equal(1, i2);
    }

    [Fact]
    public void ClashRunCacheService_GetPrevious_WrapsAround()
    {
        ClashRunCacheService.ResetCursor();
        var svc = new ClashRunCacheService();
        var run = new ClashRunResultDto
        {
            RunId = "TEST2",
            Clashes =
            [
                new ClashResultDto { ClashId = "CL-0001" },
                new ClashResultDto { ClashId = "CL-0002" }
            ]
        };

        // Cursor at 0. GetPrevious wraps to last = index 1.
        var (c, i, t) = svc.GetPrevious(run);
        Assert.Equal("CL-0002", c!.ClashId);
        Assert.Equal(2, i);
        Assert.Equal(2, t);
    }

    [Fact]
    public void ClashRunCacheService_EmptyRun_GetNextReturnsNull()
    {
        var svc = new ClashRunCacheService();
        var run = new ClashRunResultDto { RunId = "EMPTY", Clashes = [] };
        var (clash, index, total) = svc.GetNext(run);
        Assert.Null(clash);
        Assert.Equal(0, index);
        Assert.Equal(0, total);
    }

    // ── ClashResultDto ────────────────────────────────────────────────────────

    [Fact]
    public void ClashResultDto_DefaultStatus_IsNew()
    {
        var dto = new ClashResultDto();
        Assert.Equal("New", dto.Status);
    }

    [Fact]
    public void ClashResultDto_ClashIdFormat_IsCorrect()
    {
        var dto = new ClashResultDto { ClashId = "CL-0001" };
        Assert.StartsWith("CL-", dto.ClashId);
        Assert.Equal(7, dto.ClashId.Length);
    }
}
