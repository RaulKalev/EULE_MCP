namespace RevitMCP.Addin.Electrical;

/// <summary>Where a <c>{placeholder}</c> of a load-name template is read from.</summary>
public enum LoadNameValueSource
{
    ConnectedElement,
    Circuit
}

/// <summary>
/// Read order of load-name template placeholders (M12). With source=ConnectedElements a placeholder is read from the
/// circuit's first connected element and only then from the circuit: the circuit has its own Category ("Electrical
/// Circuits"), Family and Type values, which used to win, so <c>{Category}</c> never named the connected devices.
/// Pure.
/// </summary>
public static class LoadNameTemplateRules
{
    /// <param name="source">ConnectedElements (default when empty) or CircuitParameters (any other value).</param>
    public static IReadOnlyList<LoadNameValueSource> ReadOrder(string? source)
        => string.IsNullOrWhiteSpace(source) || string.Equals(source.Trim(), "ConnectedElements", StringComparison.OrdinalIgnoreCase)
            ? new[] { LoadNameValueSource.ConnectedElement, LoadNameValueSource.Circuit }
            : new[] { LoadNameValueSource.Circuit };

    /// <summary>
    /// The element property a placeholder names directly (category, family or type name), or null for an ordinary
    /// parameter name.
    /// </summary>
    public static string? PseudoField(string placeholder)
    {
        switch (placeholder.Trim().Replace(" ", string.Empty).ToLowerInvariant())
        {
            case "category": return "Category";
            case "family": case "familyname": return "Family";
            case "type": case "typename": return "Type";
            default: return null;
        }
    }
}
