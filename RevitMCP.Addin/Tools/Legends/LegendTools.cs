using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Legends;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Legends;

/// <summary>The legend and keynote tools. App.OnStartup calls <see cref="Register"/> once.</summary>
public static class LegendKeynoteToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new ListLegendsTool());
        handler.RegisterTool(new PreviewPopulateLegendTool());
        handler.RegisterTool(new PopulateLegendTool());
        handler.RegisterTool(new RevitMCP.Addin.Tools.Keynotes.GetKeynotesTool());
        handler.RegisterTool(new RevitMCP.Addin.Tools.Keynotes.PreviewReloadKeynoteTableTool());
        handler.RegisterTool(new RevitMCP.Addin.Tools.Keynotes.ReloadKeynoteTableTool());
    }
}

/// <summary>revit_list_legends: the legend views with their legend components, text notes and detail items.</summary>
public sealed class ListLegendsTool : IRevitMcpTool
{
    public string Name => "revit_list_legends";

    public string Description =>
        "Lists the legend views: id, name, scale, sheets they are on, textNotes and detailItems counts, and their legend " +
        "components (id, typeId, family, type, category, viewDirection, detailLevel, position in printed mm). canSeed says " +
        "whether the legend holds a legend component that revit_populate_legend can copy. legend (name or id) lists one; " +
        "includeComponents=false gives counts only; maxComponents caps the list per legend (default 100).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var legends = LegendSupport.Legends(doc);
        var filter = ToolArguments.GetString(request.Arguments, "legend").Trim();
        if (filter.Length > 0)
        {
            var one = LegendSupport.FindLegend(legends, filter, out var error);
            if (one == null) return Task.FromResult(LegendSupport.Fail(request, error!));
            legends = new List<View> { one };
        }
        var includeComponents = ToolArguments.GetBool(request.Arguments, "includeComponents", true);
        var maxComponents = Math.Max(0, ToolArguments.GetInt(request.Arguments, "maxComponents", 100));

        var sheetsByView = new Dictionary<long, List<string>>();
        foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
        {
            if (doc.GetElement(vp.SheetId) is not ViewSheet sheet) continue;
            var key = vp.ViewId.Value;
            if (!sheetsByView.TryGetValue(key, out var list)) sheetsByView[key] = list = new List<string>();
            list.Add(sheet.SheetNumber);
        }

        var result = new List<object>();
        foreach (var legend in legends.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = LegendSupport.ReadContent(doc, legend);
            var components = content.Components;
            result.Add(new
            {
                id = legend.Id.Value,
                name = legend.Name,
                scale = legend.Scale,
                sheets = sheetsByView.TryGetValue(legend.Id.Value, out var sheets) ? sheets.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(),
                componentCount = components.Count,
                textNotes = content.TextNotes.Count,
                detailItems = content.DetailItems.Count,
                canSeed = components.Count > 0,
                components = includeComponents
                    ? components.Take(maxComponents).Select(c => LegendSupport.DescribeComponent(doc, legend, c)).ToList()
                    : null,
                componentsTruncated = includeComponents && components.Count > maxComponents ? true : (bool?)null
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = legends.Count == 0
                ? $"'{doc.Title}' has no legend views. Make one by hand (View > Legends > Legend): Revit's API cannot create a legend from nothing."
                : $"{legends.Count} legend view(s) in '{doc.Title}'.",
            Data = new { document = doc.Title, count = legends.Count, legends = result },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_preview_populate_legend: what revit_populate_legend would place, and what it would delete.</summary>
public sealed class PreviewPopulateLegendTool : IRevitMcpTool
{
    public string Name => "revit_preview_populate_legend";

    public string Description =>
        "Previews revit_populate_legend without changing the model. Same arguments. Per type: kind (component | " +
        "detailInstance | unsupported), status, cell and label; toDelete lists what clearExisting would delete; problems " +
        "lists what must be fixed first. dryRun=true (default) also tries the placement in a rolled-back transaction, so " +
        "types Revit refuses as legend components show here.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LegendPopulateExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_populate_legend: places one symbol per family type in an existing legend view, with a text label.</summary>
public sealed class PopulateLegendTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_populate_legend";

    public string Description =>
        "Fills an existing legend view with one symbol per family type and a text label beside each, in a grid. Requires " +
        "approval. Model types (devices, fixtures, ...) are copies of a legend component already in the legend (the seed) " +
        "switched to the type; detail item and generic annotation types are placed as ordinary instances. Revit's API " +
        "cannot create a legend view or a first legend component: place one by hand. clearExisting=true DELETES the " +
        "legend components, text notes and detail items already in the legend, except the seed. One transaction, one " +
        "undo; a type Revit refuses is skipped and reported. Run revit_preview_populate_legend first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LegendPopulateExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => LegendPopulateExecutor.Summary(uiapp, request);
}

/// <summary>Revit-side reads shared by the legend tools.</summary>
internal static class LegendSupport
{
    internal sealed class Content
    {
        public List<Element> Components { get; } = new();
        public List<TextNote> TextNotes { get; } = new();
        public List<FamilyInstance> DetailItems { get; } = new();
    }

    public static List<View> Legends(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate).ToList();

    public static View? FindLegend(List<View> legends, string nameOrId, out string? error)
    {
        error = null;
        if (long.TryParse(nameOrId, out var id))
        {
            var byId = legends.FirstOrDefault(l => l.Id.Value == id);
            if (byId != null) return byId;
        }
        var byName = legends.FirstOrDefault(l => l.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        error = legends.Count == 0
            ? $"The document has no legend views, so there is no legend '{nameOrId}'. Make one by hand (View > Legends > Legend)."
            : $"No legend view '{nameOrId}'{LegendPlanning.SuggestFrom(nameOrId, legends.Select(l => l.Name))}";
        return null;
    }

    public static Content ReadContent(Document doc, View legend)
    {
        var content = new Content();
        foreach (var e in new FilteredElementCollector(doc, legend.Id).WhereElementIsNotElementType())
        {
            if (e.OwnerViewId != legend.Id) continue;
            if (e.Category?.Id.Value == (long)BuiltInCategory.OST_LegendComponents) content.Components.Add(e);
            else if (e is TextNote note) content.TextNotes.Add(note);
            else if (e is FamilyInstance fi && fi.Symbol != null && IsDetailCategory(fi.Symbol.Category)) content.DetailItems.Add(fi);
        }
        content.Components.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return content;
    }

    public static bool IsDetailCategory(Category? category) =>
        category != null && (category.Id.Value == (long)BuiltInCategory.OST_DetailComponents
                             || category.Id.Value == (long)BuiltInCategory.OST_GenericAnnotation);

    /// <summary>How a type can get into a legend, and why not when it cannot.</summary>
    public static LegendPlacementKind KindOf(ElementType type, out string? reason)
    {
        reason = null;
        var category = type.Category;
        if (category == null)
        {
            reason = "The type has no category.";
            return LegendPlacementKind.Unsupported;
        }
        if (type is FamilySymbol symbol)
        {
            if (IsDetailCategory(category)) return LegendPlacementKind.DetailInstance;
            if (symbol.Family?.FamilyPlacementType == FamilyPlacementType.ViewBased)
            {
                reason = $"'{category.Name}' is a view-based annotation family (a tag or symbol); a legend cannot show it as a legend component.";
                return LegendPlacementKind.Unsupported;
            }
        }
        if (category.CategoryType == CategoryType.Model) return LegendPlacementKind.Component;
        reason = $"'{category.Name}' is not a model category; legend components show model types, and only detail item and generic annotation families can be placed as symbols.";
        return LegendPlacementKind.Unsupported;
    }

    public static ElementType? ComponentType(Document doc, Element component)
    {
        var p = component.get_Parameter(BuiltInParameter.LEGEND_COMPONENT);
        var id = p?.AsElementId();
        return id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id) as ElementType;
    }

    public static object DescribeComponent(Document doc, View legend, Element component)
    {
        var type = ComponentType(doc, component);
        var centre = Centre(component, legend);
        return new
        {
            id = component.Id.Value,
            typeId = type?.Id.Value,
            family = type?.FamilyName,
            type = type?.Name,
            category = type?.Category?.Name,
            viewDirection = component.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW)?.AsValueString(),
            detailLevel = component.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_DETAIL_LEVEL)?.AsValueString(),
            position = centre == null ? null : ToPaper(legend, centre)
        };
    }

    /// <summary>The centre of an element's box in the view, in model coordinates; null when Revit gives no box.</summary>
    public static XYZ? Centre(Element element, View view)
    {
        var box = element.get_BoundingBox(view);
        return box == null ? null : (box.Min + box.Max) * 0.5;
    }

    public static double[] ToPaper(View view, XYZ point) => new[]
    {
        LegendPlanning.ModelFeetToPaperMm(point.DotProduct(view.RightDirection), view.Scale),
        LegendPlanning.ModelFeetToPaperMm(point.DotProduct(view.UpDirection), view.Scale)
    };

    /// <summary>Printed mm to a model point on the view plane, at <paramref name="depth"/> along the view direction.</summary>
    public static XYZ ToModel(View view, double xMm, double yMm, double depth) =>
        view.RightDirection * LegendPlanning.PaperMmToModelFeet(xMm, view.Scale)
        + view.UpDirection * LegendPlanning.PaperMmToModelFeet(yMm, view.Scale)
        + view.ViewDirection * depth;

    public static LegendExistingItem Existing(Element element, View view, string kind)
    {
        var box = element.get_BoundingBox(view);
        double[]? min = box == null ? null : ToPaper(view, box.Min);
        double[]? max = box == null ? null : ToPaper(view, box.Max);
        return new LegendExistingItem
        {
            Id = element.Id.Value,
            Kind = kind,
            MinXMm = min == null ? null : Math.Min(min[0], max![0]),
            MinYMm = min == null ? null : Math.Min(min[1], max![1])
        };
    }

    public static McpToolResult Fail(McpToolRequest request, string message, string? status = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = status,
        Message = message,
        Errors = [message]
    };
}

internal static class LegendPopulateExecutor
{
    private sealed class PlannedType
    {
        public LegendTypeInfo Info { get; set; } = new();
        public ElementType Type { get; set; } = null!;
        public LegendCell? Cell { get; set; }
        public string? Label { get; set; }
        public string Status { get; set; } = "willPlace";
        public string? Reason { get; set; }
        public long? ElementId { get; set; }
        public long? LabelId { get; set; }
        public string? ViewDirection { get; set; }
        public bool LabelOverlaps { get; set; }
    }

    private sealed class Plan
    {
        public Document Doc { get; set; } = null!;
        public LegendPopulateRequest Request { get; set; } = new();
        public View? Legend { get; set; }
        public Element? Seed { get; set; }
        public List<string> Problems { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<PlannedType> Types { get; } = new();
        public List<LegendExistingItem> Existing { get; } = new();
        public List<LegendExistingItem> ToClear { get; set; } = new();
        public ElementId? TextTypeId { get; set; }
        public string? TextTypeName { get; set; }
        public double[] Origin { get; set; } = { 0, 0 };
        public double Depth { get; set; }
    }

    public static string Summary(UIApplication uiapp, McpToolRequest request)
    {
        var document = ToolArguments.GetString(request.Arguments, "document").Trim();
        try
        {
            var target = ToolDocument.Resolve(uiapp, request, out _);
            if (target != null)
            {
                var plan = BuildPlan(target.Document, request);
                var placeable = plan.Types.Count(t => t.Status == "willPlace");
                return LegendPlanning.ApprovalSummary(target.Document.Title, plan.Legend?.Name ?? plan.Request.Legend, placeable,
                    plan.Types.Count(t => t.Status == "willPlace" && t.Label != null), plan.ToClear, plan.Request.ClearExisting, plan.Problems.Count);
            }
        }
        catch
        {
            // Fall back to the arguments alone.
        }
        var errors = new List<string>();
        var parsed = LegendPlanning.Parse(request.Arguments, errors);
        return LegendPlanning.ApprovalSummary(document, parsed.Legend, parsed.Types.Count, 0, null, parsed.ClearExisting, errors.Count);
    }

    private static Plan BuildPlan(Document doc, McpToolRequest request)
    {
        var plan = new Plan { Doc = doc };
        plan.Request = LegendPlanning.Parse(request.Arguments, plan.Problems);
        var r = plan.Request;
        if (r.Legend.Length == 0) return plan;

        plan.Legend = LegendSupport.FindLegend(LegendSupport.Legends(doc), r.Legend, out var legendError);
        if (plan.Legend == null)
        {
            plan.Problems.Add(legendError!);
            return plan;
        }
        var legend = plan.Legend;

        var content = LegendSupport.ReadContent(doc, legend);
        if (r.SeedId != null)
        {
            plan.Seed = content.Components.FirstOrDefault(c => c.Id.Value == r.SeedId);
            if (plan.Seed == null)
                plan.Problems.Add($"seedId {r.SeedId} is not a legend component of '{legend.Name}'" +
                                  (content.Components.Count > 0 ? $"; its components are {string.Join(", ", content.Components.Take(10).Select(c => c.Id.Value))}." : "; it has none."));
        }
        else plan.Seed = content.Components.FirstOrDefault();

        plan.Existing.AddRange(content.Components.Select(c => LegendSupport.Existing(c, legend, "component")));
        plan.Existing.AddRange(content.TextNotes.Select(n => LegendSupport.Existing(n, legend, "textNote")));
        plan.Existing.AddRange(content.DetailItems.Select(d => LegendSupport.Existing(d, legend, "detailItem")));
        plan.ToClear = r.ClearExisting ? LegendPlanning.ToClear(plan.Existing, plan.Seed?.Id.Value) : new List<LegendExistingItem>();

        // The types the model offers.
        var types = new Dictionary<long, ElementType>();
        var available = new List<LegendTypeInfo>();
        foreach (var type in new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>())
        {
            if (type.Category == null) continue;
            var kind = LegendSupport.KindOf(type, out var reason);
            types[type.Id.Value] = type;
            available.Add(new LegendTypeInfo
            {
                Id = type.Id.Value,
                Family = type.FamilyName ?? string.Empty,
                Type = type.Name,
                Category = type.Category.Name,
                Kind = kind,
                UnsupportedReason = reason
            });
        }

        string? categoryName = null;
        if (r.Category != null)
        {
            var resolved = new CategoryResolver().Resolve(doc, r.Category);
            if (resolved.Category == null)
            {
                plan.Problems.Add(resolved.Message + (resolved.Suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", resolved.Suggestions)}?" : string.Empty));
                return plan;
            }
            categoryName = resolved.Category.Name;
        }

        var selected = LegendPlanning.Select(r, available, categoryName, plan.Problems);
        if (r.Label == LegendLabelMode.Parameter)
        {
            foreach (var info in selected) info.LabelParameterValue = ParameterText(types[info.Id], r.LabelParameter!);
            selected = LegendPlanning.Sort(selected, r);
        }

        foreach (var info in selected)
        {
            var planned = new PlannedType { Info = info, Type = types[info.Id], Label = LegendPlanning.LabelText(r, info) };
            if (info.Kind == LegendPlacementKind.Unsupported)
            {
                planned.Status = "unsupported";
                planned.Reason = info.UnsupportedReason;
            }
            else if (info.Kind == LegendPlacementKind.Component && plan.Seed == null && r.SeedId == null)
            {
                planned.Status = "needsSeed";
                planned.Reason = "A model type is shown by copying a legend component, and the legend has none.";
            }
            if (r.Label == LegendLabelMode.Parameter && planned.Label == null && planned.Status == "willPlace")
                plan.Warnings.Add($"'{info.Display}' has no value for '{r.LabelParameter}'; it gets no label.");
            plan.Types.Add(planned);
        }

        if (plan.Types.Any(t => t.Status == "needsSeed"))
            plan.Problems.Add($"Legend '{legend.Name}' has no legend component to copy for the model types. Place one by hand " +
                              "(Annotate > Component > Legend Component, any type) and run again: Revit's API cannot create the first one.");
        if (selected.Count > 0 && plan.Types.All(t => t.Status == "unsupported"))
            plan.Problems.Add("None of the selected types can be shown in a legend (see each type's reason).");

        // Labels.
        if (r.Label != LegendLabelMode.None)
        {
            plan.TextTypeId = ResolveTextType(doc, r.TextType, plan.Problems, out var textTypeName);
            plan.TextTypeName = textTypeName;
        }

        // The grid.
        var seedCentre = plan.Seed == null ? null : LegendSupport.Centre(plan.Seed, legend);
        var depthPoint = seedCentre ?? legend.Origin;
        plan.Depth = depthPoint.DotProduct(legend.ViewDirection);
        var kept = plan.Existing.Where(e => !plan.ToClear.Any(c => c.Id == e.Id));
        plan.Origin = r.Origin ?? LegendPlanning.DefaultOrigin(seedCentre == null ? null : LegendSupport.ToPaper(legend, seedCentre), kept, r.RowSpacingMm);
        var placeable = plan.Types.Where(t => t.Status == "willPlace").ToList();
        var cells = LegendPlanning.Cells(placeable.Count, r.Columns, r.RowSpacingMm, r.ColumnSpacingMm, plan.Origin, r.TextOffsetMm);
        for (var i = 0; i < placeable.Count; i++) placeable[i].Cell = cells[i];
        return plan;
    }

    private static string? ParameterText(Element type, string name)
    {
        var p = type.LookupParameter(name);
        if (p == null || !p.HasValue) return null;
        return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
    }

    private static ElementId? ResolveTextType(Document doc, string? nameOrId, List<string> problems, out string? name)
    {
        name = null;
        var all = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
        if (nameOrId != null)
        {
            var match = long.TryParse(nameOrId, out var id) ? all.FirstOrDefault(t => t.Id.Value == id) : null;
            match ??= all.FirstOrDefault(t => t.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                problems.Add($"textType: no text note type '{nameOrId}'{LegendPlanning.SuggestFrom(nameOrId, all.Select(t => t.Name))}");
                return null;
            }
            name = match.Name;
            return match.Id;
        }
        var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var chosen = defaultId != null && defaultId != ElementId.InvalidElementId ? doc.GetElement(defaultId) as TextNoteType : all.FirstOrDefault();
        if (chosen == null)
        {
            problems.Add("The document has no text note type for the labels; use label=none.");
            return null;
        }
        name = chosen.Name;
        return chosen.Id;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var plan = BuildPlan(doc, request);
        var r = plan.Request;

        if (!apply)
        {
            string? trialNote = null;
            var ready = plan.Problems.Count == 0 && plan.Types.Any(t => t.Status == "willPlace");
            if (!r.DryRun) trialNote = "dryRun=false: only the static checks ran; Revit was not asked to place anything.";
            else if (!ready) trialNote = "No trial run: fix the problems first.";
            else if (doc.IsReadOnly || doc.IsModifiable) trialNote = "No trial run: the document is read-only or has an open transaction.";
            else
            {
                using var tx = new Transaction(doc, "Revit MCP - Populate legend (preview, rolled back)");
                try
                {
                    RollBackOnErrorPreprocessor.Attach(tx, new List<string>());
                    if (tx.Start() == TransactionStatus.Started)
                    {
                        Run(plan, cancellationToken);
                        trialNote = "Trial run rolled back: the model is unchanged. Element ids below are from the trial.";
                    }
                    else trialNote = "No trial run: Revit did not start a transaction.";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    trialNote = $"The trial run could not complete ({ex.Message}); only the static checks are reliable.";
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                }
            }

            sw.Stop();
            var willPlace = plan.Types.Count(t => t.Status is "willPlace" or "placed");
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = plan.Problems.Count == 0
                    ? $"Preview: {willPlace} symbol(s) to place in '{plan.Legend?.Name}'" +
                      $"{(plan.Types.Count(t => t.Status == "refused") is var refused and > 0 ? $", {refused} refused by Revit" : string.Empty)}" +
                      $"{(plan.Types.Count(t => t.Status == "unsupported") is var unsupported and > 0 ? $", {unsupported} not supported in a legend" : string.Empty)}" +
                      $"{(plan.ToClear.Count > 0 ? $"; clearExisting deletes {plan.ToClear.Count} element(s)" : string.Empty)}."
                    : $"Preview: {plan.Problems.Count} problem(s) to fix before populating the legend.",
                Data = Describe(plan, ready: plan.Problems.Count == 0, trialNote, preview: true),
                Warnings = plan.Warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (plan.Problems.Count > 0)
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "invalid_arguments",
                Message = $"Nothing was changed: {plan.Problems.Count} problem(s). {string.Join(" ", plan.Problems.Take(3))}",
                Errors = plan.Problems,
                Warnings = plan.Warnings,
                DurationMs = sw.ElapsedMilliseconds
            };

        cancellationToken.ThrowIfCancellationRequested();
        int placed;
        try
        {
            using var tx = new Transaction(doc, "Revit MCP - Populate legend");
            RollBackOnErrorPreprocessor.Attach(tx, plan.Warnings);
            tx.Start();
            Run(plan, cancellationToken);
            placed = plan.Types.Count(t => t.Status == "placed");
            if (placed == 0)
            {
                tx.RollBack();
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "transaction_failed",
                    Message = "Nothing was changed: Revit refused every type. " +
                              string.Join(" ", plan.Types.Where(t => t.Reason != null).Take(3).Select(t => $"'{t.Info.Display}': {t.Reason}")),
                    Data = Describe(plan, ready: false, null, preview: false),
                    Warnings = plan.Warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
            TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LegendSupport.Fail(request, $"Populating the legend failed and nothing was changed: {ex.Message}", "transaction_failed");
        }

        sw.Stop();
        var refusedCount = plan.Types.Count(t => t.Status == "refused");
        var unsupportedCount = plan.Types.Count(t => t.Status == "unsupported");
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{placed} symbol(s) placed in '{plan.Legend!.Name}'" +
                      $"{(plan.ToClear.Count > 0 ? $", {plan.ToClear.Count} earlier element(s) deleted" : string.Empty)}" +
                      $"{(refusedCount > 0 ? $"; Revit refused {refusedCount}" : string.Empty)}" +
                      $"{(unsupportedCount > 0 ? $"; {unsupportedCount} not supported in a legend" : string.Empty)}. One undo removes them.",
            Data = Describe(plan, ready: true, null, preview: false),
            Warnings = plan.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>Deletes what clearExisting removes, then places every ready type, each in its own SubTransaction.</summary>
    private static void Run(Plan plan, CancellationToken cancellationToken)
    {
        var doc = plan.Doc;
        var legend = plan.Legend!;
        if (plan.ToClear.Count > 0)
        {
            var ids = plan.ToClear.Select(c => new ElementId(c.Id)).Where(id => doc.GetElement(id) != null).ToList();
            if (ids.Count > 0) doc.Delete(ids);
        }

        foreach (var planned in plan.Types.Where(t => t.Status == "willPlace" && t.Cell != null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cell = planned.Cell!;
            var targetPoint = LegendSupport.ToModel(legend, cell.XMm, cell.YMm, plan.Depth);
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var element = planned.Info.Kind == LegendPlacementKind.Component
                    ? PlaceComponent(doc, legend, plan.Seed!, planned.Type, targetPoint)
                    : PlaceDetail(doc, legend, (FamilySymbol)planned.Type, targetPoint, plan.Request.TextOffsetMm);
                Recentre(doc, legend, element, targetPoint);
                planned.ElementId = element.Id.Value;
                planned.ViewDirection = element.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW)?.AsValueString();

                if (planned.Label != null && plan.TextTypeId != null)
                {
                    var labelPoint = LegendSupport.ToModel(legend, cell.LabelXMm, cell.LabelYMm, plan.Depth);
                    var options = new TextNoteOptions(plan.TextTypeId)
                    {
                        HorizontalAlignment = HorizontalTextAlignment.Left,
                        VerticalAlignment = VerticalTextAlignment.Middle
                    };
                    var note = TextNote.Create(doc, legend.Id, labelPoint, planned.Label, options);
                    planned.LabelId = note.Id.Value;
                    var box = element.get_BoundingBox(legend);
                    if (box != null)
                    {
                        var right = Math.Max(LegendSupport.ToPaper(legend, box.Min)[0], LegendSupport.ToPaper(legend, box.Max)[0]);
                        planned.LabelOverlaps = right > cell.LabelXMm - 0.5;
                    }
                }
                sub.Commit();
                planned.Status = "placed";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                planned.Status = "refused";
                planned.Reason = ex.Message;
                planned.ElementId = null;
                planned.LabelId = null;
            }
        }
        foreach (var overlap in plan.Types.Where(t => t.LabelOverlaps))
            plan.Warnings.Add($"The label of '{overlap.Info.Display}' overlaps its symbol; raise textOffsetMm.");
    }

    /// <summary>Copies the seed legend component to the target and switches it to the type (LEGEND_COMPONENT).</summary>
    private static Element PlaceComponent(Document doc, View legend, Element seed, ElementType type, XYZ target)
    {
        var seedCentre = LegendSupport.Centre(seed, legend) ?? target;
        var copies = ElementTransformUtils.CopyElement(doc, seed.Id, target - seedCentre);
        var copy = copies.Select(doc.GetElement)
                       .FirstOrDefault(e => e?.Category?.Id.Value == (long)BuiltInCategory.OST_LegendComponents)
                   ?? throw new InvalidOperationException("Revit copied the seed legend component but returned no legend component.");
        var parameter = copy.get_Parameter(BuiltInParameter.LEGEND_COMPONENT)
                        ?? throw new InvalidOperationException("The copied legend component has no Component Type parameter.");
        if (parameter.IsReadOnly) throw new InvalidOperationException("The Component Type parameter of the copy is read-only.");
        if (parameter.AsElementId() != type.Id && !parameter.Set(type.Id))
            throw new InvalidOperationException("Revit does not accept this type as a legend component.");
        doc.Regenerate();
        if (parameter.AsElementId() != type.Id)
            throw new InvalidOperationException("Revit kept another type on the legend component; it does not show this type in a legend.");
        return copy;
    }

    /// <summary>Places a detail item or generic annotation type as a view-specific instance.</summary>
    private static Element PlaceDetail(Document doc, View legend, FamilySymbol symbol, XYZ target, double textOffsetMm)
    {
        if (!symbol.IsActive)
        {
            symbol.Activate();
            doc.Regenerate();
        }
        if (symbol.Family?.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail)
        {
            // A line-based detail item: a horizontal piece ending 2 mm before the label.
            var halfMm = Math.Max(1, Math.Abs(textOffsetMm) - 2);
            var half = legend.RightDirection * LegendPlanning.PaperMmToModelFeet(halfMm, legend.Scale);
            return doc.Create.NewFamilyInstance(Line.CreateBound(target - half, target + half), symbol, legend);
        }
        return doc.Create.NewFamilyInstance(target, symbol, legend);
    }

    /// <summary>Moves the element so the centre of its box in the view lands on the target (a new type has another size).</summary>
    private static void Recentre(Document doc, View legend, Element element, XYZ target)
    {
        var centre = LegendSupport.Centre(element, legend);
        if (centre == null) return;
        var offset = target - centre;
        var move = legend.RightDirection * offset.DotProduct(legend.RightDirection) + legend.UpDirection * offset.DotProduct(legend.UpDirection);
        if (move.GetLength() > 1e-6) ElementTransformUtils.MoveElement(doc, element.Id, move);
    }

    private static object Describe(Plan plan, bool ready, string? trialNote, bool preview) => new
    {
        ready = preview ? ready : (bool?)null,
        problems = plan.Problems,
        trialNote,
        document = plan.Doc.Title,
        legend = plan.Legend == null ? null : new { id = plan.Legend.Id.Value, name = plan.Legend.Name, scale = plan.Legend.Scale },
        seedId = plan.Seed?.Id.Value,
        origin = new { xMm = plan.Origin[0], yMm = plan.Origin[1] },
        units = "printed mm (model mm divided by the legend's scale)",
        textType = plan.TextTypeName,
        clearExisting = plan.Request.ClearExisting,
        toDelete = plan.Request.ClearExisting
            ? plan.ToClear.Select(c => new { id = c.Id, kind = c.Kind }).ToList()
            : null,
        types = plan.Types.Select(t => new
        {
            typeId = t.Info.Id,
            family = t.Info.Family,
            type = t.Info.Type,
            category = t.Info.Category,
            kind = t.Info.Kind switch
            {
                LegendPlacementKind.Component => "component",
                LegendPlacementKind.DetailInstance => "detailInstance",
                _ => "unsupported"
            },
            status = t.Status,
            reason = t.Reason,
            elementId = t.ElementId,
            label = t.Label,
            labelId = t.LabelId,
            viewDirection = t.ViewDirection,
            cell = t.Cell == null ? null : new { row = t.Cell.Row, column = t.Cell.Column, xMm = t.Cell.XMm, yMm = t.Cell.YMm, labelXMm = t.Cell.LabelXMm }
        }).ToList()
    };
}
