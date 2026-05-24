using RevitMCP.Addin.Skills.Tasks.Common;
using RevitMCP.Addin.Skills.Tasks.Coordination;
using RevitMCP.Addin.Skills.Tasks.Delivery;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;
using RevitMCP.Addin.Skills.Tasks.ParameterQA;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// Maps skill task IDs (from .skill.json files) to their <see cref="ISkillTask"/> implementations.
/// Add new task implementations here when they are created.
/// </summary>
public class SkillTaskRegistry
{
    private readonly Dictionary<string, ISkillTask> _tasks;

    public SkillTaskRegistry()
    {
        var list = new ISkillTask[]
        {
            // Lehtede Nimetamise Kontroll
            new CollectRevitSheetsTask(),
            new ReadExcelDocumentRegisterTask(),
            new ValidateSheetNumbersTask(),
            new ValidateSheetNamesTask(),
            new ValidateSheetParametersTask(),
            new ValidateDisciplinesTask(),
            new ValidateStagesTask(),
            new CompareExcelRegisterTask(),
            new ExportSheetNamingExcelReportTask(),
            new ExportSheetNamingJsonReportTask(),

            // Common export + merge tasks
            new ExportIssueExcelReportTask(),
            new ExportIssueHtmlDashboardTask(),
            new MergeIssueReportsTask(),

            // Delivery tasks
            new ScanDeliveryFolderTask(),
            new CompareDeliveryRevitSheetsTask(),

            // Coordination tasks
            new RunClashPresetTask(),

            // Parameter QA tasks
            new RunParameterQaRuleSetTask(),
        };

        _tasks = list.ToDictionary(t => t.Id, t => t, StringComparer.OrdinalIgnoreCase);
    }

    public ISkillTask? Get(string taskId)
        => _tasks.TryGetValue(taskId, out var t) ? t : null;

    public IReadOnlyList<ISkillTask> GetAll() => _tasks.Values.ToList();

    public bool HasTask(string taskId) => _tasks.ContainsKey(taskId);
}
