using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.LinkOperations;

public sealed class LinkModelRequest
{
    public string Path { get; set; } = string.Empty;
    /// <summary>ifc | rvt; null = from the extension.</summary>
    public string? Kind { get; set; }
    public string Positioning { get; set; } = "origin";
    public bool Pin { get; set; }
    public string IfExists { get; set; } = "skip";
    public bool RegenerateCache { get; set; }
}

public sealed class ExistingLinkInfo
{
    public long TypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    /// <summary>ifc | rvt (a CAD link is not a model link).</summary>
    public string Kind { get; set; } = LinkKinds.Rvt;
    public string Status { get; set; } = string.Empty;
    public int InstanceCount { get; set; }
}

public sealed class LinkPlanContext
{
    public string TargetKey { get; set; } = string.Empty;
    public Func<string, bool> FileExists { get; set; } = _ => false;
    /// <summary>The connector's path policy (FilePathPolicy.ValidateRead); null error = allowed.</summary>
    public Func<string, string?> ValidateRead { get; set; } = _ => null;
    public Func<string, LinkCacheFacts?> CacheOf { get; set; } = _ => null;
    public IReadOnlyList<ExistingLinkInfo> ExistingLinks { get; set; } = new List<ExistingLinkInfo>();
    public IReadOnlyList<LinkOperation> Operations { get; set; } = new List<LinkOperation>();
}

public static class LinkPlanActions
{
    /// <summary>A new link will be created.</summary>
    public const string Link = "link";
    /// <summary>An existing link is refreshed through the reload path.</summary>
    public const string Refresh = "refresh";
    /// <summary>Already linked, ifExists=skip.</summary>
    public const string Skip = "skip";
    public const string Invalid = "invalid";
    /// <summary>An operation for this file is queued or running: its id is returned, nothing starts.</summary>
    public const string InProgress = "already_in_progress";
    /// <summary>An earlier operation ended unknown: reconcile first.</summary>
    public const string NeedsReview = "needs_review";
    public const string QueueFull = "queue_full";
}

public sealed class LinkPlanItem
{
    public LinkModelRequest Request { get; set; } = new();
    public string Kind { get; set; } = string.Empty;
    public string CanonicalSource { get; set; } = string.Empty;
    public string CachePath { get; set; } = string.Empty;
    public string Action { get; set; } = LinkPlanActions.Invalid;
    public string? Reason { get; set; }
    public ExistingLinkInfo? ExistingLink { get; set; }
    public LinkOperation? Operation { get; set; }
    public LinkCacheFacts? Cache { get; set; }
    public List<string> Notes { get; set; } = new();

    /// <summary>Would create an operation.</summary>
    public bool WillRun => Action == LinkPlanActions.Link || Action == LinkPlanActions.Refresh;
}

/// <summary>
/// Parses and plans revit_link_models (#163), pure: IFC-first model linking. Per file it decides link, refresh,
/// skip, invalid or already in progress, and says what the conversion will cost. Positioning other than origin and
/// shared, formats other than .ifc and .rvt, and relative or disallowed paths are refused here rather than at Revit.
/// </summary>
public static class LinkModelsPlanner
{
    public const int MaxFiles = 25;
    public static readonly string[] Positionings = { "origin", "shared" };

    public static List<LinkModelRequest> Parse(Dictionary<string, object?> args, List<string> errors)
    {
        var defaultPositioning = GetString(args, "positioning") ?? "origin";
        var defaultPin = GetBool(args, "pin");
        var defaultIfExists = GetString(args, "ifExists") ?? "skip";
        var defaultRegenerate = GetBool(args, "regenerateIfcCache");
        var result = new List<LinkModelRequest>();

        var files = GetToken(args, "files", errors) as JArray;
        var paths = GetToken(args, "paths", errors) as JArray;
        if ((files == null || files.Count == 0) && (paths == null || paths.Count == 0))
        {
            if (errors.Count == 0)
                errors.Add("files is required: [{\"path\": \"C:\\\\...\\\\model.ifc\"}] (or paths: [\"...\"]). IFC first; .rvt works too.");
            return result;
        }

        var items = new List<JToken>();
        if (files != null) items.AddRange(files);
        if (paths != null) items.AddRange(paths);
        if (items.Count > MaxFiles)
        {
            errors.Add($"At most {MaxFiles} files per call; got {items.Count}. Split them: they run one at a time anyway.");
            return result;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var obj = item as JObject;
            var path = obj != null ? (string?)obj["path"] : item.Type == JTokenType.String ? (string?)item : null;
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add($"files[{i}].path is required.");
                continue;
            }
            result.Add(new LinkModelRequest
            {
                Path = path!.Trim().Trim('"'),
                Kind = (obj != null ? (string?)obj["kind"] : null)?.Trim().ToLowerInvariant(),
                Positioning = ((obj != null ? (string?)obj["positioning"] : null) ?? defaultPositioning).Trim().ToLowerInvariant(),
                Pin = obj?["pin"] != null ? (bool)obj["pin"]! : defaultPin,
                IfExists = ((obj != null ? (string?)obj["ifExists"] : null) ?? defaultIfExists).Trim().ToLowerInvariant(),
                RegenerateCache = obj?["regenerateIfcCache"] != null ? (bool)obj["regenerateIfcCache"]! : defaultRegenerate
            });
        }
        return result;
    }

    public static List<LinkPlanItem> Plan(IReadOnlyList<LinkModelRequest> requests, LinkPlanContext context)
    {
        var plan = new List<LinkPlanItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Operations this very plan would create, so two files in one call cannot admit each other twice.
        foreach (var request in requests)
        {
            var item = new LinkPlanItem { Request = request, CanonicalSource = LinkSourcePath.Canonical(request.Path) };
            plan.Add(item);

            var fail = Validate(request, item, context);
            if (fail != null)
            {
                item.Action = LinkPlanActions.Invalid;
                item.Reason = fail;
                continue;
            }
            if (!seen.Add(item.CanonicalSource))
            {
                item.Action = LinkPlanActions.Invalid;
                item.Reason = "duplicate_in_request: the same file appears twice in this call.";
                continue;
            }

            if (item.Kind == LinkKinds.Ifc)
            {
                item.CachePath = LinkSourcePath.CachePathOf(request.Path);
                item.Cache = context.CacheOf(item.CachePath);
                if (item.Cache is { Exists: true, Fresh: true } && !request.RegenerateCache)
                    item.Notes.Add($"The IFC cache {item.CachePath} is newer than the IFC: Revit reuses it, no conversion.");
                else
                    item.Notes.Add("Revit converts the IFC to " + item.CachePath + ". For a large IFC this takes minutes, during which Revit " +
                                   "does not answer; revit_link_operation_status answers without Revit. It is never started twice.");
            }
            if (LinkSourcePath.IsNetworkPath(request.Path))
                item.Notes.Add("Network path: reading and converting over the network is slow and the cache is written next to the IFC.");

            var existing = context.ExistingLinks.FirstOrDefault(l =>
                l.Kind == item.Kind && LinkSourcePath.SameSource(l.Path, request.Path));
            item.ExistingLink = existing;

            var admission = LinkOperationRules.Admit(context.Operations, item.CanonicalSource, context.TargetKey,
                existing != null && request.IfExists == "refresh" ? LinkOperationTypes.Refresh : LinkOperationTypes.Link);
            if (admission.Outcome == AdmissionOutcome.AlreadyInProgress)
            {
                item.Action = LinkPlanActions.InProgress;
                item.Operation = admission.Existing;
                item.Reason = admission.Message;
                continue;
            }
            if (admission.Outcome == AdmissionOutcome.NeedsReview)
            {
                item.Action = LinkPlanActions.NeedsReview;
                item.Operation = admission.Existing;
                item.Reason = admission.Message;
                continue;
            }

            if (existing != null)
            {
                if (request.IfExists == "refresh")
                {
                    item.Action = LinkPlanActions.Refresh;
                    item.Notes.Add(item.Kind == LinkKinds.Ifc
                        ? "Refresh uses the same path as revit_reload_links_from: an IFC from the same file updates in place (ids and overrides kept)."
                        : "Refresh reloads the RVT link in place.");
                }
                else
                {
                    item.Action = LinkPlanActions.Skip;
                    item.Reason = $"already_linked: '{existing.Name}' (type {existing.TypeId}, {existing.Status}, {existing.InstanceCount} instance(s)). Pass ifExists=refresh to reload it.";
                }
                continue;
            }

            item.Action = LinkPlanActions.Link;
            if (request.IfExists == "refresh") item.Notes.Add("ifExists=refresh, but no link to this file exists: it is linked new.");
        }

        // Admission of the whole call: bound the open operations, counting this call's new ones.
        var open = context.Operations.Count(o => LinkOperationStates.IsActive(o.State));
        foreach (var item in plan.Where(p => p.WillRun))
        {
            if (open++ >= LinkOperationRules.MaxOpenOperations)
            {
                item.Action = LinkPlanActions.QueueFull;
                item.Reason = $"{LinkOperationRules.MaxOpenOperations} link operations are already queued or running.";
            }
        }
        var runs = plan.Count(p => p.WillRun);
        if (runs > 1)
            foreach (var item in plan.Where(p => p.WillRun))
                item.Notes.Add($"{runs} files: they run one at a time, in request order, never in parallel.");
        return plan;
    }

    private static string? Validate(LinkModelRequest request, LinkPlanItem item, LinkPlanContext context)
    {
        string? error = null;
        try
        {
            if (!System.IO.Path.IsPathRooted(request.Path)) error = "path_not_absolute: pass a full path.";
        }
        catch (ArgumentException)
        {
            error = "invalid_path: the path contains characters Windows does not allow.";
        }
        if (error != null) return error;

        var kind = LinkSourcePath.KindOf(request.Path);
        if (kind == null)
        {
            var lower = request.Path.ToLowerInvariant();
            if (lower.EndsWith(".ifc.rvt", StringComparison.Ordinal))
                return "unsupported_format: that is the conversion cache. Link the .ifc itself; Revit manages the cache.";
            if (lower.EndsWith(".dwg", StringComparison.Ordinal) || lower.EndsWith(".dxf", StringComparison.Ordinal) || lower.EndsWith(".dgn", StringComparison.Ordinal))
                return "unsupported_format: CAD files link with revit_link_dwg.";
            return "unsupported_format: only .ifc (first choice) and .rvt files are linked here (.ifczip and .ifcxml are not supported).";
        }
        if (request.Kind != null && request.Kind != kind)
            return $"kind_mismatch: kind '{request.Kind}' does not match the .{kind} extension.";
        item.Kind = kind;

        if (Array.IndexOf(Positionings, request.Positioning) < 0)
        {
            return request.Positioning is "center" or "centre" or "project" or "base"
                ? $"unsupported_positioning: '{request.Positioning}' is not available through the Revit API for links. Use origin (origin to origin) or shared (shared coordinates)."
                : $"unsupported_positioning: '{request.Positioning}'. Use origin or shared.";
        }
        if (request.IfExists != "skip" && request.IfExists != "refresh")
            return $"invalid_ifExists: '{request.IfExists}'. Use skip or refresh.";

        var policy = context.ValidateRead(request.Path);
        if (policy != null) return "path_not_allowed: " + policy;
        if (!context.FileExists(request.Path)) return "file_not_found: " + request.Path;
        return null;
    }

    private static object? GetToken(Dictionary<string, object?> args, string key, List<string> errors)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return null;
        try
        {
            return raw switch
            {
                JArray array => array,
                string text when string.IsNullOrWhiteSpace(text) => null,
                string text => JToken.Parse(text) as JArray,
                _ => JArray.FromObject(raw)
            };
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or ArgumentException)
        {
            errors.Add($"{key} could not be parsed: {ex.Message}");
            return null;
        }
    }

    private static string? GetString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return null;
        var text = raw is JValue value ? value.ToString() : raw.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool GetBool(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return false;
        if (raw is bool b) return b;
        if (raw is JValue { Type: JTokenType.Boolean } jv) return (bool)jv;
        return bool.TryParse(raw.ToString(), out var parsed) && parsed;
    }
}
