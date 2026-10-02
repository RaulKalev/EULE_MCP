using System.Text.Json;
using System.Text.Json.Nodes;
using RevitMCP.Benchmark;

// RevitMCP MCP benchmark (issue #67).
//
//   RevitMCP.Benchmark [--bridge <RevitMCP.Bridge.exe>] [--scenarios <file>] [--out <dir>]
//                      [--label <text>] [--profiles full,query,...] [--schema-only]
//                      [--var name=value ...] [--no-graph-build]
//
// Starts the bridge once per tool profile with the official MCP client, measures the advertised
// tool-schema size, builds the model graph (cost recorded), then runs each scenario's variants
// against the running Revit and writes a Markdown + JSON report.

var options = ParseArgs(args);
var root = FindRepoRoot();
var bridge = options.GetValueOrDefault("bridge")
             ?? Path.Combine(root ?? ".", "RevitMCP.Bridge", "bin", "Release", "net8.0", "RevitMCP.Bridge.exe");
var scenarioFile = options.GetValueOrDefault("scenarios") ?? Path.Combine(AppContext.BaseDirectory, "scenarios.json");
var outDir = options.GetValueOrDefault("out") ?? Path.Combine(root ?? ".", "docs", "benchmarks");
var schemaOnly = options.ContainsKey("schema-only");
var profiles = (options.GetValueOrDefault("profiles") ?? "full,query,read-only")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (!File.Exists(bridge))
{
    Console.Error.WriteLine($"Bridge not found: {bridge}. Build it (dotnet build RevitMCP.Bridge -c Release) or pass --bridge.");
    return 2;
}

var spec = JsonNode.Parse(File.ReadAllText(scenarioFile)) as JsonObject ?? new JsonObject();
var globals = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
if (spec["variables"] is JsonObject vars)
    foreach (var v in vars) globals[v.Key] = v.Value?.DeepClone();
foreach (var kv in options.Where(o => o.Key.StartsWith("var:")))
    globals[kv.Key.Substring(4)] = JsonValue.Create(kv.Value);

var run = new BenchmarkRun
{
    Label = options.GetValueOrDefault("label") ?? "run",
    StartedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
    BridgePath = Path.GetFullPath(bridge)
};

await using (var runner = new BenchmarkRunner(bridge))
{
    foreach (var profile in profiles)
    {
        Console.WriteLine($"schema: {profile}");
        run.Schemas.Add(await runner.MeasureSchema(profile));
    }

    if (!schemaOnly)
    {
        var (status, statusJson) = await runner.Call("full", "revit_get_connection_status", new JsonObject());
        run.Model = statusJson?["data"]?["documentTitle"]?.ToString()
                    ?? statusJson?["data"]?["DocumentTitle"]?.ToString()
                    ?? (status.Success ? "?" : "Revit not reachable: " + status.Error);

        if (!options.ContainsKey("no-graph-build"))
        {
            Console.WriteLine("graph build (full)");
            run.GraphBuild = await MeasureBuild(runner, incremental: false);
            Console.WriteLine("graph build (incremental)");
            run.GraphIncremental = await MeasureBuild(runner, incremental: true);
        }

        var scenarioRunner = new ScenarioRunner(runner, globals);
        foreach (var scenario in (spec["scenarios"] as JsonArray ?? []).OfType<JsonObject>())
        {
            Console.WriteLine($"scenario: {scenario["id"]}");
            run.Scenarios.Add(await scenarioRunner.Run(scenario, profiles));
        }
    }
}

Directory.CreateDirectory(outDir);
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var safeLabel = string.Concat(run.Label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
var md = Path.Combine(outDir, $"{stamp}-{safeLabel}.md");
var json = Path.Combine(outDir, $"{stamp}-{safeLabel}.json");
File.WriteAllText(md, BenchmarkReport.ToMarkdown(run));
File.WriteAllText(json, JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(BenchmarkReport.ToMarkdown(run));
Console.WriteLine($"Report: {md}");
return run.Scenarios.Any(s => s.Variants.Any(v => v.Failures > 0)) ? 1 : 0;

static async Task<GraphBuildMeasurement> MeasureBuild(BenchmarkRunner runner, bool incremental)
{
    var args = new JsonObject { ["incremental"] = incremental };
    var (step, json) = await runner.Call("full", "revit_graph_build", args);
    var data = json?["data"];
    return new GraphBuildMeasurement
    {
        Success = step.Success,
        ElapsedMs = step.ElapsedMs,
        NodeCount = (int?)data?["nodeCount"],
        EdgeCount = (int?)data?["edgeCount"],
        ModelElementCount = (int?)data?["elementCount"],
        Incremental = (bool?)data?["incremental"]?["applied"],
        Error = step.Error
    };
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i].Substring(2);
        if (key == "var" && i + 1 < args.Length)
        {
            var kv = args[++i].Split('=', 2);
            if (kv.Length == 2) result["var:" + kv[0]] = kv[1];
            continue;
        }
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
        result[key] = hasValue ? args[++i] : "true";
    }
    return result;
}

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx"))) dir = dir.Parent;
    return dir?.FullName;
}
