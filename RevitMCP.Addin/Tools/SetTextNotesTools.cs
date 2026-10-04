using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Annotation;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public sealed class PreviewSetTextNotesTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_text_notes";

    public string Description =>
        "Previews editing the content of existing text notes in place, without changing the model. " +
        "Same arguments as revit_set_text_notes; returns old text → new text per note with its view, " +
        "plus changed / unchanged / skipped counts.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(SetTextNotesExecutor.Execute(uiapp, request, cancellationToken, previewOnly: true));
}

public sealed class SetTextNotesTool : IRevitMcpTool
{
    public string Name => "revit_set_text_notes";

    public string Description =>
        "Edits the content of existing text notes in place (element ids stay the same). Targets: " +
        "elementIds, useSelection, or viewId (-1 active view, 0 all views, >0 one view), each narrowed " +
        "by an optional textFilter (case-insensitive substring). Modes: set (text), findReplace (find, " +
        "replace, matchCase, wholeWord), append (suffix), prepend (prefix), map (JSON object old text → " +
        "new text, matched on the whole trimmed text; notes not in the map stay unchanged). Optional " +
        "widthMm (paper width) or autoWidth=true (estimated widening so the longest line fits). Edits " +
        "are spliced into the note's formatted text, so bold/italic/underline runs and list formatting " +
        "outside the edited range are kept; inserted text takes the formatting of the text it replaces " +
        "or follows. If Revit refuses the in-place edit the note's plain text is replaced instead " +
        "(formattingPreserved=false). Notes owned by another user or out of date with central are " +
        "skipped. One transaction; requires approval; reversible via Revit Undo. Run " +
        "revit_preview_set_text_notes first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(SetTextNotesExecutor.Execute(uiapp, request, cancellationToken, previewOnly: false));
}

internal static class SetTextNotesExecutor
{
    private const double FeetToMm = 304.8;
    private const int MaxUnchangedListed = 100;

    private sealed class NoteWork
    {
        public TextNote Note = null!;
        public TextNoteEditPlan Plan = null!;
        public double OldWidthFt;
        public double NewWidthFt;
        public string ViewName = string.Empty;
        public bool WidthChanged => Math.Abs(NewWidthFt - OldWidthFt) > 1e-6;
        public bool Changed => Plan.TextChanged || WidthChanged;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken, bool previewOnly)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null)
            return Fail(request, "No active document.");

        var args = request.Arguments;
        request.Arguments.TryGetValue("map", out var rawMap);
        var options = TextNoteEditPlanner.ParseOptions(
            ToolArguments.GetString(args, "mode"),
            ToolArguments.GetString(args, "text"),
            ToolArguments.GetString(args, "find"),
            ToolArguments.GetString(args, "replace"),
            ToolArguments.GetString(args, "prefix"),
            ToolArguments.GetString(args, "suffix"),
            ToolArguments.GetBool(args, "matchCase"),
            ToolArguments.GetBool(args, "wholeWord"),
            rawMap,
            ToolArguments.GetDouble(args, "widthMm"),
            ToolArguments.GetBool(args, "autoWidth"),
            out var optionsError);
        if (options == null)
            return Fail(request, optionsError!);

        var warnings = new List<string>();
        var skipped = new List<object>();
        var notes = ResolveTargets(uidoc, doc, args, warnings, skipped, out var scope, out var targetError);
        if (notes == null)
            return Fail(request, targetError!);

        var textFilter = ToolArguments.GetString(args, "textFilter");
        notes = notes.Where(n => TextNoteEditPlanner.MatchesFilter(SafeText(n), textFilter)).ToList();
        if (notes.Count == 0 && skipped.Count == 0)
            return Fail(request, $"No text notes matched ({scope}{(string.IsNullOrEmpty(textFilter) ? string.Empty : $", textFilter '{textFilter}'")}).");

        var viewNames = new Dictionary<long, string>();
        var changes = new List<NoteWork>();
        var unchanged = new List<object>();
        var unchangedCount = 0;
        var isWorkshared = doc.IsWorkshared;

        foreach (var note in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var viewName = ViewName(doc, note.OwnerViewId, viewNames);
            var plan = TextNoteEditPlanner.Plan(SafeText(note), options);

            if (plan.SkipReason != null)
            {
                skipped.Add(Skip(note, viewName, plan.SkipReason));
                continue;
            }

            var work = new NoteWork { Note = note, Plan = plan, ViewName = viewName, OldWidthFt = note.Width };
            work.NewWidthFt = work.OldWidthFt;
            if (options.ChangesWidth && (plan.TextChanged || options.Mode == TextNoteEditMode.None))
                work.NewWidthFt = PlanWidth(doc, note, plan.NewText, options, warnings);

            if (!work.Changed)
            {
                unchangedCount++;
                if (unchanged.Count < MaxUnchangedListed)
                    unchanged.Add(new
                    {
                        elementId = note.Id.Value,
                        viewId = note.OwnerViewId.Value,
                        viewName,
                        text = plan.OldText,
                        reason = plan.UnchangedReason ?? "nothing to change"
                    });
                continue;
            }

            var blocked = isWorkshared ? WorksharingBlock(doc, note.Id) : null;
            if (blocked != null)
            {
                skipped.Add(Skip(note, viewName, blocked));
                continue;
            }

            changes.Add(work);
        }

        if (unchangedCount > unchanged.Count)
            warnings.Add($"Listed {unchanged.Count} of {unchangedCount} unchanged notes.");

        if (previewOnly)
        {
            sw.Stop();
            return Result(request, true, $"Preview: {changes.Count} text note(s) would change; {unchangedCount} unchanged, {skipped.Count} skipped.",
                scope, options, changes.Select(c => Describe(c, null)).ToList(), unchangedCount, unchanged, skipped, 0, warnings, null, sw.ElapsedMilliseconds);
        }

        if (changes.Count == 0)
        {
            sw.Stop();
            return Result(request, true, $"Nothing to change: {unchangedCount} unchanged, {skipped.Count} skipped.",
                scope, options, new List<object>(), unchangedCount, unchanged, skipped, 0, warnings, null, sw.ElapsedMilliseconds);
        }

        var applied = new List<object>();
        var failed = 0;
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Edit Text Notes", () =>
        {
            foreach (var work in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    bool? formattingPreserved = null;
                    if (work.Plan.TextChanged)
                        formattingPreserved = ApplyText(work.Note, work.Plan);
                    if (work.WidthChanged)
                        work.Note.Width = work.NewWidthFt;
                    applied.Add(Describe(work, formattingPreserved));
                }
                catch (Exception ex)
                {
                    failed++;
                    skipped.Add(Skip(work.Note, work.ViewName, $"Revit refused the edit: {ex.Message}"));
                }
            }
        });
        sw.Stop();

        if (!txSuccess)
        {
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = diagnostics.OriginalError ?? "Transaction failed — no text notes were changed.",
                Warnings = warnings,
                Data = new { transactionDiagnostics = diagnostics },
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        return Result(request, applied.Count > 0 || failed == 0,
            $"Changed {applied.Count} text note(s); {unchangedCount} unchanged, {skipped.Count} skipped{(failed > 0 ? $" ({failed} failed)" : string.Empty)}.",
            scope, options, applied, unchangedCount, unchanged, skipped, failed, warnings, diagnostics, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Splices the planned edits into the note's FormattedText (keeps character formatting outside
    /// the edited ranges). Falls back to replacing the plain text when Revit refuses or the result
    /// does not read back as planned. Returns whether formatting was preserved.
    /// </summary>
    private static bool ApplyText(TextNote note, TextNoteEditPlan plan)
    {
        try
        {
            var formatted = note.GetFormattedText();
            if (formatted != null && string.Equals(formatted.GetPlainText(), plan.OldText, StringComparison.Ordinal))
            {
                // Right to left so earlier indexes stay valid.
                for (var i = plan.Splices.Count - 1; i >= 0; i--)
                {
                    var splice = plan.Splices[i];
                    formatted.SetPlainText(new TextRange(splice.Start, splice.Length), splice.Replacement);
                }

                if (string.Equals(formatted.GetPlainText(), plan.NewText, StringComparison.Ordinal))
                {
                    note.SetFormattedText(formatted);
                    return true;
                }
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Fall through to the plain-text path.
        }
        catch (ArgumentException)
        {
        }

        note.Text = plan.NewText;
        return false;
    }

    private static double PlanWidth(Document doc, TextNote note, string newText, TextNoteEditOptions options, List<string> warnings)
    {
        var current = note.Width;
        double target;
        if (options.WidthMm > 0)
        {
            target = options.WidthMm / FeetToMm;
        }
        else
        {
            var type = doc.GetElement(note.GetTypeId()) as TextNoteType;
            var sizeMm = (type?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0) * FeetToMm;
            var factor = type?.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.AsDouble() ?? 1.0;
            var estimateMm = TextNoteEditPlanner.EstimateWidthMm(newText, sizeMm, factor);
            if (estimateMm <= 0) return current;
            target = Math.Max(current, estimateMm / FeetToMm); // autoWidth never narrows
        }

        try
        {
            var min = TextNote.GetMinimumAllowedWidth(doc, note.GetTypeId());
            var max = TextNote.GetMaximumAllowedWidth(doc, note.GetTypeId());
            var clamped = Math.Min(Math.Max(target, min), max);
            if (Math.Abs(clamped - target) > 1e-9)
                warnings.Add($"Text note {note.Id.Value}: width {target * FeetToMm:F1} mm is outside the allowed range and was clamped to {clamped * FeetToMm:F1} mm.");
            return clamped;
        }
        catch (Exception)
        {
            return target;
        }
    }

    private static List<TextNote>? ResolveTargets(
        UIDocument uidoc, Document doc, Dictionary<string, object?> args,
        List<string> warnings, List<object> skipped, out string scope, out string? error)
    {
        error = null;
        var elementIds = ToolArguments.GetLongArray(args, "elementIds");
        if (elementIds.Length > 0)
        {
            scope = $"{elementIds.Length} element id(s)";
            var list = new List<TextNote>();
            foreach (var id in elementIds.Distinct())
            {
                var element = doc.GetElement(new ElementId(id));
                if (element is TextNote tn) list.Add(tn);
                else skipped.Add(new { elementId = id, viewId = (long?)null, viewName = string.Empty, text = string.Empty,
                    reason = element == null ? "element not found" : "not a text note" });
            }
            return list;
        }

        if (ToolArguments.GetBool(args, "useSelection"))
        {
            scope = "current selection";
            var list = new List<TextNote>();
            var others = 0;
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                if (doc.GetElement(id) is TextNote tn) list.Add(tn);
                else others++;
            }
            if (others > 0)
                warnings.Add($"{others} selected element(s) are not text notes and were ignored.");
            if (list.Count == 0)
            {
                error = "The current selection contains no text notes.";
                return null;
            }
            return list;
        }

        var viewId = ToolArguments.GetLong(args, "viewId", -1L);
        if (viewId == 0)
        {
            scope = "all views";
            return new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
        }

        View? view;
        if (viewId > 0)
        {
            view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                scope = $"view {viewId}";
                error = $"Element {viewId} is not a view.";
                return null;
            }
        }
        else
        {
            view = uidoc.ActiveView;
            if (view == null)
            {
                scope = "active view";
                error = "No active view. Pass viewId=0 for all views, a view id, elementIds or useSelection.";
                return null;
            }
        }

        scope = $"view '{view.Name}' ({view.Id.Value})";
        return new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
    }

    private static string? WorksharingBlock(Document doc, ElementId id)
    {
        try
        {
            var checkout = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
            if (checkout == CheckoutStatus.OwnedByOtherUser)
                return $"borrowed by another user ({owner}); not editable";

            var updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
            if (updates == ModelUpdatesStatus.UpdatedInCentral)
                return "updated in central by another user; reload latest first";
            if (updates == ModelUpdatesStatus.DeletedInCentral)
                return "deleted in central";
        }
        catch (Exception)
        {
            // Status not available (e.g. detached copy): let Revit decide on commit.
        }
        return null;
    }

    private static string SafeText(TextNote note)
    {
        try { return note.Text ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private static string ViewName(Document doc, ElementId viewId, Dictionary<long, string> cache)
    {
        if (cache.TryGetValue(viewId.Value, out var name)) return name;
        name = (doc.GetElement(viewId) as View)?.Name ?? string.Empty;
        cache[viewId.Value] = name;
        return name;
    }

    private static object Skip(TextNote note, string viewName, string reason) => new
    {
        elementId = note.Id.Value,
        viewId = (long?)note.OwnerViewId.Value,
        viewName,
        text = SafeText(note),
        reason
    };

    private static object Describe(NoteWork work, bool? formattingPreserved) => new
    {
        elementId = work.Note.Id.Value,
        viewId = work.Note.OwnerViewId.Value,
        viewName = work.ViewName,
        oldText = work.Plan.OldText,
        newText = work.Plan.NewText,
        textChanged = work.Plan.TextChanged,
        oldWidthMm = Math.Round(work.OldWidthFt * FeetToMm, 1),
        newWidthMm = Math.Round(work.NewWidthFt * FeetToMm, 1),
        widthChanged = work.WidthChanged,
        formattingPreserved
    };

    private static McpToolResult Result(
        McpToolRequest request, bool success, string message, string scope, TextNoteEditOptions options,
        List<object> changed, int unchangedCount, List<object> unchanged, List<object> skipped, int failed,
        List<string> warnings, object? diagnostics, long durationMs) => new()
    {
        RequestId = request.RequestId,
        Success = success,
        Message = message,
        Warnings = warnings,
        Data = new
        {
            scope,
            mode = options.Mode.ToString(),
            changedCount = changed.Count,
            unchangedCount,
            skippedCount = skipped.Count,
            failedCount = failed,
            changed,
            unchanged,
            skipped,
            transactionDiagnostics = diagnostics
        },
        DurationMs = durationMs
    };

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
