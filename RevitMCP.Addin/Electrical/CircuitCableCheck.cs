namespace RevitMCP.Addin.Electrical;

/// <summary>
/// The one "missing cable" rule of the circuit checks (M11). A circuit misses its cable when neither the Revit 2025+
/// CableType nor the legacy WireType resolves to a name (<c>CircuitDtoBuilder.GetWireTypeName</c>). The health
/// check used to report this fact twice, as MissingCableType and MissingWireType, so every circuit without a cable
/// counted as two issues (928 for 464 circuits). MissingWireType is still accepted as a check name and means the
/// same check. Pure.
/// </summary>
public static class CircuitCableCheck
{
    public const string Issue = "MissingCableType";
    public const string LegacyIssue = "MissingWireType";

    /// <param name="resolvedTypeName">The cable or wire type name the circuit resolves to; empty when none.</param>
    public static bool IsMissing(string? resolvedTypeName) => string.IsNullOrWhiteSpace(resolvedTypeName);

    /// <summary>A requested check name with the legacy name mapped onto <see cref="Issue"/>.</summary>
    public static string NormalizeCheckName(string check)
        => string.Equals(check?.Trim(), LegacyIssue, StringComparison.OrdinalIgnoreCase) ? Issue : check?.Trim() ?? string.Empty;
}
