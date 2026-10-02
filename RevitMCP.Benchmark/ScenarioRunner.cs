using System.Text.Json.Nodes;

namespace RevitMCP.Benchmark;

/// <summary>
/// Runs the scenarios of a scenario file. Setup steps resolve bindings (ids, names) from the model
/// and are not measured; a scenario whose bindings do not resolve is reported as skipped with the
/// reason, so models without (say) electrical panels still produce an honest report.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly BenchmarkRunner _runner;
    private readonly Dictionary<string, JsonNode?> _globals;

    public ScenarioRunner(BenchmarkRunner runner, Dictionary<string, JsonNode?> globals)
    {
        _runner = runner;
        _globals = globals;
    }

    public async Task<ScenarioResult> Run(JsonObject scenario, IReadOnlyCollection<string>? onlyProfiles)
    {
        var result = new ScenarioResult
        {
            Id = scenario["id"]?.ToString() ?? "?",
            Title = scenario["title"]?.ToString() ?? scenario["id"]?.ToString() ?? "?"
        };
        var bindings = new Dictionary<string, JsonNode?>(_globals, StringComparer.OrdinalIgnoreCase);

        if (scenario["disabled"]?.ToString() is { Length: > 0 } disabled)
        {
            result.Skipped = disabled;
            return result;
        }

        // Setup: resolve bindings with the full profile; not measured.
        foreach (var step in (scenario["setup"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var (ok, error) = await RunStep("full", step, bindings, measure: null);
            if (!ok)
            {
                result.Skipped = error;
                return result;
            }
        }

        var variants = (scenario["variants"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var variant in variants)
        {
            var profileTemplate = variant["profile"]?.ToString() ?? "full";
            var profile = BenchmarkMath.Fill(profileTemplate, bindings, out _) ?? profileTemplate;
            var v = new VariantResult { Name = variant["name"]?.ToString() ?? "?", Profile = profile };
            result.Variants.Add(v);
            if (onlyProfiles is { Count: > 0 } && !onlyProfiles.Contains(profile, StringComparer.OrdinalIgnoreCase))
            {
                v.Skipped = "profile not selected";
                continue;
            }

            var schema = await _runner.MeasureSchema(profile);
            if (schema.Error != null)
            {
                v.Skipped = "profile failed to start: " + schema.Error;
                continue;
            }
            v.SchemaTokens = schema.SchemaTokens;

            var steps = variant["steps"] as JsonArray;
            if (steps == null && variant["sameStepsAs"]?.ToString() is { } other)
                steps = variants.FirstOrDefault(x => x["name"]?.ToString() == other)?["steps"] as JsonArray;

            var local = new Dictionary<string, JsonNode?>(bindings, StringComparer.OrdinalIgnoreCase);
            foreach (var step in (steps ?? []).OfType<JsonObject>())
            {
                var (ok, error) = await RunStep(profile, step, local, v.Steps);
                if (!ok && error != null)
                {
                    // A binding the workflow needs is missing (e.g. no devices in that room).
                    v.Skipped = error;
                    break;
                }
                if (!ok) break;   // a measured call failed; it is reported in the step list
            }
        }
        return result;
    }

    /// <summary>Runs one step; binds values from its result. Returns false with a reason when the workflow cannot continue.</summary>
    private async Task<(bool Ok, string? Error)> RunStep(
        string profile, JsonObject step, Dictionary<string, JsonNode?> bindings, List<StepMeasurement>? measure)
    {
        var tool = step["tool"]?.ToString() ?? throw new InvalidOperationException("Step without 'tool'.");
        var template = (step["args"] ?? new JsonObject()).ToJsonString();
        var filled = BenchmarkMath.Fill(template, bindings, out var missing);
        if (filled == null) return (false, $"binding '{missing}' is not available");

        var args = JsonNode.Parse(filled) as JsonObject ?? new JsonObject();
        var (m, json) = await _runner.Call(profile, tool, args);
        measure?.Add(m);
        if (!m.Success)
            return (false, measure == null ? $"setup call {tool} failed: {m.Error}" : null);

        if (step["bind"] is JsonObject bind)
        {
            foreach (var pair in bind)
            {
                var value = BenchmarkMath.Select(json, pair.Value?.ToString() ?? "");
                if (pair.Key.EndsWith("ids", StringComparison.OrdinalIgnoreCase)) value = BenchmarkMath.AsIdArray(value);
                if (value == null)
                {
                    var reason = step["skipReason"]?.ToString() ?? $"'{pair.Key}' not found in the {tool} result";
                    return (false, reason);
                }
                bindings[pair.Key] = value;
            }
        }
        return (true, null);
    }
}
