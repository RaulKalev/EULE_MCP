using RevitMCP.Addin.Coordination.Clash.DTOs;

namespace RevitMCP.Addin.Coordination.Clash.Services;

/// <summary>
/// Groups a list of clash results by various fields. No Revit API dependency — testable.
/// </summary>
public static class ClashSummaryService
{
    public static Dictionary<string, object> BuildSummary(
        List<ClashResultDto> clashes,
        IEnumerable<string>? groupByFields = null)
    {
        var groups = (groupByFields ?? ["Rule", "Level", "LinkedModel", "CategoryPair", "Severity"])
            .ToList();
        var summary = new Dictionary<string, object>
        {
            ["totalClashes"] = clashes.Count,
            ["byStatus"] = clashes.GroupBy(c => c.Status)
                .ToDictionary(g => g.Key, g => (object)g.Count())
        };

        foreach (var field in groups)
        {
            switch (field)
            {
                case "Rule":
                    summary["byRule"] = clashes.GroupBy(c => c.RuleName)
                        .ToDictionary(g => g.Key, g => (object)g.Count());
                    break;
                case "Level":
                    summary["byLevel"] = clashes
                        .GroupBy(c => string.IsNullOrWhiteSpace(c.Level) ? "(no level)" : c.Level)
                        .ToDictionary(g => g.Key, g => (object)g.Count());
                    break;
                case "LinkedModel":
                    summary["byLinkedModel"] = clashes
                        .GroupBy(c => c.Target.Model == "Host" ? "Host" : (c.Target.LinkName ?? c.Target.Model))
                        .ToDictionary(g => g.Key, g => (object)g.Count());
                    break;
                case "CategoryPair":
                    summary["byCategoryPair"] = clashes
                        .GroupBy(c => $"{c.Source.Category} ↔ {c.Target.Category}")
                        .OrderByDescending(g => g.Count())
                        .ToDictionary(g => g.Key, g => (object)g.Count());
                    break;
                case "Severity":
                    summary["bySeverity"] = clashes
                        .GroupBy(c => c.Severity)
                        .ToDictionary(g => g.Key, g => (object)g.Count());
                    break;
            }
        }

        return summary;
    }
}
