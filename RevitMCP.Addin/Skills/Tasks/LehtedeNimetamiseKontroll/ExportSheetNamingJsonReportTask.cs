using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: export.json-report
/// Writes a JSON report with Estonian field names and full issue details.
/// </summary>
internal sealed class ExportSheetNamingJsonReportTask : SheetNamingBaseTask
{
    public override string Id   => "export.json-report";
    public override string Name => "Ekspordi JSON aruanne";

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var sheets     = GetSheets(ctx) ?? new List<SheetNamingItem>();
            var issues     = GetOrCreateIssuesList(ctx);
            var register   = GetDocumentRegister(ctx) ?? new List<DocumentRegisterItem>();
            var projectNum = GetProjectNumber(ctx);

            var fileNamePattern = SkillSettings.GetString(taskDef.Settings, "fileNamePattern",
                "Lehtede_Nimetamise_Kontroll_{ProjectNumber}.json");
            var fileName = fileNamePattern.Replace("{ProjectNumber}", projectNum.Length > 0 ? projectNum : "projekt");

            var outputFolder = SkillSettings.GetString(taskDef.Settings, "outputFolder");
            if (string.IsNullOrWhiteSpace(outputFolder))
                outputFolder = ctx.OutputFolder;
            Directory.CreateDirectory(outputFolder);

            var filePath = Path.Combine(outputFolder, fileName);

            var report = new JObject
            {
                ["skillId"]           = ctx.SkillId,
                ["runId"]             = ctx.RunId,
                ["projektiNumber"]    = projectNum,
                ["projektiNimi"]      = ctx.ProjectName,
                ["kontrollKuupäev"]   = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["kokkulehti"]        = sheets.Count,
                ["kokku_probleeme"]   = issues.Count,
                ["kriitilised"]       = issues.Count(i => i.Severity == "Kriitiline"),
                ["vead"]              = issues.Count(i => i.Severity == "Viga"),
                ["hoiatused"]         = issues.Count(i => i.Severity == "Hoiatus"),
                ["info"]              = issues.Count(i => i.Severity == "Info"),
                ["lehed"]             = new JArray(sheets.Select(SheetToJson)),
                ["probleemid"]        = new JArray(issues.Select(IssueToJson)),
                ["excelLoetelu"]      = new JArray(register.Select(RegItemToJson)),
            };

            File.WriteAllText(filePath, report.ToString(Formatting.Indented));

            return OkResult(0, $"JSON aruanne salvestatud: {filePath}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return FailResult($"JSON aruande kirjutamine ebaõnnestus: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    private static JObject SheetToJson(SheetNamingItem s) => new()
    {
        ["leheNumber"]      = s.SheetNumber,
        ["leheNimi"]        = s.SheetName,
        ["projektiTähis"]   = s.ProjectNumber,
        ["staadium"]        = s.Stage,
        ["eriala"]          = s.Discipline,
        ["grupp"]           = s.Group,
        ["järjekorranumber"]= s.Sequence,
        ["revisioon"]       = s.Revision,
        ["kohaasendaja"]    = s.IsPlaceholder,
        ["parsitudEdukalt"] = s.ParsedSuccessfully,
    };

    private static JObject IssueToJson(SheetNamingIssue i) => new()
    {
        ["tõsidus"]     = i.Severity,
        ["leheNumber"]  = i.SheetNumber,
        ["leheNimi"]    = i.SheetName,
        ["reegel"]      = i.RuleId,
        ["probleem"]    = i.MessageEt,
        ["soovitus"]    = i.RecommendationEt,
        ["allikas"]     = i.Source,
    };

    private static JObject RegItemToJson(DocumentRegisterItem r) => new()
    {
        ["dokumendiNr"]  = r.DocumentNumber,
        ["nimetus"]      = r.DocumentName,
        ["eriala"]       = r.Discipline,
        ["staadium"]     = r.Stage,
        ["rida"]         = r.RowNumber,
    };
}
