using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Editing;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Editing;

/// <summary>revit_preview_change_element_type (#159): which instances can switch to a type, without changing anything.</summary>
public sealed class PreviewChangeElementTypeTool : IRevitMcpTool
{
    public const string ToolName = "revit_preview_change_element_type";
    public string Name => ToolName;

    public string Description =>
        "Previews changing the type of existing instances, without changing the model. Same arguments as " +
        "revit_change_element_type. Reports each element as switchable, unchanged (already that type) or blocked with a " +
        "reason (notFound, isElementType, linkedDocument, noType, inGroup, pinned, notEditable, categoryMismatch, " +
        "invalidForElement), plus the target type and counts with up to sampleSize examples.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ChangeElementTypeExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_change_element_type (#159): Element.ChangeTypeId over a batch, with old/new id mappings.</summary>
public sealed class ChangeElementTypeTool : IRevitMcpTool, IApprovalSummaryTool
{
    public const string ToolName = "revit_change_element_type";
    public string Name => ToolName;

    public string Description =>
        "Changes the type of existing instances (Element.ChangeTypeId) without deleting and recreating them. Requires " +
        "approval. Elements: elementIds and/or useSelection. Target: typeId, or typeName (+ familyName, categoryName when " +
        "several types share the name; an ambiguous name is refused, never guessed). The target must be in the same " +
        "category and accepted by Revit for each element; elements in a group, pinned, in a linked model or owned by " +
        "someone else are blocked. requireAll=true writes nothing when any element is blocked or fails (all or nothing); " +
        "the default changes what can change and reports the rest. Revit may return a new element id for a switch; every " +
        "old id to new id mapping is reported (oldId, newId, oldTypeId, newTypeId, status changed | replaced | failed). " +
        "Instance parameter values are kept where the new type has the same parameter; tags, dimensions and constraints " +
        "that point at a replaced element should be rechecked. One transaction, one undo step. Run " +
        "revit_preview_change_element_type first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ChangeElementTypeExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        try
        {
            return ChangeElementTypeExecutor.ApprovalSummary(uiapp, request);
        }
        catch (Exception ex)
        {
            Swallowed.Note(ex);
            return null;
        }
    }
}

internal static class ChangeElementTypeExecutor
{
    private sealed class Plan
    {
        public Document Doc = null!;
        public ElementType Target = null!;
        public TypeCandidate TargetInfo = null!;
        public List<TypeChangeDecision> Decisions = new();
        public Dictionary<long, long> OldTypes = new();
        public Dictionary<long, string> OldTypeNames = new();
        public bool RequireAll;
        public bool NeedsActivation => Target is FamilySymbol fs && !fs.IsActive;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var args = request.Arguments;
        if (ToolArguments.GetBool(args, "useSelection") && ToolDocument.UiDocumentOf(uiapp, target) == null)
            return ToolDocument.ActiveRequired(request, "useSelection", target.Document.Title);

        var plan = BuildPlan(uiapp, target, request, cancellationToken, out var errorResult);
        if (plan == null) return errorResult!;
        var sample = ChangeElementTypePlanning.ClampSample(ToolArguments.GetInt(args, "sampleSize", ChangeElementTypePlanning.DefaultSampleSize));
        var (switchable, unchanged, blocked) = ChangeElementTypePlanning.Count(plan.Decisions);

        var warnings = new List<string>();
        if (plan.NeedsActivation) warnings.Add("The target type is not active in the project; it is activated when the change is applied.");

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preview: {switchable} of {plan.Decisions.Count} element(s) can switch to '{plan.TargetInfo.Display}'; {unchanged} already that type, {blocked} blocked.",
                Data = Summary(plan, sample, null, applied: false),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (ChangeElementTypePlanning.ShouldRefuse(plan.RequireAll, plan.Decisions))
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "validation_failed",
                Message = $"Nothing changed: requireAll=true and {blocked} of {plan.Decisions.Count} element(s) are blocked.",
                Data = Summary(plan, sample, new List<TypeChangeOutcome>(), applied: true),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (switchable == 0)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Nothing to change: {unchanged} already '{plan.TargetInfo.Display}', {blocked} blocked.",
                Data = Summary(plan, sample, new List<TypeChangeOutcome>(), applied: true),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var doc = plan.Doc;
        var outcomes = new List<TypeChangeOutcome>();
        var rolledBack = false;
        cancellationToken.ThrowIfCancellationRequested();
        using (var tx = new Transaction(doc, "Revit MCP - Change Element Type"))
        {
            tx.Start();
            if (plan.NeedsActivation)
            {
                ((FamilySymbol)plan.Target).Activate();
                doc.Regenerate();
            }
            var n = 0;
            foreach (var d in plan.Decisions.Where(d => d.Status == TypeChangeStatus.Switchable))
            {
                if (++n % 200 == 0) cancellationToken.ThrowIfCancellationRequested();
                var oldId = d.ElementId;
                var oldTypeId = plan.OldTypes.TryGetValue(oldId, out var ot) ? ot : 0;
                var element = doc.GetElement(MoveElementsService.ToElementId(oldId));
                if (element == null)
                {
                    outcomes.Add(ChangeElementTypePlanning.Failure(oldId, oldTypeId, plan.TargetInfo.Id, "The element disappeared."));
                    if (plan.RequireAll) break;
                    continue;
                }
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    var returned = element.ChangeTypeId(plan.Target.Id);
                    var returnedValue = returned == null || returned == ElementId.InvalidElementId ? oldId : returned.Value;
                    var now = doc.GetElement(MoveElementsService.ToElementId(returnedValue));
                    if (now == null || now.GetTypeId() != plan.Target.Id)
                    {
                        sub.RollBack();
                        outcomes.Add(ChangeElementTypePlanning.Failure(oldId, oldTypeId, plan.TargetInfo.Id, "Revit accepted the call but the element did not take the type."));
                    }
                    else
                    {
                        sub.Commit();
                        outcomes.Add(ChangeElementTypePlanning.Outcome(oldId, oldTypeId, plan.TargetInfo.Id, returnedValue));
                    }
                }
                catch (Exception ex)
                {
                    try { sub.RollBack(); } catch (Exception rb) { Swallowed.Note(rb); }
                    outcomes.Add(ChangeElementTypePlanning.Failure(oldId, oldTypeId, plan.TargetInfo.Id, ex.Message));
                }
                if (plan.RequireAll && outcomes[^1].Status == "failed") break;
            }

            if (plan.RequireAll && outcomes.Any(o => o.Status == "failed"))
            {
                tx.RollBack();
                rolledBack = true;
            }
            else if (!outcomes.Any(o => o.Status != "failed"))
            {
                tx.RollBack();
                rolledBack = true;
            }
            else
            {
                TransactionCommitGuard.CommitOrThrow(tx);
            }
        }

        sw.Stop();
        var changed = rolledBack ? 0 : outcomes.Count(o => o.Status != "failed");
        var replaced = rolledBack ? 0 : outcomes.Count(o => o.Status == "replaced");
        var failed = outcomes.Count(o => o.Status == "failed");
        if (replaced > 0)
            warnings.Add($"{replaced} element(s) got a new id (see idMappings); anything that referred to the old id (tags, dimensions, schedules by id, saved lists) must use the new one.");
        if (rolledBack && plan.RequireAll)
            warnings.Add("requireAll=true: a failure undid the whole batch.");
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = changed > 0,
            Status = changed == 0 ? "transaction_failed" : null,
            Message = rolledBack
                ? $"Nothing changed: {failed} element(s) failed" + (plan.RequireAll ? " and requireAll=true undid the batch." : ".")
                : $"Changed the type of {changed} element(s) to '{plan.TargetInfo.Display}' ({replaced} with a new id); {unchanged} already that type, {blocked} blocked, {failed} failed.",
            Data = Summary(plan, sample, outcomes, applied: true, rolledBack: rolledBack),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    public static string ApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return "Change element type (the document could not be resolved).";
        var plan = BuildPlan(uiapp, target, request, CancellationToken.None, out var error);
        if (plan == null) return "Change element type — the input cannot be used: " + (error?.Message ?? "unknown");
        var (switchable, unchanged, blocked) = ChangeElementTypePlanning.Count(plan.Decisions);

        var lines = new List<string>
        {
            $"Change the type of {switchable} element(s) in '{target.Document.Title}' to '{plan.TargetInfo.Display}' [{plan.TargetInfo.CategoryName}].",
            $"{plan.Decisions.Count} element(s): {switchable} switch, {unchanged} already that type, {blocked} blocked."
        };
        var reasons = ChangeElementTypePlanning.CountByReason(plan.Decisions);
        if (reasons.Count > 0) lines.Add("Blocked: " + string.Join(", ", reasons.Select(r => $"{r.Value} {r.Key}")) + ".");
        foreach (var from in plan.Decisions.Where(d => d.Status == TypeChangeStatus.Switchable)
                     .GroupBy(d => plan.OldTypeNames.TryGetValue(d.ElementId, out var n) ? n : "?").OrderByDescending(g => g.Count()).Take(5))
            lines.Add($"  {from.Count()} x '{from.Key}' → '{plan.TargetInfo.Display}'");
        if (plan.RequireAll && blocked > 0) lines.Add("requireAll=true: the call will change nothing while elements are blocked.");
        lines.Add("Revit may give switched elements new ids; the result lists old and new. One transaction, one undo step.");
        return string.Join("\n", lines);
    }

    // ── Plan ─────────────────────────────────────────────────────────────

    private static Plan? BuildPlan(UIApplication uiapp, RevitDocumentResolver.DocumentTarget target, McpToolRequest request, CancellationToken cancellationToken, out McpToolResult? error)
    {
        error = null;
        var doc = target.Document;
        var args = request.Arguments;
        var typeId = ToolArguments.GetLong(args, "typeId");
        var familyName = ToolArguments.GetString(args, "familyName");
        var typeName = ToolArguments.GetString(args, "typeName");
        var categoryName = ToolArguments.GetString(args, "categoryName");
        var useSelection = ToolArguments.GetBool(args, "useSelection");
        var ids = new List<long>(ToolArguments.GetLongArray(args, "elementIds"));

        var argError = ChangeElementTypePlanning.ValidateArguments(ids.Count, useSelection, typeId, familyName, typeName);
        if (argError != null) { error = Fail(request, argError); return null; }

        if (useSelection)
        {
            var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
            if (uidoc != null)
                foreach (var id in uidoc.Selection.GetElementIds()) ids.Add(id.Value);
        }
        ids = ids.Distinct().ToList();
        if (ids.Count == 0) { error = Fail(request, "No elements: the selection is empty and no elementIds were given."); return null; }
        if (ids.Count > ChangeElementTypePlanning.MaxElements)
        {
            error = Fail(request, $"At most {ChangeElementTypePlanning.MaxElements} elements per call (got {ids.Count}); split the list.");
            return null;
        }

        // Candidate types: only when a name has to be resolved; an id is looked up directly.
        var types = new Dictionary<long, ElementType>();
        var candidates = new List<TypeCandidate>();
        if (typeId > 0)
        {
            if (doc.GetElement(MoveElementsService.ToElementId(typeId)) is ElementType one)
            {
                types[typeId] = one;
                candidates.Add(Candidate(one));
            }
        }
        else
        {
            foreach (var t in new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>())
            {
                if (!string.Equals(t.Name, typeName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                types[t.Id.Value] = t;
                candidates.Add(Candidate(t));
            }
        }
        var resolution = ChangeElementTypePlanning.ResolveTarget(candidates, typeId, familyName, typeName, categoryName);
        if (resolution.Type == null)
        {
            error = new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "validation_failed",
                Message = resolution.Error ?? "The target type could not be resolved.",
                Data = new
                {
                    code = resolution.Code,
                    matches = resolution.Matches.Take(20).Select(m => new { typeId = m.Id, family = m.FamilyName, type = m.TypeName, category = m.CategoryName }).ToList()
                }
            };
            return null;
        }

        var targetType = types[resolution.Type.Id];
        var plan = new Plan
        {
            Doc = doc,
            Target = targetType,
            TargetInfo = resolution.Type,
            RequireAll = ToolArguments.GetBool(args, "requireAll")
        };
        var targetFacts = new TypeChangeTargetFacts
        {
            Id = targetType.Id.Value,
            CategoryId = targetType.Category?.Id.Value,
            FamilyName = resolution.Type.FamilyName,
            TypeName = resolution.Type.TypeName
        };

        var n = 0;
        foreach (var id in ids)
        {
            if (++n % 500 == 0) cancellationToken.ThrowIfCancellationRequested();
            var element = doc.GetElement(MoveElementsService.ToElementId(id));
            var facts = new TypeChangeElementFacts { Id = id, Exists = element != null, InLinkedDocument = doc.IsLinked };
            if (element != null)
            {
                facts.IsElementType = element is ElementType;
                var currentTypeId = element.GetTypeId();
                facts.HasType = currentTypeId != null && currentTypeId != ElementId.InvalidElementId;
                facts.CurrentTypeId = currentTypeId != null && facts.HasType ? currentTypeId.Value : 0;
                facts.CategoryId = element.Category?.Id.Value;
                facts.GroupId = element.GroupId != null && element.GroupId != ElementId.InvalidElementId ? element.GroupId.Value : 0;
                facts.Pinned = element.Pinned;
                facts.EditProblem = OwnershipReader.EditProblem(doc, element.Id, out _);
                if (!facts.IsElementType && facts.HasType && facts.CurrentTypeId != targetFacts.Id && facts.CategoryId == targetFacts.CategoryId)
                    facts.TargetIsValid = SafeIsValidType(element, targetType.Id);
                plan.OldTypes[id] = facts.CurrentTypeId;
                if (facts.HasType && doc.GetElement(currentTypeId) is ElementType oldType)
                    plan.OldTypeNames[id] = Candidate(oldType).Display;
            }
            plan.Decisions.Add(ChangeElementTypePlanning.Classify(facts, targetFacts));
        }
        return plan;
    }

    private static bool SafeIsValidType(Element element, ElementId typeId)
    {
        try { return element.IsValidType(typeId); }
        catch (Exception ex)
        {
            Swallowed.Note(ex);
            return false;
        }
    }

    private static TypeCandidate Candidate(ElementType t) => new()
    {
        Id = t.Id.Value,
        FamilyName = t.FamilyName ?? string.Empty,
        TypeName = t.Name ?? string.Empty,
        CategoryName = t.Category?.Name ?? string.Empty
    };

    private static object Summary(Plan plan, int sample, List<TypeChangeOutcome>? outcomes, bool applied, bool rolledBack = false)
    {
        var (switchable, unchanged, blocked) = ChangeElementTypePlanning.Count(plan.Decisions);
        object Row(TypeChangeDecision d) => new
        {
            elementId = d.ElementId,
            status = d.Status.ToString().ToLowerInvariant(),
            reason = d.Reason,
            detail = d.Detail,
            fromType = plan.OldTypeNames.TryGetValue(d.ElementId, out var n) ? n : null,
            oldTypeId = plan.OldTypes.TryGetValue(d.ElementId, out var t) ? (long?)t : null
        };

        var data = new Dictionary<string, object?>
        {
            ["applied"] = applied && !rolledBack && outcomes is { Count: > 0 },
            ["target"] = new
            {
                typeId = plan.TargetInfo.Id,
                family = plan.TargetInfo.FamilyName,
                type = plan.TargetInfo.TypeName,
                category = plan.TargetInfo.CategoryName,
                needsActivation = plan.NeedsActivation
            },
            ["requireAll"] = plan.RequireAll,
            ["counts"] = new { elements = plan.Decisions.Count, switchable, unchanged, blocked },
            ["blockedByReason"] = ChangeElementTypePlanning.CountByReason(plan.Decisions),
            ["sample"] = new
            {
                switchable = plan.Decisions.Where(d => d.Status == TypeChangeStatus.Switchable).Take(sample).Select(Row).ToList(),
                unchanged = plan.Decisions.Where(d => d.Status == TypeChangeStatus.Unchanged).Take(Math.Min(sample, 5)).Select(Row).ToList(),
                blocked = plan.Decisions.Where(d => d.Status == TypeChangeStatus.Blocked).Take(sample).Select(Row).ToList()
            }
        };
        if (outcomes != null)
        {
            var mappings = ChangeElementTypePlanning.IdMappings(outcomes);
            data["result"] = new
            {
                changed = rolledBack ? 0 : outcomes.Count(o => o.Status != "failed"),
                replaced = rolledBack ? 0 : mappings.Count,
                failed = outcomes.Count(o => o.Status == "failed"),
                rolledBack
            };
            data["idMappings"] = rolledBack ? new List<object>() : mappings.Take(ChangeElementTypePlanning.MaxSampleSize * 5).Select(m => (object)new { oldId = m.Key, newId = m.Value }).ToList();
            data["changes"] = outcomes.Take(sample).Select(o => new { oldId = o.OldId, newId = o.NewId, oldTypeId = o.OldTypeId, newTypeId = o.NewTypeId, status = rolledBack && o.Status != "failed" ? "rolledBack" : o.Status, reason = o.Reason }).ToList();
        }
        return data;
    }

    private static McpToolResult Fail(McpToolRequest request, string message) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = "validation_failed",
        Message = message
    };
}
