using System.Diagnostics;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: validate.sheet-numbers
/// Validates sheet number format, detects duplicates, validates sequence length,
/// and populates each SheetNamingItem's parsed components (Stage, Discipline, etc.).
/// </summary>
internal sealed class ValidateSheetNumbersTask : SheetNamingBaseTask
{
    public override string Id   => "validate.sheet-numbers";
    public override string Name => "Valideeri lehe numbrid";

    private static readonly string[] DefaultPatterns =
    {
        "{ProjectNumber}_{Stage}_{Discipline}-{Group}-{Sequence}",
        "{ProjectNumber}_{Stage}_{Discipline}-{Group}-{Sequence}_{Revision}",
        "{ProjectNumber}_{Stage}_{Discipline}-{Group}-{Sequence}_{Date}",
    };

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        var sheets = GetSheets(ctx);
        if (sheets is null)
            return SkipResult("Lehti ei leitud SharedData's — käivita enne collect.revit.sheets.", sw.ElapsedMilliseconds);

        var issues    = GetOrCreateIssuesList(ctx);
        var before    = issues.Count;
        var patterns  = SkillSettings.GetStringArray(taskDef.Settings, "sheetNumberPatterns", DefaultPatterns);
        var seqMin    = SkillSettings.GetInt(taskDef.Settings, "sequenceNumberMinLength", 2);
        var seqMax    = SkillSettings.GetInt(taskDef.Settings, "sequenceNumberMaxLength", 3);
        var requirePN = SkillSettings.GetBool(taskDef.Settings, "requireProjectNumber", true);

        // Validate format + populate components
        foreach (var sheet in sheets)
        {
            if (string.IsNullOrWhiteSpace(sheet.SheetNumber))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Kriitiline",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = sheet.SheetName,
                    RuleId           = "sheet-number-empty",
                    MessageEt        = "Lehe number on tühi.",
                    RecommendationEt = "Sisesta lehe number vastavalt projekti reeglitele.",
                });
                continue;
            }

            // Sheet numbers must never contain spaces
            if (sheet.SheetNumber.Contains(' '))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Kriitiline",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = sheet.SheetName,
                    RuleId           = "sheet-number-contains-space",
                    MessageEt        = $"Lehe number sisaldab tühikut: '{sheet.SheetNumber}'.",
                    RecommendationEt = "Lehe numbris ei tohi olla tühikuid.",
                });
                continue;
            }

            var parsed = SheetNumberParser.Parse(sheet.SheetNumber, patterns);
            if (parsed is null)
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Viga",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = sheet.SheetName,
                    RuleId           = "sheet-number-format",
                    MessageEt        = $"Lehe numbri formaat ei vasta reeglile: '{sheet.SheetNumber}'.",
                    RecommendationEt = $"Oodatav formaat: {string.Join(" või ", patterns)}.",
                });
                continue;
            }

            // Populate parsed fields on the item (used by later tasks)
            sheet.ParsedSuccessfully = true;
            sheet.ProjectNumber      = parsed.ProjectNumber;
            sheet.Stage              = parsed.Stage;
            sheet.Discipline         = parsed.Discipline;
            sheet.Group              = parsed.Group;
            sheet.Sequence           = parsed.Sequence;
            sheet.Revision           = parsed.Revision;

            // Validate sequence length
            if (!string.IsNullOrEmpty(parsed.Sequence) &&
                (parsed.Sequence.Length < seqMin || parsed.Sequence.Length > seqMax))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Hoiatus",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = sheet.SheetName,
                    RuleId           = "sequence-length",
                    MessageEt        = $"Järjekorranumber '{parsed.Sequence}' pikkus on väljaspool lubatud vahemikku ({seqMin}–{seqMax} kohta).",
                    RecommendationEt = $"Kasuta {seqMin}–{seqMax} kohaline järjekorranumber, nt '01' või '001'.",
                });
            }

            // Validate project number presence
            if (requirePN && string.IsNullOrEmpty(parsed.ProjectNumber))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Kriitiline",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = sheet.SheetName,
                    RuleId           = "missing-project-number",
                    MessageEt        = "Puudub projekti tähis.",
                    RecommendationEt = "Lehe number peab algama projekti tähisega, nt '1626'.",
                });
            }
        }

        // Detect duplicates
        var duplicates = sheets
            .Where(s => !string.IsNullOrWhiteSpace(s.SheetNumber))
            .GroupBy(s => s.SheetNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var dup in duplicates)
        {
            foreach (var sheet in dup)
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Kriitiline",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = sheet.SheetName,
                    RuleId           = "duplicate-sheet-number",
                    MessageEt        = $"Duplikaatne lehe number: '{sheet.SheetNumber}'.",
                    RecommendationEt = "Iga lehe number peab olema unikaalne projektis.",
                });
            }
        }

        var added = issues.Count - before;
        return OkResult(added, $"Kontrollitud {sheets.Count} lehe numbrit. Leitud {added} probleemi.", sw.ElapsedMilliseconds,
            issues.Skip(before).ToList());
    }
}
