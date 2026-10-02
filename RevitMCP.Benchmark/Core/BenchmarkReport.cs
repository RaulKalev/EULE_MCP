using System.Globalization;
using System.Text;

namespace RevitMCP.Benchmark;

/// <summary>Advertised tool-schema cost of one bridge profile.</summary>
public sealed class SchemaMeasurement
{
    public string Profile { get; set; } = string.Empty;
    public int ToolCount { get; set; }
    public int SchemaBytes { get; set; }
    public int SchemaTokens { get; set; }
    public string? Error { get; set; }
}

/// <summary>One tool call inside a workflow variant.</summary>
public sealed class StepMeasurement
{
    public string Tool { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int ResultBytes { get; set; }
    public int ResultTokens { get; set; }
    public long ElapsedMs { get; set; }
    public int LiveElements { get; set; }
    public string? Error { get; set; }
}

/// <summary>One way of doing a scenario (broad live, graph-first, graph-first with a reduced surface).</summary>
public sealed class VariantResult
{
    public string Name { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public int SchemaTokens { get; set; }
    public List<StepMeasurement> Steps { get; set; } = [];
    public string? Skipped { get; set; }

    public int ToolCalls => Steps.Count;
    public int ResultBytes => Steps.Sum(s => s.ResultBytes);
    public int ResultTokens => Steps.Sum(s => s.ResultTokens);
    public long ElapsedMs => Steps.Sum(s => s.ElapsedMs);
    public int LiveElements => Steps.Sum(s => s.LiveElements);
    public int Failures => Steps.Count(s => !s.Success);
}

public sealed class ScenarioResult
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Skipped { get; set; }
    public List<VariantResult> Variants { get; set; } = [];
}

public sealed class GraphBuildMeasurement
{
    public bool Success { get; set; }
    public long ElapsedMs { get; set; }
    public int? NodeCount { get; set; }
    public int? EdgeCount { get; set; }
    public int? ModelElementCount { get; set; }
    public bool? Incremental { get; set; }
    public string? Error { get; set; }
}

public sealed class BenchmarkRun
{
    public string Label { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string StartedAt { get; set; } = string.Empty;
    public string BridgePath { get; set; } = string.Empty;
    public List<SchemaMeasurement> Schemas { get; set; } = [];
    public GraphBuildMeasurement? GraphBuild { get; set; }
    public GraphBuildMeasurement? GraphIncremental { get; set; }
    public List<ScenarioResult> Scenarios { get; set; } = [];
}

/// <summary>Renders a run as Markdown. Pure — unit tested in RevitMCP.Tests.</summary>
public static class BenchmarkReport
{
    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    public static string ToMarkdown(BenchmarkRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# MCP benchmark - {run.Label}");
        sb.AppendLine();
        sb.AppendLine($"- Model: `{run.Model}`");
        sb.AppendLine($"- Started: {run.StartedAt}");
        sb.AppendLine($"- Bridge: `{run.BridgePath}`");
        sb.AppendLine($"- Token estimates: characters / {BenchmarkMath.CharsPerToken:0.#} (approximation; bytes are exact)");
        sb.AppendLine();

        sb.AppendLine("## Tool-schema cost per profile");
        sb.AppendLine();
        sb.AppendLine("Paid once per session (or per request when the client does not cache tool definitions).");
        sb.AppendLine();
        sb.AppendLine("| Profile | Tools | `tools/list` bytes | ~tokens |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var s in run.Schemas)
            sb.AppendLine(s.Error != null
                ? $"| `{s.Profile}` | — | — | error: {s.Error} |"
                : $"| `{s.Profile}` | {s.ToolCount} | {N(s.SchemaBytes)} | {N(s.SchemaTokens)} |");
        sb.AppendLine();

        if (run.GraphBuild != null || run.GraphIncremental != null)
        {
            sb.AppendLine("## Graph build cost");
            sb.AppendLine();
            sb.AppendLine("| Build | Result | Elapsed | Nodes | Edges | Model elements |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|");
            void Row(string name, GraphBuildMeasurement? g)
            {
                if (g == null) return;
                sb.AppendLine($"| {name} | {(g.Success ? "ok" : "failed: " + g.Error)} | {N(g.ElapsedMs)} ms | {(g.NodeCount is { } n ? N(n) : "—")} | {(g.EdgeCount is { } e ? N(e) : "—")} | {(g.ModelElementCount is { } m ? N(m) : "—")} |");
            }
            Row("full", run.GraphBuild);
            Row("incremental", run.GraphIncremental);
            sb.AppendLine();
        }

        sb.AppendLine("## Scenarios");
        sb.AppendLine();
        sb.AppendLine("Result cost is paid per call. *Total* = schema tokens of the profile + result tokens of the workflow.");
        sb.AppendLine();
        foreach (var sc in run.Scenarios)
        {
            sb.AppendLine($"### {sc.Title} (`{sc.Id}`)");
            sb.AppendLine();
            if (sc.Skipped != null)
            {
                sb.AppendLine($"Skipped: {sc.Skipped}");
                sb.AppendLine();
                continue;
            }
            sb.AppendLine("| Variant | Profile | Calls | Result bytes | Result ~tokens | Schema ~tokens | Total ~tokens | Live elements | Elapsed | Failures |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var v in sc.Variants)
            {
                if (v.Skipped != null)
                {
                    sb.AppendLine($"| {v.Name} | `{v.Profile}` | skipped: {v.Skipped} ||||||||");
                    continue;
                }
                sb.AppendLine($"| {v.Name} | `{v.Profile}` | {v.ToolCalls} | {N(v.ResultBytes)} | {N(v.ResultTokens)} | {N(v.SchemaTokens)} | {N(v.SchemaTokens + v.ResultTokens)} | {N(v.LiveElements)} | {N(v.ElapsedMs)} ms | {v.Failures} |");
            }
            var failed = sc.Variants.SelectMany(v => v.Steps.Where(s => !s.Success).Select(s => $"{v.Name}: `{s.Tool}` — {s.Error}")).ToList();
            if (failed.Count > 0)
            {
                sb.AppendLine();
                foreach (var f in failed) sb.AppendLine($"- {f}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
