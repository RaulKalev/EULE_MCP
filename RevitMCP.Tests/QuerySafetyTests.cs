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
}
