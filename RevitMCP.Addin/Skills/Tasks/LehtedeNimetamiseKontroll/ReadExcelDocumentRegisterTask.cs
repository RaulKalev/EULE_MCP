using System.Diagnostics;
using System.IO;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: read.excel.document-register
/// Reads an Excel document register file and stores results in SharedData["documentRegister"].
/// If no file path is configured the task is skipped with a warning.
/// </summary>
internal sealed class ReadExcelDocumentRegisterTask : SheetNamingBaseTask
{
    public override string Id   => "read.excel.document-register";
    public override string Name => "Loe Exceli dokumentide loetelu";

    private static readonly string[] DefaultNumberAliases    = { "Dokumendi nr", "Joonise nr", "Lehe number", "Number", "Nr" };
    private static readonly string[] DefaultNameAliases      = { "Nimetus", "Joonise nimetus", "Lehe nimi", "Nimi" };
    private static readonly string[] DefaultDisciplineAliases = { "Eriala", "Osa" };
    private static readonly string[] DefaultStageAliases     = { "Staadium", "Etapp" };

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();

        var excelFilePath = SkillSettings.GetString(taskDef.Settings, "excelFilePath");
        if (string.IsNullOrWhiteSpace(excelFilePath))
        {
            ctx.SharedData["documentRegister"] = new List<DocumentRegisterItem>();
            return SkipResult("Exceli faili tee ei ole seadistatud — dokumentide loetelu ei laeta.", sw.ElapsedMilliseconds);
        }

        if (!File.Exists(excelFilePath))
        {
            ctx.SharedData["documentRegister"] = new List<DocumentRegisterItem>();
            return FailResult($"Exceli fail ei leita: {excelFilePath}", sw.ElapsedMilliseconds);
        }

        try
        {
            var numberAliases    = SkillSettings.GetStringArray(taskDef.Settings, "documentNumberColumnAliases",    DefaultNumberAliases);
            var nameAliases      = SkillSettings.GetStringArray(taskDef.Settings, "documentNameColumnAliases",      DefaultNameAliases);
            var disciplineAliases = SkillSettings.GetStringArray(taskDef.Settings, "disciplineColumnAliases",       DefaultDisciplineAliases);
            var stageAliases     = SkillSettings.GetStringArray(taskDef.Settings, "stageColumnAliases",             DefaultStageAliases);

            var items = ExcelDocumentRegisterReader.Read(excelFilePath,
                numberAliases, nameAliases, disciplineAliases, stageAliases,
                out var warnings);

            ctx.SharedData["documentRegister"] = items;

            var result = OkResult(0, $"Loetud {items.Count} kirjet Exceli loetelust.", sw.ElapsedMilliseconds);
            result.Warnings.AddRange(warnings);
            return result;
        }
        catch (Exception ex)
        {
            ctx.SharedData["documentRegister"] = new List<DocumentRegisterItem>();
            return FailResult($"Exceli faili lugemine ebaõnnestus: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }
}
