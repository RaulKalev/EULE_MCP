using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Plans;

/// <summary>What a write call names in its arguments: the cheap, early half of a plan check.</summary>
public sealed class PlanTargets
{
    /// <summary>Element ids the call acts on (not the views, types or levels it refers to).</summary>
    public HashSet<long> ElementIds { get; } = new();
    public HashSet<long> ViewIds { get; } = new();
    public List<string> Categories { get; } = new();
    public int? Limit { get; set; }
    public bool UseSelection { get; set; }
}

/// <summary>
/// The pre-check of a plan (#99): collects the ids a call names from its arguments and refuses the call before
/// it runs when they are outside the envelope. A refusal never fails the call — it falls back to the per-call
/// approval. The post-check (<see cref="PlanPostCheck"/>) on what Revit actually changed is authoritative. Pure.
/// </summary>
public static class PlanArgumentScope
{
    /// <summary>Keys whose ids are the elements a call works on.</summary>
    private static readonly HashSet<string> TargetKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "elementId", "elementIds", "id", "ids", "circuitId", "circuitIds", "tagId", "tagIds", "textNoteId", "textNoteIds",
        "sheetId", "sheetIds", "sourceElementIds", "targetElementIds", "hostElementIds", "deviceIds", "panelIds"
    };

    /// <summary>Keys whose ids are views: checked against the envelope's views, not its element list.</summary>
    private static readonly HashSet<string> ViewKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "viewId", "viewIds", "targetViewId", "targetViewIds", "ownerViewId"
    };

    /// <summary>Arguments that are never targets: routing and the plan's own id.</summary>
    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "planId", "checkpointId", "requestId", "approvalId", "document", "expectedStateToken", "pageId"
    };

    public static PlanTargets Extract(IDictionary<string, object?> arguments)
    {
        var targets = new PlanTargets();
        foreach (var pair in arguments)
        {
            if (IgnoredKeys.Contains(pair.Key)) continue;
            var token = PlanArgs.Token(pair.Value);
            if (token == null) continue;

            if (string.Equals(pair.Key, "useSelection", StringComparison.OrdinalIgnoreCase))
                targets.UseSelection = PlanArgs.Bool(token, false);
            else if (string.Equals(pair.Key, "limit", StringComparison.OrdinalIgnoreCase))
                targets.Limit = PlanArgs.Int(token);
            else if (string.Equals(pair.Key, "category", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(pair.Key, "categories", StringComparison.OrdinalIgnoreCase))
                targets.Categories.AddRange(PlanArgs.Strings(token));
            else
                Collect(pair.Key, token, targets, depth: 0);
        }
        return targets;
    }

    /// <summary>
    /// Null when the call may run under the plan, otherwise the reason it waits for approval instead.
    /// </summary>
    /// <param name="selection">The current selection, read only when the call uses it.</param>
    public static string? Check(Plan plan, string toolName, PlanTargets targets, Func<IReadOnlyCollection<long>> selection)
    {
        var env = plan.Envelope;
        if (!env.Tools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            return $"{toolName} is not one of the plan's tools ({string.Join(", ", env.Tools)})";
        if (!env.AllowCreate && PlanToolClassification.MayCreate(toolName))
            return $"{toolName} creates elements and the plan does not allow creating";

        var ids = new HashSet<long>(targets.ElementIds);
        if (targets.UseSelection) ids.UnionWith(selection());

        if (env.ElementIds.Count > 0)
        {
            var outside = ids.Where(id => !env.ElementIds.Contains(id)).Take(10).ToList();
            if (outside.Count > 0)
                return $"element id(s) {string.Join(", ", outside)} are outside the plan's element list";
        }
        if (env.ViewIds.Count > 0)
        {
            var outside = targets.ViewIds.Where(id => !env.ViewIds.Contains(id)).Take(10).ToList();
            if (outside.Count > 0)
                return $"view id(s) {string.Join(", ", outside)} are outside the plan's views";
        }
        if (env.Categories.Count > 0)
        {
            var outside = targets.Categories.Where(c => !env.Categories.Contains(c)).ToList();
            if (outside.Count > 0)
                return $"categor{(outside.Count == 1 ? "y" : "ies")} {string.Join(", ", outside)} outside the plan's categories";
        }
        if (ids.Count > env.MaxElementsPerCall)
            return $"the call names {ids.Count} elements; the plan allows {env.MaxElementsPerCall} per call";
        if (targets.Limit is int limit && limit > env.MaxElementsPerCall)
            return $"limit {limit} is above the plan's {env.MaxElementsPerCall} elements per call";
        return null;
    }

    private static void Collect(string key, JToken token, PlanTargets targets, int depth)
    {
        if (depth > 4) return;
        switch (token)
        {
            case JObject obj:
                foreach (var prop in obj.Properties())
                    if (!IgnoredKeys.Contains(prop.Name)) Collect(prop.Name, prop.Value, targets, depth + 1);
                break;
            case JArray array when array.Count > 0 && array[0] is JObject:
                foreach (var item in array)
                    Collect(key, item, targets, depth + 1);
                break;
            default:
                if (ViewKeys.Contains(key)) targets.ViewIds.UnionWith(PlanArgs.Longs(token));
                else if (IsTargetKey(key)) targets.ElementIds.UnionWith(PlanArgs.Longs(token));
                break;
        }
    }

    /// <summary>Known target keys, and any "…ElementId(s)" key a newer tool adds.</summary>
    private static bool IsTargetKey(string key)
        => TargetKeys.Contains(key) ||
           key.EndsWith("ElementId", StringComparison.OrdinalIgnoreCase) ||
           key.EndsWith("ElementIds", StringComparison.OrdinalIgnoreCase);
}
