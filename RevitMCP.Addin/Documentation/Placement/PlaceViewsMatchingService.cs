using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Documentation.Placement;

/// <summary>
/// Reproduces the PlaceViews plugin's sheet-first matching behavior: each
/// selected sheet chooses its best selected view.
/// </summary>
public static class PlaceViewsMatchingService
{
    public const string ModePlaceViews = "PlaceViews";

    public sealed record MatchProposal(
        long SheetId,
        string SheetNumber,
        string SheetName,
        long? ViewId,
        string? ViewName,
        string? ViewType,
        int Score,
        bool IsExact,
        string Reason);

    public static bool IsPlaceViewsMode(string matchMode) =>
        string.Equals(matchMode, ModePlaceViews, StringComparison.OrdinalIgnoreCase);

    public static bool IsCandidateView(View? view)
    {
        return view != null &&
               !view.IsTemplate &&
               view.ViewType != ViewType.DrawingSheet &&
               view.ViewType != ViewType.Internal &&
               view.ViewType != ViewType.ProjectBrowser &&
               view.CanBePrinted;
    }

    public static List<MatchProposal> Match(
        Document doc,
        IEnumerable<long> viewIds,
        IEnumerable<ViewSheet> candidateSheets)
    {
        var views = viewIds
            .Distinct()
            .Select(viewId => doc.GetElement(new ElementId(viewId)) as View)
            .Where(IsCandidateView)
            .Cast<View>()
            .ToList();

        var proposals = new List<MatchProposal>();
        foreach (var sheet in candidateSheets)
        {
            var exactMatch = views.FirstOrDefault(view =>
                string.Equals(view.Name, sheet.Name, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                proposals.Add(CreateProposal(sheet, exactMatch, 1000, true, "Exact sheet-name/view-name match."));
                continue;
            }

            var best = views
                .Select(view => new
                {
                    View = view,
                    Score = PlaceViewsNameMatcher.CalculateMatchScore(sheet.Name, view.Name)
                })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => Math.Abs(candidate.View.Name.Length - sheet.Name.Length))
                .FirstOrDefault();

            proposals.Add(best != null
                ? CreateProposal(
                    sheet,
                    best.View,
                    best.Score,
                    false,
                    $"PlaceViews number/word match (score={best.Score}).")
                : new MatchProposal(
                    sheet.Id.Value,
                    sheet.SheetNumber,
                    sheet.Name,
                    null,
                    null,
                    null,
                    0,
                    false,
                    "No selected view matched the sheet name."));
        }

        return proposals;
    }

    private static MatchProposal CreateProposal(
        ViewSheet sheet,
        View view,
        int score,
        bool isExact,
        string reason)
    {
        return new MatchProposal(
            sheet.Id.Value,
            sheet.SheetNumber,
            sheet.Name,
            view.Id.Value,
            view.Name,
            view.ViewType.ToString(),
            score,
            isExact,
            reason);
    }
}
