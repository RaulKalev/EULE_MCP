using System.Text;

namespace RevitMCP.Addin.Plans;

/// <summary>The approval text of a plan (#99): what the user reads once before walking away. Pure.</summary>
public static class PlanSummary
{
    public const int MaxStepsShown = 12;

    public static string Build(PlanDefinition plan, string? documentTitle = null)
    {
        var env = plan.Envelope;
        var sb = new StringBuilder();
        sb.Append("PLAN: ").Append(plan.Title);
        if (plan.Steps.Count > 0) sb.Append(" (").Append(plan.Steps.Count).Append(plan.Steps.Count == 1 ? " step)" : " steps)");
        if (!string.IsNullOrEmpty(documentTitle)) sb.Append(" in '").Append(documentTitle).Append('\'');
        sb.AppendLine();

        sb.Append("Tools: ").AppendLine(env.Tools.Count > 0 ? string.Join(", ", env.Tools) : "(none)");

        var scope = new List<string>();
        if (env.ElementIds.Count > 0) scope.Add($"{env.ElementIds.Count:N0} listed elements");
        if (env.Categories.Count > 0) scope.Add("categories " + string.Join(", ", env.Categories));
        if (env.LevelIds.Count > 0) scope.Add($"{env.LevelIds.Count} level(s)");
        if (env.ViewIds.Count > 0) scope.Add("views " + string.Join(", ", env.ViewIds));
        if (env.Worksets.Count > 0) scope.Add("worksets " + string.Join(", ", env.Worksets));
        sb.Append("Scope: ").AppendLine(scope.Count > 0 ? string.Join(" · ", scope) : "not restricted (counters only)");

        sb.Append("Limits: ≤ ").Append(env.MaxElementsPerCall.ToString("N0")).Append(" elements per call, ≤ ")
          .Append(env.MaxElementsTotal.ToString("N0")).Append(" in total, ≤ ").Append(env.MaxCalls).Append(" calls, expires in ")
          .Append(env.ExpiresMinutes).Append(" min, ")
          .Append(env.AllowCreate ? "may create" : "may not create").Append(", ")
          .Append(env.AllowModify ? "may modify" : "may not modify").Append(", may not delete")
          .AppendLine(env.Strict ? ", strict (side effects count)" : string.Empty);

        if (plan.Steps.Count > 0)
        {
            var shown = plan.Steps.Take(MaxStepsShown).Select((s, i) => $"{i + 1}. {s}").ToList();
            sb.Append("Steps: ").Append(string.Join(" ", shown));
            if (plan.Steps.Count > MaxStepsShown) sb.Append(" … ").Append(plan.Steps.Count).Append(". ").Append(plan.Steps[plan.Steps.Count - 1]);
            sb.AppendLine();
        }

        sb.Append("Everything runs without further prompts inside these limits and is undoable per call; a checkpoint is created for review and rollback.");
        return sb.ToString();
    }
}
