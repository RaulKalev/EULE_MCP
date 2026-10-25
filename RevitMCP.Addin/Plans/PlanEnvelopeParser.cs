using Newtonsoft.Json.Linq;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Plans;

/// <summary>A submitted plan before activation: what the user approves.</summary>
public sealed class PlanDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public PlanEnvelope Envelope { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// revit_plan_submit arguments → <see cref="PlanDefinition"/> (#99): defaults, caps, and the tools a plan may
/// never auto-approve. Takes the tool lookups as delegates so it is testable without the handler. Pure.
/// </summary>
public static class PlanEnvelopeParser
{
    public const int MaxSteps = 200;
    public const int MaxTitleLength = 120;
    public const int MaxStepLength = 300;
    public const int MaxElementsPerCallCap = 5_000;
    public const int MaxCallsCap = 1_000;

    /// <param name="permissionOf">The registered tool's permission; null for an unknown name.</param>
    /// <param name="isBackground">True for tools that run off the Revit API thread (they cannot run inside a plan's transaction group).</param>
    public static PlanDefinition Parse(
        IDictionary<string, object?> arguments,
        Func<string, ToolPermission?> permissionOf,
        Func<string, bool> isBackground)
    {
        var def = new PlanDefinition();

        var requestedId = PlanArgs.String(PlanArgs.Token(arguments.TryGetValue("planId", out var pid) ? pid : null)).Trim();
        if (requestedId.Length > 0 && !PlanIds.IsValid(requestedId))
            def.Errors.Add($"planId '{requestedId}' is not a plan id (\"plan-\" + 6 hex); leave it out to get one.");
        def.Id = PlanIds.IsValid(requestedId) ? requestedId : PlanIds.NewId();

        def.Title = PlanArgs.String(PlanArgs.Token(arguments.TryGetValue("title", out var t) ? t : null)).Trim();
        if (def.Title.Length == 0) def.Errors.Add("title is required: one line saying what the plan does.");
        if (def.Title.Length > MaxTitleLength) def.Title = def.Title.Substring(0, MaxTitleLength);

        def.Steps = PlanArgs.Strings(PlanArgs.Token(arguments.TryGetValue("steps", out var s) ? s : null))
            .Select(x => x.Length > MaxStepLength ? x.Substring(0, MaxStepLength) : x).ToList();
        if (def.Steps.Count > MaxSteps)
        {
            def.Warnings.Add($"Only the first {MaxSteps} steps are kept.");
            def.Steps = def.Steps.Take(MaxSteps).ToList();
        }

        var envelope = PlanArgs.Token(arguments.TryGetValue("envelope", out var e) ? e : null) as JObject;
        if (envelope == null)
        {
            def.Errors.Add("envelope is required: at least {\"tools\": [...]} with the write tools the plan will use.");
            return def;
        }

        var env = def.Envelope;
        foreach (var tool in PlanArgs.Strings(envelope["tools"]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var permission = permissionOf(tool);
            if (permission == null)
                def.Errors.Add($"Unknown tool '{tool}'.");
            else if (permission == ToolPermission.DestructiveRequiresManualApproval)
                def.Errors.Add($"'{tool}' is destructive and always needs a manual approval; a plan can never run it without a prompt. Leave it out and call it on its own.");
            else if (isBackground(tool))
                def.Errors.Add($"'{tool}' does not change the Revit model and cannot run under a plan.");
            else if (permission != ToolPermission.RequiresApproval)
                def.Warnings.Add($"'{tool}' needs no approval; it is left out of the plan.");
            else
                env.Tools.Add(tool);
        }
        if (env.Tools.Count == 0 && def.Errors.Count == 0)
            def.Errors.Add("envelope.tools names no write tool that needs approval.");

        var ids = PlanArgs.Longs(envelope["elementIds"]);
        if (ids.Count > PlanEnvelope.MaxElementIds)
            def.Errors.Add($"envelope.elementIds lists {ids.Count:N0} ids; a plan allows at most {PlanEnvelope.MaxElementIds:N0}. Use categories or split the work into several plans.");
        else
            env.ElementIds.UnionWith(ids);
        env.Categories.UnionWith(PlanArgs.Strings(envelope["categories"]));
        env.LevelIds.UnionWith(PlanArgs.Longs(envelope["levelIds"]));
        env.ViewIds.UnionWith(PlanArgs.Longs(envelope["viewIds"]));
        env.Worksets.UnionWith(PlanArgs.Strings(envelope["worksets"]));
        env.AllowCreate = PlanArgs.Bool(envelope["allowCreate"], true);
        env.AllowModify = PlanArgs.Bool(envelope["allowModify"], true);
        env.Strict = PlanArgs.Bool(envelope["strict"], false);

        env.MaxElementsPerCall = Clamp(def, "maxElementsPerCall", PlanArgs.Int(envelope["maxElementsPerCall"]) ?? 500, 1, MaxElementsPerCallCap);
        env.MaxElementsTotal = Clamp(def, "maxElementsTotal", PlanArgs.Int(envelope["maxElementsTotal"]) ?? 2000, 1, PlanEnvelope.MaxElementIds);
        env.MaxCalls = Clamp(def, "maxCalls", PlanArgs.Int(envelope["maxCalls"]) ?? 100, 1, MaxCallsCap);
        env.ExpiresMinutes = Clamp(def, "expiresMinutes", PlanArgs.Int(envelope["expiresMinutes"]) ?? 120, 1, PlanEnvelope.MaxExpiresMinutes);
        if (env.MaxElementsPerCall > env.MaxElementsTotal) env.MaxElementsPerCall = env.MaxElementsTotal;

        if (!env.AllowCreate && !env.AllowModify)
            def.Errors.Add("allowCreate and allowModify are both false: the plan could do nothing.");
        if (env.AllowCreate is false)
        {
            foreach (var tool in env.Tools.Where(PlanToolClassification.MayCreate))
                def.Warnings.Add($"'{tool}' creates elements but allowCreate is false: its calls will wait for approval.");
        }
        if (env.ElementIds.Count == 0 && env.Categories.Count == 0)
            def.Warnings.Add("The envelope is not restricted by elementIds or categories: only the counters limit it.");
        return def;
    }

    private static int Clamp(PlanDefinition def, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            var clamped = Math.Max(min, Math.Min(max, value));
            def.Warnings.Add($"{name} {value} is outside {min}–{max}; {clamped} is used.");
            return clamped;
        }
        return value;
    }
}
