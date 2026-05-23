using System.Diagnostics;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: validate.disciplines
/// Checks that each parsed sheet's Discipline code is in the allowed list.
/// Only runs on sheets that were successfully parsed by ValidateSheetNumbersTask.
/// </summary>
internal sealed class ValidateDisciplinesTask : SheetNamingBaseTask
{
    public override string Id   => "validate.disciplines";
    public override string Name => "Valideeri erialad";

    private static readonly string[] DefaultAllowed = { "EL", "EN", "EA" };

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        var sheets = GetSheets(ctx);
        if (sheets is null)
            return SkipResult("Lehti ei leitud SharedData's — käivita enne collect.revit.sheets.", sw.ElapsedMilliseconds);

        var issues  = GetOrCreateIssuesList(ctx);
        var before  = issues.Count;
        var allowed = SkillSettings.GetStringArray(taskDef.Settings, "allowedDisciplines", DefaultAllowed);

        if (allowed.Length == 0)
            return OkResult(0, "Lubatud erialade loetelu on tühi — eriala kontroll vahele jäetud.", sw.ElapsedMilliseconds);

        var parsedSheets = sheets.Where(s => s.ParsedSuccessfully).ToList();

        foreach (var sheet in parsedSheets)
        {
            if (string.IsNullOrEmpty(sheet.Discipline)) continue;

            if (!allowed.Any(a => string.Equals(a, sheet.Discipline, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Viga",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = sheet.SheetName,
                    RuleId           = "discipline-not-allowed",
                    MessageEt        = $"Eriala tähis ei vasta projekti reeglitele: '{sheet.Discipline}'.",
                    RecommendationEt = $"Lubatud erialad: {string.Join(", ", allowed)}.",
                });
            }
        }

        var added = issues.Count - before;
        return OkResult(added, $"Kontrollitud {parsedSheets.Count} lehe eriala. Leitud {added} probleemi.", sw.ElapsedMilliseconds,
            issues.Skip(before).ToList());
    }
}
