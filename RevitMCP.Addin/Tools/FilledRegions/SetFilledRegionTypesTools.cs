using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.FilledRegions;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FilledRegions;

/// <summary>Fill pattern lookups for the type tools.</summary>
internal static class FilledRegionPatterns
{
    public static object? Describe(Document doc, ElementId id)
    {
        if (id == null || id == ElementId.InvalidElementId || doc.GetElement(id) is not FillPatternElement element) return null;
        var pattern = element.GetFillPattern();
        return new { id = id.Value, name = element.Name, target = pattern.Target.ToString(), isSolid = pattern.IsSolidFill };
    }

    public static string? Hex(Color? color) => color == null || !color.IsValid ? null : $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    public static Color ToRevit(RegionColor color) => new(color.R, color.G, color.B);

    /// <summary>
    /// "none" or empty is no pattern, "solid" the solid fill, otherwise a name or id. A name prefers the drafting pattern;
    /// a background only takes drafting patterns. Null with a reason when it does not resolve.
    /// </summary>
    public static ElementId? Resolve(Document doc, string reference, bool background, out string? code, out string? reason)
    {
        code = null;
        reason = null;
        var text = reference.Trim();
        if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return ElementId.InvalidElementId;

        var patterns = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();
        FillPatternElement? match;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            match = patterns.FirstOrDefault(p => p.Id.Value == id);
        }
        else if (text.Equals("solid", StringComparison.OrdinalIgnoreCase))
        {
            match = patterns.FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
        }
        else
        {
            var named = patterns.Where(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            match = named.FirstOrDefault(p => p.GetFillPattern().Target == FillPatternTarget.Drafting) ?? named.FirstOrDefault();
        }

        if (match == null)
        {
            code = RegionCodes.PatternNotFound;
            reason = $"No fill pattern '{text}' (see revit_list_filled_region_types includePatterns=true).";
            return null;
        }
        if (background && match.GetFillPattern().Target != FillPatternTarget.Drafting)
        {
            code = RegionCodes.PatternNotValid;
            reason = $"Fill pattern '{match.Name}' is a model pattern; a background pattern must be a drafting pattern.";
            return null;
        }
        return match.Id;
    }

    public static string Name(Document doc, ElementId id) =>
        id == ElementId.InvalidElementId ? "none" : doc.GetElement(id)?.Name ?? id.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>revit_preview_set_filled_region_types (#135): what a type change is, and every region it reaches.</summary>
public sealed class PreviewSetFilledRegionTypesTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_filled_region_types";

    public string Description =>
        "Previews revit_set_filled_region_types without changing the model. Same arguments. Per type: from/to per field, a status " +
        "code (type_not_found, type_name_exists, pattern_not_found, pattern_not_valid, line_weight_not_valid, masking_not_valid, " +
        "...) and affectedRegions: every region of the type, by view. dryRun=true (default) also tries the changes in a rolled-back transaction.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SetFilledRegionTypesExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_set_filled_region_types (#135): create types by duplicating, edit patterns, colours, line weight and masking.</summary>
public sealed class SetFilledRegionTypesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_set_filled_region_types";

    public string Description =>
        "Creates and edits filled region types (max 100). Requires approval. Each spec: type (edit it, name renames it) or duplicateFrom " +
        "+ name (create), and any of foregroundPattern, backgroundPattern (name, id, \"solid\" or \"none\"), foregroundColor, " +
        "backgroundColor (\"#RRGGBB\"), lineWeight (1-16), isMasking. A type edit changes every region of the type. One transaction, " +
        "one undo. Run revit_preview_set_filled_region_types first: it lists the regions affected.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SetFilledRegionTypesExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var parsed = FilledRegionEditParser.ParseTypes(request.Arguments, out _);
        if (parsed == null) return null;

        var affected = 0;
        var byType = SetFilledRegionTypesExecutor.RegionsByType(target.Document);
        foreach (var spec in parsed.Types.Where(t => !t.IsCreate && !string.IsNullOrWhiteSpace(t.Type)))
            if (FilledRegionSupport.ResolveType(target.Document, spec.Type!) is { } type && byType.TryGetValue(type.Id.Value, out var regions))
                affected += regions.Count;
        return FilledRegionSummaries.Types(parsed) + (affected > 0 ? $" {affected} existing region(s) are affected." : string.Empty);
    }
}

internal sealed class ResolvedTypeSpec
{
    public RegionTypeSpec Spec { get; init; } = null!;
    public FilledRegionType? Source { get; set; }
    public ElementId? ForegroundId { get; set; }
    public ElementId? BackgroundId { get; set; }
    public List<object> Changes { get; } = new();
    public List<FilledRegion> Affected { get; set; } = new();
    public string Status { get; set; } = RegionStatus.WillEdit;
    public string? Code { get; set; }
    public string? Reason { get; set; }
    public long? ResultId { get; set; }
    public bool IsReady => Status is RegionStatus.WillEdit or RegionStatus.WillCreate or RegionStatus.Edited or RegionStatus.Created;

    public void Refuse(string code, string reason)
    {
        Status = RegionStatus.Invalid;
        Code = code;
        Reason = reason;
    }
}

internal static class SetFilledRegionTypesExecutor
{
    public static Dictionary<long, List<FilledRegion>> RegionsByType(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>()
            .GroupBy(r => r.GetTypeId().Value).ToDictionary(g => g.Key, g => g.ToList());

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var parsed = FilledRegionEditParser.ParseTypes(request.Arguments, out var parseError);
        if (parsed == null)
            return new McpToolResult { RequestId = request.RequestId, Success = false, Status = RegionCodes.InvalidSpec, Message = parseError! };

        var names = new HashSet<string>(FilledRegionSupport.Types(doc).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var byType = RegionsByType(doc);
        var specs = parsed.Types.Select(spec => Resolve(doc, spec, names, byType)).ToList();

        return apply
            ? Apply(request, doc, parsed, specs, sw, cancellationToken)
            : Preview(request, doc, parsed, specs, sw, cancellationToken);
    }

    private static ResolvedTypeSpec Resolve(Document doc, RegionTypeSpec spec, HashSet<string> names, Dictionary<long, List<FilledRegion>> byType)
    {
        var resolved = new ResolvedTypeSpec { Spec = spec, Status = spec.IsCreate ? RegionStatus.WillCreate : RegionStatus.WillEdit };
        if (spec.Errors.Count > 0)
        {
            resolved.Refuse(RegionCodes.InvalidSpec, string.Join(" ", spec.Errors));
            return resolved;
        }

        resolved.Source = FilledRegionSupport.ResolveType(doc, spec.IsCreate ? spec.DuplicateFrom! : spec.Type!);
        if (resolved.Source == null)
        {
            resolved.Refuse(RegionCodes.TypeNotFound, $"No filled region type '{(spec.IsCreate ? spec.DuplicateFrom : spec.Type)}'. Types: {FilledRegionSupport.TypeNames(doc)}.");
            return resolved;
        }
        var source = resolved.Source;

        if (!string.IsNullOrWhiteSpace(spec.Name))
        {
            var renamingToItself = !spec.IsCreate && string.Equals(source.Name, spec.Name, StringComparison.OrdinalIgnoreCase);
            if (!renamingToItself && !names.Add(spec.Name!))
            {
                resolved.Refuse(RegionCodes.TypeNameExists, $"A filled region type named '{spec.Name}' already exists.");
                return resolved;
            }
            if (!spec.IsCreate && source.Name != spec.Name) AddChange(resolved, "name", source.Name, spec.Name);
        }

        if (spec.ForegroundPattern != null)
        {
            resolved.ForegroundId = FilledRegionPatterns.Resolve(doc, spec.ForegroundPattern, background: false, out var code, out var reason);
            if (resolved.ForegroundId == null)
            {
                resolved.Refuse(code!, reason!);
                return resolved;
            }
            if (!source.IsValidFillPatternId(resolved.ForegroundId) || !source.IsValidForegroundPatternId(resolved.ForegroundId))
            {
                resolved.Refuse(RegionCodes.PatternNotValid, $"Revit does not take '{spec.ForegroundPattern}' as a foreground pattern here.");
                return resolved;
            }
            AddChange(resolved, "foregroundPattern", FilledRegionPatterns.Name(doc, source.ForegroundPatternId), FilledRegionPatterns.Name(doc, resolved.ForegroundId));
        }

        if (spec.BackgroundPattern != null)
        {
            resolved.BackgroundId = FilledRegionPatterns.Resolve(doc, spec.BackgroundPattern, background: true, out var code, out var reason);
            if (resolved.BackgroundId == null)
            {
                resolved.Refuse(code!, reason!);
                return resolved;
            }
            if (!source.IsValidFillPatternId(resolved.BackgroundId) || !source.IsValidBackgroundPatternId(resolved.BackgroundId))
            {
                resolved.Refuse(RegionCodes.PatternNotValid, $"Revit does not take '{spec.BackgroundPattern}' as a background pattern here.");
                return resolved;
            }
            AddChange(resolved, "backgroundPattern", FilledRegionPatterns.Name(doc, source.BackgroundPatternId), FilledRegionPatterns.Name(doc, resolved.BackgroundId));
        }

        if (spec.ForegroundColor is { } foreground) AddChange(resolved, "foregroundColor", FilledRegionPatterns.Hex(source.ForegroundPatternColor), foreground.ToHex());
        if (spec.BackgroundColor is { } background) AddChange(resolved, "backgroundColor", FilledRegionPatterns.Hex(source.BackgroundPatternColor), background.ToHex());

        if (spec.LineWeight is { } weight)
        {
            if (!FilledRegionType.IsValidLineWeight(weight))
            {
                resolved.Refuse(RegionCodes.LineWeightNotValid, $"Revit refuses line weight {weight}.");
                return resolved;
            }
            AddChange(resolved, "lineWeight", source.LineWeight, weight);
        }

        if (spec.IsMasking is { } masking)
        {
            if (!source.IsValidMasking(masking))
            {
                resolved.Refuse(RegionCodes.MaskingNotValid, $"Revit refuses isMasking={masking} for this type.");
                return resolved;
            }
            AddChange(resolved, "isMasking", source.IsMasking, masking);
        }

        if (!spec.IsCreate && byType.TryGetValue(source.Id.Value, out var regions)) resolved.Affected = regions;
        return resolved;
    }

    private static void AddChange(ResolvedTypeSpec resolved, string field, object? from, object? to) =>
        resolved.Changes.Add(new { field, from, to });

    // ── Preview and apply ────────────────────────────────────────────────────

    private static McpToolResult Preview(
        McpToolRequest request, Document doc, RegionTypeRequest parsed, List<ResolvedTypeSpec> specs, Stopwatch sw, CancellationToken cancellationToken)
    {
        var described = specs.Select(s => Describe(doc, s, applied: false)).ToList();
        var dryRun = ToolArguments.GetBool(request.Arguments, "dryRun", true);
        string? trialNote = null;
        var trialRan = false;

        if (!dryRun) trialNote = "dryRun=false: only the static checks ran; Revit was not asked to try the changes.";
        else if (!specs.Any(s => s.IsReady)) trialNote = "No trial run: no type spec is ready.";
        else if (doc.IsReadOnly || doc.IsModifiable) trialNote = "No trial run: the document is read-only or has an open transaction. Only the static checks ran.";
        else
        {
            using var transaction = new Transaction(doc, "Revit MCP - Filled Region Types (preview, rolled back)");
            try
            {
                RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
                if (transaction.Start() == TransactionStatus.Started)
                {
                    trialRan = true;
                    RunAll(doc, specs, atomic: false, cancellationToken);
                }
                else trialNote = "No trial run: Revit did not start a transaction. Only the static checks ran.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                trialNote = $"The trial run could not complete ({ex.GetType().Name}: {ex.Message}). Only the static checks are reliable.";
            }
            finally
            {
                if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            }
            trialNote ??= "Trial run rolled back: the model is unchanged.";
        }

        var refused = specs.Where(s => s.Status == RegionStatus.Failed).Select(s => new { index = s.Spec.Index, code = s.Code, reason = s.Reason }).ToList();
        var ready = specs.Count(s => s.IsReady);
        var invalid = specs.Count(s => s.Status == RegionStatus.Invalid);
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Status = ready == 0 ? specs.Select(s => s.Code).FirstOrDefault(c => c != null) : null,
            Message = $"Preview: {ready} of {specs.Count} type spec(s) would apply" +
                      (invalid > 0 ? $", {invalid} invalid" : string.Empty) +
                      (refused.Count > 0 ? $", {refused.Count} refused by Revit in the trial run" : string.Empty) + ".",
            Data = new
            {
                total = specs.Count,
                ready,
                invalid,
                trialRun = trialRan,
                trialNote,
                trialRefused = refused.Count > 0 ? refused : null,
                summary = FilledRegionSummaries.Types(parsed),
                affectedRegionTotal = specs.Where(s => s.IsReady).Sum(s => s.Affected.Count),
                types = described
            },
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult Apply(
        McpToolRequest request, Document doc, RegionTypeRequest parsed, List<ResolvedTypeSpec> specs, Stopwatch sw, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var invalid = specs.Count(s => s.Status == RegionStatus.Invalid);
        if (parsed.Atomic && invalid > 0)
        {
            foreach (var spec in specs.Where(s => s.IsReady)) spec.Status = RegionStatus.NotAttempted;
            return Result(request, doc, specs, warnings, sw, false, specs.Select(s => s.Code).FirstOrDefault(c => c != null),
                $"Nothing was changed: atomic=true and {invalid} of {specs.Count} type spec(s) are invalid.", null);
        }
        if (!specs.Any(s => s.IsReady))
            return Result(request, doc, specs, warnings, sw, false, specs.Select(s => s.Code).FirstOrDefault(c => c != null), "Nothing was changed: no type spec is valid.", null);

        cancellationToken.ThrowIfCancellationRequested();
        var (committed, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Filled Region Types", () =>
            RunAll(doc, specs, parsed.Atomic, cancellationToken));

        if (!committed)
        {
            foreach (var spec in specs.Where(s => s.IsReady))
            {
                spec.ResultId = null;
                spec.Status = RegionStatus.RolledBack;
            }
            warnings.AddRange(diagnostics.ToErrorLines());
            return Result(request, doc, specs, warnings, sw, false, RegionCodes.RevitRefused, "The transaction did not commit, so nothing was changed.", diagnostics);
        }

        warnings.AddRange(diagnostics.FailureMessages);
        var created = specs.Count(s => s.Status == RegionStatus.Created);
        var edited = specs.Count(s => s.Status == RegionStatus.Edited);
        var failed = specs.Count(s => s.Status == RegionStatus.Failed);
        return Result(request, doc, specs, warnings, sw, created + edited > 0, created + edited > 0 ? null : specs.Select(s => s.Code).FirstOrDefault(c => c != null),
            $"Created {created} and edited {edited} filled region type(s)" +
            (failed > 0 ? $"; Revit refused {failed}" : string.Empty) +
            (invalid > 0 ? $"; {invalid} invalid" : string.Empty) + ". One Undo reverts it.", diagnostics);
    }

    private static void RunAll(Document doc, List<ResolvedTypeSpec> specs, bool atomic, CancellationToken cancellationToken)
    {
        foreach (var spec in specs.Where(s => s.IsReady))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var type = spec.Spec.IsCreate ? (FilledRegionType)spec.Source!.Duplicate(spec.Spec.Name!) : spec.Source!;
                if (!spec.Spec.IsCreate && !string.IsNullOrWhiteSpace(spec.Spec.Name) && type.Name != spec.Spec.Name) type.Name = spec.Spec.Name!;
                if (spec.ForegroundId != null) type.ForegroundPatternId = spec.ForegroundId;
                if (spec.BackgroundId != null) type.BackgroundPatternId = spec.BackgroundId;
                if (spec.Spec.ForegroundColor is { } foreground) type.ForegroundPatternColor = FilledRegionPatterns.ToRevit(foreground);
                if (spec.Spec.BackgroundColor is { } background) type.BackgroundPatternColor = FilledRegionPatterns.ToRevit(background);
                if (spec.Spec.LineWeight is { } weight) type.LineWeight = weight;
                if (spec.Spec.IsMasking is { } masking) type.IsMasking = masking;
                sub.Commit();
                spec.ResultId = type.Id.Value;
                spec.Status = spec.Spec.IsCreate ? RegionStatus.Created : RegionStatus.Edited;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                spec.Status = RegionStatus.Failed;
                spec.Code = RegionCodes.RevitRefused;
                spec.Reason = ex.Message;
                spec.ResultId = null;
                if (atomic) throw new TypeBatchAbortedException($"atomic=true: type spec {spec.Spec.Index} was refused ({ex.Message}), so the whole batch was undone.");
            }
        }
    }

    private static McpToolResult Result(
        McpToolRequest request, Document doc, List<ResolvedTypeSpec> specs, List<string> warnings, Stopwatch sw,
        bool success, string? status, string message, TransactionDiagnostics? diagnostics)
    {
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = success,
            Status = status,
            Message = message,
            Data = new
            {
                total = specs.Count,
                created = specs.Count(s => s.Status == RegionStatus.Created),
                edited = specs.Count(s => s.Status == RegionStatus.Edited),
                failed = specs.Count(s => s.Status == RegionStatus.Failed),
                invalid = specs.Count(s => s.Status == RegionStatus.Invalid),
                newTypeIds = specs.Where(s => s.Status == RegionStatus.Created).Select(s => s.ResultId).ToList(),
                types = specs.Select(s => Describe(doc, s, applied: true)).ToList(),
                transaction = diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Describe(Document doc, ResolvedTypeSpec spec, bool applied)
    {
        var views = spec.Affected.GroupBy(r => r.OwnerViewId.Value).Select(g => new
        {
            viewId = g.Key,
            viewName = doc.GetElement(new ElementId(g.Key))?.Name,
            regionIds = g.Select(r => r.Id.Value).ToList()
        }).ToList();
        return new
        {
            index = spec.Spec.Index,
            mode = spec.Spec.IsCreate ? "create" : "edit",
            status = spec.Status,
            code = spec.Code,
            reason = spec.Reason,
            type = spec.Source == null ? null : new { id = spec.Source.Id.Value, name = spec.Source.Name },
            name = spec.Spec.Name,
            typeId = applied ? spec.ResultId : null,
            changes = spec.Changes,
            affectedRegions = new { count = spec.Affected.Count, byView = views }
        };
    }

    /// <summary>Thrown to abandon an atomic batch: escaping the transaction body is what rolls it back.</summary>
    private sealed class TypeBatchAbortedException : Exception
    {
        public TypeBatchAbortedException(string message) : base(message)
        {
        }
    }
}
