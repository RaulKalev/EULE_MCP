using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Schedules;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Schedules;

/// <summary>revit_preview_create_schedule (#103): resolves the spec and builds the schedule in a rolled-back trial run.</summary>
public sealed class PreviewCreateScheduleTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_schedule";

    public string Description =>
        "Previews revit_create_schedule without changing the model: same arguments. Resolves the category and every " +
        "field (missingFields with the nearest names), the filter types and how each value is read, and the sorting; " +
        "then builds the schedule in a transaction that is rolled back and reports the fields as created, the row count " +
        "and the first rows. Fails with field_not_found when a field is missing, unless allowMissingFields=true.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Schedules;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateScheduleExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_create_schedule (#103): creates a schedule from a spec in one transaction.</summary>
public sealed class CreateScheduleTool : IRevitMcpTool
{
    public string Name => "revit_create_schedule";

    public string Description =>
        "Creates a schedule from a spec. Requires approval. name (made unique), category (or multiCategory=true, or " +
        "keySchedule=true with keyName), fields in order — names from revit_list_schedulable_fields, or objects " +
        "{parameter, heading, width (mm), hidden, totals, format {units, accuracy, suppressTrailingZeros}, fieldType}, " +
        "{count: true}, {percentage: {of}} — filters [{field, type, value}] (" + ScheduleFilterTypeMap.AcceptedText + "), " +
        "sort [{field, descending, header, footer, blankLine}], itemize (default true), grandTotal (" + ScheduleGrandTotal.AcceptedText + "), " +
        "template (schedule view template name), phase. A filter or sort on a field that is not a column adds it hidden. " +
        "Formula fields cannot be created through the Revit API. One transaction: a missing field or a step Revit refuses " +
        "rolls everything back unless allowMissingFields=true. Returns scheduleId, the fields as created and rowCount. " +
        "Run revit_preview_create_schedule first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Schedules;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateScheduleExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class CreateScheduleExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var errors = new List<string>();
        var spec = ScheduleSpecParser.ParseCreate(request.Arguments, errors);
        if (errors.Count > 0)
            return ScheduleToolSupport.Fail(request, "validation_failed", string.Join(" ", errors), sw: sw);

        var categoryId = ScheduleToolSupport.ResolveCategory(doc, spec.Category, spec.MultiCategory, spec.KeySchedule, out var categoryError, out var suggestions);
        if (categoryId == null)
            return ScheduleToolSupport.Fail(request, "validation_failed", categoryError!, new { suggestions }, sw: sw);
        var categoryName = spec.MultiCategory ? "Multi-Category" : Category.GetCategory(doc, categoryId)?.Name ?? spec.Category;

        var blocker = ScheduleToolSupport.TransactionBlocker(doc);
        if (blocker != null)
            return ScheduleToolSupport.Fail(request, "validation_failed", blocker, sw: sw);

        if (string.IsNullOrWhiteSpace(spec.Name))
        {
            try
            {
                spec.Name = spec.KeySchedule
                    ? ViewSchedule.GetDefaultNameForKeySchedule(doc, categoryId)
                    : ViewSchedule.GetDefaultNameForSchedule(doc, categoryId);
            }
            catch
            {
                spec.Name = $"{categoryName} Schedule";
            }
        }
        var taken = ScheduleToolSupport.TakenViewNames(doc);
        var warnings = new List<string>();

        return apply
            ? Create(request, doc, spec, categoryId, categoryName, taken, warnings, sw, cancellationToken)
            : Preview(request, doc, spec, categoryId, categoryName, taken, warnings, sw, cancellationToken);
    }

    // ── Preview ─────────────────────────────────────────────────────────────

    private static McpToolResult Preview(
        McpToolRequest request, Document doc, ScheduleSpec spec, ElementId categoryId, string categoryName,
        HashSet<string> taken, List<string> warnings, Stopwatch sw, CancellationToken cancellationToken)
    {
        SchedulePlan? plan = null;
        ScheduleExecution? execution = null;
        object? created = null;
        int? rowCount = null;
        List<string>? columns = null;
        List<string[]>? sample = null;
        string? trialError = null;

        using (var transaction = new Transaction(doc, "Revit MCP - Create Schedule (preview, rolled back)"))
        {
            try
            {
                RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
                transaction.Start();
                var schedule = ScheduleToolSupport.CreateView(doc, categoryId, spec.KeySchedule);
                var catalog = ScheduleFieldCatalog.Read(doc, schedule.Definition, details: false);
                plan = SchedulePlanner.PlanCreate(spec, catalog, taken);
                if (plan.CanApply)
                {
                    if (plan.Name != null) schedule.Name = plan.Name;
                    execution = ScheduleBuilder.Apply(doc, schedule, plan, cancellationToken);
                    doc.Regenerate();
                    created = ScheduleDefinitionReader.Fields(doc, schedule.Definition).Select(f => f.Shape).ToList();
                    var table = ScheduleTableReader.Read(schedule);
                    rowCount = table.DataRows.Count;
                    columns = table.Columns;
                    sample = ScheduleToolSupport.Sample(table, 5);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                trialError = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
            }
        }

        if (plan == null)
            return ScheduleToolSupport.Fail(request, "tool_execution_failed", $"Revit could not make a trial schedule for '{categoryName}': {trialError}", sw: sw);

        warnings.AddRange(plan.Warnings);
        if (trialError != null) warnings.Add($"The trial run stopped: {trialError}");
        var failed = execution?.Failed.ToList() ?? new List<ScheduleActionResult>();
        foreach (var step in failed)
            warnings.Add($"Revit refused '{step.Description}': {step.Error}");

        var data = new
        {
            category = categoryName,
            name = plan.Name,
            status = plan.Status,
            plan = ScheduleToolSupport.DescribePlan(plan),
            trialRun = execution == null ? null : new
            {
                execution = ScheduleToolSupport.DescribeExecution(execution),
                fields = created,
                rowCount,
                columns,
                firstRows = sample,
                note = "Built in a transaction that was rolled back: the model is unchanged."
            },
            wouldApply = plan.CanApply && (failed.Count == 0 || spec.AllowMissingFields)
        };

        if (!plan.CanApply)
            return ScheduleToolSupport.Fail(request, plan.Status, "Preview: " + ScheduleToolSupport.PlanRefusal(plan), data, warnings, sw);
        if (failed.Count > 0 && !spec.AllowMissingFields)
            return ScheduleToolSupport.Fail(request, "validation_failed",
                $"Preview: Revit refused {failed.Count} step(s); revit_create_schedule would roll back. Fix them or pass allowMissingFields=true to keep the rest.",
                data, warnings, sw);

        return ScheduleToolSupport.Ok(request, sw,
            $"Preview: schedule '{plan.Name}' ({categoryName}) with {plan.Fields.Count} field(s), " +
            $"{plan.Actions.Count(a => a.Kind == ScheduleActionKind.AddFilter)} filter(s), " +
            $"{plan.Actions.Count(a => a.Kind == ScheduleActionKind.AddSort)} sort level(s)" +
            (rowCount.HasValue ? $"; it would show {rowCount} row(s)." : "."),
            data, warnings);
    }

    // ── Create ──────────────────────────────────────────────────────────────

    private static McpToolResult Create(
        McpToolRequest request, Document doc, ScheduleSpec spec, ElementId categoryId, string categoryName,
        HashSet<string> taken, List<string> warnings, Stopwatch sw, CancellationToken cancellationToken)
    {
        SchedulePlan? plan = null;
        ScheduleExecution? execution = null;
        ViewSchedule? schedule = null;
        string? abort = null;

        cancellationToken.ThrowIfCancellationRequested();
        var (committed, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Schedule", () =>
        {
            schedule = ScheduleToolSupport.CreateView(doc, categoryId, spec.KeySchedule);
            var catalog = ScheduleFieldCatalog.Read(doc, schedule.Definition, details: false);
            plan = SchedulePlanner.PlanCreate(spec, catalog, taken);
            if (!plan.CanApply)
            {
                abort = ScheduleToolSupport.PlanRefusal(plan);
                throw new ScheduleAbortException(abort);
            }
            if (plan.Name != null) schedule.Name = plan.Name;
            execution = ScheduleBuilder.Apply(doc, schedule, plan, cancellationToken);
            var failed = execution.Failed.ToList();
            if (failed.Count > 0 && !spec.AllowMissingFields)
            {
                abort = $"Revit refused {failed.Count} step(s): " + string.Join("; ", failed.Select(f => $"{f.Description} — {f.Error}")) +
                        ". Nothing was created. Fix them, or pass allowMissingFields=true to keep the rest.";
                throw new ScheduleAbortException(abort);
            }
            doc.Regenerate();
        });

        if (plan != null) warnings.AddRange(plan.Warnings);

        if (!committed)
        {
            if (abort != null && plan != null)
            {
                var status = plan.CanApply ? "validation_failed" : plan.Status;
                return ScheduleToolSupport.Fail(request, status, "Nothing was created: " + abort,
                    new
                    {
                        plan = ScheduleToolSupport.DescribePlan(plan),
                        execution = execution == null ? null : ScheduleToolSupport.DescribeExecution(execution)
                    }, warnings, sw);
            }
            warnings.AddRange(diagnostics.ToErrorLines());
            return ScheduleToolSupport.Fail(request, "transaction_failed", "The schedule was not created: the transaction did not commit. The model is unchanged.",
                new { transaction = diagnostics }, warnings, sw);
        }

        warnings.AddRange(diagnostics.FailureMessages);
        foreach (var step in execution!.Failed)
            warnings.Add($"Revit refused '{step.Description}': {step.Error} (kept the rest: allowMissingFields=true).");

        var table = ScheduleTableReader.Read(schedule!);
        var fields = ScheduleDefinitionReader.Fields(doc, schedule!.Definition).Select(f => f.Shape).ToList();
        return ScheduleToolSupport.Ok(request, sw,
            $"Created schedule '{schedule.Name}' ({categoryName}, id {schedule.Id.Value}) with {fields.Count} field(s); it shows {table.DataRows.Count} row(s).",
            new
            {
                scheduleId = schedule.Id.Value,
                name = schedule.Name,
                category = categoryName,
                fields,
                missingFields = ScheduleToolSupport.DescribeMissing(plan!),
                rowCount = table.DataRows.Count,
                columns = table.Columns,
                firstRows = ScheduleToolSupport.Sample(table, 5),
                execution = ScheduleToolSupport.DescribeExecution(execution)
            }, warnings);
    }
}
