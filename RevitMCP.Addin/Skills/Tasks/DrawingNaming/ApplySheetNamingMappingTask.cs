using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Skills.Tasks.DrawingNaming;

/// <summary>
/// Task ID: naming.apply-mapping
/// Applies the naming mapping to every sheet: computes the expected target parameter value
/// from the configured tokens and writes it to the sheet if it differs from the current value.
/// Wraps all writes in a single Revit Transaction.
/// </summary>
internal sealed class ApplySheetNamingMappingTask : ISkillTask
{
    public string Id           => "naming.apply-mapping";
    public string Name         => "Rakenda lehtede nimetamine";
    public bool   ChangesModel => true;

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (target, tokens) = ValidateSheetNamingMappingTask.ReadSettings(taskDef.Settings);
            if (tokens.Count == 0)
                return Skip("Mapping tokens are empty — configure 'tokens' in the task settings.", sw.ElapsedMilliseconds);

            var sheets = new FilteredElementCollector(ctx.Document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate && !s.IsPlaceholder)
                .ToList();

            var paramNames = tokens.Where(t => t.Type.Equals("Parameter", StringComparison.OrdinalIgnoreCase))
                                   .Select(t => t.Value)
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .ToList();

            // Pre-compute all renames outside the transaction
            var renames = new List<(ViewSheet Sheet, string NewValue)>();
            var skipped = 0;

            foreach (var sheet in sheets)
            {
                var paramValues = ValidateSheetNamingMappingTask.ReadParamValues(sheet, paramNames);
                var expected    = NamingMappingEngine.BuildName(tokens, paramValues);
                var current     = ValidateSheetNamingMappingTask.GetTargetValue(sheet, target);

                if (string.IsNullOrEmpty(expected)) { skipped++; continue; }
                if (string.Equals(current, expected, StringComparison.Ordinal)) continue;

                renames.Add((sheet, expected));
            }

            if (renames.Count == 0)
            {
                return new SkillTaskResult
                {
                    TaskId = Id, TaskName = Name, Success = true, IssueCount = 0,
                    Message = $"Kõik {sheets.Count - skipped} lehe {target} on juba nimistuga kooskõlas." +
                              (skipped > 0 ? $" {skipped} lehte jäeti vahele (kõik parameetrid tühjad)." : ""),
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }

            // Apply renames in a transaction
            var warnings = new List<string>();
            var renamed  = 0;

            using (var tx = new Transaction(ctx.Document, "Revit MCP - Rakenda lehtede nimetamine"))
            {
                tx.Start();
                foreach (var (sheet, newValue) in renames)
                {
                    try
                    {
                        SetTargetValue(sheet, target, newValue, ctx.Document);
                        renamed++;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"[{sheet.SheetNumber}] Nimetamine ebaõnnestus: {ex.Message}");
                    }
                }
                tx.Commit();
            }

            var result = new SkillTaskResult
            {
                TaskId     = Id,
                TaskName   = Name,
                Success    = true,
                IssueCount = warnings.Count,
                Message    = $"Nimetatud {renamed}/{renames.Count} lehte." +
                             (skipped > 0 ? $" {skipped} vahele jäetud (tühjad parameetrid)." : ""),
                DurationMs = sw.ElapsedMilliseconds,
            };
            result.Warnings.AddRange(warnings);
            return result;
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId = Id, TaskName = Name, Success = false, CriticalFailure = true,
                Message = $"Nimetamine ebaõnnestus: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
    }

    private static void SetTargetValue(ViewSheet sheet, string target, string value, Document doc)
    {
        if (target.Equals("Sheet Number", StringComparison.OrdinalIgnoreCase))
        {
            sheet.SheetNumber = value;
            return;
        }
        if (target.Equals("Sheet Name", StringComparison.OrdinalIgnoreCase))
        {
            sheet.Name = value;
            return;
        }

        var p = sheet.LookupParameter(target);
        if (p is null || p.IsReadOnly || p.StorageType != StorageType.String)
            throw new InvalidOperationException($"Parameter '{target}' not found or is read-only on sheet '{sheet.SheetNumber}'.");
        p.Set(value);
    }

    private SkillTaskResult Skip(string message, long ms) => new()
    {
        TaskId = Id, TaskName = Name, Success = true, WasSkipped = true,
        Message = message, DurationMs = ms,
    };
}
