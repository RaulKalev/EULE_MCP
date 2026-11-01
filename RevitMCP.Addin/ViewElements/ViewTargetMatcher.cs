using System.Globalization;
using RevitMCP.Addin.Documentation.Placement;

namespace RevitMCP.Addin.ViewElements;

/// <summary>A view or sheet as the matcher sees it (no Revit types, unit-testable).</summary>
public sealed class ViewTargetCandidate
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>The <c>ViewType</c> name: FloorPlan, DrawingSheet, Schedule, ...</summary>
    public string ViewType { get; set; } = string.Empty;
    public bool IsSheet { get; set; }
    public string? SheetNumber { get; set; }
    public bool IsTemplate { get; set; }
    public bool IsPlaceholderSheet { get; set; }
}

public enum ViewTargetOutcome
{
    Resolved,
    NotFound,
    Ambiguous,
    Template,
    NotCollectable
}

public sealed class ViewTargetMatch
{
    public ViewTargetOutcome Outcome { get; set; }
    public ViewTargetCandidate? Target { get; set; }
    /// <summary>id | name | sheetNumber | sheetLabel | active.</summary>
    public string? MatchedBy { get; set; }
    /// <summary>The views to choose from: the ambiguous matches, or the closest names for an unknown reference.</summary>
    public List<ViewTargetCandidate> Candidates { get; set; } = new();
    /// <summary>Set when an id also reads as another view's name or sheet number: the id won.</summary>
    public string? Note { get; set; }
    public string? Status => Outcome switch
    {
        ViewTargetOutcome.NotFound => ViewTargetMatcher.StatusNotFound,
        ViewTargetOutcome.Ambiguous => ViewTargetMatcher.StatusAmbiguous,
        ViewTargetOutcome.Template => ViewTargetMatcher.StatusTemplate,
        ViewTargetOutcome.NotCollectable => ViewTargetMatcher.StatusNotCollectable,
        _ => null
    };
}

/// <summary>
/// Resolves the <c>view</c> argument (#136) against the document's views and sheets. Pure. An id wins over everything;
/// then a whole-string match on view name, sheet name, sheet number or "number - name", ignoring case. Several matches
/// are an error rather than a guess (a floor plan and a ceiling plan may share a name), and a partial match is only
/// offered as a candidate, never picked.
/// </summary>
public static class ViewTargetMatcher
{
    public const string ActiveKeyword = "active";
    public const string StatusNotFound = "view_not_found";
    public const string StatusAmbiguous = "view_ambiguous";
    public const string StatusTemplate = "view_is_template";
    public const string StatusNotCollectable = "view_not_collectable";

    public const int MaxCandidates = 15;

    public static bool IsActiveKeyword(string? reference) =>
        string.Equals(reference?.Trim(), ActiveKeyword, StringComparison.OrdinalIgnoreCase);

    public static ViewTargetMatch Match(IReadOnlyList<ViewTargetCandidate> views, string? reference)
    {
        var wanted = reference?.Trim() ?? string.Empty;
        if (wanted.Length == 0) return new ViewTargetMatch { Outcome = ViewTargetOutcome.NotFound };

        if (long.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            var byId = views.FirstOrDefault(v => v.Id == id);
            if (byId != null)
            {
                var result = Validate(byId, "id");
                if (views.Any(v => v.Id != id && !v.IsTemplate && MatchKind(v, wanted) != null))
                    result.Note = $"'{wanted}' is the id of '{byId.Name}' and also the name or number of another view; the id was used.";
                return result;
            }
        }

        var exact = views.Where(v => MatchKind(v, wanted) != null).ToList();
        var usable = exact.Where(v => !v.IsTemplate).ToList();
        if (usable.Count == 1) return Validate(usable[0], MatchKind(usable[0], wanted)!);
        if (usable.Count > 1)
            return new ViewTargetMatch { Outcome = ViewTargetOutcome.Ambiguous, Candidates = usable.Take(MaxCandidates).ToList() };
        if (exact.Count > 0) return Validate(exact[0], "name");

        return new ViewTargetMatch { Outcome = ViewTargetOutcome.NotFound, Candidates = Closest(views, wanted) };
    }

    /// <summary>The checks every target goes through, the active view included.</summary>
    public static ViewTargetMatch Validate(ViewTargetCandidate view, string matchedBy)
    {
        var outcome = view.IsTemplate
            ? ViewTargetOutcome.Template
            : view.IsPlaceholderSheet || !ViewElementRules.IsCollectableViewType(view.ViewType)
                ? ViewTargetOutcome.NotCollectable
                : ViewTargetOutcome.Resolved;
        return new ViewTargetMatch { Outcome = outcome, Target = view, MatchedBy = matchedBy };
    }

    private static string? MatchKind(ViewTargetCandidate view, string wanted)
    {
        if (Same(view.Name, wanted)) return "name";
        if (!view.IsSheet || string.IsNullOrEmpty(view.SheetNumber)) return null;
        if (Same(view.SheetNumber, wanted)) return "sheetNumber";
        return Same($"{view.SheetNumber} - {view.Name}", wanted) ? "sheetLabel" : null;
    }

    private static bool Same(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);

    private static List<ViewTargetCandidate> Closest(IReadOnlyList<ViewTargetCandidate> views, string wanted) =>
        views.Where(v => !v.IsTemplate)
            .Select(v => (View: v, Score: Score(v, wanted)))
            .Where(x => x.Score >= 0.5)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.View.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .Select(x => x.View)
            .ToList();

    private static double Score(ViewTargetCandidate view, string wanted)
    {
        var best = NameScore(view.Name, wanted);
        if (view.IsSheet && !string.IsNullOrEmpty(view.SheetNumber))
            best = Math.Max(best, NameScore(view.SheetNumber!, wanted));
        return best;
    }

    private static double NameScore(string name, string wanted)
    {
        // A partial name is the usual near miss ("Plan 1" for "EN Plan 1 ATS"), so it outranks spelling distance.
        if (name.Length > 0 &&
            (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 || wanted.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
            return 1.0;
        return FuzzyNameMatcher.Similarity(name, wanted);
    }
}
