using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Schedules;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Schedules;

/// <summary>revit_preview_edit_schedule (#103): what an edit would change, with the field order before and after.</summary>
public sealed class PreviewEditScheduleTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_schedule";

    public string Description =>
        "Previews revit_edit_schedule without changing the model: same arguments. Shows the field order before and " +
        "after, every step, names that do not resolve (with the nearest names), and — from a trial run that is rolled " +
        "back — the steps Revit would refuse and the row count after the edit.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Schedules;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(EditScheduleExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_edit_schedule (#103): changes an existing schedule's fields, filters and sorting.</summary>
public sealed class EditScheduleTool : IRevitMcpTool
{
    public string Name => "revit_edit_schedule";

    public string Description =>
        "Edits a schedule. Requires approval. schedule (name or id); addFields (same shapes as revit_create_schedule, " +
        "optional insertAt), removeFields (names or 0-based indexes), order (names; unlisted fields follow), rename " +
        "{field: heading}, hide[], show[], format {field: {units, accuracy, suppressTrailingZeros}}, widths {field: mm}, " +
        "setFilters (replaces all; [] clears), setSort (replaces all; [] clears), itemize, grandTotal. Key schedules: " +
        "fields only. Refuses templates, title-block revision schedules and panel schedules. One transaction: a missing " +
        "name or a refused step rolls everything back unless allowMissingFields=true. Run revit_preview_edit_schedule first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Schedules;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(EditScheduleExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class EditScheduleExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var schedule = ScheduleLookup.Resolve(doc, request, ToolArguments.GetString(request.Arguments, "schedule"), 0, forEdit: true, out var failure);
        if (schedule == null) return failure!;

        var errors = new List<string>();
        var edit = ScheduleSpecParser.ParseEdit(request.Arguments, errors);
        if (errors.Count > 0)
            return ScheduleToolSupport.Fail(request, "validation_failed", string.Join(" ", errors), sw: sw);

        var definition = schedule.Definition;
        var current = ScheduleFieldCatalog.Current(definition);
        var catalog = ScheduleFieldCatalog.Read(doc, definition, details: false);
        var plan = SchedulePlanner.PlanEdit(edit, current, catalog, definition.IsKeySchedule);
        var warnings = new List<string>(plan.Warnings);

        int rowsBefore;
        try { rowsBefore = ScheduleTableReader.Read(schedule).DataRows.Count; }
        catch { rowsBefore = -1; }

        if (!plan.CanApply)
            return ScheduleToolSupport.Fail(request, plan.Status, (apply ? "Nothing was changed: " : "Preview: ") + ScheduleToolSupport.PlanRefusal(plan),
                Shape(schedule, plan, null, rowsBefore, null, null), warnings, sw);

        var blocker = ScheduleToolSupport.TransactionBlocker(doc);
        if (blocker != null)
            return ScheduleToolSupport.Fail(request, "validation_failed", blocker, sw: sw);

        return apply
            ? Edit(request, doc, schedule, edit, plan, rowsBefore, warnings, sw, cancellationToken)
            : Preview(request, doc, schedule, edit, plan, rowsBefore, warnings, sw, cancellationToken);
    }

    private static McpToolResult Preview(
        McpToolRequest request, Document doc, ViewSchedule schedule, ScheduleEditSpec edit, SchedulePlan plan, int rowsBefore,
        List<string> warnings, Stopwatch sw, CancellationToken cancellationToken)
    {
        ScheduleExecution? execution = null;
        int? rowsAfter = null;
        List<string>? orderAfter = null;
        using (var transaction = new Transaction(doc, "Revit MCP - Edit Schedule (preview, rolled back)"))
        {
            try
            {
                RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
                transaction.Start();
                execution = ScheduleBuilder.Apply(doc, schedule, plan, cancellationToken);
                doc.Regenerate();
                orderAfter = ScheduleFieldCatalog.Current(schedule.Definition).Select(Display).ToList();
                rowsAfter = ScheduleTableReader.Read(schedule).DataRows.Count;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"The trial run stopped: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
            }
        }

        var failed = execution?.Failed.ToList() ?? new List<ScheduleActionResult>();
        foreach (var step in failed)
            warnings.Add($"Revit refused '{step.Description}': {step.Error}");
        var data = Shape(schedule, plan, execution, rowsBefore, rowsAfter, orderAfter);

        if (failed.Count > 0 && !edit.AllowMissingFields)
            return ScheduleToolSupport.Fail(request, "validation_failed",
                $"Preview: Revit refused {failed.Count} step(s); revit_edit_schedule would roll back. Fix them or pass allowMissingFields=true to keep the rest.",
                data, warnings, sw);

        return ScheduleToolSupport.Ok(request, sw,
            $"Preview: {plan.Actions.Count} step(s) on '{schedule.Name}'; fields {plan.OrderBefore.Count} → {plan.Fields.Count}" +
            (rowsAfter.HasValue ? $", rows {rowsBefore} → {rowsAfter}." : "."),
            data, warnings);
    }

    private static McpToolResult Edit(
        McpToolRequest request, Document doc, ViewSchedule schedule, ScheduleEditSpec edit, SchedulePlan plan, int rowsBefore,
        List<string> warnings, Stopwatch sw, CancellationToken cancellationToken)
    {
        ScheduleExecution? execution = null;
        string? abort = null;
        cancellationToken.ThrowIfCancellationRequested();
        var (committed, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Edit Schedule", () =>
        {
            execution = ScheduleBuilder.Apply(doc, schedule, plan, cancellationToken);
            var failed = execution.Failed.ToList();
            if (failed.Count > 0 && !edit.AllowMissingFields)
            {
                abort = $"Revit refused {failed.Count} step(s): " + string.Join("; ", failed.Select(f => $"{f.Description} — {f.Error}")) +
                        ". Nothing was changed. Fix them, or pass allowMissingFields=true to keep the rest.";
                throw new ScheduleAbortException(abort);
            }
            doc.Regenerate();
        });

        if (!committed)
        {
            if (abort != null)
                return ScheduleToolSupport.Fail(request, "validation_failed", abort, Shape(schedule, plan, execution, rowsBefore, null, null), warnings, sw);
            warnings.AddRange(diagnostics.ToErrorLines());
            return ScheduleToolSupport.Fail(request, "transaction_failed", "The schedule was not changed: the transaction did not commit.",
                new { transaction = diagnostics }, warnings, sw);
        }

        warnings.AddRange(diagnostics.FailureMessages);
        foreach (var step in execution!.Failed)
            warnings.Add($"Revit refused '{step.Description}': {step.Error} (kept the rest: allowMissingFields=true).");
        var orderAfter = ScheduleFieldCatalog.Current(schedule.Definition).Select(Display).ToList();
        int? rowsAfter;
        try { rowsAfter = ScheduleTableReader.Read(schedule).DataRows.Count; }
        catch { rowsAfter = null; }

        return ScheduleToolSupport.Ok(request, sw,
            $"Edited schedule '{schedule.Name}': {execution.Results.Count(r => r.Ok)} step(s); fields {plan.OrderBefore.Count} → {orderAfter.Count}" +
            (rowsAfter.HasValue ? $", rows {rowsBefore} → {rowsAfter}." : "."),
            Shape(schedule, plan, execution, rowsBefore, rowsAfter, orderAfter), warnings);
    }

    private static string Display(ExistingFieldInfo field)
    {
        var label = string.IsNullOrEmpty(field.Heading) ? field.Name : field.Heading;
        return field.Hidden ? label + " (hidden)" : label;
    }

    private static object Shape(ViewSchedule schedule, SchedulePlan plan, ScheduleExecution? execution, int rowsBefore, int? rowsAfter, List<string>? orderAfter) => new
    {
        scheduleId = schedule.Id.Value,
        name = schedule.Name,
        status = plan.Status,
        fieldsBefore = plan.OrderBefore,
        fieldsAfter = orderAfter ?? plan.OrderAfter,
        plannedFieldsAfter = plan.OrderAfter,
        plan = ScheduleToolSupport.DescribePlan(plan),
        execution = execution == null ? null : ScheduleToolSupport.DescribeExecution(execution),
        rowsBefore = rowsBefore >= 0 ? rowsBefore : (int?)null,
        rowsAfter
    };
}
