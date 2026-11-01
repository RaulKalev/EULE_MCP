using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.ViewElements;

internal sealed class ViewTarget
{
    public ViewTarget(View view, string matchedBy, string? note)
    {
        View = view;
        MatchedBy = matchedBy;
        Note = note;
    }

    public View View { get; }
    public ViewSheet? Sheet => View as ViewSheet;
    public string MatchedBy { get; }
    public string? Note { get; }
}

/// <summary>Finds the view or sheet of a <c>view</c> argument in the target document, or the failure to answer with (#136).</summary>
internal static class ViewTargetResolver
{
    public static ViewTarget? Resolve(
        UIApplication uiapp, RevitDocumentResolver.DocumentTarget target, McpToolRequest request, string reference, out McpToolResult? failure)
    {
        failure = null;
        var doc = target.Document;
        ViewTargetMatch match;
        View? view;

        if (ViewTargetMatcher.IsActiveKeyword(reference))
        {
            var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
            if (uidoc == null)
            {
                failure = ToolDocument.ActiveRequired(request, "view=active", doc.Title);
                failure.Message = $"view=active needs the document to be active, and '{doc.Title}' is not. Call revit_activate_document first, or pass a view id or name.";
                return null;
            }
            view = uidoc.ActiveView;
            if (view == null)
            {
                failure = Failure(request, ViewTargetMatcher.StatusNotFound, "Revit has no active view.", null);
                return null;
            }
            match = ViewTargetMatcher.Validate(ToCandidate(view), "active");
        }
        else
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            match = ViewTargetMatcher.Match(views.Select(ToCandidate).ToList(), reference);
            view = match.Target == null ? null : views.FirstOrDefault(v => v.Id.Value == match.Target.Id);
        }

        if (match.Outcome == ViewTargetOutcome.Resolved && view != null && !IsIterable(doc, view))
            match.Outcome = ViewTargetOutcome.NotCollectable;

        if (match.Outcome == ViewTargetOutcome.Resolved && view != null)
            return new ViewTarget(view, match.MatchedBy ?? "name", match.Note);

        failure = Failure(request, match.Status ?? ViewTargetMatcher.StatusNotFound, Message(match, reference), match);
        return null;
    }

    /// <summary>Can a collector list what this view draws? Many view templates and non-graphical views cannot.</summary>
    public static bool IsIterable(Document doc, View view)
    {
        try { return FilteredElementCollector.IsViewValidForElementIteration(doc, view.Id); }
        catch { return false; }
    }

    public static ViewTargetCandidate ToCandidate(View view)
    {
        var sheet = view as ViewSheet;
        return new ViewTargetCandidate
        {
            Id = view.Id.Value,
            Name = view.Name,
            ViewType = view.ViewType.ToString(),
            IsSheet = sheet != null,
            SheetNumber = sheet?.SheetNumber,
            IsTemplate = view.IsTemplate,
            IsPlaceholderSheet = sheet?.IsPlaceholder == true
        };
    }

    private static string Message(ViewTargetMatch match, string reference)
    {
        var name = match.Target?.Name ?? reference;
        switch (match.Outcome)
        {
            case ViewTargetOutcome.Ambiguous:
                return $"'{reference}' matches {match.Candidates.Count} views or sheets. Pass the id of the one you mean (see candidates).";
            case ViewTargetOutcome.Template:
                return $"'{name}' is a view template, which shows no elements. Pass a view that uses it (revit_list_views).";
            case ViewTargetOutcome.NotCollectable:
                return match.Target?.IsPlaceholderSheet == true
                    ? $"'{name}' is a placeholder sheet, which has no elements."
                    : $"'{name}' is a {match.Target?.ViewType} view, which does not draw elements a collector can list. Find the schedule tools with revit_tools_search query=\"schedule rows\".";
            default:
                return match.Candidates.Count > 0
                    ? $"No view or sheet matches '{reference}'. Closest names are in candidates; pass an exact name or id."
                    : $"No view or sheet matches '{reference}'. See revit_list_views and revit_list_sheets.";
        }
    }

    private static McpToolResult Failure(McpToolRequest request, string status, string message, ViewTargetMatch? match) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = status,
        Message = message,
        Data = match == null
            ? null
            : new
            {
                view = match.Target == null ? null : Describe(match.Target),
                candidates = match.Candidates.Select(Describe).ToList()
            }
    };

    private static object Describe(ViewTargetCandidate view) => new
    {
        id = view.Id,
        name = view.Name,
        viewType = view.ViewType,
        sheetNumber = view.SheetNumber
    };
}
