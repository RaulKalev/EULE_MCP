using System.Diagnostics;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: validate.sheet-parameters
///
/// Two kinds of checks:
///   1. requiredParamNames / requiredParamValues (parallel arrays) —
///      each sheet must have the exact expected value (e.g. Projekteerija = "EULE OÜ").
///
///   2. consistentParamNames —
///      each sheet must have a non-empty value AND all sheets must share the same value
///      (e.g. Peaprojekteerija must be identical everywhere).
/// </summary>
internal sealed class ValidateSheetParametersTask : SheetNamingBaseTask
{
    public override string Id   => "validate.sheet-parameters";
    public override string Name => "Valideeri lehe parameetrid";

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw     = Stopwatch.StartNew();
        var sheets = GetSheets(ctx);
        if (sheets is null)
            return SkipResult("Lehti ei leitud SharedData's — käivita enne collect.revit.sheets.", sw.ElapsedMilliseconds);

        var issues = GetOrCreateIssuesList(ctx);
        var before = issues.Count;

        // ── Required-value checks (Projekteerija group) ──────────────────────────
        var requiredNames  = SkillSettings.GetStringArray(taskDef.Settings, "requiredParamNames",  Array.Empty<string>());
        var requiredValues = SkillSettings.GetStringArray(taskDef.Settings, "requiredParamValues", Array.Empty<string>());
        var pairCount      = Math.Min(requiredNames.Length, requiredValues.Length);

        for (int i = 0; i < pairCount; i++)
        {
            var paramName     = requiredNames[i];
            var expectedValue = requiredValues[i];

            foreach (var sheet in sheets)
            {
                var actual = sheet.ExtParams.TryGetValue(paramName, out var v) ? v : string.Empty;

                if (string.IsNullOrWhiteSpace(actual))
                {
                    issues.Add(new SheetNamingIssue
                    {
                        Severity         = "Viga",
                        SheetNumber      = sheet.SheetNumber,
                        SheetName        = sheet.SheetName,
                        RuleId           = "sheet-param-missing",
                        MessageEt        = $"Parameeter '{paramName}' on tühi.",
                        RecommendationEt = $"Sisesta väärtus: '{expectedValue}'.",
                    });
                }
                else if (!string.Equals(actual, expectedValue, StringComparison.Ordinal))
                {
                    issues.Add(new SheetNamingIssue
                    {
                        Severity         = "Viga",
                        SheetNumber      = sheet.SheetNumber,
                        SheetName        = sheet.SheetName,
                        RuleId           = "sheet-param-wrong-value",
                        MessageEt        = $"Parameeter '{paramName}' vale väärtus. Oodatav: '{expectedValue}', tegelik: '{actual}'.",
                        RecommendationEt = $"Muuda väärtuseks: '{expectedValue}'.",
                    });
                }
            }
        }

        // ── Consistency checks (Peaprojekteerija group) ───────────────────────────
        var consistentNames = SkillSettings.GetStringArray(taskDef.Settings, "consistentParamNames", Array.Empty<string>());

        foreach (var paramName in consistentNames)
        {
            var values = sheets
                .Select(s => s.ExtParams.TryGetValue(paramName, out var v) ? v : string.Empty)
                .ToList();

            var allEmpty = values.All(string.IsNullOrWhiteSpace);
            if (allEmpty)
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Hoiatus",
                    SheetNumber      = "*",
                    SheetName        = "*",
                    RuleId           = "sheet-param-all-empty",
                    MessageEt        = $"Parameeter '{paramName}' on kõikidel lehtedel tühi.",
                    RecommendationEt = $"Täida parameeter '{paramName}' kõikidel lehtedel.",
                });
                continue;
            }

            // Report individual sheets where value is missing
            foreach (var sheet in sheets)
            {
                if (!sheet.ExtParams.TryGetValue(paramName, out var actual) || string.IsNullOrWhiteSpace(actual))
                {
                    issues.Add(new SheetNamingIssue
                    {
                        Severity         = "Viga",
                        SheetNumber      = sheet.SheetNumber,
                        SheetName        = sheet.SheetName,
                        RuleId           = "sheet-param-missing",
                        MessageEt        = $"Parameeter '{paramName}' on tühi.",
                        RecommendationEt = $"Täida parameeter '{paramName}'.",
                    });
                }
            }

            // All filled values must be identical
            var distinctNonEmpty = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
            if (distinctNonEmpty.Count > 1)
            {
                // Majority value = most common filled value
                var majority = values
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .GroupBy(v => v)
                    .OrderByDescending(g => g.Count())
                    .First().Key;

                foreach (var sheet in sheets)
                {
                    if (!sheet.ExtParams.TryGetValue(paramName, out var actual)) continue;
                    if (string.IsNullOrWhiteSpace(actual) || actual == majority) continue;

                    issues.Add(new SheetNamingIssue
                    {
                        Severity         = "Viga",
                        SheetNumber      = sheet.SheetNumber,
                        SheetName        = sheet.SheetName,
                        RuleId           = "sheet-param-inconsistent",
                        MessageEt        = $"Parameeter '{paramName}' erineb teistest lehtedest. Väärtus: '{actual}', oodatav: '{majority}'.",
                        RecommendationEt = $"Ühtlusta parameeter '{paramName}' kõikidel lehtedel.",
                    });
                }
            }
        }

        var added = issues.Count - before;
        return OkResult(added, $"Kontrollitud {sheets.Count} lehe parameetrit ({pairCount} kohustuslikku + {consistentNames.Length} ühtset). Leitud {added} probleemi.",
            sw.ElapsedMilliseconds, issues.Skip(before).ToList());
    }
}
