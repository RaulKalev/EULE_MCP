using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Documents;

/// <summary>A view a DWG can be linked into, as the planner sees it.</summary>
public sealed class DwgLinkViewInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>plan | drafting | section | 3d | other (sheets, schedules, legends: nothing can be linked there).</summary>
    public string Kind { get; set; } = "other";
    public string ViewType { get; set; } = string.Empty;
    public bool IsTemplate { get; set; }
}

/// <summary>A DWG link already in the document: its file and the view that owns it (0 = not view-specific).</summary>
public sealed class DwgExistingLink
{
    public string Path { get; set; } = string.Empty;
    public long OwnerViewId { get; set; }
    public long InstanceId { get; set; }
}

/// <summary>One DWG to link, after defaults are applied.</summary>
public sealed class DwgLinkRequest
{
    public int Index { get; set; }
    public string Path { get; set; } = string.Empty;
    public long ViewId { get; set; }
    public string ViewName { get; set; } = string.Empty;
    /// <summary>null = the rule: current view only wherever Revit allows it.</summary>
    public bool? ThisViewOnly { get; set; }
    public string Placement { get; set; } = DwgLinkPlanner.DefaultPlacement;
    public string Unit { get; set; } = DwgLinkPlanner.DefaultUnit;
    public string Colors { get; set; } = "preserve";
    public string Layers { get; set; } = "all";
    public bool CorrectLinesOffAxis { get; set; } = true;
    public bool Pin { get; set; }
}

public sealed class DwgLinkPlanItem
{
    public DwgLinkRequest Request { get; set; } = new();
    public DwgLinkViewInfo? View { get; set; }
    /// <summary>willLink | willSkip | invalid.</summary>
    public string Status { get; set; } = "invalid";
    public bool ThisViewOnly { get; set; }
    public string? Reason { get; set; }
    public List<string> Notes { get; } = new();
    public long? ExistingInstanceId { get; set; }
}

public sealed class DwgLinkContext
{
    public long ActiveViewId { get; set; }
    public IReadOnlyList<DwgLinkViewInfo> Views { get; set; } = Array.Empty<DwgLinkViewInfo>();
    public IReadOnlyList<DwgExistingLink> Existing { get; set; } = Array.Empty<DwgExistingLink>();
    public Func<string, bool> FileExists { get; set; } = File.Exists;
    /// <summary>The file access policy's read check: null = allowed.</summary>
    public Func<string, string?> ValidateRead { get; set; } = _ => null;
}

/// <summary>
/// Plans revit_link_dwg: which DWG goes into which view with which options. The office rule: a DWG linked into a
/// plan, section or drafting view is linked "current view only", and the scale is Revit's automatic detection.
/// A 3D view cannot hold a view-specific link, so the link there is model-wide. Pure, unit tested.
/// </summary>
public static class DwgLinkPlanner
{
    public const int MaxLinks = 50;
    public const string DefaultPlacement = "origin";
    public const string DefaultUnit = "auto";

    public static readonly string[] Placements = { "origin", "center", "shared" };
    public static readonly string[] Units = { "auto", "mm", "cm", "dm", "m", "in", "ft" };
    public static readonly string[] ColorModes = { "preserve", "blackAndWhite", "invert" };
    public static readonly string[] LayerModes = { "all", "visible" };

    /// <summary>Reads links[] (or paths[]) and the top-level defaults.</summary>
    public static List<DwgLinkRequest> Parse(IReadOnlyDictionary<string, object?> args, List<string> errors)
    {
        var defaults = new DwgLinkRequest
        {
            ViewId = Long(Get(args, "viewId")) ?? 0,
            ViewName = Str(Get(args, "view")),
            ThisViewOnly = Bool(Get(args, "thisViewOnly")),
            Placement = Choice(Str(Get(args, "placement")), Placements, DefaultPlacement, "placement", errors),
            Unit = Choice(Str(Get(args, "unit")), Units, DefaultUnit, "unit", errors),
            Colors = Choice(Str(Get(args, "colors")), ColorModes, "preserve", "colors", errors),
            Layers = Choice(Str(Get(args, "layers")), LayerModes, "all", "layers", errors),
            CorrectLinesOffAxis = Bool(Get(args, "correctLinesOffAxis")) ?? true,
            Pin = Bool(Get(args, "pin")) ?? false
        };

        var list = new List<DwgLinkRequest>();
        var index = 0;
        foreach (var token in Array(Get(args, "links"), "links", errors))
        {
            var i = index++;
            if (token is JValue { Type: JTokenType.String } pathOnly)
            {
                list.Add(Copy(defaults, i, ((string?)pathOnly ?? string.Empty).Trim()));
                continue;
            }
            if (token is not JObject item)
            {
                errors.Add($"links[{i}] must be an object {{path, viewId?, …}} or a path.");
                continue;
            }
            var request = Copy(defaults, i, Str(item["path"]));
            if (item["viewId"] != null || item["view"] != null)
            {
                request.ViewId = Long(item["viewId"]) ?? 0;
                request.ViewName = Str(item["view"]);
            }
            request.ThisViewOnly = Bool(item["thisViewOnly"]) ?? defaults.ThisViewOnly;
            if (item["placement"] != null) request.Placement = Choice(Str(item["placement"]), Placements, defaults.Placement, $"links[{i}].placement", errors);
            if (item["unit"] != null) request.Unit = Choice(Str(item["unit"]), Units, defaults.Unit, $"links[{i}].unit", errors);
            if (item["colors"] != null) request.Colors = Choice(Str(item["colors"]), ColorModes, defaults.Colors, $"links[{i}].colors", errors);
            if (item["layers"] != null) request.Layers = Choice(Str(item["layers"]), LayerModes, defaults.Layers, $"links[{i}].layers", errors);
            request.CorrectLinesOffAxis = Bool(item["correctLinesOffAxis"]) ?? defaults.CorrectLinesOffAxis;
            request.Pin = Bool(item["pin"]) ?? defaults.Pin;
            list.Add(request);
        }
        foreach (var token in Array(Get(args, "paths"), "paths", errors))
            list.Add(Copy(defaults, index++, ((string?)token ?? string.Empty).Trim()));

        if (list.Count == 0) errors.Add("Nothing to link: give links [{path, viewId?}] or paths [...] with a viewId (default: the active view).");
        else if (list.Count > MaxLinks) errors.Add($"{list.Count} links were requested; at most {MaxLinks} per call.");
        return list;
    }

    public static List<DwgLinkPlanItem> Plan(IReadOnlyList<DwgLinkRequest> requests, DwgLinkContext context, string ifExists)
    {
        var linkAgain = string.Equals(ifExists, "linkAgain", StringComparison.OrdinalIgnoreCase);
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<DwgLinkPlanItem>();
        foreach (var request in requests)
        {
            var item = new DwgLinkPlanItem { Request = request };
            items.Add(item);

            var pathError = CheckPath(request.Path, context);
            if (pathError != null) { item.Reason = pathError; continue; }

            var (view, viewError) = ResolveView(request, context);
            item.View = view;
            if (view == null) { item.Reason = viewError; continue; }

            switch (view.Kind)
            {
                case "plan":
                case "section":
                    item.ThisViewOnly = request.ThisViewOnly ?? true;
                    if (!item.ThisViewOnly)
                        item.Notes.Add($"thisViewOnly=false: the DWG shows in every view that sees it, not only '{view.Name}'.");
                    break;
                case "drafting":
                    item.ThisViewOnly = true;
                    if (request.ThisViewOnly == false)
                        item.Notes.Add("A drafting view only holds view-specific links: linked current view only.");
                    break;
                case "3d":
                    if (request.ThisViewOnly == true)
                    {
                        item.Reason = $"'{view.Name}' is a 3D view: Revit cannot link current view only there. Pick a plan, or pass thisViewOnly=false.";
                        continue;
                    }
                    item.ThisViewOnly = false;
                    item.Notes.Add($"'{view.Name}' is a 3D view: the DWG is linked into the model and shows in every view that sees it.");
                    break;
                default:
                    item.Reason = $"'{view.Name}' ({view.ViewType}) cannot hold a DWG link: pick a plan, section, drafting or 3D view.";
                    continue;
            }

            var key = Normalize(request.Path) + "|" + (item.ThisViewOnly ? view.Id : 0);
            if (!planned.Add(key))
            {
                item.Reason = item.ThisViewOnly
                    ? $"The same file is already in this request for '{view.Name}'."
                    : "The same file is already in this request as a model-wide link.";
                continue;
            }

            var existing = context.Existing.FirstOrDefault(e =>
                string.Equals(Normalize(e.Path), Normalize(request.Path), StringComparison.OrdinalIgnoreCase)
                && e.OwnerViewId == (item.ThisViewOnly ? view.Id : 0));
            if (existing != null)
            {
                item.ExistingInstanceId = existing.InstanceId;
                if (!linkAgain)
                {
                    item.Status = "willSkip";
                    item.Reason = item.ThisViewOnly
                        ? $"This file is already linked into '{view.Name}' (instance {existing.InstanceId}); ifExists=skip."
                        : $"This file is already linked model-wide (instance {existing.InstanceId}); ifExists=skip.";
                    continue;
                }
                item.Notes.Add($"Already linked (instance {existing.InstanceId}); ifExists=linkAgain adds a second instance.");
            }

            if (request.Unit == "auto")
                item.Notes.Add("Scale: Revit detects the DWG's units; check the extents in the result.");
            item.Status = "willLink";
        }
        return items;
    }

    /// <summary>Plain-language check of the linked extents: a wrong unit shows as a drawing 1000 times too big or small.</summary>
    public static string? CheckExtents(double widthM, double heightM)
    {
        var size = Math.Max(widthM, heightM);
        if (size <= 0) return null;
        if (size > 5000) return $"The linked DWG is {size:0} m across: the detected units may be wrong (mm read as m?). Relink with an explicit unit.";
        if (size < 0.5) return $"The linked DWG is only {size:0.###} m across: the detected units may be wrong (m read as mm?). Relink with an explicit unit.";
        return null;
    }

    public static string Normalize(string path)
    {
        try { return System.IO.Path.GetFullPath(path.Trim()).TrimEnd('\\', '/'); }
        catch { return path.Trim(); }
    }

    private static string? CheckPath(string path, DwgLinkContext context)
    {
        if (string.IsNullOrWhiteSpace(path)) return "path is empty.";
        if (!path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) return $"'{path}' is not a .dwg file.";
        if (!System.IO.Path.IsPathRooted(path)) return $"'{path}' must be an absolute path.";
        var policy = context.ValidateRead(path);
        if (policy != null) return $"'{path}': {policy}";
        return context.FileExists(path) ? null : $"'{path}' does not exist.";
    }

    private static (DwgLinkViewInfo? View, string? Error) ResolveView(DwgLinkRequest request, DwgLinkContext context)
    {
        if (request.ViewId > 0)
        {
            var byId = context.Views.FirstOrDefault(v => v.Id == request.ViewId);
            if (byId == null) return (null, $"View {request.ViewId} was not found.");
            return byId.IsTemplate ? (null, $"'{byId.Name}' is a view template.") : (byId, null);
        }
        if (request.ViewName.Length > 0)
        {
            var matches = context.Views.Where(v => !v.IsTemplate && string.Equals(v.Name, request.ViewName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return (matches[0], null);
            return matches.Count == 0
                ? (null, $"No view is named '{request.ViewName}'.")
                : (null, $"{matches.Count} views are named '{request.ViewName}' ({string.Join(", ", matches.Select(m => $"{m.ViewType} {m.Id}"))}); pass viewId.");
        }
        var active = context.Views.FirstOrDefault(v => v.Id == context.ActiveViewId);
        return active != null ? (active, null) : (null, "No view given and there is no active view: pass viewId or view.");
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static DwgLinkRequest Copy(DwgLinkRequest d, int index, string path) => new()
    {
        Index = index, Path = path, ViewId = d.ViewId, ViewName = d.ViewName, ThisViewOnly = d.ThisViewOnly,
        Placement = d.Placement, Unit = d.Unit, Colors = d.Colors, Layers = d.Layers,
        CorrectLinesOffAxis = d.CorrectLinesOffAxis, Pin = d.Pin
    };

    private static string Choice(string value, string[] allowed, string fallback, string name, List<string> errors)
    {
        if (value.Length == 0) return fallback;
        var match = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;
        errors.Add($"{name} '{value}' is not one of {string.Join(" | ", allowed)}.");
        return fallback;
    }

    private static object? Get(IReadOnlyDictionary<string, object?> args, string key) => args.TryGetValue(key, out var v) ? v : null;

    private static JToken? Token(object? raw) => raw switch
    {
        null => null,
        JToken t => t,
        string s when s.TrimStart().StartsWith("[", StringComparison.Ordinal) || s.TrimStart().StartsWith("{", StringComparison.Ordinal) => TryParse(s),
        string s => new JValue(s),
        _ => TryFrom(raw)
    };

    private static IEnumerable<JToken> Array(object? raw, string name, List<string> errors)
    {
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return System.Array.Empty<JToken>();
        if (token is JArray array) return array;
        if (token.Type == JTokenType.String && ((string?)token ?? string.Empty).Trim().Length == 0) return System.Array.Empty<JToken>();
        errors.Add($"{name} must be an array.");
        return System.Array.Empty<JToken>();
    }

    private static string Str(object? raw)
    {
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return string.Empty;
        return (token is JValue v ? v.Value?.ToString() : token.ToString())?.Trim() ?? string.Empty;
    }

    private static long? Long(object? raw)
    {
        var token = Token(raw);
        if (token == null) return null;
        if (token.Type == JTokenType.Integer) return (long)token;
        if (token.Type == JTokenType.Float) return (long)(double)token;
        return long.TryParse(Str(token), out var v) ? v : null;
    }

    private static bool? Bool(object? raw)
    {
        if (raw is bool b) return b;
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type == JTokenType.Boolean) return (bool)token;
        return bool.TryParse(Str(token), out var v) ? v : null;
    }

    private static JToken? TryParse(string s)
    {
        try { return JToken.Parse(s); }
        catch { return null; }
    }

    private static JToken? TryFrom(object raw)
    {
        try { return JToken.FromObject(raw); }
        catch { return null; }
    }
}
