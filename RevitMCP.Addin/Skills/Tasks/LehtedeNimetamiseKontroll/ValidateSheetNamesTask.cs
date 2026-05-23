using System.Diagnostics;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: validate.sheet-names
/// Checks for empty names, leading/trailing spaces, double spaces, and forbidden characters.
/// </summary>
internal sealed class ValidateSheetNamesTask : SheetNamingBaseTask
{
    public override string Id   => "validate.sheet-names";
    public override string Name => "Valideeri lehe nimetused";

    private static readonly string[] DefaultForbiddenChars = { "\\", "/", ":", "*", "?", "\"", "<", ">", "|" };

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        var sheets = GetSheets(ctx);
        if (sheets is null)
            return SkipResult("Lehti ei leitud SharedData's — käivita enne collect.revit.sheets.", sw.ElapsedMilliseconds);

        var issues        = GetOrCreateIssuesList(ctx);
        var before        = issues.Count;
        var forbidden     = SkillSettings.GetStringArray(taskDef.Settings, "forbiddenCharacters", DefaultForbiddenChars);
        var checkDoubles  = SkillSettings.GetBool(taskDef.Settings, "detectDoubleSpaces", false);
        var checkTrim     = SkillSettings.GetBool(taskDef.Settings, "trimWhitespace", false);
        var minLen        = SkillSettings.GetInt(taskDef.Settings, "minNameLength", 3);
        var checkNimetus  = SkillSettings.GetBool(taskDef.Settings, "checkNimetusSpaces", true);
        var checkMarkus   = SkillSettings.GetBool(taskDef.Settings, "checkMarkusSpaces", true);

        foreach (var sheet in sheets)
        {
            var name = sheet.SheetName;

            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Kriitiline",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = name,
                    RuleId           = "sheet-name-empty",
                    MessageEt        = "Lehe nimi on tühi.",
                    RecommendationEt = "Sisesta lehe nimetus.",
                });
                continue;
            }

            if (checkTrim && (name.StartsWith(" ") || name.EndsWith(" ")))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Hoiatus",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = name,
                    RuleId           = "sheet-name-whitespace",
                    MessageEt        = "Lehe nime alguses või lõpus on tühik.",
                    RecommendationEt = "Eemalda lehe nime ees- ja järeltühikud.",
                });
            }

            if (checkDoubles && name.Contains("  "))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Hoiatus",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = name,
                    RuleId           = "sheet-name-double-space",
                    MessageEt        = "Lehe nimes on topelttühikud.",
                    RecommendationEt = "Asenda topelttühikud ühe tühikuga.",
                });
            }

            if (minLen > 0 && name.Trim().Length < minLen)
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Hoiatus",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = name,
                    RuleId           = "sheet-name-too-short",
                    MessageEt        = $"Lehe nimi on liiga lühike (vähem kui {minLen} tähemärki): '{name}'.",
                    RecommendationEt = "Kasuta sisukat ja kirjeldavat lehe nimetust.",
                });
            }

        }

        // Nimetus — no spaces, no forbidden characters
        if (checkNimetus)
        {
            foreach (var sheet in sheets.Where(s => !string.IsNullOrEmpty(s.Nimetus)))
            {
                if (sheet.Nimetus.Contains(' '))
                    issues.Add(new SheetNamingIssue
                    {
                        Severity = "Viga", SheetNumber = sheet.SheetNumber, SheetName = sheet.SheetName,
                        RuleId = "nimetus-contains-space",
                        MessageEt = $"Nimetus sisaldab tühikut: '{sheet.Nimetus}'.",
                        RecommendationEt = "Nimetus ei tohi sisaldada tühikuid.",
                    });

                foreach (var ch in forbidden)
                    if (!string.IsNullOrEmpty(ch) && sheet.Nimetus.Contains(ch))
                    {
                        issues.Add(new SheetNamingIssue
                        {
                            Severity = "Viga", SheetNumber = sheet.SheetNumber, SheetName = sheet.SheetName,
                            RuleId = "nimetus-forbidden-char",
                            MessageEt = $"Nimetus sisaldab keelatud sümboli '{ch}': '{sheet.Nimetus}'.",
                            RecommendationEt = $"Eemalda sümbol '{ch}' Nimetus väljast.",
                        });
                        break;
                    }
            }
        }

        // Märkus — no spaces, no forbidden characters
        if (checkMarkus)
        {
            foreach (var sheet in sheets.Where(s => !string.IsNullOrEmpty(s.Markus)))
            {
                if (sheet.Markus.Contains(' '))
                    issues.Add(new SheetNamingIssue
                    {
                        Severity = "Viga", SheetNumber = sheet.SheetNumber, SheetName = sheet.SheetName,
                        RuleId = "markus-contains-space",
                        MessageEt = $"Märkus sisaldab tühikut: '{sheet.Markus}'.",
                        RecommendationEt = "Märkus ei tohi sisaldada tühikuid.",
                    });

                foreach (var ch in forbidden)
                    if (!string.IsNullOrEmpty(ch) && sheet.Markus.Contains(ch))
                    {
                        issues.Add(new SheetNamingIssue
                        {
                            Severity = "Viga", SheetNumber = sheet.SheetNumber, SheetName = sheet.SheetName,
                            RuleId = "markus-forbidden-char",
                            MessageEt = $"Märkus sisaldab keelatud sümboli '{ch}': '{sheet.Markus}'.",
                            RecommendationEt = $"Eemalda sümbol '{ch}' Märkus väljast.",
                        });
                        break;
                    }
            }
        }

        var added = issues.Count - before;
        return OkResult(added, $"Kontrollitud {sheets.Count} lehe nimetust. Leitud {added} probleemi.", sw.ElapsedMilliseconds,
            issues.Skip(before).ToList());
    }
}
