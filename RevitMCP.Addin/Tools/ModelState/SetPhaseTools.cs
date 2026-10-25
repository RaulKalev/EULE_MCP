using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelState;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelState;

/// <summary>revit_preview_set_phase (#109): what revit_set_phase would change, without changing the model.</summary>
public sealed class PreviewSetPhaseTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_phase";
    public string Description =>
        "Previews setting Phase Created and/or Phase Demolished on elements, without changing the model. Same arguments as " +
        "revit_set_phase: elementIds (max 2000) or useSelection=true; phaseCreated and/or phaseDemolished as a phase name or " +
        "id (phaseDemolished=none clears it); atomic. Returns per element {id, category, name, phaseCreated {before, after}, " +
        "phaseDemolished {before, after}, status, reason}: ready, unchanged, skipped (no phase parameters), read_only, " +
        "phase_order_invalid (demolished before created) or not_found. Unknown phases fail with phase_not_found and the list.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SetPhaseExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_set_phase (#109): sets PHASE_CREATED / PHASE_DEMOLISHED on elements in one transaction.</summary>
public sealed class SetPhaseTool : IRevitMcpTool
{
    public string Name => "revit_set_phase";
    public string Description =>
        "Sets Phase Created and/or Phase Demolished on elements. Requires approval. elementIds (max 2000) or useSelection=true; " +
        "phaseCreated and/or phaseDemolished as a phase name or id; phaseDemolished=none clears it (not demolished). " +
        "Validates the order (demolished may not come before created; the same phase is Revit's Temporary). Elements " +
        "without phase parameters (annotations, views) are skipped with a reason; read-only parameters (group members, " +
        "elements that follow their host) are reported. atomic=true (default): any read-only, out-of-order or missing " +
        "element rejects the whole request before anything changes; atomic=false: writes element by element and keeps " +
        "what succeeds. One transaction, one undo. Run revit_preview_set_phase first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SetPhaseExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class SetPhaseExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        if (useSelection && uidoc == null)
            return ToolDocument.ActiveRequired(request, "useSelection", doc.Title);

        var ids = (useSelection
                ? uidoc!.Selection.GetElementIds().Select(id => id.Value)
                : ToolArguments.GetLongArray(request.Arguments, "elementIds"))
            .Distinct().ToList();
        if (ids.Count == 0)
            return Fail(request, useSelection ? "The selection is empty." : "Provide elementIds or useSelection=true.", "validation_failed");
        if (ids.Count > PhaseSetPlanner.MaxElements)
            return Fail(request, $"{ids.Count} elements given; the limit is {PhaseSetPlanner.MaxElements} per request.", "validation_failed");

        var phases = PhaseReader.Phases(doc);
        if (!PhaseSetPlanner.TryParseTargets(
                ToolArguments.GetString(request.Arguments, "phaseCreated"),
                ToolArguments.GetString(request.Arguments, "phaseDemolished"),
                phases, out var created, out var demolished, out var status, out var error))
        {
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = status,
                Message = error!,
                Data = new { availablePhases = phases.Select(p => new { id = p.Id, name = p.Name, sequenceNumber = p.Sequence }).ToList() }
            };
        }

        var atomic = ToolArguments.GetBool(request.Arguments, "atomic", true);
        var byId = phases.ToDictionary(p => p.Id);
        var states = ids.Select(id => ReadState(doc, id, byId)).ToList();
        var rows = PhaseSetPlanner.Plan(states, created, demolished);
        var warnings = new List<string>();

        return apply
            ? Apply(request, doc, rows, atomic, created, demolished, warnings, sw, cancellationToken)
            : Result(request, rows, atomic, created, demolished, warnings, sw, success: true,
                $"Preview: {rows.Count(r => r.WillChange)} of {rows.Count} element(s) would change" +
                Tail(rows) + (PhaseSetPlanner.ShouldReject(atomic, rows) ? ". With atomic=true the request would be rejected as a whole." : "."),
                null);
    }

    private static McpToolResult Apply(
        McpToolRequest request, Document doc, List<PhaseSetRow> rows, bool atomic, PhaseTarget created, PhaseTarget demolished,
        List<string> warnings, Stopwatch sw, CancellationToken cancellationToken)
    {
        if (PhaseSetPlanner.ShouldReject(atomic, rows))
        {
            var blocking = rows.Where(r => r.IsBlocking).ToList();
            foreach (var row in blocking.Take(20)) warnings.Add($"Element {row.Id}: {row.Status} — {row.Reason}");
            var status = blocking.All(r => r.Status == PhaseSetPlanner.StatusOrderInvalid) ? PhaseSetPlanner.StatusOrderInvalid : "validation_failed";
            return Result(request, rows, atomic, created, demolished, warnings, sw, success: false,
                $"Nothing was changed: atomic=true and {blocking.Count} of {rows.Count} element(s) cannot be set. " +
                "Fix or drop them, or pass atomic=false to set the rest.", null, status);
        }

        var ready = rows.Where(r => r.WillChange).ToList();
        if (ready.Count == 0)
        {
            return Result(request, rows, atomic, created, demolished, warnings, sw, success: true,
                "Nothing to change: every element is already in the requested phases, skipped or blocked" + Tail(rows) + ".", null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (ok, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Set Phase", () =>
        {
            foreach (var row in ready)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (atomic)
                {
                    Write(doc, row);
                    row.Status = PhaseSetPlanner.StatusSet;
                    continue;
                }

                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    Write(doc, row);
                    sub.Commit();
                    row.Status = PhaseSetPlanner.StatusSet;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    row.Status = PhaseSetPlanner.StatusFailed;
                    row.Reason = ex.Message;
                }
            }
        });

        if (!ok)
        {
            foreach (var row in ready.Where(r => r.Status == PhaseSetPlanner.StatusSet || r.Status == PhaseSetPlanner.StatusReady))
                row.Status = PhaseSetPlanner.StatusRolledBack;
            warnings.AddRange(diagnostics.ToErrorLines());
            return Result(request, rows, atomic, created, demolished, warnings, sw, success: false,
                "Rolled back: Revit refused a phase change, so nothing was changed. The model is unchanged.", diagnostics);
        }

        warnings.AddRange(diagnostics.FailureMessages);
        foreach (var row in rows.Where(r => r.Status == PhaseSetPlanner.StatusFailed))
            warnings.Add($"Element {row.Id} could not be set: {row.Reason}");
        var set = rows.Count(r => r.Status == PhaseSetPlanner.StatusSet);
        return Result(request, rows, atomic, created, demolished, warnings, sw, success: set > 0,
            $"Set the phases of {set} of {rows.Count} element(s)" + Tail(rows) + ". One undo step.", diagnostics);
    }

    /// <summary>Writes one row; throws when Revit refuses, so an atomic run rolls back as a whole.</summary>
    private static void Write(Document doc, PhaseSetRow row)
    {
        var element = doc.GetElement(new ElementId(row.Id)) ?? throw new InvalidOperationException("The element no longer exists.");
        void SetCreated()
        {
            if (!row.SetCreated || row.NewCreatedId == null) return;
            var p = element.get_Parameter(BuiltInParameter.PHASE_CREATED) ?? throw new InvalidOperationException("Phase Created is missing.");
            if (!p.Set(new ElementId(row.NewCreatedId.Value))) throw new InvalidOperationException("Revit refused the Phase Created value.");
        }
        void SetDemolished()
        {
            if (!row.SetDemolished) return;
            var p = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED) ?? throw new InvalidOperationException("Phase Demolished is missing.");
            var value = row.NewDemolishedId != null ? new ElementId(row.NewDemolishedId.Value) : ElementId.InvalidElementId;
            if (!p.Set(value)) throw new InvalidOperationException("Revit refused the Phase Demolished value.");
        }

        if (row.DemolishedFirst) { SetDemolished(); SetCreated(); }
        else { SetCreated(); SetDemolished(); }
    }

    private static PhaseElementState ReadState(Document doc, long id, Dictionary<long, PhaseInfo> phases)
    {
        Element? element;
        try { element = doc.GetElement(new ElementId(id)); } catch { element = null; }
        if (element == null) return new PhaseElementState { Id = id, Exists = false };

        var state = new PhaseElementState
        {
            Id = id,
            Category = SafeString(() => element.Category?.Name),
            Name = SafeString(() => element.Name)
        };
        if (element is ElementType || element is View) return state;

        Parameter? pc = null, pd = null;
        try
        {
            pc = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
            pd = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
        }
        catch { }
        if (pc == null || pd == null || pc.StorageType != StorageType.ElementId || pd.StorageType != StorageType.ElementId) return state;

        state.HasPhaseParameters = true;
        state.CreatedReadOnly = pc.IsReadOnly;
        state.DemolishedReadOnly = pd.IsReadOnly;
        state.Created = Lookup(pc, phases);
        state.Demolished = Lookup(pd, phases);
        if (state.CreatedReadOnly || state.DemolishedReadOnly)
        {
            ElementId? groupId = null;
            try { groupId = element.GroupId; } catch { }
            state.ReadOnlyReason = groupId != null && groupId != ElementId.InvalidElementId
                ? $"The element is a member of group {groupId.Value}: change the phase of the group instead."
                : "Revit keeps this element's phase read-only (it follows its host or another element).";
        }
        return state;
    }

    private static PhaseInfo? Lookup(Parameter p, Dictionary<long, PhaseInfo> phases)
    {
        try
        {
            var id = p.AsElementId();
            return id != null && id != ElementId.InvalidElementId && phases.TryGetValue(id.Value, out var phase) ? phase : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Tail(List<PhaseSetRow> rows)
    {
        var parts = new List<string>();
        void Add(string status, string label)
        {
            var n = rows.Count(r => r.Status == status);
            if (n > 0) parts.Add($"{n} {label}");
        }
        Add(PhaseSetPlanner.StatusUnchanged, "unchanged");
        Add(PhaseSetPlanner.StatusSkipped, "skipped");
        Add(PhaseSetPlanner.StatusReadOnly, "read-only");
        Add(PhaseSetPlanner.StatusOrderInvalid, "out of phase order");
        Add(PhaseSetPlanner.StatusNotFound, "not found");
        Add(PhaseSetPlanner.StatusFailed, "failed");
        return parts.Count == 0 ? string.Empty : " (" + string.Join(", ", parts) + ")";
    }

    private static McpToolResult Result(
        McpToolRequest request, List<PhaseSetRow> rows, bool atomic, PhaseTarget created, PhaseTarget demolished,
        List<string> warnings, Stopwatch sw, bool success, string message, TransactionDiagnostics? diagnostics, string? status = null)
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
                total = rows.Count,
                willChange = rows.Count(r => r.WillChange),
                set = rows.Count(r => r.Status == PhaseSetPlanner.StatusSet),
                blocked = rows.Count(r => r.IsBlocking),
                atomic,
                phaseCreated = created.Kind == PhaseTargetKind.Set ? created.Phase?.Name : null,
                phaseDemolished = demolished.Kind switch
                {
                    PhaseTargetKind.Set => demolished.Phase?.Name,
                    PhaseTargetKind.Clear => PhaseSetPlanner.NoneKeyword,
                    _ => null
                },
                elements = rows.Select(r => new
                {
                    id = r.Id,
                    category = r.Category,
                    name = r.Name,
                    phaseCreated = new { before = r.CreatedBefore, after = r.CreatedAfter },
                    phaseDemolished = new { before = r.DemolishedBefore, after = r.DemolishedAfter },
                    status = r.Status,
                    reason = r.Reason
                }).ToList(),
                transaction = diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static string SafeString(Func<string?> read)
    {
        try { return read() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static McpToolResult Fail(McpToolRequest request, string message, string status) =>
        new() { RequestId = request.RequestId, Success = false, Status = status, Message = message };
}
