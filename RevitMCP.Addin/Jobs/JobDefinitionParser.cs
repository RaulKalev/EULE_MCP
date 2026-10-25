using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Plans;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Jobs;

/// <summary>A submitted job before it starts: the job itself and the plan its approval activates.</summary>
public sealed class JobDefinition
{
    public Job Job { get; set; } = new();
    public PlanDefinition Plan { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// revit_job_submit arguments → <see cref="JobDefinition"/> (#100): steps with their tools checked against the
/// registry, the destructive-step policy, placeholders that only use variables bound by an earlier step, bind
/// paths in the supported subset, stop conditions, and the plan envelope (its tools default to the job's
/// approval-gated write tools). Pure.
/// </summary>
public static class JobDefinitionParser
{
    public const int MaxSteps = 200;
    public const int MaxTitleLength = 120;

    public static JobDefinition Parse(
        IDictionary<string, object?> arguments,
        Func<string, ToolPermission?> permissionOf,
        Func<string, bool> isBackground,
        string? jobId = null)
    {
        var def = new JobDefinition();
        var job = def.Job;
        job.Id = JobIds.IsValid(jobId) ? jobId! : JobIds.NewId();
        job.CreatedAt = DateTimeOffset.Now;

        job.Title = PlanArgs.String(Arg(arguments, "title")).Trim();
        if (job.Title.Length == 0) def.Errors.Add("title is required: one line saying what the job does.");
        if (job.Title.Length > MaxTitleLength) job.Title = job.Title.Substring(0, MaxTitleLength);

        var allowDestructive = PlanArgs.Bool(Arg(arguments, "allowDestructiveSteps"), false);
        job.Report = PlanArgs.String(Arg(arguments, "report"), "auto").Trim().ToLowerInvariant() == "never" ? "never" : "auto";
        job.OpenReport = PlanArgs.Bool(Arg(arguments, "openReport"), true);

        ParseSteps(def, Arg(arguments, "steps") as JArray, permissionOf, allowDestructive);
        ParseStop(def, Arg(arguments, "stop") as JObject);
        ParseEnvelope(def, arguments, permissionOf, isBackground);
        return def;
    }

    /// <summary>The plan definition for <paramref name="job"/>'s stored envelope, with a fresh plan id (resume).</summary>
    public static PlanDefinition PlanFor(Job job, Func<string, ToolPermission?> permissionOf, Func<string, bool> isBackground)
        => PlanEnvelopeParser.Parse(new Dictionary<string, object?>
        {
            ["title"] = "Job: " + job.Title,
            ["steps"] = new JArray(job.PlanSteps),
            ["envelope"] = job.Envelope.DeepClone()
        }, permissionOf, isBackground);

    private static void ParseSteps(JobDefinition def, JArray? steps, Func<string, ToolPermission?> permissionOf, bool allowDestructive)
    {
        if (steps == null || steps.Count == 0)
        {
            def.Errors.Add("steps is required: [{\"tool\":\"revit_…\", \"arguments\":{…}, \"bind\":{\"ids\":\"$.data.items[*].id\"}}, …].");
            return;
        }
        if (steps.Count > MaxSteps)
        {
            def.Errors.Add($"A job holds at most {MaxSteps} steps; this one has {steps.Count}. Split it into several jobs.");
            return;
        }

        var bound = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var label = $"step {i + 1}";
            if (steps[i] is not JObject s)
            {
                def.Errors.Add($"{label} is not an object.");
                continue;
            }
            var step = new JobStep
            {
                Index = i,
                Tool = PlanArgs.String(s["tool"]).Trim(),
                Arguments = s["arguments"] as JObject ?? new JObject(),
                Title = PlanArgs.String(s["title"]) is { Length: > 0 } title ? title.Trim() : null,
                ContinueOnError = PlanArgs.Bool(s["continueOnError"], false),
                Optional = PlanArgs.Bool(s["optional"], false)
            };
            def.Job.Steps.Add(step);

            // A combined bridge tool (revit_set_parameters_bulk entity=views …) runs as the add-in tool it routes to.
            string? routeError = null;
            if (permissionOf(step.Tool) == null &&
                BridgeToolRoutes.TryResolveStep(step.Tool, step.Arguments, out var resolvedTool, out var resolvedArguments, out routeError))
            {
                step.Tool = resolvedTool;
                step.Arguments = resolvedArguments;
            }

            var permission = step.Tool.Length == 0 ? null : permissionOf(step.Tool);
            if (routeError != null)
                def.Errors.Add($"{label}: {routeError}");
            else if (permission == null)
                def.Errors.Add($"{label}: unknown tool '{step.Tool}'.");
            else if (step.Tool.StartsWith("revit_job_", StringComparison.Ordinal) || step.Tool.StartsWith("revit_plan_", StringComparison.Ordinal))
                def.Errors.Add($"{label}: {step.Tool} cannot run inside a job.");
            else if (permission == ToolPermission.DestructiveRequiresManualApproval && !allowDestructive)
                def.Errors.Add($"{label}: {step.Tool} is destructive. Pass allowDestructiveSteps=true to include it; it will still wait for a manual approval when it runs.");
            step.IsReadOnly = permission == ToolPermission.ReadOnly;

            foreach (var name in JobJsonPath.Placeholders(step.Arguments))
            {
                if (!bound.Contains(name))
                    def.Errors.Add($"{label}: {{{{{name}}}}} is not bound by an earlier step (bind it with \"bind\": {{\"{name}\": \"$.data…\"}}).");
            }

            if (s["bind"] is JObject bind)
            {
                step.Bind = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in bind.Properties())
                {
                    var path = PlanArgs.String(prop.Value);
                    if (!JobJsonPath.TryParse(path, out var error)) def.Errors.Add($"{label}: bind '{prop.Name}': {error}");
                    else step.Bind[prop.Name] = path;
                }
                bound.UnionWith(step.Bind.Keys);
            }
        }
    }

    private static void ParseStop(JobDefinition def, JObject? stop)
    {
        var s = def.Job.Stop;
        if (stop == null) return;
        s.StopOnFailure = PlanArgs.Bool(stop["stopOnFailure"], true);
        var mode = PlanArgs.String(stop["stopOnNewWarnings"], "error").Trim().ToLowerInvariant();
        if (mode is "none" or "error" or "any") s.StopOnNewWarnings = mode;
        else def.Warnings.Add($"stopOnNewWarnings '{mode}' is not none, error or any; error is used.");
        s.MaxNewWarnings = Clamp(PlanArgs.Int(stop["maxNewWarnings"]) ?? 0, 0, 1000);
        s.MaxDurationMinutes = Clamp(PlanArgs.Int(stop["maxDurationMinutes"]) ?? 120, 1, PlanEnvelope.MaxExpiresMinutes);
        s.MaxConsecutiveBusy = Clamp(PlanArgs.Int(stop["maxConsecutiveBusy"]) ?? 20, 1, 100);
    }

    private static void ParseEnvelope(JobDefinition def, IDictionary<string, object?> arguments, Func<string, ToolPermission?> permissionOf, Func<string, bool> isBackground)
    {
        var envelope = (Arg(arguments, "envelope") as JObject)?.DeepClone() as JObject ?? new JObject();
        if (envelope["tools"] is not JArray { Count: > 0 })
        {
            // Default: every approval-gated write tool the steps call (destructive ones always prompt anyway).
            var tools = def.Job.Steps
                .Select(s => s.Tool)
                .Where(t => permissionOf(t) == ToolPermission.RequiresApproval && !isBackground(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            envelope["tools"] = new JArray(tools);
            if (tools.Count == 0)
                def.Warnings.Add("The job has no approval-gated write steps; its plan covers nothing.");
        }
        // The plan's duration follows the job's unless the envelope sets one.
        if (envelope["expiresMinutes"] == null) envelope["expiresMinutes"] = def.Job.Stop.MaxDurationMinutes;

        def.Job.Envelope = envelope;
        def.Job.PlanSteps = def.Job.Steps.Select(s => s.DisplayTitle).ToList();
        if (def.Job.Steps.Count == 0) return;

        def.Plan = PlanFor(def.Job, permissionOf, isBackground);
        // An empty tool list is fine for a read-only job: the plan simply covers nothing.
        def.Errors.AddRange(def.Plan.Errors.Where(e => !e.StartsWith("envelope.tools names no", StringComparison.Ordinal)));
        def.Warnings.AddRange(def.Plan.Warnings);

        foreach (var step in def.Job.Steps.Where(s => !s.IsReadOnly))
        {
            if (permissionOf(step.Tool) == ToolPermission.RequiresApproval && !isBackground(step.Tool) &&
                !def.Plan.Envelope.Tools.Contains(step.Tool, StringComparer.OrdinalIgnoreCase))
                def.Warnings.Add($"step {step.Index + 1}: {step.Tool} is not in envelope.tools; it will wait for its own approval.");
        }
    }

    private static JToken? Arg(IDictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var v) ? PlanArgs.Token(v) : null;

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
