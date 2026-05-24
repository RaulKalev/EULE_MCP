using RevitMCP.Addin.Skills.Tasks.DrawingNaming.Models;

namespace RevitMCP.Addin.Skills.Tasks.DrawingNaming;

/// <summary>
/// Pure-logic engine for building a target parameter value from a list of naming tokens.
/// Has no Revit API dependency — fully unit-testable.
///
/// Algorithm:
/// - Iterate tokens left-to-right.
/// - Accumulate "pending" separator tokens between parameter tokens.
/// - When a non-empty parameter value is encountered: flush pending separators to output, then append the value.
/// - When an empty parameter value is encountered: discard pending separators (the separator before
///   and after an empty token are both dropped).
/// - Never write trailing separators at the end of output.
/// </summary>
internal static class NamingMappingEngine
{
    private const string TypeParameter = "Parameter";
    private const string TypeSeparator  = "Separator";

    /// <summary>
    /// Builds the computed name string from the token list and a dictionary of resolved parameter values.
    /// </summary>
    /// <param name="tokens">Ordered list of tokens from the mapping configuration.</param>
    /// <param name="paramValues">
    ///   Map of parameter name → value (trimmed strings; empty string = parameter has no value).
    /// </param>
    /// <returns>The assembled name string, or <see cref="string.Empty"/> if no non-empty parameter was found.</returns>
    public static string BuildName(
        IReadOnlyList<NamingMappingToken> tokens,
        IReadOnlyDictionary<string, string> paramValues)
    {
        var result          = new System.Text.StringBuilder();
        var pendingSeps     = new System.Text.StringBuilder();

        foreach (var token in tokens)
        {
            if (token.Type.Equals(TypeSeparator, StringComparison.OrdinalIgnoreCase))
            {
                pendingSeps.Append(token.Value);
                continue;
            }

            if (!token.Type.Equals(TypeParameter, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = paramValues.TryGetValue(token.Value, out var v) ? v : string.Empty;

            if (string.IsNullOrEmpty(value))
            {
                // Discard accumulated separators — they had nowhere to connect
                pendingSeps.Clear();
                continue;
            }

            // Non-empty param: flush pending separators (only if we already have output)
            if (result.Length > 0)
                result.Append(pendingSeps);
            pendingSeps.Clear();

            result.Append(value);
        }

        return result.ToString();
    }
}
