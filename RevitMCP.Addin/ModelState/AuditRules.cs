using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelState;

/// <summary>The badge of an audit figure or section.</summary>
public static class AuditStatus
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Bad = "bad";
    /// <summary>The collector failed; the rest of the report is still valid.</summary>
    public const string Error = "error";
    /// <summary>The section does not apply (not workshared, no rooms) or was not requested.</summary>
    public const string Skipped = "skipped";

    public static int Rank(string? status) => status switch
    {
        Bad => 4,
        Error => 3,
        Warn => 2,
        Ok => 1,
        _ => 0
    };

    public static string Worst(IEnumerable<string?> statuses)
    {
        string best = Ok;
        var any = false;
        foreach (var s in statuses)
        {
            if (s == null) continue;
            any = true;
            if (Rank(s) > Rank(best)) best = s;
        }
        return any ? best : Ok;
    }
}

/// <summary>When a figure gets a warn or bad badge: strictly above <see cref="Warn"/> / <see cref="Bad"/>. Null = no badge.</summary>
public sealed class AuditThreshold
{
    public string Key { get; set; } = string.Empty;
    public double? Warn { get; set; }
    public double? Bad { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Thresholds of the model health audit (#109): company defaults, overridable per key from <c>audit.thresholds</c> in
/// the company, user and project config (later scopes win). Every figure is "lower is better": a value strictly above
/// <c>warn</c> is warn, strictly above <c>bad</c> is bad. Pure.
/// </summary>
public sealed class AuditRules
{
    public const string ConfigSection = "audit";
    public const string ConfigKey = "thresholds";

    private readonly Dictionary<string, AuditThreshold> _rules;

    private AuditRules(Dictionary<string, AuditThreshold> rules) => _rules = rules;

    /// <summary>The company defaults (docs/model-state.md lists them).</summary>
    public static AuditRules Default() => new(Defaults().ToDictionary(t => t.Key, t => t, StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<AuditThreshold> Defaults()
    {
        yield return T("warnings.total", 100, 500, "Revit warnings in the model");
        yield return T("types.unused", 100, 500, "Family types with no instance");
        yield return T("views.unplacedPercent", 20, 50, "Share of views (no templates, schedules or sheets) not on a sheet, %");
        yield return T("sheets.withoutViews", 0, null, "Sheets with no view or schedule");
        yield return T("rooms.unplaced", 0, 50, "Rooms not placed");
        yield return T("rooms.notEnclosed", 0, 20, "Rooms placed but not enclosed (area 0)");
        yield return T("rooms.redundant", 0, null, "Redundant rooms (several rooms in one enclosed region)");
        yield return T("imports.cadImports", 0, 10, "Imported (not linked) CAD instances");
        yield return T("imports.importedLinePatterns", 20, null, "Line patterns brought in by CAD imports (IMPORT-*)");
        yield return T("families.inPlace", 10, 50, "In-place families");
        yield return T("groups.unusedTypes", 10, null, "Group types with no instance");
        yield return T("worksets.ownedByOthers", 0, null, "User worksets checked out by someone else");
        yield return T("worksets.elementsOwnedByOthers", 0, null, "Elements (sampled) checked out by someone else");
        yield return T("links.notLoaded", 0, 5, "Links that are not loaded");
        yield return T("links.absolutePath", 0, null, "Links with an absolute path");
        yield return T("links.unpinned", 0, null, "Link instances that are not pinned");
        yield return T("file.sizeMb", 300, 600, "Model file size, MB");
        yield return T("graph.stale", 0, null, "Model graph missing or stale (1 = yes)");
    }

    private static AuditThreshold T(string key, double? warn, double? bad, string description) =>
        new() { Key = key, Warn = warn, Bad = bad, Description = description };

    public IReadOnlyCollection<AuditThreshold> All => _rules.Values;

    public AuditThreshold? Get(string key) => _rules.TryGetValue(key, out var t) ? t : null;

    /// <summary>ok, warn or bad for a figure with a rule; null when no rule covers <paramref name="key"/>.</summary>
    public string? Evaluate(string key, double value)
    {
        if (!_rules.TryGetValue(key, out var t) || (t.Warn == null && t.Bad == null)) return null;
        if (t.Bad is double bad && value > bad) return AuditStatus.Bad;
        if (t.Warn is double warn && value > warn) return AuditStatus.Warn;
        return AuditStatus.Ok;
    }

    /// <summary>
    /// A copy with the overrides of one config scope applied. <paramref name="thresholdsJson"/> is the
    /// <c>audit.thresholds</c> object: <c>{"warnings.total": {"warn": 200, "bad": 1000}, "families.inPlace": 5,
    /// "imports.cadImports": false}</c> — an object sets warn and/or bad (null clears one), a number sets warn,
    /// false or null turns the badge off. Unknown keys are kept (a project can badge nothing new) and reported.
    /// </summary>
    public AuditRules WithOverrides(string? thresholdsJson, string source, List<string> warnings)
    {
        var copy = _rules.ToDictionary(p => p.Key, p => Clone(p.Value), StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(thresholdsJson)) return new AuditRules(copy);

        JObject obj;
        try
        {
            var token = JToken.Parse(thresholdsJson!);
            if (token is not JObject o)
            {
                warnings.Add($"{source}: {ConfigSection}.{ConfigKey} must be an object of threshold keys; ignored.");
                return new AuditRules(copy);
            }
            obj = o;
        }
        catch (Exception ex)
        {
            warnings.Add($"{source}: {ConfigSection}.{ConfigKey} is not valid JSON ({ex.Message}); ignored.");
            return new AuditRules(copy);
        }

        foreach (var property in obj.Properties())
        {
            var key = property.Name.Trim();
            if (key.Length == 0) continue;
            if (!copy.TryGetValue(key, out var rule))
            {
                warnings.Add($"{source}: '{key}' is not an audit figure; the threshold has no effect.");
                rule = new AuditThreshold { Key = key, Description = key };
                copy[key] = rule;
            }

            var value = property.Value;
            switch (value.Type)
            {
                case JTokenType.Null:
                case JTokenType.Boolean when !value.Value<bool>():
                    rule.Warn = null;
                    rule.Bad = null;
                    break;
                case JTokenType.Integer:
                case JTokenType.Float:
                    rule.Warn = value.Value<double>();
                    break;
                case JTokenType.Object:
                    if (((JObject)value).TryGetValue("warn", StringComparison.OrdinalIgnoreCase, out var w))
                        rule.Warn = Number(w, key, "warn", source, warnings, rule.Warn);
                    if (((JObject)value).TryGetValue("bad", StringComparison.OrdinalIgnoreCase, out var b))
                        rule.Bad = Number(b, key, "bad", source, warnings, rule.Bad);
                    break;
                default:
                    warnings.Add($"{source}: '{key}' must be a number, an object {{warn, bad}} or false; ignored.");
                    break;
            }
        }
        return new AuditRules(copy);
    }

    private static double? Number(JToken token, string key, string field, string source, List<string> warnings, double? keep)
    {
        if (token.Type == JTokenType.Null) return null;
        if (token.Type is JTokenType.Integer or JTokenType.Float) return token.Value<double>();
        if (token.Type == JTokenType.String &&
            double.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        warnings.Add($"{source}: '{key}.{field}' is not a number; kept {keep?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
        return keep;
    }

    private static AuditThreshold Clone(AuditThreshold t) => new() { Key = t.Key, Warn = t.Warn, Bad = t.Bad, Description = t.Description };
}
