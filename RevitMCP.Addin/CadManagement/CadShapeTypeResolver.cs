using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.CadManagement;

/// <summary>One family type the caller has pinned to a shape signature.</summary>
internal sealed class CadShapeTypeBinding
{
    public string Signature { get; set; } = string.Empty;
    public FamilySymbol Symbol { get; set; } = null!;

    /// <summary>How the type was arrived at: <c>map</c> from the caller, <c>auto</c> from the footprint.</summary>
    public string Source { get; set; } = "map";

    /// <summary>How far the family's own footprint is from the drawn shape, in mm. Null when mapped explicitly.</summary>
    public double? FootprintErrorMm { get; set; }
}

/// <summary>
/// Turns shape signatures into family types.
///
/// The caller's map wins outright. Signatures it does not cover fall back to matching the drawn
/// footprint against the family's own plan footprint, which is the only thing left to go on once
/// text is off the table — and that fallback refuses to choose when two families fit equally well,
/// because a silently wrong luminaire type is worse than an unplaced one.
/// </summary>
internal static class CadShapeTypeResolver
{
    public const double DefaultAutoMatchToleranceMm = 50.0;

    /// <summary>Two candidates closer together than this are a tie, and a tie is never guessed.</summary>
    private const double AmbiguityMarginMm = 1.0;

    public const string SourceMap = "map";
    public const string SourceAuto = "auto";

    /// <summary>
    /// Reads the <c>typeMap</c> argument: an array of
    /// <c>{ "signature": "rectangle 1200x200", "typeId": 123 }</c>, or the same with
    /// <c>familyName</c> / <c>typeName</c> instead of an id.
    /// </summary>
    public static Dictionary<string, CadShapeTypeBinding>? ParseTypeMap(
        Document doc,
        Dictionary<string, object?> arguments,
        out string? error)
    {
        error = null;
        var bindings = new Dictionary<string, CadShapeTypeBinding>(StringComparer.OrdinalIgnoreCase);

        if (!arguments.TryGetValue("typeMap", out var raw) || raw == null)
            return bindings;

        // The bridge sends an empty string when the caller omitted the map. That is "no map", not a
        // broken one — it has to fall through to auto-matching rather than fail the whole request.
        if (raw is string empty && string.IsNullOrWhiteSpace(empty))
            return bindings;

        var array = raw as JArray
                    ?? (raw is string text ? ToolArguments.TryParseJArray(text) : null);

        if (array == null)
        {
            error = "typeMap could not be read as a JSON array. Expected " +
                    "[{\"signature\": \"rectangle 1200x200\", \"typeId\": 123}, ...] — " +
                    "run revit_get_cad_shapes to see the signatures this drawing produces.";
            return null;
        }

        foreach (var token in array)
        {
            var signature = token["signature"]?.Value<string>()?.Trim();
            if (string.IsNullOrEmpty(signature))
            {
                error = "Every typeMap entry needs a 'signature'. " +
                        "Run revit_get_cad_shapes to see the signatures this drawing produces.";
                return null;
            }

            long typeId;
            string familyName, typeName;
            try
            {
                typeId = token["typeId"]?.Value<long>() ?? 0L;
                familyName = token["familyName"]?.Value<string>() ?? string.Empty;
                typeName = token["typeName"]?.Value<string>() ?? string.Empty;
            }
            catch (Exception ex)
            {
                error = $"typeMap entry '{signature}' could not be read: {ex.Message}. Expected " +
                        "typeId as a number, or familyName / typeName as strings.";
                return null;
            }

            var (symbol, symbolError) = FamilyInstancePlacer.ResolveSymbol(
                doc, typeId, familyName, typeName);

            if (symbol == null)
            {
                error = $"typeMap entry '{signature}': {symbolError}";
                return null;
            }

            bindings[signature!] = new CadShapeTypeBinding
            {
                Signature = signature!,
                Symbol = symbol,
                Source = SourceMap
            };
        }

        return bindings;
    }

    /// <summary>
    /// The families auto-matching is allowed to pick from. Narrowed by family name or by category,
    /// because matching a 1200x200 rectangle against every symbol in the model would happily return
    /// a door.
    /// </summary>
    public static List<FamilySymbol> BuildAutoMatchPool(
        Document doc,
        string familyNameFilter,
        string categoryFilter)
    {
        var pool = new List<FamilySymbol>();
        var hasFamilyFilter = !string.IsNullOrWhiteSpace(familyNameFilter);
        var hasCategoryFilter = !string.IsNullOrWhiteSpace(categoryFilter);

        if (!hasFamilyFilter && !hasCategoryFilter)
            return pool;

        foreach (var element in new FilteredElementCollector(doc)
                     .OfClass(typeof(FamilySymbol))
                     .WhereElementIsElementType())
        {
            if (element is not FamilySymbol symbol)
                continue;

            try
            {
                if (hasFamilyFilter && !Contains(symbol.Family?.Name, familyNameFilter))
                    continue;

                if (hasCategoryFilter && !Contains(symbol.Category?.Name, categoryFilter))
                    continue;
            }
            catch { continue; }

            pool.Add(symbol);
        }

        return pool;
    }

    /// <summary>A missing name never matches: an unnamed symbol must not slip past a filter.</summary>
    private static bool Contains(string? value, string needle) =>
        value != null && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// The family's own plan footprint, long side first, in millimetres. Null when the symbol has no
    /// readable geometry — an unloaded or purely parametric type measures nothing, and inventing a
    /// size for it would put it in front of families that really do fit.
    /// </summary>
    public static (double LongMm, double ShortMm)? Footprint(FamilySymbol symbol)
    {
        BoundingBoxXYZ? box;
        try { box = symbol.get_BoundingBox(null); }
        catch { return null; }

        if (box == null)
            return null;

        var width = CadPlacementRequest.FtToMm(box.Max.X - box.Min.X);
        var depth = CadPlacementRequest.FtToMm(box.Max.Y - box.Min.Y);

        if (width <= 0 || depth <= 0)
            return null;

        return (Math.Max(width, depth), Math.Min(width, depth));
    }

    /// <summary>
    /// Picks the family whose footprint is closest to the drawn shape. Returns null with a reason
    /// when nothing is close enough, or when two families are equally close.
    /// </summary>
    public static CadShapeTypeBinding? AutoMatch(
        CadShape shape,
        IReadOnlyList<FamilySymbol> pool,
        double toleranceMm,
        out string? reason)
    {
        reason = null;

        if (pool.Count == 0)
        {
            reason = "no family types were offered to match against — pass autoMatchFamilyName " +
                     "or autoMatchCategory, or map this signature in typeMap";
            return null;
        }

        FamilySymbol? best = null;
        var bestError = double.MaxValue;
        var runnerUpError = double.MaxValue;
        var measured = 0;

        foreach (var symbol in pool)
        {
            var footprint = Footprint(symbol);
            if (footprint == null)
                continue;

            measured++;

            var longError = Math.Abs(footprint.Value.LongMm - shape.LengthMm);
            var shortError = Math.Abs(footprint.Value.ShortMm - shape.WidthMm);
            if (longError > toleranceMm || shortError > toleranceMm)
                continue;

            var error = longError + shortError;
            if (error < bestError)
            {
                runnerUpError = bestError;
                bestError = error;
                best = symbol;
            }
            else if (error < runnerUpError)
            {
                runnerUpError = error;
            }
        }

        if (measured == 0)
        {
            reason = "none of the candidate family types has readable geometry to measure";
            return null;
        }

        if (best == null)
        {
            reason = $"no candidate family footprint is within {toleranceMm:F0} mm of " +
                     $"{shape.LengthMm:F0} x {shape.WidthMm:F0} mm";
            return null;
        }

        if (runnerUpError - bestError < AmbiguityMarginMm)
        {
            reason = $"two family types fit {shape.LengthMm:F0} x {shape.WidthMm:F0} mm equally well — " +
                     "map this signature in typeMap to say which";
            return null;
        }

        return new CadShapeTypeBinding
        {
            Signature = shape.Signature,
            Symbol = best,
            Source = SourceAuto,
            FootprintErrorMm = bestError
        };
    }
}
