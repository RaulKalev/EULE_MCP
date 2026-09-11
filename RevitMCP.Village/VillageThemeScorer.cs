using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace RevitMCP.Village;

/// <summary>One family/type name with the number of instances it has in the graph (capped by the reader).</summary>
public sealed class VillageTypeSample
{
    public string Name { get; set; } = string.Empty;
    public long InstanceCount { get; set; }
}

/// <summary>Evidence handed to the scorer. Built by <see cref="VillageGraphReader"/> from read-only graph summaries.</summary>
public sealed class VillageThemeInput
{
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Element counts per Revit category (the graph summary's <c>elementsByCategory</c>).</summary>
    public List<KeyValuePair<string, long>> ElementsByCategory { get; set; } = new();

    /// <summary>Family/type names with instance counts. Only used for keyword evidence.</summary>
    public List<VillageTypeSample> Types { get; set; } = new();
}

public sealed class VillageThemeEvidence
{
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("count")] public long Count { get; set; }
}

/// <summary>How one system scored, for the diagnostics panel.</summary>
public sealed class VillageThemeScore
{
    [JsonProperty("system")] public string System { get; set; } = string.Empty;
    [JsonProperty("label")] public string Label { get; set; } = string.Empty;
    [JsonProperty("category_score")] public double CategoryScore { get; set; }
    [JsonProperty("name_score")] public double NameScore { get; set; }
    /// <summary>Name evidence after weighting and capping.</summary>
    [JsonProperty("name_contribution")] public double NameContribution { get; set; }
    [JsonProperty("score")] public double Score { get; set; }
    [JsonProperty("share")] public double Share { get; set; }
    [JsonProperty("visible")] public bool Visible { get; set; }
    [JsonProperty("dominant")] public bool Dominant { get; set; }
    [JsonProperty("matched_types")] public int MatchedTypes { get; set; }
    [JsonProperty("categories")] public List<VillageThemeEvidence> Categories { get; set; } = new();
    [JsonProperty("keywords")] public List<VillageThemeEvidence> Keywords { get; set; } = new();
    [JsonProperty("primary")] public string Primary { get; set; } = string.Empty;
    [JsonProperty("accent")] public string Accent { get; set; } = string.Empty;
}

/// <summary>Stable per-project look derived from the model id, independent of the theme.</summary>
public sealed class VillageIdentity
{
    [JsonProperty("hue")] public int Hue { get; set; }
    [JsonProperty("pattern")] public int Pattern { get; set; }
    [JsonProperty("seed")] public int Seed { get; set; }
}

/// <summary>Result of theme scoring; serialized into the state snapshot as <c>theme</c>.</summary>
public sealed class VillageThemeResult
{
    /// <summary>A system id, <c>mixed</c> or <c>neutral</c>.</summary>
    [JsonProperty("theme")] public string Theme { get; set; } = VillageThemeConfig.Neutral;
    [JsonProperty("label")] public string Label { get; set; } = "Neutral";
    [JsonProperty("dominant")] public string? Dominant { get; set; }
    [JsonProperty("visible_systems")] public List<string> VisibleSystems { get; set; } = new();
    [JsonProperty("scores")] public List<VillageThemeScore> Scores { get; set; } = new();
    [JsonProperty("total_score")] public double TotalScore { get; set; }
    [JsonProperty("sample_count")] public long SampleCount { get; set; }
    [JsonProperty("types_examined")] public int TypesExamined { get; set; }
    [JsonProperty("reason")] public string Reason { get; set; } = string.Empty;
    [JsonProperty("primary")] public string Primary { get; set; } = "#7a8b99";
    [JsonProperty("accent")] public string Accent { get; set; } = "#c9d3dc";
    [JsonProperty("identity")] public VillageIdentity Identity { get; set; } = new();
    [JsonProperty("thresholds")] public Dictionary<string, double> Thresholds { get; set; } = new();
    [JsonProperty("config_source")] public string ConfigSource { get; set; } = "default";
}

/// <summary>
/// Deterministic system-theme scoring. Category counts are strong evidence; family/type name
/// keywords are weaker evidence that is weighted down and capped, so a handful of misnamed
/// families can never turn a project red. Same input → same output, always.
/// </summary>
public sealed class VillageThemeScorer
{
    private static readonly Regex TokenSplit = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

    private readonly VillageThemeConfig _config;

    public VillageThemeScorer(VillageThemeConfig? config = null)
    {
        _config = config ?? VillageThemeConfig.Default();
    }

    public VillageThemeConfig Config => _config;

    public VillageThemeResult Score(VillageThemeInput input)
    {
        var result = new VillageThemeResult
        {
            Identity = IdentityFor(input.ModelId),
            ConfigSource = _config.Source,
            TypesExamined = input.Types.Count,
            Thresholds =
            {
                ["min_sample_count"] = _config.MinSampleCount,
                ["min_system_count"] = _config.MinSystemCount,
                ["dominant_share"] = _config.DominantShare,
                ["visible_share"] = _config.VisibleShare,
                ["category_weight"] = _config.CategoryWeight,
                ["name_weight"] = _config.NameWeight
            }
        };

        long sample = 0;
        foreach (var system in _config.Systems)
        {
            var score = new VillageThemeScore
            {
                System = system.Id,
                Label = system.Label,
                Primary = system.Primary,
                Accent = system.Accent
            };

            // Category evidence
            var categories = new HashSet<string>(system.Categories.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in input.ElementsByCategory)
            {
                if (kv.Value <= 0 || !categories.Contains(kv.Key.Trim())) continue;
                score.CategoryScore += kv.Value;
                score.Categories.Add(new VillageThemeEvidence { Name = kv.Key, Count = kv.Value });
            }
            score.Categories = score.Categories.OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal).Take(5).ToList();

            // Name evidence
            var keywordHits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in input.Types)
            {
                if (type.InstanceCount <= 0) continue;
                var keyword = FirstMatchingKeyword(type.Name, system.Keywords);
                if (keyword == null) continue;
                score.NameScore += type.InstanceCount;
                score.MatchedTypes++;
                keywordHits[keyword] = (keywordHits.TryGetValue(keyword, out var existing) ? existing : 0) + type.InstanceCount;
            }
            score.Keywords = keywordHits
                .OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(k => new VillageThemeEvidence { Name = k.Key, Count = k.Value })
                .ToList();

            // Names can add at most (category evidence + the minimum system count): a few
            // misclassified names cannot outweigh what the categories say.
            var cap = score.CategoryScore * _config.CategoryWeight + _config.MinSystemCount;
            score.NameContribution = Math.Min(score.NameScore * _config.NameWeight, cap);
            score.Score = (score.CategoryScore * _config.CategoryWeight + score.NameContribution) * system.Weight;
            sample += (long)score.CategoryScore;
            result.Scores.Add(score);
        }

        result.SampleCount = sample;
        result.TotalScore = result.Scores.Sum(s => s.Score);

        if (result.TotalScore > 0)
        {
            foreach (var s in result.Scores)
            {
                s.Share = Math.Round(s.Score / result.TotalScore, 4);
                s.Visible = s.Share >= _config.VisibleShare && s.Score >= _config.MinSystemCount;
            }
        }

        result.Scores = result.Scores
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.System, StringComparer.Ordinal)
            .ToList();
        result.VisibleSystems = result.Scores.Where(s => s.Visible).Select(s => s.System).ToList();

        if (sample < _config.MinSampleCount)
        {
            result.Theme = VillageThemeConfig.Neutral;
            result.Label = "Neutral";
            result.Reason = $"Only {sample} classified elements (minimum {_config.MinSampleCount}); the village stays neutral.";
            return Finish(result);
        }

        var top = result.Scores[0];
        if (top.Share >= _config.DominantShare && top.CategoryScore >= _config.MinSystemCount)
        {
            top.Dominant = true;
            result.Theme = top.System;
            result.Label = top.Label;
            result.Dominant = top.System;
            result.Primary = top.Primary;
            result.Accent = top.Accent;
            result.Reason = $"{top.Label} holds {Pct(top.Share)} of the evidence " +
                            $"({Num((long)top.CategoryScore)} by category, {Num((long)top.NameScore)} by name) — dominant.";
            return Finish(result);
        }

        if (result.VisibleSystems.Count >= 2)
        {
            result.Theme = VillageThemeConfig.Mixed;
            result.Label = "Mixed";
            result.Primary = "#6c8e5a";
            result.Accent = "#b7d49b";
            result.Reason = $"No system reaches {Pct(_config.DominantShare)}; {result.VisibleSystems.Count} districts are visible — blended village.";
            return Finish(result);
        }

        if (result.VisibleSystems.Count == 1)
        {
            // One visible system that is not dominant (or whose evidence is names only): show its district, keep neutral colours.
            result.Theme = VillageThemeConfig.Neutral;
            result.Label = "Neutral";
            result.Reason = $"{top.Label} is visible at {Pct(top.Share)} but does not dominate " +
                            $"(needs {Pct(_config.DominantShare)} and {_config.MinSystemCount} category-classified elements).";
            return Finish(result);
        }

        result.Theme = VillageThemeConfig.Neutral;
        result.Label = "Neutral";
        result.Reason = "No system has enough evidence to be visible.";
        return Finish(result);
    }

    private static VillageThemeResult Finish(VillageThemeResult result)
    {
        foreach (var s in result.Scores)
        {
            s.CategoryScore = Math.Round(s.CategoryScore, 2);
            s.NameScore = Math.Round(s.NameScore, 2);
            s.NameContribution = Math.Round(s.NameContribution, 2);
            s.Score = Math.Round(s.Score, 2);
        }
        result.TotalScore = Math.Round(result.TotalScore, 2);
        return result;
    }

    /// <summary>Stable hue/pattern from the model id so the same model always looks the same.</summary>
    public static VillageIdentity IdentityFor(string? modelId)
    {
        var id = (modelId ?? string.Empty).Trim().ToLowerInvariant();
        if (id.Length == 0 || id == VillageModelId.NoDocument)
            return new VillageIdentity { Hue = 200, Pattern = 0, Seed = 0 };

        // FNV-1a over the id: cheap, stable across processes and frameworks.
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in id)
            {
                hash ^= c;
                hash *= 16777619;
            }
            var seed = (int)(hash & 0x7fffffff);
            return new VillageIdentity
            {
                Hue = (int)(hash % 360),
                Pattern = (int)((hash >> 9) % 4),
                Seed = seed
            };
        }
    }

    /// <summary>
    /// First keyword (in configured order) matching the name. Keywords of four characters or
    /// fewer must equal a whole token (tokens split on non-alphanumerics and camelCase);
    /// longer keywords match as substrings. Case-insensitive.
    /// </summary>
    public static string? FirstMatchingKeyword(string? name, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(name) || keywords.Count == 0) return null;
        var lower = name!.ToLowerInvariant();
        HashSet<string>? tokens = null;

        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword)) continue;
            var k = keyword.Trim().ToLowerInvariant();
            if (k.Length <= ShortKeywordLength)
            {
                tokens ??= Tokenize(name!);
                if (tokens.Contains(k)) return keyword;
            }
            else if (lower.IndexOf(k, StringComparison.Ordinal) >= 0)
            {
                return keyword;
            }
        }
        return null;
    }

    /// <summary>Keywords up to this length must match a whole token.</summary>
    public const int ShortKeywordLength = 4;

    /// <summary>Lower-cased tokens: split on non-alphanumerics, then at lower→upper camelCase boundaries.</summary>
    public static HashSet<string> Tokenize(string name)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in TokenSplit.Split(name))
        {
            if (raw.Length == 0) continue;
            var start = 0;
            for (var i = 1; i < raw.Length; i++)
            {
                if (char.IsLower(raw[i - 1]) && char.IsUpper(raw[i]))
                {
                    tokens.Add(raw.Substring(start, i - start).ToLowerInvariant());
                    start = i;
                }
            }
            tokens.Add(raw.Substring(start).ToLowerInvariant());
            tokens.Add(raw.ToLowerInvariant());
        }
        return tokens;
    }

    private static string Pct(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    private static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
