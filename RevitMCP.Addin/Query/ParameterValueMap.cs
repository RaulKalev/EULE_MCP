namespace RevitMCP.Addin.Query;

/// <summary>
/// Turns read parameters into a name → value map when several share one name: a built-in "Sheet Number" and an empty
/// project parameter called "Sheet Number", or an instance and a type parameter. A plain <c>map[p.Name] = p.Value</c>
/// let whichever came last win, so an empty duplicate hid the real value (M16). Per name the first of these wins:
/// a value that is set, then a project/shared/family parameter over a built-in one, then instance over type — the
/// same order <see cref="ParameterResolution"/> uses for writes, so a read shows what a write would change. Pure.
/// </summary>
public static class ParameterValueMap
{
    public static Dictionary<string, string> Build(IEnumerable<ParameterValueDto> parameters, Func<ParameterValueDto, bool>? include = null)
    {
        var best = new Dictionary<string, ParameterValueDto>();
        foreach (var p in parameters)
        {
            if (include != null && !include(p)) continue;
            if (!best.TryGetValue(p.Name, out var current) || Rank(p) < Rank(current))
                best[p.Name] = p;
        }
        return best.ToDictionary(pair => pair.Key, pair => pair.Value.Value);
    }

    /// <summary>Lower is better; ties keep the earlier parameter.</summary>
    private static int Rank(ParameterValueDto p) =>
        (string.IsNullOrEmpty(p.Value) ? 4 : 0) +
        (string.IsNullOrEmpty(p.BuiltInParameterName) ? 0 : 2) +
        (string.Equals(p.Scope, "Type", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
}
