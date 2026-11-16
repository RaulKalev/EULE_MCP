using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ViewFilters;

namespace RevitMCP.Addin.ElementGraphics;

/// <summary>The four operations of revit_set_element_graphics (#160).</summary>
public static class ElementGraphicsOperations
{
    public const string Hide = "hide";
    public const string Unhide = "unhide";
    public const string Override = "override";
    public const string ResetOverrides = "resetOverrides";

    public static readonly string[] All = { Hide, Unhide, Override, ResetOverrides };
}

/// <summary>Why an element is, or stays, not drawn in a view, or why a request cannot be applied to it.</summary>
public static class ElementGraphicsCodes
{
    // Eligibility (per element, for a write).
    public const string NotFound = "notFound";
    public const string NotInView = "notInView";
    public const string CannotBeHidden = "cannotBeHidden";
    public const string AlreadyHidden = "alreadyHidden";
    public const string NotHidden = "notHidden";
    public const string NoOverrides = "noOverrides";
    public const string SameOverrides = "sameOverrides";

    // Reasons an element is not drawn (read, and "stays invisible" after a write).
    public const string HiddenInView = "hiddenInView";
    public const string CategoryHidden = "categoryHidden";
    public const string FilterHidden = "filterHidden";
    public const string TemporaryHideIsolate = "temporaryHideIsolate";
    public const string WorksetHidden = "worksetHidden";
}
// NotInView (above) doubles as the "not drawn for another reason" code.

/// <summary>What is known about one element in one view. Filled from the Revit API; judged here, so the judgement is unit tested.</summary>
public sealed class ElementGraphicsFacts
{
    public bool Exists { get; set; } = true;

    /// <summary>The view's own element collector returns the element (hidden elements are not returned).</summary>
    public bool InViewCollector { get; set; }

    /// <summary>Element.IsHidden(view): hidden through "Hide in View > Elements".</summary>
    public bool HiddenPermanently { get; set; }
    public bool CanBeHidden { get; set; } = true;
    public bool CategoryHidden { get; set; }
    public List<string> HidingFilters { get; } = new();
    public bool WorksetHidden { get; set; }

    /// <summary>True when the view is in temporary hide/isolate and the element is not shown by it; null when the view is not in that mode.</summary>
    public bool? HiddenByTemporaryMode { get; set; }

    public bool HasOverrides { get; set; }
}

public sealed class ElementGraphicsReason
{
    public ElementGraphicsReason(string code, string text)
    {
        Code = code;
        Text = text;
    }

    public string Code { get; }
    public string Text { get; }
}

/// <summary>The outcome of one element for one operation.</summary>
public enum ElementGraphicsOutcome
{
    WillChange,
    Unchanged,
    Ineligible
}

public sealed class ElementGraphicsDecision
{
    public ElementGraphicsOutcome Outcome { get; set; }
    public string? Code { get; set; }
    public string? Reason { get; set; }

    /// <summary>For a decision that will change the element: reasons it still will not be drawn afterwards.</summary>
    public List<ElementGraphicsReason> StillSuppressed { get; } = new();
}

/// <summary>Pure judgement of what an operation does to an element and why an element is not drawn.</summary>
public static class ElementGraphicsPlanner
{
    /// <summary>Everything that keeps the element out of the view, optionally including its own permanent hide.</summary>
    public static List<ElementGraphicsReason> SuppressionReasons(ElementGraphicsFacts facts, bool includePermanentHide)
    {
        var reasons = new List<ElementGraphicsReason>();
        if (!facts.Exists) return reasons;
        if (includePermanentHide && facts.HiddenPermanently)
            reasons.Add(new ElementGraphicsReason(ElementGraphicsCodes.HiddenInView, "Hidden in this view (Hide in View > Elements)."));
        if (facts.CategoryHidden)
            reasons.Add(new ElementGraphicsReason(ElementGraphicsCodes.CategoryHidden, "Its category is switched off in this view (Visibility/Graphics; may be controlled by the view template)."));
        foreach (var filter in facts.HidingFilters)
            reasons.Add(new ElementGraphicsReason(ElementGraphicsCodes.FilterHidden, $"View filter '{filter}' has its visibility switched off and the element passes it."));
        if (facts.WorksetHidden)
            reasons.Add(new ElementGraphicsReason(ElementGraphicsCodes.WorksetHidden, "Its workset is not visible in this view."));
        if (facts.HiddenByTemporaryMode == true)
            reasons.Add(new ElementGraphicsReason(ElementGraphicsCodes.TemporaryHideIsolate, "The view is in temporary hide/isolate mode and the element is not part of it."));
        return reasons;
    }

    /// <summary>
    /// True when nothing hides the element but it is also not returned by the view: outside the crop, the view range or
    /// its level/phase/design option, or not a view-specific element of a drafting or legend view.
    /// </summary>
    public static bool NotDrawnForOtherReasons(ElementGraphicsFacts facts) =>
        facts.Exists && !facts.InViewCollector && !facts.HiddenPermanently && SuppressionReasons(facts, includePermanentHide: false).Count == 0;

    /// <summary>Every reason the element is not drawn; empty when nothing known suppresses it. Never promises visibility: see <see cref="NotDrawnForOtherReasons"/>.</summary>
    public static List<ElementGraphicsReason> AllReasons(ElementGraphicsFacts facts)
    {
        var reasons = SuppressionReasons(facts, includePermanentHide: true);
        if (NotDrawnForOtherReasons(facts))
            reasons.Add(new ElementGraphicsReason(ElementGraphicsCodes.NotInView, NotInViewText));
        return reasons;
    }

    public static ElementGraphicsDecision Decide(string operation, ElementGraphicsFacts facts, bool overridesDiffer = true)
    {
        var decision = new ElementGraphicsDecision();
        if (!facts.Exists)
            return Ineligible(decision, ElementGraphicsCodes.NotFound, "No such element in the document.");

        switch (operation)
        {
            case ElementGraphicsOperations.Hide:
                if (facts.HiddenPermanently) return Unchanged(decision, ElementGraphicsCodes.AlreadyHidden, "Already hidden in this view.");
                if (!facts.CanBeHidden) return Ineligible(decision, ElementGraphicsCodes.CannotBeHidden, "Revit does not allow this element to be hidden in this view.");
                if (!facts.InViewCollector) return Ineligible(decision, ElementGraphicsCodes.NotInView, NotInViewText);
                return decision;

            case ElementGraphicsOperations.Unhide:
                if (!facts.HiddenPermanently) return Unchanged(decision, ElementGraphicsCodes.NotHidden, "Not hidden through Hide in View > Elements; nothing to unhide.");
                decision.StillSuppressed.AddRange(SuppressionReasons(facts, includePermanentHide: false));
                return decision;

            case ElementGraphicsOperations.Override:
                if (facts.HiddenPermanently)
                {
                    // Allowed, but invisible: say so instead of promising a visible result.
                    decision.StillSuppressed.Add(new ElementGraphicsReason(ElementGraphicsCodes.HiddenInView, "Hidden in this view: the override is stored but not drawn until it is unhidden."));
                }
                else if (!facts.InViewCollector)
                {
                    return Ineligible(decision, ElementGraphicsCodes.NotInView, NotInViewText);
                }
                if (!overridesDiffer) return Unchanged(decision, ElementGraphicsCodes.SameOverrides, "The element already has exactly these overrides.");
                decision.StillSuppressed.AddRange(SuppressionReasons(facts, includePermanentHide: false));
                return decision;

            case ElementGraphicsOperations.ResetOverrides:
                if (!facts.HasOverrides) return Unchanged(decision, ElementGraphicsCodes.NoOverrides, "The element has no element-level overrides in this view.");
                return decision;

            default:
                return Ineligible(decision, "invalidOperation", $"Unknown operation '{operation}'.");
        }
    }

    private const string NotInViewText =
        "The element is not in this view (other level, outside the crop or view range, another phase or design option, or suppressed by category/filter/workset).";

    private static ElementGraphicsDecision Ineligible(ElementGraphicsDecision d, string code, string reason)
    {
        d.Outcome = ElementGraphicsOutcome.Ineligible;
        d.Code = code;
        d.Reason = reason;
        return d;
    }

    private static ElementGraphicsDecision Unchanged(ElementGraphicsDecision d, string code, string reason)
    {
        d.Outcome = ElementGraphicsOutcome.Unchanged;
        d.Code = code;
        d.Reason = reason;
        return d;
    }
}

/// <summary>Parsed arguments of revit_get_element_graphics / revit_preview_set_element_graphics / revit_set_element_graphics.</summary>
public sealed class ElementGraphicsRequest
{
    public const int MaxElements = 5000;
    public const int MaxReadElements = 500;

    public long ViewId { get; set; }
    public bool UseActiveView { get; set; }
    public bool UseSelection { get; set; }
    public List<long> ElementIds { get; } = new();
    public string Operation { get; set; } = string.Empty;
    public ViewFilterGraphicsSpec? Graphics { get; set; }
    public List<ViewFilterIssue> GraphicsIssues { get; } = new();
}

public static class ElementGraphicsRequestParser
{
    /// <param name="args">The tool arguments.</param>
    /// <param name="requireOperation">false for the read tool.</param>
    /// <param name="error">The reason the arguments cannot be used.</param>
    public static ElementGraphicsRequest? Parse(Dictionary<string, object?> args, bool requireOperation, out string? error)
    {
        error = null;
        var request = new ElementGraphicsRequest
        {
            ViewId = Long(args, "viewId"),
            UseActiveView = Bool(args, "useActiveView"),
            UseSelection = Bool(args, "useSelection")
        };

        if (request.ViewId <= 0 && !request.UseActiveView)
        {
            error = "Name the view: viewId, or useActiveView=true for the active view of the active document.";
            return null;
        }
        if (request.ViewId > 0 && request.UseActiveView)
        {
            error = "Give viewId or useActiveView, not both.";
            return null;
        }

        if (args.TryGetValue("elementIds", out var raw) && raw != null)
        {
            var seen = new HashSet<long>();
            foreach (var token in Tokens(raw))
            {
                if (!TryLong(token, out var id) || id <= 0)
                {
                    error = $"elementIds must be positive integers (got {token}).";
                    return null;
                }
                if (seen.Add(id)) request.ElementIds.Add(id);
            }
        }
        if (request.UseSelection && request.ElementIds.Count > 0)
        {
            error = "Give elementIds or useSelection, not both.";
            return null;
        }
        if (!request.UseSelection && request.ElementIds.Count == 0)
        {
            error = "No elements: give elementIds or useSelection=true.";
            return null;
        }
        if (request.ElementIds.Count > ElementGraphicsRequest.MaxElements)
        {
            error = $"{request.ElementIds.Count} elements; the limit is {ElementGraphicsRequest.MaxElements} per call.";
            return null;
        }

        if (!requireOperation) return request;

        request.Operation = ResolveOperation(Text(args, "operation"));
        if (request.Operation.Length == 0)
        {
            error = $"operation must be one of {string.Join(", ", ElementGraphicsOperations.All)}.";
            return null;
        }

        args.TryGetValue("graphics", out var rawGraphics);
        var graphicsObject = rawGraphics as JObject;
        if (rawGraphics != null && graphicsObject == null && !(rawGraphics is JToken { Type: JTokenType.Null }))
        {
            error = "graphics must be an object, for example {\"projectionLines\":{\"color\":\"#FF0000\",\"weight\":5},\"halftone\":true}.";
            return null;
        }
        if (graphicsObject != null)
            request.Graphics = ViewFilterRequestParser.ParseGraphics(graphicsObject, request.GraphicsIssues, "graphics");

        if (request.Operation == ElementGraphicsOperations.Override)
        {
            if (request.GraphicsIssues.Count > 0)
            {
                error = "graphics: " + string.Join("; ", request.GraphicsIssues.Select(i => $"{i.Path}: {i.Reason}"));
                return null;
            }
            if (request.Graphics == null)
            {
                error = "operation=override needs graphics (at least one of halftone, transparency, projectionLines, cutLines, surfaceForeground, surfaceBackground, cutForeground, cutBackground).";
                return null;
            }
        }
        else if (graphicsObject != null && request.Graphics != null)
        {
            error = $"graphics only applies to operation=override, not {request.Operation}.";
            return null;
        }
        return request;
    }

    /// <summary>Case-insensitive operation names; "reset", "clear" and "resetoverrides" mean resetOverrides. Empty when unknown.</summary>
    public static string ResolveOperation(string text)
    {
        var key = (text ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        return key switch
        {
            "hide" => ElementGraphicsOperations.Hide,
            "unhide" or "show" => ElementGraphicsOperations.Unhide,
            "override" or "set" => ElementGraphicsOperations.Override,
            "resetoverrides" or "reset" or "clear" or "clearoverrides" => ElementGraphicsOperations.ResetOverrides,
            _ => string.Empty
        };
    }

    private static IEnumerable<JToken> Tokens(object raw)
    {
        switch (raw)
        {
            case JArray array:
                return array;
            case long[] longs:
                return longs.Select(l => (JToken)new JValue(l));
            case IEnumerable<long> list:
                return list.Select(l => (JToken)new JValue(l));
            case JToken single:
                return new[] { single };
            default:
                return new[] { (JToken)new JValue(raw) };
        }
    }

    private static bool TryLong(JToken token, out long value)
    {
        value = 0;
        if (token.Type == JTokenType.Integer)
        {
            value = token.Value<long>();
            return true;
        }
        return token.Type == JTokenType.String && long.TryParse(token.ToString(), out value);
    }

    private static string Text(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) && v != null ? v.ToString() ?? string.Empty : string.Empty;

    private static long Long(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var v) || v == null) return 0;
        return v switch
        {
            long l => l,
            int i => i,
            JValue { Type: JTokenType.Integer } jv => jv.Value<long>(),
            JValue { Type: JTokenType.String } jv => long.TryParse(jv.ToString(), out var p) ? p : 0,
            string s => long.TryParse(s, out var p) ? p : 0,
            _ => 0
        };
    }

    private static bool Bool(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var v) || v == null) return false;
        return v switch
        {
            bool b => b,
            JValue { Type: JTokenType.Boolean } jv => jv.Value<bool>(),
            JValue { Type: JTokenType.String } jv => bool.TryParse(jv.ToString(), out var p) && p,
            string s => bool.TryParse(s, out var p) && p,
            _ => false
        };
    }
}
