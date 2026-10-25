using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>revit_list_groups (#108): model, detail and attached detail group types with their instances.</summary>
public sealed class ListGroupsTool : IRevitMcpTool
{
    public string Name => "revit_list_groups";

    public string Description =>
        "Lists group types: model, detail and attached detail groups, with id, name, kind, instance count and instance ids, " +
        "member count and categories (from the first instance), and hasMixedInstances (instances whose member counts " +
        "differ, i.e. edited in place). Optional: nameContains, kind (model | detail | attachedDetail), includeMembers " +
        "(a group type name or id: also list its first instance's member ids), limit (default 200).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var nameContains = ToolArguments.GetString(request.Arguments, "nameContains").Trim();
        var kind = ToolArguments.GetString(request.Arguments, "kind").Trim();
        var includeMembers = ToolArguments.GetString(request.Arguments, "includeMembers").Trim();
        var limit = Math.Max(1, Math.Min(ToolArguments.GetInt(request.Arguments, "limit", 200), 2000));

        var all = GroupService.GroupTypes(doc);
        var filtered = all
            .Where(t => nameContains.Length == 0 || t.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(t => kind.Length == 0 || string.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .ToList();

        object? members = null;
        var warnings = new List<string>();
        if (includeMembers.Length > 0)
        {
            var type = GroupService.Resolve(all, includeMembers, out var error);
            if (type == null) warnings.Add(error!);
            else if (type.Instances.Count == 0) warnings.Add($"Group type '{type.Name}' has no placed instance, so its members cannot be listed.");
            else
            {
                var first = type.Instances[0];
                members = new
                {
                    groupTypeId = type.Id,
                    name = type.Name,
                    instanceId = first.Id.Value,
                    members = GroupService.MemberIds(first).Select(id => doc.GetElement(id)).Where(e => e != null)
                        .Select(e => new { elementId = e!.Id.Value, category = GroupService.SafeCategory(e), name = SafeName(e) }).ToList()
                };
            }
        }

        var rows = filtered.Take(limit).Select(t =>
        {
            var counts = t.Instances.Select(g => GroupService.MemberIds(g).Count).ToList();
            var first = t.Instances.FirstOrDefault();
            return new
            {
                groupTypeId = t.Id,
                name = t.Name,
                kind = t.Kind,
                instanceCount = t.Instances.Count,
                instanceIds = t.Instances.Take(50).Select(g => g.Id.Value).ToList(),
                memberCount = first == null ? (int?)null : counts[0],
                memberCategories = first == null ? null : GroupService.CategoriesOf(doc, GroupService.MemberIds(first)),
                hasMixedInstances = GroupSupport.HasMixedInstances(counts),
                ownerViewIds = t.Kind == GroupKind.Model ? null : t.Instances.Select(g => g.OwnerViewId.Value).Distinct().Take(20).ToList()
            };
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{filtered.Count} group type(s)" + (filtered.Count > limit ? $", first {limit} listed" : string.Empty) +
                      $" ({filtered.Count(t => t.Kind == GroupKind.Model)} model, {filtered.Count(t => t.Kind == GroupKind.Detail)} detail, " +
                      $"{filtered.Count(t => t.Kind == GroupKind.AttachedDetail)} attached detail).",
            Data = new { total = filtered.Count, truncated = filtered.Count > limit, groupTypes = rows, members },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string SafeName(Element element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}

/// <summary>revit_preview_create_group (#108): the kind, name and blockers of a new group.</summary>
public sealed class PreviewCreateGroupTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_group";

    public string Description =>
        "Previews creating a group from elements, without changing the model. Same arguments as revit_create_group. " +
        "Returns the kind (model when every element is a model element, detail when all belong to one view), the " +
        "resulting unique name, members per category, and what blocks the group (pinned, already grouped, missing, mixed " +
        "model and view-specific elements — status group_members_mixed with the offending ids).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateGroupExecutor.Execute(uiapp, request, apply: false));
}

/// <summary>revit_create_group (#108): doc.Create.NewGroup with a unique name.</summary>
public sealed class CreateGroupTool : IRevitMcpTool
{
    public string Name => "revit_create_group";

    public string Description =>
        "Creates a group from elements with doc.Create.NewGroup and names its type. Requires approval. Required: elementIds " +
        "(up to 2000). Optional: name (made unique against existing group type names: 'Name 2', 'Name 3', …; default " +
        "'MCP Group'). Kind is detected: all model elements → model group; all view-specific elements of one view → detail " +
        "group. A mix is refused (group_members_mixed) naming the offending ids — Revit makes attached detail groups only in " +
        "its UI. Pinned and already grouped elements block. One transaction, one undo. Run revit_preview_create_group first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateGroupExecutor.Execute(uiapp, request, apply: true));
}

internal static class CreateGroupExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var ids = ToolArguments.GetLongArray(request.Arguments, "elementIds").Distinct().ToList();
        if (ids.Count == 0) return Fail(request, "validation_failed", "Provide elementIds: the elements to group.", null, sw);
        if (ids.Count > GroupSupport.MaxMembers)
            return Fail(request, "validation_failed", $"'elementIds' holds {ids.Count} entries; the limit is {GroupSupport.MaxMembers}.", null, sw);

        var requestedName = ToolArguments.GetString(request.Arguments, "name");
        if (!string.IsNullOrWhiteSpace(requestedName) && !GroupSupport.IsValidName(requestedName.Trim(), out var nameError))
            return Fail(request, "validation_failed", nameError, null, sw);

        var members = GroupService.Members(doc, ids);
        var kind = GroupSupport.DetectKind(members);
        var existing = GroupService.GroupTypes(doc).Select(t => t.Name).ToList();
        var name = GroupSupport.ResolveUniqueName(requestedName, existing);
        var view = kind.ViewId.HasValue ? doc.GetElement(MoveElementsService.ToElementId(kind.ViewId.Value)) as View : null;
        var categories = GroupService.CategoriesOf(doc, ids.Select(MoveElementsService.ToElementId));

        object Payload(long? groupId, long? groupTypeId, int? memberCount) => new
        {
            kind = kind.Kind,
            name,
            nameChanged = !string.IsNullOrWhiteSpace(requestedName) && !string.Equals(name, requestedName.Trim(), StringComparison.Ordinal),
            viewId = kind.ViewId,
            viewName = view?.Name,
            elementCount = ids.Count,
            membersByCategory = categories,
            blockers = kind.Blockers.Select(b => new { elementId = b.Id, reason = b.Reason }).ToList(),
            offenders = kind.Offenders,
            groupId,
            groupTypeId,
            memberCount
        };

        if (kind.Kind == null)
            return Fail(request, kind.Status ?? "validation_failed",
                (apply ? "Nothing was grouped: " : "Preview: the group cannot be created: ") + kind.Error, Payload(null, null, null), sw);

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preview: a {kind.Kind} group '{name}' of {ids.Count} element(s)" + (view != null ? $" in view '{view.Name}'" : string.Empty) + ".",
                Data = Payload(null, null, null),
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        Group? group = null;
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Group", () =>
        {
            group = doc.Create.NewGroup(ids.Select(MoveElementsService.ToElementId).ToList());
            group.GroupType.Name = name;
        });

        if (!txSuccess || group == null)
        {
            var failed = Fail(request, "transaction_failed", "The group was not created: " + (diagnostics.OriginalError ?? "Revit refused it."), Payload(null, null, null), sw);
            failed.Warnings.AddRange(diagnostics.ToErrorLines());
            return failed;
        }

        sw.Stop();
        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Created {kind.Kind} group '{name}' (instance {group.Id.Value}, type {group.GroupType.Id.Value}) from {ids.Count} element(s).",
            Data = Payload(group.Id.Value, group.GroupType.Id.Value, GroupService.MemberIds(group).Count),
            DurationMs = sw.ElapsedMilliseconds
        };
        result.Warnings.AddRange(diagnostics.FailureMessages);
        return result;
    }

    internal static McpToolResult Fail(McpToolRequest request, string status, string message, object? data, Stopwatch sw)
    {
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = false,
            Status = status,
            Message = message,
            Data = data,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}

/// <summary>revit_ungroup (#108): Group.UngroupMembers for instances or every instance of a type; the type stays.</summary>
public sealed class UngroupTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_ungroup";

    public string Description =>
        "Ungroups group instances with Group.UngroupMembers: the members stay in the model as ordinary elements. Requires " +
        "approval. Give groupIds (instance ids) or groupType (a group type name or id: every instance is ungrouped). The " +
        "group type stays in the project; revit_ungroup_and_delete_type also deletes types left without instances. " +
        "Returns the member ids per instance. One transaction, one undo.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(UngroupExecutor.Execute(uiapp, request, deleteTypes: false));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => UngroupExecutor.Summary(uiapp, request, deleteTypes: false);
}

/// <summary>revit_ungroup_and_delete_type (#108): ungroups, then deletes the group types left without instances.</summary>
public sealed class UngroupAndDeleteTypeTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_ungroup_and_delete_type";

    public string Description =>
        "Ungroups group instances (as revit_ungroup) and then deletes every group type of those instances that has no " +
        "instance left. Destructive: needs manual approval in Revit, never covered by a plan. Give groupIds or groupType. " +
        "The members are kept; only the empty group types are deleted. One transaction, one undo.";

    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(UngroupExecutor.Execute(uiapp, request, deleteTypes: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => UngroupExecutor.Summary(uiapp, request, deleteTypes: true);
}

internal static class UngroupExecutor
{
    public static string? Summary(UIApplication uiapp, McpToolRequest request, bool deleteTypes)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var (groups, _, error, _) = GroupService.UngroupTargets(target.Document, request.Arguments);
        if (error != null) return null;
        var memberCount = groups.Sum(g => GroupService.MemberIds(g).Count);
        var typeNames = groups.Select(g => SafeTypeName(g)).Distinct().ToList();
        return TransformsAndGroupsApprovalSummaries.Ungroup(groups.Count, memberCount, typeNames, deleteTypes);
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool deleteTypes)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var (groups, _, error, status) = GroupService.UngroupTargets(doc, request.Arguments);
        if (error != null) return CreateGroupExecutor.Fail(request, status ?? "validation_failed", error, null, sw);

        var typeIds = groups.Select(g => g.GroupType.Id).Distinct().ToList();
        var typeNames = typeIds.ToDictionary(id => id.Value, id => doc.GetElement(id)?.Name ?? string.Empty);
        var ungrouped = new List<object>();
        var deletedTypes = new List<object>();
        var keptTypes = new List<object>();

        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc,
            deleteTypes ? "Revit MCP - Ungroup and Delete Group Types" : "Revit MCP - Ungroup", () =>
            {
                foreach (var group in groups)
                {
                    var groupId = group.Id.Value;
                    var typeId = group.GroupType.Id.Value;
                    var members = group.UngroupMembers();
                    ungrouped.Add(new { groupId, groupTypeId = typeId, groupType = typeNames[typeId], memberIds = members.Select(m => m.Value).ToList() });
                }

                if (!deleteTypes) return;
                doc.Regenerate();
                foreach (var typeId in typeIds)
                {
                    var type = doc.GetElement(typeId) as GroupType;
                    var remaining = 0;
                    try { remaining = type?.Groups.Size ?? 0; } catch { remaining = 0; }
                    if (type == null || remaining > 0)
                    {
                        keptTypes.Add(new { groupTypeId = typeId.Value, name = typeNames[typeId.Value], remainingInstances = remaining });
                        continue;
                    }
                    doc.Delete(typeId);
                    deletedTypes.Add(new { groupTypeId = typeId.Value, name = typeNames[typeId.Value] });
                }
            });

        if (!txSuccess)
        {
            var failed = CreateGroupExecutor.Fail(request, "transaction_failed",
                "Nothing was ungrouped: " + (diagnostics.OriginalError ?? "Revit refused it."), null, sw);
            failed.Warnings.AddRange(diagnostics.ToErrorLines());
            return failed;
        }

        sw.Stop();
        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Ungrouped {ungrouped.Count} group instance(s)" +
                      (deleteTypes ? $"; deleted {deletedTypes.Count} empty group type(s), kept {keptTypes.Count} that still have instances." : "; the group types stay in the project."),
            Data = new { ungrouped, deletedTypes = deleteTypes ? deletedTypes : null, keptTypes = deleteTypes ? keptTypes : null },
            DurationMs = sw.ElapsedMilliseconds
        };
        result.Warnings.AddRange(diagnostics.FailureMessages);
        return result;
    }

    private static string SafeTypeName(Group group)
    {
        try { return group.GroupType?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}

/// <summary>revit_preview_place_group_instances (#108): the group type, its kind, the path and the level per point.</summary>
public sealed class PreviewPlaceGroupInstancesTool : IRevitMcpTool
{
    public string Name => "revit_preview_place_group_instances";

    public string Description =>
        "Previews placing group instances, without changing the model. Same arguments as revit_place_group_instances. " +
        "Returns the resolved group type and kind, the placement path per point (placeGroup for model groups, " +
        "copiedFromInstance for detail groups) and the nearest level per point.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(PlaceGroupExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_place_group_instances (#108): PlaceGroup at points with rotation; detail groups by copy.</summary>
public sealed class PlaceGroupInstancesTool : IRevitMcpTool
{
    public string Name => "revit_place_group_instances";

    public string Description =>
        "Places instances of a group type at points. Requires approval. Required: groupType (name or id, revit_list_groups) " +
        "and placements (up to 500): [{pointMm {x, y, z?}, rotationDeg?, viewId?, levelOverride?}]. Model groups: " +
        "doc.Create.PlaceGroup at the point, then rotated about Z there; without z the point is on the nearest level " +
        "(levelOverride: a level name or id). Detail groups: PlaceGroup is model-only, so an existing instance is copied " +
        "(in its view, or into viewId) and moved onto the point — the result says which path was used. atomic (default " +
        "true). One transaction, one undo. Run revit_preview_place_group_instances first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(PlaceGroupExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class PlaceGroupExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var plan = GroupPlacementService.Plan(doc, request.Arguments, out var error, out var status);
        if (plan == null) return CreateGroupExecutor.Fail(request, status ?? "validation_failed", error!, null, sw);

        var blocked = plan.Items.Count(i => i.Status == "blocked");
        var warnings = plan.Items.Where(i => i.Reason != null).Select(i => $"placements[{i.Request.Index}]: {i.Reason}").ToList();

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preview: {plan.Items.Count - blocked} of {plan.Items.Count} instance(s) of {plan.Type.Kind} group '{plan.Type.Name}' would be placed" +
                          (blocked > 0 ? $"; {blocked} cannot" : string.Empty) + ".",
                Data = Payload(plan),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if ((plan.Atomic && blocked > 0) || blocked == plan.Items.Count)
        {
            foreach (var item in plan.Items.Where(i => i.CanPlace)) item.Status = "notAttempted";
            var rejected = CreateGroupExecutor.Fail(request, "validation_failed",
                $"Nothing was placed: {blocked} of {plan.Items.Count} placement(s) cannot be made" +
                (plan.Atomic && blocked < plan.Items.Count ? " and atomic=true. Fix them, or pass atomic=false." : "."), Payload(plan), sw);
            rejected.Warnings.AddRange(warnings);
            return rejected;
        }

        var placed = 0;
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Place Group Instances", () =>
        {
            placed = GroupPlacementService.Execute(doc, plan, cancellationToken);
        });

        if (!txSuccess)
        {
            foreach (var item in plan.Items.Where(i => i.Status == "placed"))
            {
                item.Status = "rolledBack";
                item.NewGroupId = null;
            }
            var failed = CreateGroupExecutor.Fail(request, "transaction_failed",
                "Rolled back: a placement failed, so no group instance was placed. See the per-placement reasons.", Payload(plan), sw);
            failed.Warnings.AddRange(warnings);
            failed.Warnings.AddRange(diagnostics.ToErrorLines());
            return failed;
        }

        warnings.AddRange(diagnostics.FailureMessages);
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = placed > 0,
            Message = $"Placed {placed} of {plan.Items.Count} instance(s) of {plan.Type.Kind} group '{plan.Type.Name}' " +
                      $"({(plan.Type.Kind == GroupKind.Model ? "PlaceGroup" : "copied from an existing instance")}).",
            Data = Payload(plan),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Payload(GroupPlacementPlan plan) => new
    {
        groupTypeId = plan.Type.Id,
        groupType = plan.Type.Name,
        kind = plan.Type.Kind,
        atomic = plan.Atomic,
        newGroupIds = plan.Items.Where(i => i.NewGroupId.HasValue).Select(i => i.NewGroupId!.Value).ToList(),
        placements = plan.Items.Select(i => new
        {
            index = i.Request.Index,
            path = i.Path,
            pointMm = Point(i.TargetMm),
            rotationDeg = i.Request.RotationDeg,
            level = i.Level?.Name,
            levelId = i.Level?.Id.Value,
            viewId = i.View?.Id.Value,
            viewName = i.View?.Name,
            sourceInstanceId = i.SourceInstance?.Id.Value,
            status = i.Status,
            reason = i.Reason,
            newGroupId = i.NewGroupId,
            resultPointMm = i.ResultMm.HasValue ? Point(i.ResultMm.Value) : null,
            resultLevel = i.ResultLevel
        }).ToList()
    };

    private static object Point(PointMm point) => new { x = Math.Round(point.X, 1), y = Math.Round(point.Y, 1), z = Math.Round(point.Z, 1) };
}
