namespace RevitMCP.Core.Safety;

/// <summary>
/// Pre-execution validation helpers for query parameters.
/// All methods are pure (no I/O) and safe to call on any thread.
/// </summary>
public static class QueryGuard
{
    /// <summary>
    /// Normalises a caller-supplied page size against the configured limits.
    /// Null or &lt;= 0 values fall back to <see cref="QueryLimits.DefaultPageSize"/>.
    /// Values above <see cref="QueryLimits.MaxPageSize"/> are clamped.
    /// </summary>
    public static int NormalizePageSize(int? requestedPageSize, QueryLimits? limits = null)
    {
        limits ??= QueryLimits.Default;
        if (!requestedPageSize.HasValue || requestedPageSize.Value <= 0)
            return limits.DefaultPageSize;
        return Math.Min(requestedPageSize.Value, limits.MaxPageSize);
    }

    /// <summary>
    /// Validates that a geometry query is scoped with at least one narrowing filter.
    /// Returns null when the query is acceptable, or a <see cref="SafeToolResult"/> with
    /// error details when it is too broad.
    /// </summary>
    public static SafeToolResult? ValidateGeometryQuery(
        bool includeGeometry,
        bool hasCategoryFilter,
        bool hasElementIds,
        bool hasViewFilter)
    {
        if (!includeGeometry)
            return null;

        if (hasCategoryFilter || hasElementIds || hasViewFilter)
            return null;

        return new SafeToolResult
        {
            Success = false,
            Error = ToolErrorCodes.GeometryQueryTooBroad,
            Message = "Geometry queries must include a category, element id list, or view filter to prevent retrieving geometry for the entire model.",
            SuggestedActions = new[]
            {
                "Add a category filter (e.g. category: 'Walls')",
                "Provide explicit element ids to query",
                "Enable 'useSelection' to limit to the active selection",
                "Use summary mode first to understand the model scope"
            }
        };
    }

    /// <summary>
    /// Truncates a string to <paramref name="maxLength"/> characters and appends a marker.
    /// Returns the original string when it is within the limit or <paramref name="maxLength"/> is 0.
    /// </summary>
    public static string TruncateString(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (maxLength <= 0 || value!.Length <= maxLength) return value!;
        return value.Substring(0, maxLength) + "... [truncated]";
    }

    /// <summary>
    /// Resolves effective page size, parameter cap, and string truncation length from
    /// caller-supplied sentinel values against the configured <see cref="QueryLimits"/>.
    /// <list type="bullet">
    /// <item>Negative / zero <paramref name="requestedPageSize"/> → <see cref="QueryLimits.DefaultPageSize"/>, capped at <paramref name="requestedLimit"/>.</item>
    /// <item>Values above <see cref="QueryLimits.MaxPageSize"/> are clamped.</item>
    /// <item>Zero <paramref name="maxParametersPerElement"/> / <paramref name="truncateStringLength"/> → limit default.</item>
    /// </list>
    /// </summary>
    public static (int effectivePageSize, int effectiveMaxParameters, int effectiveTruncateLength) ResolveEffectiveLimits(
        int requestedPageSize,
        int requestedLimit,
        int maxParametersPerElement,
        int truncateStringLength,
        QueryLimits? limits = null)
    {
        limits ??= QueryLimits.Default;

        int capLimit = requestedLimit > 0 ? requestedLimit : limits.DefaultPageSize;

        int effectivePageSize = requestedPageSize > 0
            ? NormalizePageSize(requestedPageSize, limits)
            : Math.Min(capLimit, limits.DefaultPageSize);

        // Ensure page size never exceeds the requested scan/return cap
        effectivePageSize = Math.Min(effectivePageSize, capLimit);

        int effectiveMaxParameters = maxParametersPerElement > 0
            ? Math.Min(maxParametersPerElement, limits.MaxParametersPerElement)
            : limits.MaxParametersPerElement;

        int effectiveTruncateLength = truncateStringLength > 0
            ? Math.Min(truncateStringLength, limits.MaxStringLength)
            : limits.MaxStringLength;

        return (effectivePageSize, effectiveMaxParameters, effectiveTruncateLength);
    }
}
