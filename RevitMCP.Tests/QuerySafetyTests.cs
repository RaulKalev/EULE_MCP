using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;
using Xunit;

namespace RevitMCP.Tests;

public class QuerySafetyTests
{
    // ── QueryLimits defaults ─────────────────────────────────────────────────

    [Fact]
    public void QueryLimits_Default_HasExpectedValues()
    {
        var limits = QueryLimits.Default;
        Assert.Equal(100, limits.DefaultPageSize);
        Assert.Equal(500, limits.MaxPageSize);
        Assert.Equal(40, limits.MaxParametersPerElement);
        Assert.Equal(500, limits.MaxStringLength);
        Assert.Equal(1_000_000, limits.MaxResponseBytes);
        Assert.False(limits.EnableStrictMode);
        Assert.Equal(20_000, limits.MaxScanElements);
        Assert.Equal(2_000, limits.MaxUnscopedScanElements);
    }

    // ── QueryGuard.ShouldStopScan ─────────────────────────────────────────────

    [Fact]
    public void ShouldStopScan_Narrowed_BelowCap_ReturnsFalse()
    {
        var limits = new QueryLimits { MaxScanElements = 100, MaxUnscopedScanElements = 10 };
        Assert.False(QueryGuard.ShouldStopScan(50, isNarrowed: true, limits));
    }

    [Fact]
    public void ShouldStopScan_Narrowed_AtCap_ReturnsTrue()
    {
        var limits = new QueryLimits { MaxScanElements = 100, MaxUnscopedScanElements = 10 };
        Assert.True(QueryGuard.ShouldStopScan(100, isNarrowed: true, limits));
    }

    [Fact]
    public void ShouldStopScan_Narrowed_JustBelowCap_ReturnsFalse()
    {
        var limits = new QueryLimits { MaxScanElements = 100, MaxUnscopedScanElements = 10 };
        Assert.False(QueryGuard.ShouldStopScan(99, isNarrowed: true, limits));
    }

    [Fact]
    public void ShouldStopScan_Unnarrowed_UsesTighterCap()
    {
        var limits = new QueryLimits { MaxScanElements = 100, MaxUnscopedScanElements = 10 };
        // Below the narrowed cap but at/above the unscoped cap — must still stop.
        Assert.True(QueryGuard.ShouldStopScan(10, isNarrowed: false, limits));
        Assert.False(QueryGuard.ShouldStopScan(9, isNarrowed: false, limits));
    }

    [Fact]
    public void ShouldStopScan_NullLimits_UsesDefaultInstance()
    {
        Assert.False(QueryGuard.ShouldStopScan(1, isNarrowed: false, null));
        Assert.True(QueryGuard.ShouldStopScan(QueryLimits.Default.MaxUnscopedScanElements, isNarrowed: false, null));
    }

    // ── QueryGuard.ShouldStopScan (needsParamRead overload) ───────────────────

    [Fact]
    public void ShouldStopScan_NoParamRead_Unnarrowed_IgnoresTightUnscopedCap()
    {
        // e.g. revit_group_elements grouping only by Category with no filters, no scope —
        // this is cheap (no per-element parameter read) and must not hit the tight cap
        // meant for expensive unscoped scans.
        var limits = new QueryLimits { MaxScanElements = 100, MaxUnscopedScanElements = 10 };
        Assert.False(QueryGuard.ShouldStopScan(50, isNarrowed: false, needsParamRead: false, limits));
    }

    [Fact]
    public void ShouldStopScan_NoParamRead_Unnarrowed_StillStopsAtGenerousCap()
    {
        var limits = new QueryLimits { MaxScanElements = 100, MaxUnscopedScanElements = 10 };
        Assert.True(QueryGuard.ShouldStopScan(100, isNarrowed: false, needsParamRead: false, limits));
        Assert.False(QueryGuard.ShouldStopScan(99, isNarrowed: false, needsParamRead: false, limits));
    }

    [Fact]
    public void ShouldStopScan_NoParamRead_Narrowed_UsesGenerousCap()
    {
        var limits = new QueryLimits { MaxScanElements = 100, MaxUnscopedScanElements = 10 };
        Assert.True(QueryGuard.ShouldStopScan(100, isNarrowed: true, needsParamRead: false, limits));
        Assert.False(QueryGuard.ShouldStopScan(99, isNarrowed: true, needsParamRead: false, limits));
    }

    [Fact]
    public void ShouldStopScan_ParamRead_Unnarrowed_UsesTightCap()
    {
        var limits = new QueryLimits { MaxScanElements = 100, MaxUnscopedScanElements = 10 };
        Assert.True(QueryGuard.ShouldStopScan(10, isNarrowed: false, needsParamRead: true, limits));
    }

    [Fact]
    public void ShouldStopScan_ThreeArgOverload_MatchesNeedsParamReadTrue()
    {
        var limits = new QueryLimits { MaxScanElements = 100, MaxUnscopedScanElements = 10 };
        Assert.Equal(
            QueryGuard.ShouldStopScan(10, isNarrowed: false, needsParamRead: true, limits),
            QueryGuard.ShouldStopScan(10, isNarrowed: false, limits));
    }

    // ── QueryGuard.BuildNarrowingGuidance ─────────────────────────────────────

    [Fact]
    public void BuildNarrowingGuidance_NoElements_ReturnsEmptyModelMessage()
    {
        var msg = QueryGuard.BuildNarrowingGuidance(0, new Dictionary<string, int>());
        Assert.Contains("no elements", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildNarrowingGuidance_EmptyDictionary_ReturnsEmptyModelMessage()
    {
        // TotalElements > 0 but no category breakdown available — still treat as empty.
        var msg = QueryGuard.BuildNarrowingGuidance(5, new Dictionary<string, int>());
        Assert.Contains("no elements", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildNarrowingGuidance_IncludesTotalAndCategoryCount()
    {
        var counts = new Dictionary<string, int> { ["Walls"] = 100, ["Doors"] = 20 };
        var msg = QueryGuard.BuildNarrowingGuidance(120, counts);

        Assert.Contains("120 elements", msg);
        Assert.Contains("2 categories", msg);
        Assert.Contains("Walls (100)", msg);
        Assert.Contains("Doors (20)", msg);
    }

    [Fact]
    public void BuildNarrowingGuidance_OrdersCategoriesByCountDescending()
    {
        var counts = new Dictionary<string, int> { ["Doors"] = 20, ["Walls"] = 100, ["Windows"] = 50 };
        var msg = QueryGuard.BuildNarrowingGuidance(170, counts);

        var wallsIdx = msg.IndexOf("Walls", StringComparison.Ordinal);
        var windowsIdx = msg.IndexOf("Windows", StringComparison.Ordinal);
        var doorsIdx = msg.IndexOf("Doors", StringComparison.Ordinal);
        Assert.True(wallsIdx < windowsIdx);
        Assert.True(windowsIdx < doorsIdx);
    }

    [Fact]
    public void BuildNarrowingGuidance_MoreCategoriesThanShowLimit_TruncatesWithCount()
    {
        var counts = Enumerable.Range(1, 20)
            .ToDictionary(i => $"Category{i}", i => i);

        var msg = QueryGuard.BuildNarrowingGuidance(210, counts, maxCategoriesToShow: 5);

        Assert.Contains("5 more categories", msg);
    }

    [Fact]
    public void BuildNarrowingGuidance_FewerCategoriesThanShowLimit_NoTruncationNote()
    {
        var counts = new Dictionary<string, int> { ["Walls"] = 100, ["Doors"] = 20 };
        var msg = QueryGuard.BuildNarrowingGuidance(120, counts, maxCategoriesToShow: 15);

        Assert.DoesNotContain("more categories", msg);
    }

    // ── QueryGuard.NormalizePageSize ─────────────────────────────────────────

    [Fact]
    public void NormalizePageSize_Null_ReturnsDefault()
    {
        var limits = new QueryLimits { DefaultPageSize = 50, MaxPageSize = 200 };
        Assert.Equal(50, QueryGuard.NormalizePageSize(null, limits));
    }

    [Fact]
    public void NormalizePageSize_Zero_ReturnsDefault()
    {
        var limits = new QueryLimits { DefaultPageSize = 50, MaxPageSize = 200 };
        Assert.Equal(50, QueryGuard.NormalizePageSize(0, limits));
    }

    [Fact]
    public void NormalizePageSize_Negative_ReturnsDefault()
    {
        var limits = new QueryLimits { DefaultPageSize = 50, MaxPageSize = 200 };
        Assert.Equal(50, QueryGuard.NormalizePageSize(-10, limits));
    }

    [Fact]
    public void NormalizePageSize_Valid_ReturnsValue()
    {
        var limits = new QueryLimits { DefaultPageSize = 50, MaxPageSize = 200 };
        Assert.Equal(75, QueryGuard.NormalizePageSize(75, limits));
    }

    [Fact]
    public void NormalizePageSize_ExceedsMax_ClampsToMax()
    {
        var limits = new QueryLimits { DefaultPageSize = 50, MaxPageSize = 200 };
        Assert.Equal(200, QueryGuard.NormalizePageSize(999, limits));
    }

    [Fact]
    public void NormalizePageSize_ExactlyAtMax_ReturnsMax()
    {
        var limits = new QueryLimits { DefaultPageSize = 50, MaxPageSize = 200 };
        Assert.Equal(200, QueryGuard.NormalizePageSize(200, limits));
    }

    [Fact]
    public void NormalizePageSize_NullLimits_UsesDefaultInstance()
    {
        // Should not throw; falls back to QueryLimits.Default
        var result = QueryGuard.NormalizePageSize(null, null);
        Assert.Equal(QueryLimits.Default.DefaultPageSize, result);
    }

    // ── QueryGuard.ValidateGeometryQuery ──────────────────────────────────────

    [Fact]
    public void ValidateGeometryQuery_NoGeometry_ReturnsNull()
    {
        var result = QueryGuard.ValidateGeometryQuery(
            includeGeometry: false,
            hasCategoryFilter: false,
            hasElementIds: false,
            hasViewFilter: false);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateGeometryQuery_WithGeometryAndCategory_ReturnsNull()
    {
        var result = QueryGuard.ValidateGeometryQuery(
            includeGeometry: true,
            hasCategoryFilter: true,
            hasElementIds: false,
            hasViewFilter: false);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateGeometryQuery_WithGeometryAndElementIds_ReturnsNull()
    {
        var result = QueryGuard.ValidateGeometryQuery(
            includeGeometry: true,
            hasCategoryFilter: false,
            hasElementIds: true,
            hasViewFilter: false);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateGeometryQuery_WithGeometryAndView_ReturnsNull()
    {
        var result = QueryGuard.ValidateGeometryQuery(
            includeGeometry: true,
            hasCategoryFilter: false,
            hasElementIds: false,
            hasViewFilter: true);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateGeometryQuery_WithGeometryNoFilter_ReturnsError()
    {
        var result = QueryGuard.ValidateGeometryQuery(
            includeGeometry: true,
            hasCategoryFilter: false,
            hasElementIds: false,
            hasViewFilter: false);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal(ToolErrorCodes.GeometryQueryTooBroad, result.Error);
        Assert.NotEmpty(result.SuggestedActions);
    }

    // ── QueryGuard.TruncateString ─────────────────────────────────────────────

    [Fact]
    public void TruncateString_ShortValue_Unchanged()
    {
        Assert.Equal("hello", QueryGuard.TruncateString("hello", 100));
    }

    [Fact]
    public void TruncateString_ExactlyAtLimit_Unchanged()
    {
        var value = new string('x', 50);
        Assert.Equal(value, QueryGuard.TruncateString(value, 50));
    }

    [Fact]
    public void TruncateString_LongValue_Truncated()
    {
        var value = new string('a', 600);
        var result = QueryGuard.TruncateString(value, 500);
        Assert.StartsWith(new string('a', 500), result);
        Assert.Contains("[truncated]", result);
    }

    [Fact]
    public void TruncateString_ZeroMaxLength_Unchanged()
    {
        // maxLength == 0 means "no truncation"
        var value = new string('a', 1000);
        Assert.Equal(value, QueryGuard.TruncateString(value, 0));
    }

    [Fact]
    public void TruncateString_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, QueryGuard.TruncateString(null, 100));
    }

    [Fact]
    public void TruncateString_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, QueryGuard.TruncateString(string.Empty, 100));
    }

    // ── ResponseGuard.GuardResult ─────────────────────────────────────────────

    [Fact]
    public void ResponseGuard_SmallResult_ReturnedUnchanged()
    {
        var result = new McpToolResult
        {
            RequestId = "test-1",
            Success = true,
            Message = "OK",
            Data = new { count = 1 }
        };

        var limits = new QueryLimits { MaxResponseBytes = 1_000_000 };
        var guarded = ResponseGuard.GuardResult(result, limits);

        Assert.Same(result, guarded);
    }

    [Fact]
    public void ResponseGuard_OversizedResult_ReturnsFallback()
    {
        // Create a result whose serialized form will exceed a tiny limit
        var result = new McpToolResult
        {
            RequestId = "test-2",
            Success = true,
            Message = "OK",
            Data = new string('x', 200) // 200-char string will exceed 100-byte limit
        };

        var limits = new QueryLimits { MaxResponseBytes = 100 };
        var guarded = ResponseGuard.GuardResult(result, limits);

        Assert.NotSame(result, guarded);
        Assert.False(guarded.Success);
        Assert.Equal(ToolErrorCodes.ResponseTooLarge, guarded.Status);
        Assert.Equal("test-2", guarded.RequestId);
        Assert.NotNull(guarded.Data);
        Assert.IsType<SafeToolResult>(guarded.Data);

        var safe = (SafeToolResult)guarded.Data!;
        Assert.Equal(ToolErrorCodes.ResponseTooLarge, safe.Error);
        Assert.NotEmpty(safe.SuggestedActions);
    }

    [Fact]
    public void ResponseGuard_OversizedResult_PreservesDurationMs()
    {
        var result = new McpToolResult
        {
            RequestId = "test-3",
            Success = true,
            Message = "OK",
            Data = new string('x', 200),
            DurationMs = 42
        };

        var limits = new QueryLimits { MaxResponseBytes = 100 };
        var guarded = ResponseGuard.GuardResult(result, limits);

        Assert.Equal(42, guarded.DurationMs);
    }

    [Fact]
    public void ResponseGuard_OversizedResult_PreservesExistingWarnings()
    {
        var result = new McpToolResult
        {
            RequestId = "test-4",
            Success = true,
            Message = "OK",
            Data = new string('x', 200),
            Warnings = new List<string> { "original warning" }
        };

        var limits = new QueryLimits { MaxResponseBytes = 100 };
        var guarded = ResponseGuard.GuardResult(result, limits);

        Assert.Contains("original warning", guarded.Warnings);
        // Also has the ResponseGuard diagnostic warning
        Assert.True(guarded.Warnings.Count >= 2);
    }

    [Fact]
    public void ResponseGuard_NullLimits_UsesDefaultInstance()
    {
        var result = new McpToolResult { RequestId = "test-5", Success = true, Message = "OK" };
        // Should not throw with null limits
        var guarded = ResponseGuard.GuardResult(result, null);
        Assert.NotNull(guarded);
    }

    // ── ElementQuerySummary ──────────────────────────────────────────────────

    [Fact]
    public void ElementQuerySummary_DefaultValues_AreEmpty()
    {
        var summary = new ElementQuerySummary();
        Assert.Equal(0, summary.TotalElements);
        Assert.Empty(summary.Categories);
        Assert.Empty(summary.Families);
        Assert.Equal(string.Empty, summary.Message);
    }

    [Fact]
    public void CategoryCount_Properties_RoundTrip()
    {
        var cc = new CategoryCount { Category = "Walls", Count = 42 };
        Assert.Equal("Walls", cc.Category);
        Assert.Equal(42, cc.Count);
    }

    [Fact]
    public void FamilyCount_Properties_RoundTrip()
    {
        var fc = new FamilyCount { Family = "Basic Wall", Count = 7 };
        Assert.Equal("Basic Wall", fc.Family);
        Assert.Equal(7, fc.Count);
    }

    [Fact]
    public void ElementQuerySummary_FamiliesCap_HoldsAt50()
    {
        // Simulate what BuildSummaryResult does: Take(50)
        var families = Enumerable.Range(1, 100)
            .Select(i => new FamilyCount { Family = $"Family_{i}", Count = i })
            .OrderByDescending(x => x.Count)
            .Take(50)
            .ToList();

        Assert.Equal(50, families.Count);
        Assert.Equal("Family_100", families[0].Family);
    }

    // ── QueryGuard.ResolveEffectiveLimits ────────────────────────────────────

    [Fact]
    public void ResolveEffectiveLimits_OversizedPageSize_ClampsToMaxPageSize()
    {
        var limits = new QueryLimits { DefaultPageSize = 100, MaxPageSize = 500 };
        var (pageSize, _, _) = QueryGuard.ResolveEffectiveLimits(999_999, 500, 0, 0, limits);
        Assert.Equal(500, pageSize);
    }

    [Fact]
    public void ResolveEffectiveLimits_NegativePageSize_UsesDefaultPageSize()
    {
        var limits = new QueryLimits { DefaultPageSize = 100, MaxPageSize = 500 };
        var (pageSize, _, _) = QueryGuard.ResolveEffectiveLimits(-1, 500, 0, 0, limits);
        Assert.Equal(100, pageSize);
    }

    [Fact]
    public void ResolveEffectiveLimits_SmallLimit_CapsPageSizeBelowDefault()
    {
        // limit=10 < DefaultPageSize=100 → effectivePageSize should be 10
        var limits = new QueryLimits { DefaultPageSize = 100, MaxPageSize = 500 };
        var (pageSize, _, _) = QueryGuard.ResolveEffectiveLimits(-1, 10, 0, 0, limits);
        Assert.Equal(10, pageSize);
    }

    [Fact]
    public void ResolveEffectiveLimits_ZeroMaxParams_UsesDefault()
    {
        var limits = new QueryLimits { MaxParametersPerElement = 40 };
        var (_, maxParams, _) = QueryGuard.ResolveEffectiveLimits(-1, 500, 0, 0, limits);
        Assert.Equal(40, maxParams);
    }

    [Fact]
    public void ResolveEffectiveLimits_OversizedMaxParams_Clamped()
    {
        var limits = new QueryLimits { MaxParametersPerElement = 40 };
        var (_, maxParams, _) = QueryGuard.ResolveEffectiveLimits(-1, 500, 999, 0, limits);
        Assert.Equal(40, maxParams);
    }

    [Fact]
    public void ResolveEffectiveLimits_ZeroTruncate_UsesDefault()
    {
        var limits = new QueryLimits { MaxStringLength = 500 };
        var (_, _, truncate) = QueryGuard.ResolveEffectiveLimits(-1, 500, 0, 0, limits);
        Assert.Equal(500, truncate);
    }

    [Fact]
    public void ResolveEffectiveLimits_OversizedTruncate_Clamped()
    {
        var limits = new QueryLimits { MaxStringLength = 500 };
        var (_, _, truncate) = QueryGuard.ResolveEffectiveLimits(-1, 500, 0, 9999, limits);
        Assert.Equal(500, truncate);
    }

    [Fact]
    public void ResolveEffectiveLimits_ExplicitSmallPageSize_Respected()
    {
        var limits = new QueryLimits { DefaultPageSize = 100, MaxPageSize = 500 };
        var (pageSize, _, _) = QueryGuard.ResolveEffectiveLimits(25, 500, 0, 0, limits);
        Assert.Equal(25, pageSize);
    }
}
