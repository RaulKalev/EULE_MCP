using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelState;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelState;

/// <summary>revit_set_view_phase (#109): sets a view's Phase and Phase Filter (VIEW_PHASE, VIEW_PHASE_FILTER).</summary>
public sealed class SetViewPhaseTool : IRevitMcpTool
{
    public const string ControlledByTemplate = "controlled_by_template";
    public const int MaxViews = 500;

    public string Name => "revit_set_view_phase";
    public string Description =>
        "Sets the Phase and/or Phase Filter of views. Requires approval. viewIds (max 500); phase as a phase name or id; " +
        "phaseFilter as a phase filter name or id, or none. A view whose template controls the parameter is reported as " +
        "controlled_by_template (change the template, or remove it from the view); views without a phase (sheets, " +
        "legends) are skipped. atomic=true (default): any blocked view rejects the whole request; atomic=false sets the " +
        "rest. One transaction, one undo. revit_list_phases lists the phases and phase filters.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    private sealed class Row
    {
        public long Id;
        public string Name = string.Empty;
        public string ViewType = string.Empty;
        public string? PhaseBefore, PhaseAfter, FilterBefore, FilterAfter;
        public string Status = "ready";
        public string? Reason;
        public bool SetPhase, SetFilter;
        public bool Blocking => Status is ControlledByTemplate or "read_only" or "not_found";
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds").Distinct().ToList();
        if (viewIds.Count == 0) return Task.FromResult(Fail(request, "Provide viewIds.", "validation_failed"));
        if (viewIds.Count > MaxViews) return Task.FromResult(Fail(request, $"{viewIds.Count} views given; the limit is {MaxViews}.", "validation_failed"));

        var phaseText = ToolArguments.GetString(request.Arguments, "phase");
        var filterText = ToolArguments.GetString(request.Arguments, "phaseFilter");
        if (string.IsNullOrWhiteSpace(phaseText) && string.IsNullOrWhiteSpace(filterText))
            return Task.FromResult(Fail(request, "Nothing to set: give phase, phaseFilter, or both.", "validation_failed"));

        var phases = PhaseReader.Phases(doc);
        PhaseInfo? phase = null;
        if (!string.IsNullOrWhiteSpace(phaseText))
        {
            phase = PhaseSetPlanner.ResolvePhase(phaseText, phases);
            if (phase == null)
            {
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = PhaseSetPlanner.PhaseNotFound,
                    Message = $"Phase '{phaseText.Trim()}' was not found. Phases: {PhaseSetPlanner.Available(phases)}.",
                    Data = new { availablePhases = phases.Select(p => new { id = p.Id, name = p.Name, sequenceNumber = p.Sequence }).ToList() }
                });
            }
        }

        var filters = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
            .Select(f => (Id: f.Id.Value, Name: f.Name ?? string.Empty)).ToList();
        var setFilter = !string.IsNullOrWhiteSpace(filterText);
        long? filterId = null;
        string? filterName = null;
        if (setFilter && !PhaseSetPlanner.IsNone(filterText))
        {
            var trimmed = filterText.Trim();
            var match = filters.FirstOrDefault(f => long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && f.Id == id);
            if (match.Name == null || match.Id == 0)
                match = filters.FirstOrDefault(f => string.Equals(f.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            if (match.Name == null || match.Id == 0)
            {
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "phase_filter_not_found",
                    Message = $"Phase filter '{trimmed}' was not found. Phase filters: {string.Join(", ", filters.Select(f => f.Name))}, or none.",
                    Data = new { availablePhaseFilters = filters.Select(f => new { id = f.Id, name = f.Name }).ToList() }
                });
            }
            filterId = match.Id;
            filterName = match.Name;
        }

        var atomic = ToolArguments.GetBool(request.Arguments, "atomic", true);
        var phaseParamId = new ElementId(BuiltInParameter.VIEW_PHASE);
        var filterParamId = new ElementId(BuiltInParameter.VIEW_PHASE_FILTER);
        var phaseNames = phases.ToDictionary(p => p.Id, p => p.Name);
        var filterNames = filters.ToDictionary(f => f.Id, f => f.Name);

        var rows = new List<Row>();
        foreach (var id in viewIds)
        {
            var row = new Row { Id = id };
            rows.Add(row);
            View? view = null;
            try { view = doc.GetElement(new ElementId(id)) as View; } catch { }
            if (view == null) { row.Status = "not_found"; row.Reason = "No view with this id."; continue; }
            row.Name = view.Name ?? string.Empty;
            row.ViewType = view.ViewType.ToString();

            var pPhase = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            var pFilter = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
            if ((phase != null && pPhase == null) || (setFilter && pFilter == null))
            {
                row.Status = "skipped";
                row.Reason = "This view has no phase settings (sheets, legends and some schedules do not).";
                continue;
            }
            row.PhaseBefore = NameOf(pPhase, phaseNames);
            row.FilterBefore = NameOf(pFilter, filterNames);
            row.PhaseAfter = phase != null ? phase.Name : row.PhaseBefore;
            row.FilterAfter = setFilter ? filterName : row.FilterBefore;
            row.SetPhase = phase != null && IdOf(pPhase) != phase.Id;
            row.SetFilter = setFilter && IdOf(pFilter) != filterId;

            var controlled = ControlledByTemplateIds(doc, view);
            if ((row.SetPhase && controlled.Contains(phaseParamId)) || (row.SetFilter && controlled.Contains(filterParamId)))
            {
                row.Status = ControlledByTemplate;
                row.Reason = $"The view template '{doc.GetElement(view.ViewTemplateId)?.Name}' controls this setting.";
                continue;
            }
            if ((row.SetPhase && pPhase!.IsReadOnly) || (row.SetFilter && pFilter!.IsReadOnly))
            {
                row.Status = "read_only";
                row.Reason = "Revit keeps this view's phase setting read-only.";
                continue;
            }
            if (!row.SetPhase && !row.SetFilter)
            {
                row.Status = "unchanged";
                row.Reason = "Already set.";
            }
        }

        var warnings = new List<string>();
        if (atomic && rows.Any(r => r.Blocking))
        {
            var blocked = rows.Where(r => r.Blocking).ToList();
            foreach (var r in blocked.Take(20)) warnings.Add($"View {r.Id}: {r.Status} — {r.Reason}");
            return Task.FromResult(Result(request, rows, warnings, sw, false,
                $"Nothing was changed: atomic=true and {blocked.Count} of {rows.Count} view(s) cannot be set. Fix or drop them, or pass atomic=false.",
                null, blocked.All(r => r.Status == ControlledByTemplate) ? ControlledByTemplate : "validation_failed"));
        }

        var ready = rows.Where(r => r.Status == "ready").ToList();
        if (ready.Count == 0)
            return Task.FromResult(Result(request, rows, warnings, sw, true, "Nothing to change: the views are already set, skipped or blocked.", null));

        cancellationToken.ThrowIfCancellationRequested();
        var (ok, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Set View Phase", () =>
        {
            foreach (var row in ready)
            {
                var view = (View)doc.GetElement(new ElementId(row.Id));
                if (atomic)
                {
                    Write(view, row, phase, filterId);
                    row.Status = "set";
                    continue;
                }
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    Write(view, row, phase, filterId);
                    sub.Commit();
                    row.Status = "set";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    row.Status = "failed";
                    row.Reason = ex.Message;
                }
            }
        });

        if (!ok)
        {
            foreach (var row in ready) row.Status = "rolled_back";
            warnings.AddRange(diagnostics.ToErrorLines());
            return Task.FromResult(Result(request, rows, warnings, sw, false,
                "Rolled back: Revit refused a view phase change, so nothing was changed.", diagnostics));
        }

        warnings.AddRange(diagnostics.FailureMessages);
        var set = rows.Count(r => r.Status == "set");
        return Task.FromResult(Result(request, rows, warnings, sw, set > 0,
            $"Set the phase settings of {set} of {rows.Count} view(s). One undo step.", diagnostics));
    }

    private static void Write(View view, Row row, PhaseInfo? phase, long? filterId)
    {
        if (row.SetPhase && phase != null &&
            !view.get_Parameter(BuiltInParameter.VIEW_PHASE).Set(new ElementId(phase.Id)))
            throw new InvalidOperationException("Revit refused the phase.");
        if (row.SetFilter &&
            !view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER).Set(filterId != null ? new ElementId(filterId.Value) : ElementId.InvalidElementId))
            throw new InvalidOperationException("Revit refused the phase filter.");
    }

    /// <summary>Parameters the view's template controls (empty without a template).</summary>
    private static HashSet<ElementId> ControlledByTemplateIds(Document doc, View view)
    {
        var result = new HashSet<ElementId>();
        try
        {
            if (view.IsTemplate || view.ViewTemplateId == ElementId.InvalidElementId) return result;
            if (doc.GetElement(view.ViewTemplateId) is not View template) return result;
            var notControlled = new HashSet<ElementId>(template.GetNonControlledTemplateParameterIds());
            foreach (var id in template.GetTemplateParameterIds())
                if (!notControlled.Contains(id)) result.Add(id);
        }
        catch { }
        return result;
    }

    private static long? IdOf(Parameter? p)
    {
        try
        {
            var id = p?.AsElementId();
            return id == null || id == ElementId.InvalidElementId ? null : id.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string? NameOf(Parameter? p, Dictionary<long, string> names)
    {
        var id = IdOf(p);
        return id == null ? null : names.TryGetValue(id.Value, out var name) ? name : id.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static McpToolResult Result(McpToolRequest request, List<Row> rows, List<string> warnings, Stopwatch sw, bool success,
        string message, TransactionDiagnostics? diagnostics, string? status = null)
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
                set = rows.Count(r => r.Status == "set"),
                views = rows.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    viewType = r.ViewType,
                    phase = new { before = r.PhaseBefore, after = r.PhaseAfter },
                    phaseFilter = new { before = r.FilterBefore, after = r.FilterAfter },
                    status = r.Status,
                    reason = r.Reason
                }).ToList(),
                transaction = diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult Fail(McpToolRequest request, string message, string status) =>
        new() { RequestId = request.RequestId, Success = false, Status = status, Message = message };
}
