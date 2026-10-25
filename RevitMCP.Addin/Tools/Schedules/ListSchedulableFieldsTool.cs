using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Schedules;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Schedules;

/// <summary>revit_list_schedulable_fields (#103): the field names revit_create_schedule accepts for a category.</summary>
public sealed class ListSchedulableFieldsTool : IRevitMcpTool
{
    public string Name => "revit_list_schedulable_fields";

    public string Description =>
        "Lists the fields a schedule of a category can show (ScheduleDefinition.GetSchedulableFields): name, " +
        "qualifiedName (\"Room: Name\" for fields of related elements), parameterId, fieldType (Instance, ElementType, " +
        "Count, Room, FromRoom, …), isShared and dataType. category (name), multiCategory=true, or schedule (an existing " +
        "schedule's own catalogue). Optional nameFilter and fieldType narrow the list. Reads an existing schedule of the " +
        "category when there is one; otherwise creates a schedule in a transaction that is rolled back — the model is unchanged.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Schedules;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var args = request.Arguments;

        var scheduleArg = ToolArguments.GetString(args, "schedule").Trim();
        var categoryArg = ToolArguments.GetString(args, "category").Trim();
        var multiCategory = ToolArguments.GetBool(args, "multiCategory");
        var nameFilter = ToolArguments.GetString(args, "nameFilter").Trim();
        var fieldType = ScheduleFieldNames.NormalizeFieldType(ToolArguments.GetString(args, "fieldType"));

        List<SchedulableFieldInfo> catalog;
        string source;
        if (scheduleArg.Length > 0)
        {
            var schedule = ScheduleLookup.Resolve(doc, request, scheduleArg, 0, forEdit: false, out var failure);
            if (schedule == null) return Task.FromResult(failure!);
            catalog = ScheduleFieldCatalog.Read(doc, schedule.Definition, details: true);
            source = $"schedule '{schedule.Name}' ({schedule.Id.Value})";
        }
        else
        {
            if (categoryArg.Length == 0 && !multiCategory)
                return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", "Pass category (e.g. \"Electrical Fixtures\"), multiCategory=true, or schedule."));
            var categoryId = ScheduleToolSupport.ResolveCategory(doc, categoryArg, multiCategory, keySchedule: false, out var error, out var suggestions);
            if (categoryId == null)
                return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", error!, new { suggestions }));

            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .FirstOrDefault(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.Definition.IsKeySchedule &&
                                     !s.Definition.IsMaterialTakeoff && !s.Definition.IsEmbedded && s.Definition.CategoryId == categoryId);
            if (existing != null)
            {
                catalog = ScheduleFieldCatalog.Read(doc, existing.Definition, details: true);
                source = $"existing schedule '{existing.Name}' ({existing.Id.Value})";
            }
            else
            {
                var blocker = ScheduleToolSupport.TransactionBlocker(doc);
                if (blocker != null)
                    return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", $"No schedule of this category exists to read the fields from, and a temporary one cannot be made: {blocker}"));
                try { catalog = ReadFromTemporarySchedule(doc, categoryId); }
                catch (Exception ex)
                {
                    return Task.FromResult(ScheduleToolSupport.Fail(request, "tool_execution_failed", $"Revit could not make a temporary schedule to read the fields: {ex.Message}"));
                }
                source = "a temporary schedule (rolled back)";
            }
        }

        var total = catalog.Count;
        IEnumerable<SchedulableFieldInfo> filtered = catalog;
        if (fieldType != null)
            filtered = filtered.Where(f => string.Equals(f.FieldType, fieldType, StringComparison.OrdinalIgnoreCase));
        if (nameFilter.Length > 0)
        {
            var normalized = ParameterMatcher.Normalize(nameFilter);
            filtered = filtered.Where(f => ParameterMatcher.Normalize(f.QualifiedName).Contains(normalized));
        }

        var fields = filtered.Select(f => new
        {
            name = f.Name,
            qualifiedName = f.QualifiedName != f.Name ? f.QualifiedName : null,
            parameterId = f.ParameterId,
            fieldType = f.FieldType,
            isShared = f.IsShared,
            dataType = f.DataType
        }).ToList();

        return Task.FromResult(ScheduleToolSupport.Ok(request, sw,
            $"{fields.Count} of {total} schedulable field(s), read from {source}.",
            new
            {
                count = fields.Count,
                total,
                source,
                fieldTypes = catalog.GroupBy(f => f.FieldType).ToDictionary(g => g.Key, g => g.Count()),
                fields
            }));
    }

    /// <summary>GetSchedulableFields of a schedule made for the purpose in a transaction that is always rolled back.</summary>
    private static List<SchedulableFieldInfo> ReadFromTemporarySchedule(Document doc, ElementId categoryId)
    {
        using var transaction = new Transaction(doc, "Revit MCP - Read schedulable fields (rolled back)");
        transaction.Start();
        try
        {
            var temporary = ViewSchedule.CreateSchedule(doc, categoryId);
            return ScheduleFieldCatalog.Read(doc, temporary.Definition, details: true);
        }
        finally
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
                transaction.RollBack();
        }
    }
}
