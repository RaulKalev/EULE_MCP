using System.Diagnostics;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: validate.stages
/// Checks that each parsed sheet's Stage code is in the allowed list.
/// Only runs on sheets that were successfully parsed by ValidateSheetNumbersTask.
/// </summary>
internal sealed class ValidateStagesTask : SheetNamingBaseTask
{
    public override string Id   => "validate.stages";
    public override string Name => "Valideeri staadiumid";

    private static readonly string[] DefaultAllowed = { "EP", "PP", "TP", "TJ" };

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        var sheets = GetSheets(ctx);
        if (sheets is null)
            return SkipResult("Lehti ei leitud SharedData's — käivita enne collect.revit.sheets.", sw.ElapsedMilliseconds);

        var issues  = GetOrCreateIssuesList(ctx);
        var before  = issues.Count;
        var allowed = SkillSettings.GetStringArray(taskDef.Settings, "allowedStages", DefaultAllowed);

        if (allowed.Length == 0)
            return OkResult(0, "Lubatud staadiumide loetelu on tühi — staadiumide kontroll vahele jäetud.", sw.ElapsedMilliseconds);

        var parsedSheets = sheets.Where(s => s.ParsedSuccessfully).ToList();

        foreach (var sheet in parsedSheets)
        {
            if (string.IsNullOrEmpty(sheet.Stage)) continue;

            if (!allowed.Any(a => string.Equals(a, sheet.Stage, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Viga",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = sheet.SheetName,
                    RuleId           = "stage-not-allowed",
                    MessageEt        = $"Staadiumi tähis ei ole lubatud: '{sheet.Stage}'.",
                    RecommendationEt = $"Lubatud staadiumid: {string.Join(", ", allowed)}.",
                });
            }
        }

        var added = issues.Count - before;
        return OkResult(added, $"Kontrollitud {parsedSheets.Count} lehe staadiumi. Leitud {added} probleemi.", sw.ElapsedMilliseconds,
            issues.Skip(before).ToList());
    }
}
