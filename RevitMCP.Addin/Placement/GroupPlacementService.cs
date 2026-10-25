using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Placement;

/// <summary>How a group instance is placed.</summary>
public static class GroupPlacementPath
{
    /// <summary>doc.Create.PlaceGroup(point, groupType): model groups.</summary>
    public const string PlaceGroup = "placeGroup";

    /// <summary>
    /// Copy an existing instance with the copy service's view path, then move it onto the point: detail groups, which
    /// PlaceGroup cannot place (it is model-only, and the API cannot switch the active view).
    /// </summary>
    public const string CopiedFromInstance = "copiedFromInstance";
}

/// <summary>One placement, resolved.</summary>
internal sealed class GroupPlacementItem
{
    public GroupPlacementRequest Request { get; init; } = null!;
    public string Path { get; set; } = GroupPlacementPath.PlaceGroup;
    public Level? Level { get; set; }
    public View? View { get; set; }

    /// <summary>The instance a detail group is copied from.</summary>
    public Group? SourceInstance { get; set; }

    /// <summary>Where the group origin goes, mm (z from the level when the point had none).</summary>
    public PointMm TargetMm { get; set; }

    public string Status { get; set; } = "ready";
    public string? Reason { get; set; }
    public long? NewGroupId { get; set; }
    public PointMm? ResultMm { get; set; }
    public string? ResultLevel { get; set; }
    public bool CanPlace => Status == "ready";
}

internal sealed class GroupPlacementPlan
{
    public GroupTypeInfo Type { get; init; } = null!;
    public List<GroupPlacementItem> Items { get; } = new();
    public bool Atomic { get; init; } = true;
}

/// <summary>
/// The Revit side of revit_place_group_instances (#108). Model groups are placed with PlaceGroup and turned about Z
/// at the point; detail groups are copied from an existing instance (view path) and moved onto the point, because
/// PlaceGroup only places model groups and the API thread cannot change the active view.
/// </summary>
internal static class GroupPlacementService
{
    public static GroupPlacementPlan? Plan(Document doc, Dictionary<string, object?> arguments, out string? error, out string? status)
    {
        status = null;
        arguments.TryGetValue("placements", out var rawPlacements);
        var placements = GroupSupport.ParsePlacements(rawPlacements, out error);
        if (placements == null)
        {
            status = "validation_failed";
            return null;
        }

        var types = GroupService.GroupTypes(doc);
        var type = GroupService.Resolve(types, Tools.ToolArguments.GetString(arguments, "groupType"), out error);
        if (type == null)
        {
            status = GroupSupport.TypeNotFoundStatus;
            return null;
        }
        if (type.Kind == GroupKind.AttachedDetail)
        {
            error = $"'{type.Name}' is an attached detail group: Revit shows it with its model group (revit_list_groups names its parent); " +
                    "it is not placed on its own.";
            status = "validation_failed";
            return null;
        }

        var plan = new GroupPlacementPlan { Type = type, Atomic = Tools.ToolArguments.GetBool(arguments, "atomic", true) };
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();

        foreach (var placement in placements)
        {
            var item = new GroupPlacementItem { Request = placement, TargetMm = placement.PointMm };
            plan.Items.Add(item);

            if (type.Kind == GroupKind.Model)
            {
                item.Path = GroupPlacementPath.PlaceGroup;
                Level? level = null;
                if (placement.LevelOverride != null)
                {
                    level = FindLevel(levels, placement.LevelOverride);
                    if (level == null)
                    {
                        item.Status = "blocked";
                        item.Reason = $"levelOverride '{placement.LevelOverride}' is not a level name or id in this document.";
                        continue;
                    }
                }
                level ??= placement.HasZ
                    ? PlacementHelpers.NearestLevel(doc, MoveElementsMath.MmToFt(placement.PointMm.Z))
                    : PlacementHelpers.NearestLevel(doc, 0);
                item.Level = level;
                if (!placement.HasZ && level != null)
                    item.TargetMm = new PointMm(placement.PointMm.X, placement.PointMm.Y, MoveElementsMath.FtToMm(level.Elevation));
                continue;
            }

            // Detail group: copy an existing instance, preferring one in the requested view.
            item.Path = GroupPlacementPath.CopiedFromInstance;
            var view = placement.ViewId > 0 ? doc.GetElement(MoveElementsService.ToElementId(placement.ViewId)) as View : null;
            if (placement.ViewId > 0 && view == null)
            {
                item.Status = "blocked";
                item.Reason = $"viewId {placement.ViewId} is not a view in this document.";
                continue;
            }

            var source = type.Instances.FirstOrDefault(g => view != null && g.OwnerViewId == view.Id) ?? type.Instances.FirstOrDefault();
            if (source == null)
            {
                item.Status = "blocked";
                item.Reason = $"Detail group '{type.Name}' has no placed instance to copy from, and Revit's API places detail groups " +
                              "only by copying one. Place one instance in Revit first, then call again.";
                continue;
            }

            item.SourceInstance = source;
            item.View = view ?? MoveElementsService.OwnerViewOf(doc, source);
            if (item.View == null)
            {
                item.Status = "blocked";
                item.Reason = "The detail group instance to copy from has no owner view.";
                continue;
            }
            if (item.View.IsTemplate)
            {
                item.Status = "blocked";
                item.Reason = $"View '{item.View.Name}' is a view template.";
            }
        }

        error = null;
        return plan;
    }

    /// <summary>Places every ready item. Must run inside a transaction; throws on an atomic failure.</summary>
    public static int Execute(Document doc, GroupPlacementPlan plan, CancellationToken cancellationToken)
    {
        var placed = 0;
        foreach (var item in plan.Items.Where(i => i.CanPlace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Atomic)
            {
                try
                {
                    PlaceOne(doc, plan.Type, item);
                }
                catch (Exception ex)
                {
                    item.Status = "failed";
                    item.Reason = ex.Message;
                    throw new GroupPlacementAbortedException(item.Request.Index, ex.Message);
                }
                placed++;
                continue;
            }

            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                PlaceOne(doc, plan.Type, item);
                sub.Commit();
                placed++;
            }
            catch (Exception ex)
            {
                if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                item.Status = "failed";
                item.Reason = ex.Message;
                item.NewGroupId = null;
            }
        }
        return placed;
    }

    private static void PlaceOne(Document doc, GroupTypeInfo type, GroupPlacementItem item)
    {
        var target = PlacementHelpers.PointFromMm(item.TargetMm.X, item.TargetMm.Y, item.TargetMm.Z);
        Group group;
        XYZ axisDirection = XYZ.BasisZ;

        if (item.Path == GroupPlacementPath.PlaceGroup)
        {
            group = doc.Create.PlaceGroup(target, type.Type);
        }
        else
        {
            var source = item.SourceInstance!;
            var sourceView = MoveElementsService.OwnerViewOf(doc, source)!;
            var batch = new CopyBatch { Operation = CopyOperation.View, SourceView = sourceView, DestinationView = item.View };
            var created = CopyElementsService.CopySet(doc, batch, new List<ElementId> { source.Id }, Transform.Identity);
            group = created.Select(doc.GetElement).OfType<Group>().FirstOrDefault(g => g.GroupType?.Id == type.Type.Id)
                    ?? throw new InvalidOperationException("Revit copied the detail group but no new instance of its type was found.");
            doc.Regenerate();

            // Onto the point, within the destination view's plane.
            if (group.Location is LocationPoint copied)
            {
                var move = target - copied.Point;
                var frame = MoveElementsService.FrameOf(item.View);
                if (frame != null)
                {
                    var normal = new XYZ(frame.Value.Normal.X, frame.Value.Normal.Y, frame.Value.Normal.Z);
                    move -= normal * move.DotProduct(normal);
                    axisDirection = normal;
                }
                if (!move.IsZeroLength()) ElementTransformUtils.MoveElement(doc, group.Id, move);
            }
        }

        item.NewGroupId = group.Id.Value;
        doc.Regenerate();

        if (Math.Abs(item.Request.RotationDeg) > 1e-9 && group.Location is LocationPoint origin)
        {
            var axis = Line.CreateBound(origin.Point, origin.Point + axisDirection);
            ElementTransformUtils.RotateElement(doc, group.Id, axis, item.Request.RotationDeg * Math.PI / 180.0);
        }

        if (group.Location is LocationPoint result)
            item.ResultMm = MoveElementsMath.PointFromFeet(result.Point.X, result.Point.Y, result.Point.Z);
        try
        {
            var levelParam = group.get_Parameter(BuiltInParameter.GROUP_LEVEL);
            item.ResultLevel = levelParam?.AsValueString() ?? (doc.GetElement(levelParam?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Name;
        }
        catch
        {
            item.ResultLevel = null;
        }
        item.Status = "placed";
    }

    private static Level? FindLevel(List<Level> levels, string reference)
    {
        if (long.TryParse(reference, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            var byId = levels.FirstOrDefault(l => l.Id.Value == id);
            if (byId != null) return byId;
        }
        return levels.FirstOrDefault(l => string.Equals(l.Name, reference, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class GroupPlacementAbortedException : Exception
{
    public GroupPlacementAbortedException(int index, string reason)
        : base($"atomic=true: placements[{index}] failed ({reason}), so every placement was rolled back.")
    {
    }
}
