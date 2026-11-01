using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.FilledRegions;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.RoomDevices;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FilledRegions;

/// <summary>revit_preview_create_filled_regions (#135): every region a call would make, skipped sources and status codes, without writing.</summary>
public sealed class PreviewCreateFilledRegionsTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_filled_regions";

    public string Description =>
        "Previews revit_create_filled_regions without changing the model. Same arguments. Lists every region it would create " +
        "(view, type, loops, holes, area) with willCreate | invalid | skipped and a status code (loop_open, " +
        "loop_self_intersecting, view_not_supported, boundary_not_in_view_plane, type_not_found, ...), plus the sources a rule " +
        "skips and why. dryRun=true (default) also asks Revit to create them in a rolled-back transaction.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateFilledRegionsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_create_filled_regions (#135): regions from coordinates, rectangles, rooms, detail lines or rules.</summary>
public sealed class CreateFilledRegionsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_create_filled_regions";

    public string Description =>
        "Creates filled regions from specs and rules (max 500 per call). Requires approval. Each spec: view, type, lineStyle?, " +
        "parameters? and one shape: loops (closed point lists, first the outline, then holes), rectangle, room (host or linked), " +
        "detailLines or element, with paddingMm. Rules: source, shape, type or typeMap; unmatched values are skipped, never guessed. " +
        "One transaction, one undo; atomic=true undoes everything on any failure. Run revit_preview_create_filled_regions first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateFilledRegionsExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var parsed = FilledRegionSpecParser.ParseCreate(request.Arguments, out _);
        if (parsed == null) return null;
        var plan = RegionPlanner.Plan(target.Document, parsed, CancellationToken.None);
        var ready = plan.Regions.Where(r => r.IsReady).ToList();
        return FilledRegionSummaries.CreateCounted(ready.Count, ready.Select(r => r.View!.Id.Value).Distinct().Count(), plan.Skipped.Count);
    }
}

/// <summary>A region after its references and shape were read: ready for Revit, or with the code that says why not.</summary>
internal sealed class ResolvedRegion
{
    public RegionSpec Spec { get; init; } = null!;
    public View? View { get; set; }
    public ViewFrame? Frame { get; set; }
    public FilledRegionType? Type { get; set; }
    public ElementId? LineStyleId { get; set; }
    public FinishedShape? Shape { get; set; }

    /// <summary>willCreate, invalid or skipped; then created, failed, rolledBack or notAttempted.</summary>
    public string Status { get; set; } = RegionStatus.WillCreate;
    public string? Code { get; set; }
    public string? Reason { get; set; }
    public long? CreatedId { get; set; }
    public double? RevitAreaM2 { get; set; }
    public List<string> Warnings { get; } = new();

    public bool IsReady { get; set; } = true;

    public void Refuse(string code, string reason)
    {
        Status = Spec.RuleIndex != null && RegionCodes.IsSourceProblem(code) ? RegionStatus.Skipped : RegionStatus.Invalid;
        Code = code;
        Reason = reason;
        IsReady = false;
    }
}

/// <summary>What a request comes to: every region, whatever made it, and the sources rules left out.</summary>
internal sealed class RegionPlan
{
    public List<ResolvedRegion> Regions { get; } = new();
    public List<SkippedSource> Skipped { get; } = new();
    public Dictionary<string, int> UnmappedValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();
    public string? FatalCode { get; set; }
    public string? FatalReason { get; set; }
}

/// <summary>Rules become ordinary specs here, then every spec takes the same resolution path.</summary>
internal static class RegionPlanner
{
    public static RegionPlan Plan(Document doc, RegionCreateRequest request, CancellationToken cancellationToken)
    {
        var plan = new RegionPlan();
        var reader = new RegionShapeReader(doc);
        var specs = new List<RegionSpec>(request.Regions);

        foreach (var rule in request.Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ruleProblem = ExpandRule(doc, rule, specs, plan);
            if (ruleProblem != null)
            {
                var placeholder = new RegionSpec { Index = specs.Count, View = rule.View, RuleIndex = rule.Index };
                var refused = new ResolvedRegion { Spec = placeholder };
                refused.Status = RegionStatus.Invalid;
                refused.Code = ruleProblem.Value.Code;
                refused.Reason = $"Rule {rule.Index}: {ruleProblem.Value.Reason}";
                refused.IsReady = false;
                plan.Regions.Add(refused);
            }
        }

        if (specs.Count > FilledRegionSpecParser.MaxRegions)
        {
            plan.FatalCode = RegionCodes.TooManyRegions;
            plan.FatalReason = $"The request expands to {specs.Count} regions; the limit is {FilledRegionSpecParser.MaxRegions} per call. Narrow the rule or split the call.";
            return plan;
        }

        var shortCurveMm = FilledRegionSupport.ShortCurveMm(doc);
        var resolved = new List<ResolvedRegion>(specs.Count);
        foreach (var spec in specs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved.Add(Resolve(doc, spec, reader, shortCurveMm));
        }
        plan.Regions.InsertRange(0, resolved);

        foreach (var region in resolved.Where(r => r.Status == RegionStatus.Skipped))
            plan.Skipped.Add(new SkippedSource
            {
                RuleIndex = region.Spec.RuleIndex ?? 0,
                Id = region.Spec.SourceId ?? 0,
                Label = region.Spec.SourceLabel ?? string.Empty,
                Code = region.Code ?? string.Empty,
                Reason = region.Reason ?? string.Empty
            });
        return plan;
    }

    private static ResolvedRegion Resolve(Document doc, RegionSpec spec, RegionShapeReader reader, double shortCurveMm)
    {
        var region = new ResolvedRegion { Spec = spec };
        if (spec.Errors.Count > 0)
        {
            region.Refuse(RegionCodes.InvalidSpec, string.Join(" ", spec.Errors));
            return region;
        }

        region.View = FilledRegionSupport.ResolveView(doc, spec.View!, out var viewReason);
        if (region.View == null)
        {
            region.Refuse(RegionCodes.ViewNotFound, viewReason!);
            return region;
        }
        var unsupported = FilledRegionSupport.UnsupportedReason(doc, region.View);
        if (unsupported != null)
        {
            region.Refuse(RegionCodes.ViewNotSupported, unsupported);
            return region;
        }
        region.Frame = FilledRegionSupport.FrameOf(region.View);

        region.Type = FilledRegionSupport.ResolveType(doc, spec.Type!);
        if (region.Type == null || !FilledRegion.IsValidFilledRegionTypeId(doc, region.Type.Id))
        {
            region.Refuse(RegionCodes.TypeNotFound, $"No filled region type '{spec.Type}'. Types: {FilledRegionSupport.TypeNames(doc)}.");
            return region;
        }

        if (!string.IsNullOrWhiteSpace(spec.LineStyle))
        {
            region.LineStyleId = FilledRegionSupport.ResolveLineStyle(doc, spec.LineStyle!);
            if (region.LineStyleId == null)
            {
                region.Refuse(RegionCodes.LineStyleNotFound,
                    $"'{spec.LineStyle}' is not a line style a filled region can use (see revit_list_filled_region_types includePatterns=true).");
                return region;
            }
        }

        ShapeResult shape;
        try
        {
            shape = reader.Read(spec.Shape, region.View, region.Frame);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            shape = ShapeResult.Fail(RegionCodes.NoRoomBoundary, $"Revit could not read the shape: {ex.Message}");
        }
        region.Shape = RegionShapeFinisher.Finish(shape, spec.Shape.PaddingMm, shortCurveMm);
        region.Warnings.AddRange(region.Shape.Warnings);
        if (!region.Shape.IsValid) region.Refuse(region.Shape.Validation.Code!, region.Shape.Validation.Reason ?? string.Empty);
        return region;
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    /// <summary>Adds the rule's regions and skipped sources; returns a problem with the rule as a whole.</summary>
    private static (string Code, string Reason)? ExpandRule(Document doc, RegionRule rule, List<RegionSpec> specs, RegionPlan plan)
    {
        if (rule.Errors.Count > 0) return (RegionCodes.InvalidSpec, string.Join(" ", rule.Errors));

        var view = FilledRegionSupport.ResolveView(doc, rule.View!, out var viewReason);
        if (view == null) return (RegionCodes.ViewNotFound, viewReason!);
        var unsupported = FilledRegionSupport.UnsupportedReason(doc, view);
        if (unsupported != null) return (RegionCodes.ViewNotSupported, unsupported);

        var problem = CollectSources(doc, rule, view, plan.Warnings, out var sources);
        if (problem != null) return problem;

        var expansion = RuleExpander.Expand(rule, sources, specs.Count);
        specs.AddRange(expansion.Regions);
        plan.Skipped.AddRange(expansion.Skipped);
        foreach (var pair in expansion.UnmappedValues)
            plan.UnmappedValues[pair.Key] = (plan.UnmappedValues.TryGetValue(pair.Key, out var count) ? count : 0) + pair.Value;
        if (sources.Count == 0) plan.Warnings.Add($"Rule {rule.Index}: no source matches the selection.");
        return null;
    }

    private static (string Code, string Reason)? CollectSources(
        Document doc, RegionRule rule, View targetView, List<string> warnings, out List<RuleSourceItem> items)
    {
        items = new List<RuleSourceItem>();
        var source = RoomSourceService.ResolveSource(doc, rule.LinkInstanceId != 0 ? "link" : "host", rule.LinkInstanceId, out var linkError);
        if (source == null) return (RegionCodes.LinkNotFound, linkError ?? "The link was not found.");

        var elements = new List<(Element Element, string Label)>();
        var unitWarnings = new List<string>();
        if (rule.ElementIds.Count > 0)
        {
            foreach (var id in rule.ElementIds)
            {
                var element = source.Doc.GetElement(new ElementId(id));
                if (element == null) warnings.Add($"Rule {rule.Index}: element {id} does not exist in the {source.Label}.");
                else elements.Add((element, LabelOf(element)));
            }
        }
        else if (rule.SourceKind == RuleSourceKind.Rooms)
        {
            var filter = new RoomFilter { LevelName = rule.Level?.Trim() ?? string.Empty };
            var records = new RoomSourceService(source).LoadRooms(filter, unitWarnings, 5000);
            warnings.AddRange(unitWarnings.Select(w => $"Rule {rule.Index}: {w}"));

            if (!string.IsNullOrWhiteSpace(rule.InView))
            {
                var problem = RoomsInView(doc, rule, source, records, warnings, out records);
                if (problem != null) return problem;
            }
            else if (string.IsNullOrWhiteSpace(rule.Level) && targetView.GenLevel != null)
            {
                warnings.Add($"Rule {rule.Index}: no source.level or source.inView, so rooms of every level are drawn in '{targetView.Name}'.");
            }
            elements.AddRange(records.Select(r => (r.Element, $"Room {r.Number} {r.Name}".TrimEnd())));
        }
        else
        {
            var problem = CollectElements(doc, rule, source, elements, warnings);
            if (problem != null) return problem;
        }

        var reader = new ParameterReader();
        foreach (var (element, label) in elements)
        {
            if (rule.Filters.Count > 0)
            {
                var values = reader.ReadParameters(element.Document, element, new ParameterReadOptions());
                if (!ParameterFilterEvaluator.Passes(values, rule.Filters, ParameterReader.ReadIdentity(element.Document, element))) continue;
            }

            if (items.Count >= rule.Limit)
            {
                warnings.Add($"Rule {rule.Index}: stopped at source.limit = {rule.Limit}; more sources match.");
                break;
            }

            var found = true;
            string? value = null;
            if (rule.TypeMap != null) found = ReadMapValue(element, rule.TypeMap.Parameter, out value);
            items.Add(new RuleSourceItem { Id = element.Id.Value, Label = label, ParameterFound = found, ParameterValue = value });
        }
        return null;
    }

    /// <summary>Keeps the rooms Revit lists in the view. A plan listing none falls back to its level, with a warning.</summary>
    private static (string Code, string Reason)? RoomsInView(
        Document doc, RegionRule rule, RoomSource source, List<RoomRecord> records, List<string> warnings, out List<RoomRecord> kept)
    {
        kept = records;
        var inView = FilledRegionSupport.ResolveView(doc, rule.InView!, out var reason);
        if (inView == null) return (RegionCodes.ViewNotFound, $"source.inView: {reason}");

        var ids = IdsInView(doc, inView.Id, source, BuiltInCategory.OST_Rooms, warnings, rule.Index);
        kept = records.Where(r => ids.Contains(r.SourceId)).ToList();
        if (kept.Count == 0 && inView.GenLevel != null)
        {
            var level = inView.GenLevel;
            kept = records.Where(r => r.HostLevel != null && r.HostLevel.Id == level.Id).ToList();
            warnings.Add($"Rule {rule.Index}: Revit listed no rooms in view '{inView.Name}'; took the rooms on its level '{level.Name}' instead.");
        }
        return null;
    }

    private static (string Code, string Reason)? CollectElements(
        Document doc, RegionRule rule, RoomSource source, List<(Element, string)> elements, List<string> warnings)
    {
        ElementId? viewId = null;
        if (!string.IsNullOrWhiteSpace(rule.InView))
        {
            var inView = FilledRegionSupport.ResolveView(doc, rule.InView!, out var reason);
            if (inView == null) return (RegionCodes.ViewNotFound, $"source.inView: {reason}");
            viewId = inView.Id;
        }

        var collectors = new List<FilteredElementCollector>();
        if (rule.Categories.Count == 0)
        {
            collectors.Add(CollectorFor(doc, source, viewId));
        }
        else
        {
            var resolver = new CategoryResolver();
            foreach (var name in rule.Categories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var resolved = resolver.Resolve(source.Doc, name);
                if (resolved.Category == null) return (RegionCodes.CategoryNotFound, $"'{name}': {resolved.Message}");
                collectors.Add(CollectorFor(doc, source, viewId).OfCategoryId(resolved.Category.Id));
            }
        }

        var seen = new HashSet<long>();
        foreach (var collector in collectors)
            foreach (var element in collector.WhereElementIsNotElementType())
                if (seen.Add(element.Id.Value) && element.Category != null && element is not FilledRegion)
                    elements.Add((element, LabelOf(element)));
        return null;
    }

    private static FilteredElementCollector CollectorFor(Document doc, RoomSource source, ElementId? viewId)
    {
        if (viewId == null) return new FilteredElementCollector(source.Doc);
        return source.Link != null
            ? new FilteredElementCollector(doc, viewId, source.Link.Id)
            : new FilteredElementCollector(doc, viewId);
    }

    private static HashSet<long> IdsInView(Document doc, ElementId viewId, RoomSource source, BuiltInCategory category, List<string> warnings, int ruleIndex)
    {
        try
        {
            return new HashSet<long>(CollectorFor(doc, source, viewId).OfCategory(category).WhereElementIsNotElementType().ToElementIds().Select(id => id.Value));
        }
        catch (Exception ex)
        {
            warnings.Add($"Rule {ruleIndex}: Revit could not list the view's elements ({ex.Message}).");
            return new HashSet<long>();
        }
    }

    private static bool ReadMapValue(Element element, string parameterName, out string? value)
    {
        value = null;
        var document = element.Document;
        if (ElementIdentityParameters.IsIdentityName(parameterName))
            return ElementIdentityParameters.TryGetValue(parameterName, ParameterReader.ReadIdentity(document, element), out value!);

        var resolution = ParameterResolver.Resolve(document, element, new ParameterTarget { Name = parameterName, ExactMatch = true }, includeInstance: true, includeType: element is not ElementType);
        var parameter = ParameterResolver.Selected(resolution);
        if (parameter == null) return false;
        value = ParameterResolver.DisplayValue(parameter);
        return true;
    }

    private static string LabelOf(Element element)
    {
        if (element is Autodesk.Revit.DB.Architecture.Room room) return $"Room {room.Number} {room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()}".TrimEnd();
        string name;
        try { name = element.Name ?? string.Empty; }
        catch { name = string.Empty; }
        return $"{element.Category?.Name} {name}".Trim();
    }
}

internal static class CreateFilledRegionsExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var parsed = FilledRegionSpecParser.ParseCreate(request.Arguments, out var parseError);
        if (parsed == null) return Fail(request, RegionCodes.InvalidSpec, parseError!);

        var plan = RegionPlanner.Plan(doc, parsed, cancellationToken);
        if (plan.FatalCode != null) return Fail(request, plan.FatalCode, plan.FatalReason!);

        return apply
            ? Create(request, doc, parsed, plan, sw, cancellationToken)
            : Preview(request, doc, parsed, plan, sw, cancellationToken);
    }

    private static McpToolResult Preview(
        McpToolRequest request, Document doc, RegionCreateRequest parsed, RegionPlan plan, Stopwatch sw, CancellationToken cancellationToken)
    {
        var dryRun = ToolArguments.GetBool(request.Arguments, "dryRun", true);
        string? trialNote = null;
        var trialRan = false;
        var regions = plan.Regions;

        if (!dryRun) trialNote = "dryRun=false: only the static checks ran; Revit was not asked to try the creation.";
        else if (!regions.Any(r => r.IsReady)) trialNote = "No trial run: no region is ready to be created.";
        else if (doc.IsReadOnly || doc.IsModifiable) trialNote = "No trial run: the document is read-only or has an open transaction. Only the static checks ran.";
        else
        {
            using var transaction = new Transaction(doc, "Revit MCP - Create Filled Regions (preview, rolled back)");
            try
            {
                RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
                if (transaction.Start() == TransactionStatus.Started)
                {
                    trialRan = true;
                    RunAll(doc, regions, atomic: false, cancellationToken);
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

        var invalid = regions.Count(r => r.Status == RegionStatus.Invalid);
        var trialFailed = regions.Count(r => r.Status == RegionStatus.Failed);
        var willCreate = regions.Count(r => r.IsReady && r.Status != RegionStatus.Failed);
        var rejected = parsed.Atomic && (invalid > 0 || trialFailed > 0);
        foreach (var region in regions.Where(r => r.Status == RegionStatus.Created))
        {
            region.Status = RegionStatus.WillCreate;
            region.CreatedId = null;
        }

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Status = willCreate == 0 ? FirstCode(regions) : null,
            Message = $"Preview: {willCreate} region(s) would be created" +
                      (invalid > 0 ? $", {invalid} invalid" : string.Empty) +
                      (plan.Skipped.Count > 0 ? $", {plan.Skipped.Count} source(s) skipped" : string.Empty) +
                      (trialFailed > 0 ? $", {trialFailed} refused by Revit in the trial run" : string.Empty) + "." +
                      (rejected ? " With atomic=true the request would be rejected as a whole." : string.Empty),
            Data = new
            {
                willCreate,
                invalid,
                skipped = plan.Skipped.Count,
                trialFailed,
                atomic = parsed.Atomic,
                trialRun = trialRan,
                trialNote,
                summary = FilledRegionSummaries.Create(parsed),
                regions = regions.Select(r => Describe(r, parsed.IncludeLoops)).ToList(),
                skippedSources = DescribeSkipped(plan),
                unmappedValues = plan.UnmappedValues.Count > 0 ? plan.UnmappedValues : null
            },
            Warnings = plan.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult Create(
        McpToolRequest request, Document doc, RegionCreateRequest parsed, RegionPlan plan, Stopwatch sw, CancellationToken cancellationToken)
    {
        var regions = plan.Regions;
        var invalid = regions.Count(r => r.Status == RegionStatus.Invalid);
        if (parsed.Atomic && invalid > 0)
        {
            foreach (var region in regions.Where(r => r.IsReady)) region.Status = RegionStatus.NotAttempted;
            return Result(request, parsed, plan, sw, false, FirstCode(regions),
                $"Nothing was created: atomic=true and {invalid} region(s) are invalid. Fix them (see each region's code and reason), or pass atomic=false to create the rest.", null);
        }

        if (!regions.Any(r => r.IsReady))
        {
            var onlySkips = invalid == 0;
            return Result(request, parsed, plan, sw, onlySkips, onlySkips ? null : FirstCode(regions),
                onlySkips ? "Nothing to create: the rules matched no source that can be drawn." : "Nothing was created: no region spec is valid.", null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (committed, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Filled Regions", () =>
            RunAll(doc, regions, parsed.Atomic, cancellationToken));

        if (!committed)
        {
            foreach (var region in regions.Where(r => r.IsReady))
            {
                region.CreatedId = null;
                if (region.Status == RegionStatus.Created) region.Status = RegionStatus.RolledBack;
                else if (region.Status == RegionStatus.WillCreate) region.Status = RegionStatus.NotAttempted;
            }
            plan.Warnings.AddRange(diagnostics.ToErrorLines());
            return Result(request, parsed, plan, sw, false, RegionCodes.RevitRefused,
                parsed.Atomic
                    ? "Rolled back: atomic=true and Revit refused a region, so nothing was created. The model is unchanged."
                    : "The transaction did not commit, so no region was created. The model is unchanged.", diagnostics);
        }

        plan.Warnings.AddRange(diagnostics.FailureMessages);
        foreach (var region in regions.Where(r => r.CreatedId != null))
            if (doc.GetElement(new ElementId(region.CreatedId!.Value)) is { } created)
                region.RevitAreaM2 = FilledRegionSupport.RevitAreaM2(created);

        var made = regions.Count(r => r.Status == RegionStatus.Created);
        var failed = regions.Count(r => r.Status == RegionStatus.Failed);
        return Result(request, parsed, plan, sw, made > 0, made > 0 ? null : FirstCode(regions),
            $"Created {made} filled region(s)" +
            (failed > 0 ? $"; Revit refused {failed}" : string.Empty) +
            (invalid > 0 ? $"; {invalid} invalid" : string.Empty) +
            (plan.Skipped.Count > 0 ? $"; {plan.Skipped.Count} source(s) skipped" : string.Empty) + ". One Undo removes them.", diagnostics);
    }

    /// <summary>Each region in its own sub-transaction, so a refusal costs only that region. atomic=true abandons the batch instead.</summary>
    private static void RunAll(Document doc, List<ResolvedRegion> regions, bool atomic, CancellationToken cancellationToken)
    {
        foreach (var region in regions.Where(r => r.IsReady))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var created = CreateOne(doc, region);
                sub.Commit();
                region.CreatedId = created.Id.Value;
                region.Status = RegionStatus.Created;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                region.Status = RegionStatus.Failed;
                region.Code ??= RegionCodes.RevitRefused;
                region.Reason = ex.Message;
                region.CreatedId = null;
                if (atomic) throw new RegionBatchAbortedException($"atomic=true: region {region.Spec.Index} could not be created ({ex.Message}), so the whole batch was undone.");
            }
        }
    }

    private static FilledRegion CreateOne(Document doc, ResolvedRegion region)
    {
        var loops = FilledRegionSupport.ToCurveLoops(region.Shape!.Validation.Loops, region.Frame!);
        var created = FilledRegion.Create(doc, region.Type!.Id, region.View!.Id, loops);
        if (region.LineStyleId != null) created.SetLineStyleId(region.LineStyleId);
        foreach (var pair in region.Spec.Parameters)
        {
            var problem = FilledRegionSupport.SetParameter(doc, created, pair.Key, pair.Value);
            if (problem == null) continue;
            region.Code = RegionCodes.ParameterNotSettable;
            throw new InvalidOperationException(problem);
        }
        return created;
    }

    private static McpToolResult Result(
        McpToolRequest request, RegionCreateRequest parsed, RegionPlan plan, Stopwatch sw, bool success, string? status, string message, TransactionDiagnostics? diagnostics)
    {
        sw.Stop();
        var regions = plan.Regions;
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = success,
            Status = status,
            Message = message,
            Data = new
            {
                created = regions.Count(r => r.Status == RegionStatus.Created),
                failed = regions.Count(r => r.Status == RegionStatus.Failed),
                invalid = regions.Count(r => r.Status == RegionStatus.Invalid),
                skipped = plan.Skipped.Count,
                atomic = parsed.Atomic,
                newRegionIds = regions.Where(r => r.CreatedId != null).Select(r => r.CreatedId!.Value).ToList(),
                regions = regions.Select(r => Describe(r, parsed.IncludeLoops)).ToList(),
                skippedSources = DescribeSkipped(plan),
                unmappedValues = plan.UnmappedValues.Count > 0 ? plan.UnmappedValues : null,
                transaction = diagnostics
            },
            Warnings = plan.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Describe(ResolvedRegion region, bool includeLoops)
    {
        var spec = region.Spec;
        var validation = region.Shape?.Validation;
        var valid = validation is { IsValid: true };
        return new
        {
            index = spec.Index,
            rule = spec.RuleIndex,
            source = spec.SourceId == null ? null : new { id = spec.SourceId, label = spec.SourceLabel },
            status = region.Status,
            code = region.Code,
            reason = region.Reason,
            regionId = region.CreatedId,
            view = region.View == null ? null : new { id = region.View.Id.Value, name = region.View.Name, viewType = region.View.ViewType.ToString() },
            type = region.Type == null ? null : new { id = region.Type.Id.Value, name = region.Type.Name },
            shape = spec.Shape.Kind.ToString(),
            outlines = valid ? validation!.OutlineCount : (int?)null,
            holes = valid ? validation!.HoleCount : (int?)null,
            areaM2 = valid ? Math.Round(validation!.AreaMm2 / 1e6, 3) : (double?)null,
            revitAreaM2 = region.RevitAreaM2,
            extentUv = valid ? FilledRegionSupport.ExtentOf(validation!.Loops, region.Frame!) : null,
            loops = valid && includeLoops ? validation!.Loops.Select(l => FilledRegionSupport.DescribeLoop(l, region.Frame!)).ToList() : null,
            warnings = region.Warnings.Count > 0 ? region.Warnings : null
        };
    }

    private static object? DescribeSkipped(RegionPlan plan) =>
        plan.Skipped.Count == 0
            ? null
            : plan.Skipped.Select(s => new { rule = s.RuleIndex, id = s.Id, label = s.Label, code = s.Code, reason = s.Reason, value = s.Value }).ToList();

    private static string? FirstCode(IEnumerable<ResolvedRegion> regions) => regions.Select(r => r.Code).FirstOrDefault(c => c != null);

    private static McpToolResult Fail(McpToolRequest request, string status, string message) =>
        new() { RequestId = request.RequestId, Success = false, Status = status, Message = message };

    /// <summary>Thrown to abandon an atomic batch: escaping the transaction body is what rolls it back.</summary>
    private sealed class RegionBatchAbortedException : Exception
    {
        public RegionBatchAbortedException(string message) : base(message)
        {
        }
    }
}
