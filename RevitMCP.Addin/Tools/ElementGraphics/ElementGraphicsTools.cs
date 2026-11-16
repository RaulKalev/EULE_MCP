using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.ElementGraphics;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Tools.ViewFilters;
using RevitMCP.Addin.ViewFilters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ElementGraphics;

/// <summary>revit_get_element_graphics (#160): per-element hide state and overrides in one view.</summary>
public sealed class GetElementGraphicsTool : IRevitMcpTool
{
    public const string ToolName = "revit_get_element_graphics";
    public string Name => ToolName;

    public string Description =>
        "Reads, for elements in one view, whether each is hidden (Hide in View > Elements), whether it can be hidden, its " +
        "element-level graphic overrides, and every known reason it is not drawn anyway (category off, a view filter with " +
        "visibility off, hidden workset, temporary hide/isolate, not in the view at all). View: viewId or useActiveView=true. " +
        "Elements: elementIds or useSelection=true (max 500 listed). Overrides use the shape of revit_apply_view_filters graphics " +
        "(#RRGGBB colors, line weight 1-16, pattern names, transparency 0-100).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ElementGraphicsExecutor.Read(uiapp, request, cancellationToken));
}

/// <summary>revit_preview_set_element_graphics (#160).</summary>
public sealed class PreviewSetElementGraphicsTool : IRevitMcpTool
{
    public const string ToolName = "revit_preview_set_element_graphics";
    public string Name => ToolName;

    public string Description =>
        "Previews hide, unhide, override or resetOverrides of individual elements in one view WITHOUT changing the model. Same " +
        "arguments as revit_set_element_graphics. Returns counts (willChange, unchanged, ineligible with a reason code), sampled " +
        "examples, the view's limits (template, temporary hide/isolate, ownership) and which elements would still not be drawn.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ElementGraphicsExecutor.Write(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_set_element_graphics (#160).</summary>
public sealed class SetElementGraphicsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public const string ToolName = "revit_set_element_graphics";
    public string Name => ToolName;

    public string Description =>
        "Per-element view graphics in ONE view, one transaction. operation = hide | unhide | override | resetOverrides. " +
        "Elements: elementIds or useSelection=true. View: viewId or useActiveView=true (never a view template). override takes " +
        "graphics {halftone, transparency 0-100, projectionLines|cutLines {color \"#RRGGBB\"|[r,g,b], weight 1-16, pattern name|solid|none}, " +
        "surfaceForeground|surfaceBackground|cutForeground|cutBackground {color, pattern, visible}} and changes only the fields given; " +
        "resetOverrides clears all element overrides. Unhide only undoes Hide in View > Elements: category, filter, workset and " +
        "temporary-isolate visibility are not touched, and the result lists elements that remain undrawn and why. Refused while the view " +
        "is in temporary hide/isolate mode or owned by another user. Does not edit view templates, categories or filters (use " +
        "revit_apply_view_filters for those). Run revit_preview_set_element_graphics first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ElementGraphicsExecutor.Write(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        try
        {
            return ElementGraphicsExecutor.ApprovalSummary(uiapp, request);
        }
        catch (Exception ex)
        {
            Swallowed.Note(ex);
            return null;
        }
    }
}

internal static class ElementGraphicsExecutor
{
    private const int DefaultSample = 20;

    private sealed class Entry
    {
        public long Id;
        public Element? Element;
        public ElementGraphicsFacts Facts = new();
        public OverrideGraphicSettings? Overrides;
        public ElementGraphicsDecision? Decision;
        public bool OverridesDiffer = true;
    }

    private sealed class Context
    {
        public Document Doc = null!;
        public View View = null!;
        public List<string> Warnings = new();
        public string? OwnedByOther;
        public bool TemporaryMode;
        public string? TemplateName;
        public List<long> ElementIds = new();
    }

    // ── Read ────────────────────────────────────────────────────────────────

    public static McpToolResult Read(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var parsed = ElementGraphicsRequestParser.Parse(request.Arguments, requireOperation: false, out var error);
        if (parsed == null) return Fail(request, error!);
        var context = Resolve(uiapp, request, parsed, out var failure);
        if (context == null) return failure!;

        var ids = context.ElementIds;
        var truncated = ids.Count > ElementGraphicsRequest.MaxReadElements;
        if (truncated) ids = ids.Take(ElementGraphicsRequest.MaxReadElements).ToList();
        if (truncated)
            context.Warnings.Add($"{context.ElementIds.Count} elements given; the first {ElementGraphicsRequest.MaxReadElements} are listed.");

        var entries = Gather(context, ids, cancellationToken);
        var rows = new JArray();
        var hidden = 0;
        var notDrawn = 0;
        var withOverrides = 0;
        var missing = 0;
        foreach (var entry in entries)
        {
            var reasons = ElementGraphicsPlanner.AllReasons(entry.Facts);
            if (!entry.Facts.Exists) missing++;
            if (entry.Facts.HiddenPermanently) hidden++;
            if (entry.Facts.HasOverrides) withOverrides++;
            if (entry.Facts.Exists && reasons.Count > 0) notDrawn++;

            var row = Describe(entry);
            row["hiddenInView"] = entry.Facts.HiddenPermanently;
            row["canBeHidden"] = entry.Facts.CanBeHidden;
            row["inView"] = entry.Facts.InViewCollector;
            row["hasOverrides"] = entry.Facts.HasOverrides;
            if (entry.Overrides != null && entry.Facts.HasOverrides) row["overrides"] = ViewFilterGraphics.Describe(context.Doc, entry.Overrides);
            row["notDrawnBecause"] = Reasons(reasons);
            rows.Add(row);
        }

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{entries.Count} element(s) in view '{context.View.Name}': {hidden} hidden, {withOverrides} with overrides, {notDrawn} not drawn for some reason, {missing} not found.",
            Data = new
            {
                view = ViewInfo(context),
                elements = rows,
                counts = new { requested = context.ElementIds.Count, listed = entries.Count, hiddenInView = hidden, withOverrides, notDrawn, notFound = missing },
                note = "notDrawnBecause lists what is known to keep an element out of the view; an empty list does not guarantee it is drawn (e.g. detail level, phase filter, a crop region)."
            },
            Warnings = context.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    // ── Preview / apply ─────────────────────────────────────────────────────

    public static McpToolResult Write(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var parsed = ElementGraphicsRequestParser.Parse(request.Arguments, requireOperation: true, out var error);
        if (parsed == null) return Fail(request, error!);
        var sample = Math.Max(1, Math.Min(200, ToolArguments.GetInt(request.Arguments, "sampleSize", DefaultSample)));
        var context = Resolve(uiapp, request, parsed, out var failure);
        if (context == null) return failure!;

        var plan = BuildPlan(context, parsed, cancellationToken, out var planError, out var graphics);
        if (plan == null) return Fail(request, planError!);

        var willChange = plan.Where(e => e.Decision!.Outcome == ElementGraphicsOutcome.WillChange).ToList();
        var unchanged = plan.Count(e => e.Decision!.Outcome == ElementGraphicsOutcome.Unchanged);
        var ineligible = plan.Count(e => e.Decision!.Outcome == ElementGraphicsOutcome.Ineligible);
        var warnings = context.Warnings;

        if (willChange.Any(e => e.Decision!.StillSuppressed.Count > 0))
            warnings.Add("Some elements will still not be drawn after this change; see stillSuppressed. Nothing here changes categories, filters, worksets or view templates.");

        var refusal = apply ? WriteRefusal(context) : null;
        if (refusal != null) return Fail(request, refusal, "view_not_writable");
        if (!apply && (context.TemporaryMode || context.OwnedByOther != null))
            warnings.Add("revit_set_element_graphics would be refused for this view right now (see view.limits).");

        if (!apply || willChange.Count == 0)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = apply
                    ? $"Nothing to change in view '{context.View.Name}': {unchanged} unchanged, {ineligible} ineligible."
                    : $"Preview ({parsed.Operation}) in view '{context.View.Name}': {willChange.Count} will change, {unchanged} unchanged, {ineligible} ineligible.",
                Data = Summary(context, parsed, plan, sample, applied: apply ? 0 : -1, afterwards: null),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        // ── Write ──
        var failures = new List<(long Id, string Message)>();
        var changedIds = new List<long>();
        cancellationToken.ThrowIfCancellationRequested();
        using (var tx = new Transaction(context.Doc, "Revit MCP - Set Element Graphics"))
        {
            tx.Start();
            var view = context.View;
            switch (parsed.Operation)
            {
                case ElementGraphicsOperations.Hide:
                case ElementGraphicsOperations.Unhide:
                    var hide = parsed.Operation == ElementGraphicsOperations.Hide;
                    var set = willChange.Select(e => new ElementId(e.Id)).ToList();
                    try
                    {
                        if (hide) view.HideElements(set);
                        else view.UnhideElements(set);
                        changedIds.AddRange(willChange.Select(e => e.Id));
                    }
                    catch (Exception)
                    {
                        // One element Revit refuses must not sink the batch: go one by one.
                        foreach (var entry in willChange)
                        {
                            try
                            {
                                var one = new List<ElementId> { new(entry.Id) };
                                if (hide) view.HideElements(one);
                                else view.UnhideElements(one);
                                changedIds.Add(entry.Id);
                            }
                            catch (Exception ex)
                            {
                                failures.Add((entry.Id, ex.Message));
                            }
                        }
                    }
                    break;

                default:
                    var n = 0;
                    foreach (var entry in willChange)
                    {
                        if (++n % 500 == 0) cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var id = new ElementId(entry.Id);
                            var settings = parsed.Operation == ElementGraphicsOperations.ResetOverrides
                                ? new OverrideGraphicSettings()
                                : new OverrideGraphicSettings(view.GetElementOverrides(id));
                            if (parsed.Operation == ElementGraphicsOperations.Override) graphics!(settings);
                            view.SetElementOverrides(id, settings);
                            changedIds.Add(entry.Id);
                        }
                        catch (Exception ex)
                        {
                            failures.Add((entry.Id, ex.Message));
                        }
                    }
                    break;
            }

            if (changedIds.Count == 0)
            {
                tx.RollBack();
            }
            else
            {
                TransactionCommitGuard.CommitOrThrow(tx);
            }
        }

        // What Revit now says about the changed elements: never assume an unhidden element is drawn.
        var afterwards = changedIds.Count > 0 && parsed.Operation != ElementGraphicsOperations.Hide
            ? Gather(context, changedIds, cancellationToken)
            : new List<Entry>();

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = changedIds.Count > 0,
            Status = changedIds.Count == 0 ? "transaction_failed" : null,
            Message = $"{Verb(parsed.Operation)} {changedIds.Count} element(s) in view '{context.View.Name}'; {unchanged} unchanged, {ineligible} ineligible, {failures.Count} failed.",
            Data = Summary(context, parsed, plan, sample, applied: changedIds.Count, afterwards, failures),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    public static string ApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var parsed = ElementGraphicsRequestParser.Parse(request.Arguments, requireOperation: true, out var error);
        if (parsed == null) return $"Change element graphics — the input cannot be used: {error}";
        var context = Resolve(uiapp, request, parsed, out _);
        if (context == null) return "Change element graphics (the document, view or elements could not be resolved).";
        var plan = BuildPlan(context, parsed, CancellationToken.None, out var planError, out _);
        if (plan == null) return $"Change element graphics — {planError}";

        var change = plan.Where(e => e.Decision!.Outcome == ElementGraphicsOutcome.WillChange).ToList();
        var lines = new List<string>
        {
            $"{Verb(parsed.Operation, present: true)} {change.Count} element(s) in view '{context.View.Name}' ({context.View.ViewType}) of '{context.Doc.Title}'.",
            $"{plan.Count} requested: {change.Count} change, {plan.Count(e => e.Decision!.Outcome == ElementGraphicsOutcome.Unchanged)} unchanged, " +
            $"{plan.Count(e => e.Decision!.Outcome == ElementGraphicsOutcome.Ineligible)} ineligible."
        };
        if (parsed.Operation == ElementGraphicsOperations.Override && parsed.Graphics != null)
            lines.Add("Overrides: " + OverrideText(parsed.Graphics));
        foreach (var e in change.Take(5))
            lines.Add($"  {e.Id}: {Name(e.Element)} ({e.Element?.Category?.Name ?? "no category"})");
        if (change.Count > 5) lines.Add($"  … and {change.Count - 5} more.");
        var still = change.Count(e => e.Decision!.StillSuppressed.Count > 0);
        if (still > 0) lines.Add($"{still} of them will still not be drawn because of other view settings.");
        if (context.TemporaryMode) lines.Add("The view is in temporary hide/isolate mode: the call will be refused.");
        if (context.TemplateName != null) lines.Add($"The view follows template '{context.TemplateName}'; the template itself is not changed.");
        lines.Add("One transaction, one undo step.");
        return string.Join("\n", lines);
    }

    // ── Planning ────────────────────────────────────────────────────────────

    private static List<Entry>? BuildPlan(Context context, ElementGraphicsRequest parsed, CancellationToken cancellationToken, out string? error, out Action<OverrideGraphicSettings>? graphics)
    {
        error = null;
        graphics = null;
        if (parsed.Operation == ElementGraphicsOperations.Override)
        {
            var issues = new List<ViewFilterIssue>();
            graphics = ViewFilterGraphics.Resolve(context.Doc, parsed.Graphics!, issues, "graphics");
            if (graphics == null)
            {
                error = "graphics: " + string.Join("; ", issues.Select(i => i.Reason));
                return null;
            }
        }

        var entries = Gather(context, context.ElementIds, cancellationToken);
        foreach (var entry in entries)
        {
            if (graphics != null && entry.Overrides != null)
            {
                var after = new OverrideGraphicSettings(entry.Overrides);
                graphics(after);
                entry.OverridesDiffer = !JToken.DeepEquals(ViewFilterGraphics.Describe(context.Doc, entry.Overrides), ViewFilterGraphics.Describe(context.Doc, after));
            }
            entry.Decision = ElementGraphicsPlanner.Decide(parsed.Operation, entry.Facts, entry.OverridesDiffer);
        }
        return entries;
    }

    private static List<Entry> Gather(Context context, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var doc = context.Doc;
        var view = context.View;

        HashSet<long> inView;
        try
        {
            var existing = ids.Select(i => new ElementId(i)).Where(id => doc.GetElement(id) != null).ToList();
            inView = existing.Count == 0
                ? new HashSet<long>()
                : new HashSet<long>(new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(existing)).ToElementIds().Select(i => i.Value));
        }
        catch (Exception ex)
        {
            Swallowed.Note(ex);
            inView = new HashSet<long>();
        }

        // Filters whose visibility is switched off in this view, with what they select.
        var offFilters = new List<(ElementId Id, string Name, FilterElement Filter)>();
        try
        {
            foreach (var filterId in view.GetFilters())
            {
                if (view.GetFilterVisibility(filterId)) continue;
                if (doc.GetElement(filterId) is FilterElement filter) offFilters.Add((filterId, filter.Name, filter));
            }
        }
        catch (Exception ex)
        {
            Swallowed.Note(ex);
        }

        var result = new List<Entry>(ids.Count);
        var n = 0;
        foreach (var id in ids)
        {
            if (++n % 500 == 0) cancellationToken.ThrowIfCancellationRequested();
            var entry = new Entry { Id = id, Element = doc.GetElement(new ElementId(id)) };
            result.Add(entry);
            var element = entry.Element;
            if (element == null)
            {
                entry.Facts.Exists = false;
                continue;
            }

            var facts = entry.Facts;
            facts.InViewCollector = inView.Contains(id);
            try { facts.HiddenPermanently = element.IsHidden(view); } catch (Exception ex) { Swallowed.Note(ex); }
            try { facts.CanBeHidden = element.CanBeHidden(view); } catch (Exception ex) { Swallowed.Note(ex); facts.CanBeHidden = false; }

            try
            {
                if (element.Category != null) facts.CategoryHidden = view.GetCategoryHidden(element.Category.Id);
            }
            catch (Exception ex)
            {
                Swallowed.Note(ex); // a category that has no hidden flag in this view
            }

            foreach (var (filterId, name, filter) in offFilters)
            {
                try
                {
                    if (!view.GetIsFilterEnabled(filterId)) continue;
                    var passes = filter switch
                    {
                        ParameterFilterElement parameterFilter =>
                            element.Category != null && parameterFilter.GetCategories().Contains(element.Category.Id) &&
                            (parameterFilter.GetElementFilter()?.PassesFilter(element) ?? true),
                        SelectionFilterElement selection => selection.GetElementIds().Contains(element.Id),
                        _ => false
                    };
                    if (passes) facts.HidingFilters.Add(name);
                }
                catch (Exception ex)
                {
                    Swallowed.Note(ex);
                }
            }

            try
            {
                if (doc.IsWorkshared && element.WorksetId != WorksetId.InvalidWorksetId)
                    facts.WorksetHidden = view.GetWorksetVisibility(element.WorksetId) == WorksetVisibility.Hidden;
            }
            catch (Exception ex)
            {
                Swallowed.Note(ex);
            }

            if (context.TemporaryMode)
            {
                try { facts.HiddenByTemporaryMode = !view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, element.Id); }
                catch (Exception ex) { Swallowed.Note(ex); }
            }

            try
            {
                entry.Overrides = view.GetElementOverrides(element.Id);
                facts.HasOverrides = ViewFilterGraphics.Describe(doc, entry.Overrides).Count > 0 ||
                                     entry.Overrides.DetailLevel != ViewDetailLevel.Undefined;
            }
            catch (Exception ex)
            {
                Swallowed.Note(ex);
            }
        }
        return result;
    }

    private static Context? Resolve(UIApplication uiapp, McpToolRequest request, ElementGraphicsRequest parsed, out McpToolResult? failure)
    {
        failure = null;
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null)
        {
            failure = documentFailure;
            return null;
        }
        var doc = target.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
        if ((parsed.UseSelection || parsed.UseActiveView) && uidoc == null)
        {
            failure = ToolDocument.ActiveRequired(request, parsed.UseSelection ? "useSelection" : "useActiveView", doc.Title);
            return null;
        }

        View? view;
        if (parsed.UseActiveView)
        {
            view = uidoc!.ActiveView;
        }
        else
        {
            view = doc.GetElement(new ElementId(parsed.ViewId)) as View;
            if (view == null)
            {
                failure = Fail(request, $"View {parsed.ViewId} was not found in '{doc.Title}'.");
                return null;
            }
        }
        if (view == null)
        {
            failure = Fail(request, "There is no active view.");
            return null;
        }
        if (view.IsTemplate)
        {
            failure = Fail(request, $"'{view.Name}' is a view template. Element graphics belong to project views and a template does not hold them; name a view that uses it. Template settings are not changed by this tool.", "view_is_template");
            return null;
        }
        if (view.ViewType is ViewType.Schedule or ViewType.PanelSchedule or ViewType.ColumnSchedule or ViewType.DrawingSheet
            or ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined
            or ViewType.CostReport or ViewType.LoadsReport or ViewType.PresureLossReport)
        {
            failure = Fail(request, $"Elements cannot be hidden or overridden in '{view.Name}' ({view.ViewType}): sheets, schedules and similar views have no element graphics.", "view_not_supported");
            return null;
        }

        var context = new Context { Doc = doc, View = view };
        if (view.ViewTemplateId != ElementId.InvalidElementId)
        {
            context.TemplateName = doc.GetElement(view.ViewTemplateId)?.Name ?? "(template)";
            context.Warnings.Add($"The view uses template '{context.TemplateName}'. Per-element hides and overrides are stored on the view, not the template, and the template is not changed; category and filter visibility stay template-controlled, so they can still suppress elements.");
        }
        try { context.TemporaryMode = view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate); } catch (Exception ex) { Swallowed.Note(ex); }
        if (context.TemporaryMode)
            context.Warnings.Add("The view is in temporary hide/isolate mode. Reset it (or make it permanent) in Revit before changing element graphics.");

        if (doc.IsWorkshared)
        {
            try
            {
                if (WorksharingUtils.GetCheckoutStatus(doc, view.Id, out var owner) == CheckoutStatus.OwnedByOtherUser)
                {
                    context.OwnedByOther = owner;
                    context.Warnings.Add($"The view is checked out by {owner}; the change cannot be saved until they relinquish it.");
                }
            }
            catch (Exception ex)
            {
                Swallowed.Note(ex);
            }
        }

        if (parsed.UseSelection)
        {
            context.ElementIds = uidoc!.Selection.GetElementIds().Select(i => i.Value).Distinct().ToList();
            if (context.ElementIds.Count == 0)
            {
                failure = Fail(request, "The selection is empty.");
                return null;
            }
            if (context.ElementIds.Count > ElementGraphicsRequest.MaxElements)
            {
                failure = Fail(request, $"The selection holds {context.ElementIds.Count} elements; the limit is {ElementGraphicsRequest.MaxElements} per call.");
                return null;
            }
        }
        else
        {
            context.ElementIds = parsed.ElementIds;
        }
        return context;
    }

    private static string? WriteRefusal(Context context)
    {
        if (context.TemporaryMode)
            return $"View '{context.View.Name}' is in temporary hide/isolate mode. Reset it in Revit, then retry; nothing was changed.";
        if (context.OwnedByOther != null)
            return $"View '{context.View.Name}' is checked out by {context.OwnedByOther}; nothing was changed.";
        return null;
    }

    // ── Output ──────────────────────────────────────────────────────────────

    private static object ViewInfo(Context context)
    {
        var limits = new List<string>();
        if (context.TemplateName != null) limits.Add($"template '{context.TemplateName}' controls category/filter visibility");
        if (context.TemporaryMode) limits.Add("temporary hide/isolate mode is on");
        if (context.OwnedByOther != null) limits.Add($"checked out by {context.OwnedByOther}");
        return new
        {
            id = context.View.Id.Value,
            name = context.View.Name,
            type = context.View.ViewType.ToString(),
            template = context.TemplateName,
            temporaryHideIsolate = context.TemporaryMode,
            limits
        };
    }

    private static object Summary(Context context, ElementGraphicsRequest parsed, List<Entry> plan, int sample, int applied, List<Entry>? afterwards,
        List<(long Id, string Message)>? failures = null)
    {
        var byReason = plan.Where(e => e.Decision!.Code != null)
            .GroupBy(e => e.Decision!.Code!)
            .ToDictionary(g => g.Key, g => g.Count());

        var examples = new JArray();
        // Ineligible first, then changes: the reader needs the surprises.
        foreach (var entry in plan.OrderBy(e => e.Decision!.Outcome == ElementGraphicsOutcome.Ineligible ? 0 : e.Decision!.Outcome == ElementGraphicsOutcome.WillChange ? 1 : 2).Take(sample))
        {
            var row = Describe(entry);
            row["outcome"] = entry.Decision!.Outcome switch
            {
                ElementGraphicsOutcome.WillChange => applied >= 0 ? "changed" : "willChange",
                ElementGraphicsOutcome.Unchanged => "unchanged",
                _ => "ineligible"
            };
            if (entry.Decision.Code != null) row["code"] = entry.Decision.Code;
            if (entry.Decision.Reason != null) row["reason"] = entry.Decision.Reason;
            if (entry.Decision.StillSuppressed.Count > 0) row["stillSuppressed"] = Reasons(entry.Decision.StillSuppressed);
            examples.Add(row);
        }

        var data = new JObject
        {
            ["view"] = JObject.FromObject(ViewInfo(context)),
            ["operation"] = parsed.Operation,
            ["counts"] = JObject.FromObject(new
            {
                requested = plan.Count,
                willChange = plan.Count(e => e.Decision!.Outcome == ElementGraphicsOutcome.WillChange),
                unchanged = plan.Count(e => e.Decision!.Outcome == ElementGraphicsOutcome.Unchanged),
                ineligible = plan.Count(e => e.Decision!.Outcome == ElementGraphicsOutcome.Ineligible),
                applied = applied >= 0 ? applied : (int?)null,
                failed = failures?.Count
            }),
            ["byReason"] = JObject.FromObject(byReason),
            ["examples"] = examples
        };
        if (parsed.Operation == ElementGraphicsOperations.Override && parsed.Graphics != null)
            data["overrides"] = OverrideText(parsed.Graphics);

        if (failures is { Count: > 0 })
            data["failures"] = new JArray(failures.Take(sample).Select(f => new JObject { ["elementId"] = f.Id, ["message"] = f.Message }));

        if (afterwards != null && afterwards.Count > 0)
        {
            var stillHidden = afterwards.Where(e => ElementGraphicsPlanner.AllReasons(e.Facts).Count > 0).ToList();
            data["afterwards"] = new JObject
            {
                ["checked"] = afterwards.Count,
                ["stillNotDrawn"] = stillHidden.Count,
                ["examples"] = new JArray(stillHidden.Take(sample).Select(e =>
                {
                    var row = Describe(e);
                    row["because"] = Reasons(ElementGraphicsPlanner.AllReasons(e.Facts));
                    return row;
                })),
                ["note"] = "Checked against the model after the change. An element not listed has no known reason to be undrawn; that is not a guarantee it is visible."
            };
        }
        return data;
    }

    private static JObject Describe(Entry entry)
    {
        var row = new JObject { ["elementId"] = entry.Id };
        if (entry.Element != null)
        {
            row["name"] = Name(entry.Element);
            row["category"] = entry.Element.Category?.Name;
        }
        else
        {
            row["exists"] = false;
        }
        return row;
    }

    private static JArray Reasons(IEnumerable<ElementGraphicsReason> reasons) =>
        new(reasons.Select(r => new JObject { ["code"] = r.Code, ["reason"] = r.Text }));

    private static string Name(Element? element)
    {
        try { return element?.Name ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private static string OverrideText(ViewFilterGraphicsSpec spec)
    {
        var parts = new List<string>();
        if (spec.Halftone != null) parts.Add($"halftone {(spec.Halftone.Value ? "on" : "off")}");
        if (spec.Transparency != null) parts.Add($"transparency {spec.Transparency}");
        void Line(string label, ViewFilterLineSpec? line)
        {
            if (line == null) return;
            var bits = new List<string>();
            if (line.Color != null) bits.Add(line.Color.ToHex());
            if (line.Weight != null) bits.Add("weight " + line.Weight);
            if (!string.IsNullOrEmpty(line.Pattern)) bits.Add("pattern " + line.Pattern);
            parts.Add($"{label} {string.Join(" ", bits)}".TrimEnd());
        }
        void Fill(string label, ViewFilterFillSpec? fill)
        {
            if (fill == null) return;
            var bits = new List<string>();
            if (fill.Color != null) bits.Add(fill.Color.ToHex());
            if (!string.IsNullOrEmpty(fill.Pattern)) bits.Add("pattern " + fill.Pattern);
            if (fill.Visible != null) bits.Add(fill.Visible.Value ? "visible" : "not visible");
            parts.Add($"{label} {string.Join(" ", bits)}".TrimEnd());
        }
        Line("projection lines", spec.ProjectionLines);
        Line("cut lines", spec.CutLines);
        Fill("surface foreground", spec.SurfaceForeground);
        Fill("surface background", spec.SurfaceBackground);
        Fill("cut foreground", spec.CutForeground);
        Fill("cut background", spec.CutBackground);
        return string.Join(", ", parts);
    }

    private static string Verb(string operation, bool present = false) => operation switch
    {
        ElementGraphicsOperations.Hide => present ? "Hide" : "Hid",
        ElementGraphicsOperations.Unhide => present ? "Unhide" : "Unhid",
        ElementGraphicsOperations.Override => present ? "Override graphics of" : "Overrode graphics of",
        _ => present ? "Reset overrides of" : "Reset overrides of"
    };

    private static McpToolResult Fail(McpToolRequest request, string message, string status = "validation_failed") => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = status,
        Message = message
    };
}
