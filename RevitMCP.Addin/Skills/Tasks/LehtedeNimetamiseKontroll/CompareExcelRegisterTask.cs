using System.Diagnostics;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: compare.excel-register
/// Compares Revit sheets against the Excel document register.
/// Reports sheets present in Excel but missing in Revit, sheets in Revit missing from Excel,
/// and name mismatches where the document number matches but the name differs.
/// </summary>
internal sealed class CompareExcelRegisterTask : SheetNamingBaseTask
{
    public override string Id   => "compare.excel-register";
    public override string Name => "Võrdle Exceli loeteluga";

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw       = Stopwatch.StartNew();
        var sheets   = GetSheets(ctx);
        var register = GetDocumentRegister(ctx);

        if (sheets is null)
            return SkipResult("Lehti ei leitud — käivita enne collect.revit.sheets.", sw.ElapsedMilliseconds);

        if (register is null || register.Count == 0)
            return SkipResult("Exceli loetelu puudub või on tühi — võrdlus vahele jäetud.", sw.ElapsedMilliseconds);

        var issues = GetOrCreateIssuesList(ctx);
        var before = issues.Count;

        // Build lookup maps (normalised: trim + lowercase)
        var revitByNumber   = sheets.ToDictionary(s => Norm(s.SheetNumber), s => s, StringComparer.OrdinalIgnoreCase);
        var excelByNumber   = register.ToDictionary(r => Norm(r.DocumentNumber), r => r, StringComparer.OrdinalIgnoreCase);

        // Sheets in Excel but missing in Revit
        foreach (var excelItem in register)
        {
            var key = Norm(excelItem.DocumentNumber);
            if (!revitByNumber.ContainsKey(key))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Viga",
                    SheetNumber      = excelItem.DocumentNumber,
                    SheetName        = excelItem.DocumentName,
                    RuleId           = "excel-sheet-missing-in-revit",
                    MessageEt        = $"Exceli loetelus olev leht puudub Revitis: '{excelItem.DocumentNumber}'.",
                    RecommendationEt = "Lisa puuduv leht Reviti projekti või eemalda see Exceli loetelust.",
                    Source           = "Excel",
                });
            }
        }

        // Sheets in Revit but missing in Excel
        foreach (var sheet in sheets)
        {
            if (string.IsNullOrWhiteSpace(sheet.SheetNumber)) continue;
            var key = Norm(sheet.SheetNumber);

            if (!excelByNumber.ContainsKey(key))
            {
                issues.Add(new SheetNamingIssue
                {
                    Severity         = "Hoiatus",
                    SheetNumber      = sheet.SheetNumber,
                    SheetName        = sheet.SheetName,
                    RuleId           = "revit-sheet-missing-in-excel",
                    MessageEt        = $"Revitis olev leht puudub Exceli loetelust: '{sheet.SheetNumber}'.",
                    RecommendationEt = "Lisa leht Exceli loetellu või kontrolli lehe numbrit.",
                    Source           = "Revit",
                });
            }
            else
            {
                // Name mismatch check
                var excelItem = excelByNumber[key];
                if (!string.IsNullOrEmpty(excelItem.DocumentName) &&
                    !NormName(sheet.SheetName).Equals(NormName(excelItem.DocumentName), StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new SheetNamingIssue
                    {
                        Severity         = "Hoiatus",
                        SheetNumber      = sheet.SheetNumber,
                        SheetName        = sheet.SheetName,
                        RuleId           = "sheet-name-mismatch",
                        MessageEt        = $"Lehe nimetus ei kattu Exceli loeteluga. Revit: '{sheet.SheetName}' | Excel: '{excelItem.DocumentName}'.",
                        RecommendationEt = "Kontrolli lehe nimetuse õigsust Revitis ja Exceli loetelus.",
                        Source           = "Revit + Excel",
                    });
                }
            }
        }

        var added = issues.Count - before;
        return OkResult(added, $"Võrreldud {sheets.Count} Revit lehte ja {register.Count} Exceli kirjet. Leitud {added} erinevust.",
            sw.ElapsedMilliseconds, issues.Skip(before).ToList());
    }

    private static string Norm(string s) => s.Trim();
    private static string NormName(string s) => s.Trim().Replace("  ", " ");
}
