using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Datums;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Datums;

/// <summary>revit_list_datums (#164): levels, grids and reference planes with ids and geometry.</summary>
public sealed class ListDatumsTool : IRevitMcpTool
{
    public const string ToolName = "revit_list_datums";
    public string Name => ToolName;

    public string Description =>
        "Lists the levels, grids and reference planes of the host model with ids, names and geometry in millimetres " +
        "(host internal coordinates): levels elevationMm/projectElevationMm, grids startMm/endMm (or curved), reference " +
        "planes bubbleEndMm/freeEndMm. Filters: kinds (levels, grids, referencePlanes; default all), nameFilter " +
        "(contains, ignoring case), limit per kind (default 200). Pinned datums are flagged. Read-only; " +
        "revit_list_levels also maps a link's levels. Use the ids with revit_preview_edit_datums.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var args = request.Arguments;
        var kinds = ToolArguments.GetStringArray(args, "kinds").Select(DatumKinds.Normalize).Where(k => k != null).ToHashSet();
        var single = ToolArguments.GetString(args, "kinds");
        if (kinds.Count == 0 && single.Length > 0 && !single.TrimStart().StartsWith("[", StringComparison.Ordinal))
            foreach (var part in single.Split(',', ';')) { var k = DatumKinds.Normalize(part); if (k != null) kinds.Add(k); }
        var all = kinds.Count == 0;
        var filter = ToolArguments.GetString(args, "nameFilter").Trim();
        var limit = Math.Max(1, Math.Min(2000, ToolArguments.GetInt(args, "limit", 200)));

        bool NameMatches(string n) => filter.Length == 0 || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        var warnings = new List<string>();
        object levels = Array.Empty<object>();
        object grids = Array.Empty<object>();
        object referencePlanes = Array.Empty<object>();
        var counts = new Dictionary<string, int>();

        if (all || kinds.Contains(DatumKinds.Level))
        {
            var rows = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => NameMatches(l.Name)).OrderBy(l => l.Elevation).ToList();
            counts["levels"] = rows.Count;
            levels = rows.Take(limit).Select(l => new
            {
                id = l.Id.Value,
                name = l.Name,
                elevationMm = GeometryUnits.RoundMm(GeometryUnits.FtToMm(l.Elevation)),
                projectElevationMm = GeometryUnits.RoundMm(GeometryUnits.FtToMm(l.ProjectElevation)),
                pinned = l.Pinned
            }).ToList();
            if (rows.Count > limit) warnings.Add($"{rows.Count} levels match; the first {limit} are listed.");
        }
        if (all || kinds.Contains(DatumKinds.Grid))
        {
            var rows = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => NameMatches(g.Name)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            counts["grids"] = rows.Count;
            grids = rows.Take(limit).Select(g =>
            {
                var curve = g.Curve;
                var line = curve as Line;
                return new
                {
                    id = g.Id.Value,
                    name = g.Name,
                    shape = line != null ? "line" : curve == null ? "none" : g.IsCurved ? "arc" : "other",
                    startMm = line != null ? DatumRevit.Mm(line.GetEndPoint(0)) : null,
                    endMm = line != null ? DatumRevit.Mm(line.GetEndPoint(1)) : null,
                    lengthMm = curve != null ? GeometryUnits.RoundMm(GeometryUnits.FtToMm(curve.Length)) : (double?)null,
                    pinned = g.Pinned
                };
            }).ToList();
            if (rows.Count > limit) warnings.Add($"{rows.Count} grids match; the first {limit} are listed.");
        }
        if (all || kinds.Contains(DatumKinds.ReferencePlane))
        {
            var rows = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Where(p => NameMatches(p.Name)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            counts["referencePlanes"] = rows.Count;
            referencePlanes = rows.Take(limit).Select(p => new
            {
                id = p.Id.Value,
                name = p.Name,
                bubbleEndMm = DatumRevit.Mm(p.BubbleEnd),
                freeEndMm = DatumRevit.Mm(p.FreeEnd),
                normal = new[] { GeometryUnits.RoundUnit(p.Normal.X), GeometryUnits.RoundUnit(p.Normal.Y), GeometryUnits.RoundUnit(p.Normal.Z) },
                pinned = p.Pinned
            }).ToList();
            if (rows.Count > limit) warnings.Add($"{rows.Count} reference planes match; the first {limit} are listed.");
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}")) + $" in '{doc.Title}'.",
            Data = new { document = doc.Title, counts, levels, grids, referencePlanes },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

public sealed class PreviewCreateDatumsTool : IRevitMcpTool
{
    public const string ToolName = "revit_preview_create_datums";
    public string Name => ToolName;

    public string Description =>
        "Previews creating levels, grids and reference planes without changing the model. Same arguments as " +
        "revit_create_datums. Reports per item whether it would be created or why it is refused: invalid or duplicate " +
        "names, a level at the same elevation as an existing one, an endpoint outside bounds, a grid or reference " +
        "plane identical to an existing one, a view that cannot hold a reference plane.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DatumExecutor.Create(uiapp, request, apply: false));
}

public sealed class CreateDatumsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public const string ToolName = "revit_create_datums";
    public string Name => ToolName;

    public string Description =>
        "Creates levels, grids and reference planes in the host document in one transaction (millimetres). " +
        "levels [{name, elevationMm}]; grids [{name?, startMm:[x,y], endMm:[x,y]}] (straight, horizontal); " +
        "referencePlanes [{name?, viewId, bubbleEndMm:[x,y(,z)], freeEndMm:[x,y(,z)]}] drawn in a plan, ceiling plan, " +
        "elevation, section or detail view. A level at the same elevation as an existing one (elevationToleranceMm, " +
        "default 1) and an identical grid or reference plane are refused unless allowEquivalentLevels / " +
        "allowDuplicateGeometry. mode=allOrNothing (default) writes nothing when any item is refused; perItem writes " +
        "the others. Creates no views: use revit_create_views for the plan views of a new level. Run " +
        "revit_preview_create_datums first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DatumExecutor.Create(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        try { return DatumExecutor.CreateApprovalSummary(uiapp, request); }
        catch (Exception ex) { Swallowed.Note(ex); return null; }
    }
}

public sealed class PreviewEditDatumsTool : IRevitMcpTool
{
    public const string ToolName = "revit_preview_edit_datums";
    public string Name => ToolName;

    public string Description =>
        "Previews renaming levels, grids and reference planes and moving them, without changing the model. Same " +
        "arguments as revit_edit_datums. Per datum: the change, its impact (none, low, high), what depends on it " +
        "(hosted elements, associated views, other dependents), and why it would be refused (name conflict, elevation " +
        "clash, pinned, checked out by someone else, not a datum of this document).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DatumExecutor.Edit(uiapp, request, apply: false));
}

public sealed class EditDatumsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public const string ToolName = "revit_edit_datums";
    public string Name => ToolName;

    public string Description =>
        "Renames and moves existing datums in the host document in one transaction. edits [{id, newName?, " +
        "elevationMm? (levels), moveByMm? [dx,dy(,dz)] (grids and reference planes)}] with ids from revit_list_datums. " +
        "Moving a level or grid carries everything that depends on it (hosted elements, associated views, dimensions), " +
        "so a change with dependents or a move of 1 m or more is high impact and is refused unless " +
        "confirmHighImpact=true. Refused too: name conflicts, a level onto another level's elevation " +
        "(allowEquivalentLevels), pinned or checked-out datums. Datums of linked models are not editable. " +
        "mode=allOrNothing (default) or perItem. Needs manual approval. Run revit_preview_edit_datums first and " +
        "revit_checkpoint_create to be able to roll back.";

    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DatumExecutor.Edit(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        try { return DatumExecutor.EditApprovalSummary(uiapp, request); }
        catch (Exception ex) { Swallowed.Note(ex); return null; }
    }
}

/// <summary>Registers the datum tools (#164). Called once from App.OnStartup.</summary>
public static class DatumToolRegistration
{
    public static void Register(Services.ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListDatumsTool());
        handler.RegisterTool(new PreviewCreateDatumsTool());
        handler.RegisterTool(new CreateDatumsTool());
        handler.RegisterTool(new PreviewEditDatumsTool());
        handler.RegisterTool(new EditDatumsTool());
    }
}

internal static class DatumRevit
{
    public static double[] Mm(XYZ p) => new[]
    {
        GeometryUnits.RoundMm(GeometryUnits.FtToMm(p.X)), GeometryUnits.RoundMm(GeometryUnits.FtToMm(p.Y)), GeometryUnits.RoundMm(GeometryUnits.FtToMm(p.Z))
    };

    public static MmPoint ToMm(XYZ p) => new(GeometryUnits.FtToMm(p.X), GeometryUnits.FtToMm(p.Y), GeometryUnits.FtToMm(p.Z));

    public static XYZ ToFeet(MmPoint p) => new(GeometryUnits.MmToFt(p.X), GeometryUnits.MmToFt(p.Y), GeometryUnits.MmToFt(p.Z));

    public static List<ExistingDatum> Existing(Document doc)
    {
        var list = new List<ExistingDatum>();
        foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            list.Add(new ExistingDatum { Id = l.Id.Value, Kind = DatumKinds.Level, Name = l.Name, ElevationMm = GeometryUnits.FtToMm(l.Elevation) });
        foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
        {
            var d = new ExistingDatum { Id = g.Id.Value, Kind = DatumKinds.Grid, Name = g.Name };
            if (g.Curve is Line line) { d.Start = ToMm(line.GetEndPoint(0)); d.End = ToMm(line.GetEndPoint(1)); }
            list.Add(d);
        }
        foreach (var p in new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
            list.Add(new ExistingDatum { Id = p.Id.Value, Kind = DatumKinds.ReferencePlane, Name = p.Name, Start = ToMm(p.BubbleEnd), End = ToMm(p.FreeEnd) });
        return list;
    }

    private static readonly HashSet<ViewType> PlaneViewTypes = new()
    {
        ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan,
        ViewType.Elevation, ViewType.Section, ViewType.Detail
    };

    public static Dictionary<long, DatumViewInfo> Views(Document doc, IEnumerable<long> ids)
    {
        var map = new Dictionary<long, DatumViewInfo>();
        foreach (var id in ids.Distinct())
        {
            if (doc.GetElement(new ElementId(id)) is not View view) continue;
            var info = new DatumViewInfo { Id = id };
            if (view.IsTemplate) info.Problem = "it is a view template";
            else if (!PlaneViewTypes.Contains(view.ViewType)) info.Problem = $"{view.ViewType} views cannot hold reference planes";
            else
            {
                var dir = view.ViewDirection;
                info.DirX = dir.X; info.DirY = dir.Y; info.DirZ = dir.Z;
            }
            map[id] = info;
        }
        return map;
    }

    /// <summary>What depends on the datum, read live.</summary>
    public static DatumDependents Dependents(Document doc, Element datum, string kind)
    {
        var d = new DatumDependents();
        try
        {
            var viewIds = new HashSet<long>();
            var hostedIds = new HashSet<long>();
            if (kind == DatumKinds.Level)
            {
                foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementLevelFilter(datum.Id)))
                    if (e.Id != datum.Id) hostedIds.Add(e.Id.Value);
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>())
                    if (!v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == datum.Id) viewIds.Add(v.Id.Value);
            }
            var others = new List<Element>();
            foreach (var id in datum.GetDependentElements(null))
            {
                if (id == datum.Id || hostedIds.Contains(id.Value) || viewIds.Contains(id.Value)) continue;
                var e = doc.GetElement(id);
                if (e == null) continue;
                if (e is View v2 && !v2.IsTemplate) viewIds.Add(id.Value);
                else others.Add(e);
            }
            d.Hosted = hostedIds.Count;
            d.Views = viewIds.Count;
            d.Other = others.Count;

            var byCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in hostedIds.Take(3000))
            {
                var name = doc.GetElement(new ElementId(id))?.Category?.Name ?? "other";
                byCategory[name] = byCategory.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            foreach (var e in others.Take(1000))
            {
                var name = e.Category?.Name ?? e.GetType().Name;
                byCategory[name] = byCategory.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            d.Examples = byCategory.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Value} {kv.Key}").ToList();
        }
        catch (Exception ex)
        {
            Swallowed.Note(ex);
        }
        return d;
    }

    public static DatumTarget? Target(Document doc, long id)
    {
        var element = doc.GetElement(new ElementId(id));
        string? kind = element switch
        {
            Level => DatumKinds.Level,
            Grid => DatumKinds.Grid,
            ReferencePlane => DatumKinds.ReferencePlane,
            _ => null
        };
        if (element == null || kind == null) return null;
        var t = new DatumTarget { Id = id, Kind = kind, Name = element.Name, Pinned = element.Pinned };
        if (element is Level level) t.ElevationMm = GeometryUnits.FtToMm(level.Elevation);
        try
        {
            if (doc.IsWorkshared)
            {
                var ownership = OwnershipReader.Read(doc, element.Id);
                t.OwnedByOther = ownership.OwnedByOther;
                t.Owner = ownership.Owner;
            }
        }
        catch (Exception ex) { Swallowed.Note(ex); }
        t.Dependents = Dependents(doc, element, kind);
        return t;
    }
}

internal static class DatumExecutor
{
    private static McpToolResult Fail(McpToolRequest request, string status, string message, object? data = null) => new()
    {
        RequestId = request.RequestId, Success = false, Status = status, Message = message, Data = data
    };

    private static DatumOptions Options(Dictionary<string, object?> args)
    {
        var tolerance = ToolArguments.GetDouble(args, "elevationToleranceMm", -1);
        return DatumSpecParser.ParseOptions(tolerance < 0 ? null : tolerance, ToolArguments.GetBool(args, "allowEquivalentLevels"),
            ToolArguments.GetBool(args, "allowDuplicateGeometry"), ToolArguments.GetBool(args, "confirmHighImpact"), ToolArguments.GetString(args, "mode"));
    }

    private static string? DocumentProblem(Document doc)
    {
        if (doc.IsFamilyDocument) return "Datums are edited in project documents only, and this is a family document.";
        if (doc.IsLinked) return "Linked documents are not edited through their visible elements; target the host project.";
        if (doc.IsReadOnly) return "The document is read-only.";
        return null;
    }

    private static object Items(IReadOnlyList<DatumItemResult> results) => results.Select(r => new
    {
        kind = r.Kind,
        index = r.Index,
        name = r.Name,
        elementId = r.ElementId,
        status = r.Status,
        impact = r.Impact,
        needsConfirmation = r.NeedsConfirmation,
        summary = r.Summary,
        problems = r.Problems.Select(p => new { code = p.Code, message = p.Message }).ToList(),
        warnings = r.Warnings
    }).ToList();

    private static object Counts(IReadOnlyList<DatumItemResult> results)
    {
        var (ok, unchanged, refused, high) = DatumPlanner.Count(results);
        return new { items = results.Count, willChange = ok, unchanged, refused, highImpact = high };
    }

    // ── Create ──

    private sealed class CreatePlan
    {
        public DatumCreateSpec Spec = new();
        public DatumOptions Options = new();
        public List<DatumItemResult> Results = new();
        public List<DatumProblem> InputProblems = new();
        public Dictionary<long, DatumViewInfo> Views = new();
    }

    private static CreatePlan BuildCreate(Document doc, Dictionary<string, object?> args)
    {
        var plan = new CreatePlan { Options = Options(args) };
        args.TryGetValue("levels", out var levels);
        args.TryGetValue("grids", out var grids);
        args.TryGetValue("referencePlanes", out var planes);
        plan.Spec = DatumSpecParser.ParseCreate(levels, grids, planes, plan.InputProblems);
        plan.Views = DatumRevit.Views(doc, plan.Spec.ReferencePlanes.Select(p => p.ViewId));
        plan.Results = DatumPlanner.CheckCreate(plan.Spec, DatumRevit.Existing(doc), plan.Views, plan.Options);
        return plan;
    }

    public static McpToolResult Create(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var documentProblem = DocumentProblem(doc);
        if (documentProblem != null) return Fail(request, "validation_failed", documentProblem);

        var plan = BuildCreate(doc, request.Arguments);
        var inputProblems = plan.InputProblems.Select(p => new { index = p.Index, code = p.Code, message = p.Message }).ToList();
        if (plan.InputProblems.Count > 0 && plan.Spec.Count == 0)
            return Fail(request, "validation_failed", plan.InputProblems[0].Message, new { inputProblems });

        var canApply = DatumPlanner.CanApply(plan.Results, plan.Options, out var reason);
        if (plan.InputProblems.Count > 0 && plan.Options.AllOrNothing)
        {
            canApply = false;
            reason = $"{plan.InputProblems.Count} input item(s) could not be read, and mode is allOrNothing.";
        }
        var data = new { document = doc.Title, mode = plan.Options.AllOrNothing ? "allOrNothing" : "perItem", counts = Counts(plan.Results), items = Items(plan.Results), inputProblems, created = (object?)null };
        if (!apply)
        {
            sw.Stop();
            var (ok, _, refused, _) = DatumPlanner.Count(plan.Results);
            return new McpToolResult
            {
                RequestId = request.RequestId, Success = true,
                Message = $"Preview: {ok} datum(s) would be created, {refused} refused." + (canApply ? string.Empty : " " + reason),
                Data = data, DurationMs = sw.ElapsedMilliseconds
            };
        }
        if (!canApply) return Fail(request, "validation_failed", "Nothing created: " + reason, data);

        var created = new List<object>();
        var failed = new List<string>();
        try
        {
            using var tx = new Transaction(doc, "Revit MCP - Create Datums");
            tx.Start();
            foreach (var item in plan.Results.Where(r => r.Status == "ok"))
            {
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    var element = CreateOne(doc, plan, item);
                    sub.Commit();
                    item.ElementId = element.Id.Value;
                    item.Name = element.Name;
                    created.Add(new { kind = item.Kind, index = item.Index, id = element.Id.Value, name = element.Name });
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    item.Status = "refused";
                    item.Problems.Add(new DatumProblem { Kind = item.Kind, Index = item.Index, Code = "createFailed", Message = ex.Message });
                    failed.Add($"{item.Kind} {item.Index}: {ex.Message}");
                    if (plan.Options.AllOrNothing) { tx.RollBack(); throw new InvalidOperationException($"{item.Kind} #{item.Index} failed ({ex.Message}); nothing was written."); }
                }
            }
            if (created.Count == 0) tx.RollBack();
            else TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (Exception ex)
        {
            return Fail(request, "transaction_failed", ex.Message, new { document = doc.Title, items = Items(plan.Results), created = new List<object>() });
        }

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created.Count > 0,
            Status = created.Count == 0 ? "transaction_failed" : null,
            Message = $"Created {created.Count} datum(s) in '{doc.Title}'." + (failed.Count > 0 ? $" {failed.Count} failed." : string.Empty),
            Data = new { document = doc.Title, mode = plan.Options.AllOrNothing ? "allOrNothing" : "perItem", counts = Counts(plan.Results), items = Items(plan.Results), inputProblems, created },
            Warnings = failed,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static Element CreateOne(Document doc, CreatePlan plan, DatumItemResult item)
    {
        switch (item.Kind)
        {
            case DatumKinds.Level:
            {
                var spec = plan.Spec.Levels.First(l => l.Index == item.Index);
                var level = Level.Create(doc, GeometryUnits.MmToFt(spec.ElevationMm));
                level.Name = spec.Name;
                return level;
            }
            case DatumKinds.Grid:
            {
                var spec = plan.Spec.Grids.First(g => g.Index == item.Index);
                var grid = Grid.Create(doc, Line.CreateBound(DatumRevit.ToFeet(spec.Start), DatumRevit.ToFeet(spec.End)));
                if (!string.IsNullOrEmpty(spec.Name)) grid.Name = spec.Name!;
                return grid;
            }
            default:
            {
                var spec = plan.Spec.ReferencePlanes.First(p => p.Index == item.Index);
                var view = (View)doc.GetElement(new ElementId(spec.ViewId));
                var rp = doc.Create.NewReferencePlane(DatumRevit.ToFeet(spec.BubbleEnd), DatumRevit.ToFeet(spec.FreeEnd), view.ViewDirection, view);
                if (!string.IsNullOrEmpty(spec.Name)) rp.Name = spec.Name!;
                return rp;
            }
        }
    }

    public static string CreateApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return "Create datums (the document could not be resolved).";
        var plan = BuildCreate(target.Document, request.Arguments);
        return DatumPlanner.ApprovalText("Create", target.Document.Title, plan.Results, plan.Options) +
               (plan.InputProblems.Count > 0 ? $"\n{plan.InputProblems.Count} input item(s) could not be read." : string.Empty);
    }

    // ── Edit ──

    private sealed class EditPlan
    {
        public List<DatumEdit> Edits = new();
        public DatumOptions Options = new();
        public List<DatumItemResult> Results = new();
        public List<DatumProblem> InputProblems = new();
    }

    private static EditPlan BuildEdit(Document doc, Dictionary<string, object?> args)
    {
        var plan = new EditPlan { Options = Options(args) };
        args.TryGetValue("edits", out var raw);
        plan.Edits = DatumSpecParser.ParseEdits(raw, plan.InputProblems);
        var targets = new Dictionary<long, DatumTarget?>();
        foreach (var id in plan.Edits.Select(e => e.Id).Distinct()) targets[id] = DatumRevit.Target(doc, id);
        plan.Results = DatumPlanner.CheckEdits(plan.Edits, targets, DatumRevit.Existing(doc), plan.Options);
        return plan;
    }

    public static McpToolResult Edit(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var documentProblem = DocumentProblem(doc);
        if (documentProblem != null) return Fail(request, "validation_failed", documentProblem);

        var plan = BuildEdit(doc, request.Arguments);
        var inputProblems = plan.InputProblems.Select(p => new { index = p.Index, code = p.Code, message = p.Message }).ToList();
        if (plan.InputProblems.Count > 0 && plan.Edits.Count == 0)
            return Fail(request, "validation_failed", plan.InputProblems[0].Message, new { inputProblems });

        var canApply = DatumPlanner.CanApply(plan.Results, plan.Options, out var reason);
        if (plan.InputProblems.Count > 0 && plan.Options.AllOrNothing)
        {
            canApply = false;
            reason = $"{plan.InputProblems.Count} input item(s) could not be read, and mode is allOrNothing.";
        }
        var warnings = plan.Results.SelectMany(r => r.Warnings.Select(w => $"{r.Name} ({r.ElementId}): {w}")).Take(20).ToList();
        object Data(object? changed) => new
        {
            document = doc.Title, mode = plan.Options.AllOrNothing ? "allOrNothing" : "perItem", counts = Counts(plan.Results),
            items = Items(plan.Results), inputProblems, changed
        };
        if (!apply)
        {
            sw.Stop();
            var (ok, _, refused, high) = DatumPlanner.Count(plan.Results);
            return new McpToolResult
            {
                RequestId = request.RequestId, Success = true,
                Message = $"Preview: {ok} datum(s) would change ({high} high impact), {refused} refused." + (canApply ? string.Empty : " " + reason),
                Data = Data(null), Warnings = warnings, DurationMs = sw.ElapsedMilliseconds
            };
        }
        if (!canApply) return Fail(request, "validation_failed", "Nothing changed: " + reason, Data(null));

        var changed = new List<object>();
        var failed = new List<string>();
        try
        {
            using var tx = new Transaction(doc, "Revit MCP - Edit Datums");
            tx.Start();
            foreach (var item in plan.Results.Where(r => r.Status == "ok"))
            {
                var edit = plan.Edits.First(e => e.Index == item.Index && e.Id == item.ElementId);
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    ApplyOne(doc, edit);
                    sub.Commit();
                    changed.Add(new { id = edit.Id, kind = item.Kind, name = doc.GetElement(new ElementId(edit.Id))?.Name });
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    item.Status = "refused";
                    item.Problems.Add(new DatumProblem { Kind = item.Kind, Index = item.Index, Code = "editFailed", Message = ex.Message });
                    failed.Add($"{item.Kind} {edit.Id}: {ex.Message}");
                    if (plan.Options.AllOrNothing) { tx.RollBack(); throw new InvalidOperationException($"{item.Kind} {edit.Id} failed ({ex.Message}); nothing was written."); }
                }
            }
            if (changed.Count == 0) tx.RollBack();
            else TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (Exception ex)
        {
            return Fail(request, "transaction_failed", ex.Message, Data(new List<object>()));
        }

        sw.Stop();
        warnings.AddRange(failed);
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = changed.Count > 0,
            Status = changed.Count == 0 ? "transaction_failed" : null,
            Message = $"Changed {changed.Count} datum(s) in '{doc.Title}'." + (failed.Count > 0 ? $" {failed.Count} failed." : string.Empty),
            Data = Data(changed),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static void ApplyOne(Document doc, DatumEdit edit)
    {
        var element = doc.GetElement(new ElementId(edit.Id)) ?? throw new InvalidOperationException("the datum no longer exists");
        if (edit.ElevationMm is { } mm && element is Level level)
        {
            var feet = GeometryUnits.MmToFt(mm);
            if (Math.Abs(level.Elevation - feet) > 1e-9) level.Elevation = feet;
        }
        if (edit.MoveByMm is { } move && element is not Level && move.DistanceTo(new MmPoint(0, 0, 0)) > 0.05)
            ElementTransformUtils.MoveElement(doc, element.Id, DatumRevit.ToFeet(move));
        if (edit.NewName != null && edit.NewName != element.Name) element.Name = edit.NewName;
    }

    public static string EditApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return "Edit datums (the document could not be resolved).";
        var plan = BuildEdit(target.Document, request.Arguments);
        return DatumPlanner.ApprovalText("Edit", target.Document.Title, plan.Results, plan.Options) +
               (plan.InputProblems.Count > 0 ? $"\n{plan.InputProblems.Count} input item(s) could not be read." : string.Empty);
    }
}
