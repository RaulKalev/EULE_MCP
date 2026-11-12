using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Parameters;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>The family contents tool. App.OnStartup calls <see cref="Register"/> once.</summary>
public static class FamilyContentsToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new GetFamilyContentsTool());
    }
}

/// <summary>revit_get_family_contents: reads what is inside a family (loaded in the project, or a .rfa file), never saving it.</summary>
public sealed class GetFamilyContentsTool : IRevitMcpTool
{
    public string Name => "revit_get_family_contents";

    public string Description =>
        "Reads what is inside a family without changing it: family (a family loaded in the project, opened as a copy) or " +
        "path (a .rfa file). Never saves; closes what it opened. Millimetres on the family's reference level, degrees. " +
        "Returns category, the view read, parameters (dataType, group, instance, shared+guid, formula, isReporting), types " +
        "with values, curves (line|arc|circle: points, lineStyle, visibleParameter), filledRegions (loops, type, lineStyle), " +
        "textNotes, nestedInstances (family, type, position, rotation, linkedParameters), the lineStyles and " +
        "filledRegionTypes used. include picks sections; limit caps each list (default 500). asSpec=true adds spec: the " +
        "arguments of revit_create_detail_family that copy it (add savePath), with specOmitted for what does not fit.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(FamilyContentsExecutor.Execute(uiapp, request, cancellationToken));
}

internal static class FamilyContentsExecutor
{
    private const double MmPerFoot = FamilyContentsGeometry.MmPerFoot;
    private const int MaxNestedFamiliesOpened = 50;

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var args = request.Arguments;
        var familyName = ToolArguments.GetString(args, "family").Trim();
        var path = ToolArguments.GetString(args, "path").Trim();
        var asSpec = ToolArguments.GetBool(args, "asSpec", false);
        var readNested = ToolArguments.GetBool(args, "readNested", true);
        var limit = FamilyContentsView.ClampLimit(ToolArguments.GetInt(args, "limit", FamilyContentsView.DefaultLimit));
        var argumentErrors = new List<string>();
        var include = FamilyContentsView.ParseInclude(ToolArguments.GetStringArray(args, "include"), argumentErrors);

        if (familyName.Length == 0 && path.Length == 0)
            argumentErrors.Add("Give family (the name of a family loaded in the project) or path (a .rfa file).");
        else if (familyName.Length > 0 && path.Length > 0)
            argumentErrors.Add("Give family or path, not both.");
        if (argumentErrors.Count > 0) return Invalid(request, argumentErrors);

        // The project: needed to open a loaded family, optional for a file (its patterns check the spec).
        Document? project;
        var documentGiven = ToolArguments.GetString(args, RevitDocumentResolver.ArgumentName).Trim().Length > 0;
        if (familyName.Length > 0 || documentGiven)
        {
            var target = ToolDocument.Resolve(uiapp, request, out var failure);
            if (target == null) return failure!;
            project = target.Document;
        }
        else
        {
            project = RevitDocumentResolver.Resolve(uiapp, request, out _)?.Document;
        }

        var warnings = new List<string>();
        Document? familyDoc = null;
        var owned = false;
        string source;
        string? filePath = null;
        try
        {
            if (familyName.Length > 0)
            {
                var opened = OpenLoaded(uiapp, project!, familyName);
                if (opened.Document == null) return Fail(request, opened.Error!, opened.Status);
                familyDoc = opened.Document;
                owned = opened.Owned;
                source = opened.Source;
                familyName = opened.Name;
            }
            else
            {
                var opened = OpenFile(uiapp, path, out owned, out filePath, out var error, out var status);
                if (opened == null) return Fail(request, error!, status);
                familyDoc = opened;
                source = owned ? "file" : "openDocument";
                familyName = TitleName(opened);
            }

            if (!familyDoc.IsFamilyDocument)
                return Fail(request, $"'{familyDoc.Title}' is not a family document.", "invalid_arguments");

            var snapshot = Read(familyDoc, familyName, readNested, warnings, cancellationToken, out var view);
            var described = FamilyContentsView.Describe(snapshot, include, limit);

            var data = new JObject
            {
                ["family"] = snapshot.FamilyName,
                ["source"] = source,
                ["path"] = filePath,
                ["document"] = project?.Title,
                ["category"] = snapshot.Category,
                ["isDetailItem"] = snapshot.IsDetailItem,
                ["isGenericAnnotation"] = snapshot.IsGenericAnnotation,
                ["view"] = view == null ? null : new JObject { ["name"] = view.Name, ["viewType"] = view.ViewType.ToString() },
                ["currentType"] = snapshot.CurrentType.Length > 0 ? snapshot.CurrentType : null
            };
            data.Merge(described);

            if (asSpec)
            {
                var projectDoc = project != null && !project.IsFamilyDocument ? project : null;
                var linePatterns = projectDoc == null ? null : PatternNames<LinePatternElement>(projectDoc);
                var fillPatterns = projectDoc == null ? null : PatternNames<FillPatternElement>(projectDoc);
                var built = FamilyContentsSpecBuilder.Build(snapshot, linePatterns, fillPatterns);
                if (projectDoc == null)
                    built.Notes.Add("No project was open, so pattern names in the spec were not checked against one.");
                data["spec"] = built.Spec;
                data["specOmitted"] = new JArray(built.Omitted.Take(100));
                if (built.Omitted.Count > 100) data["specOmittedCount"] = built.Omitted.Count;
                data["specNotes"] = new JArray(built.Notes);
                data["specProblems"] = new JArray(built.Problems);
            }

            var counts = (JObject)described["counts"]!;
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"'{snapshot.FamilyName}' ({(snapshot.Category.Length > 0 ? snapshot.Category : "no category")}): " +
                          $"{counts["parameters"]} parameter(s), {counts["types"]} type(s), {counts["curves"]} curve(s), " +
                          $"{counts["filledRegions"]} filled region(s), {counts["textNotes"]} text note(s), {counts["nestedInstances"]} nested instance(s)." +
                          (described["truncated"] != null ? $" Lists longer than {limit} were cut (truncated)." : string.Empty) +
                          (owned ? " Read from a copy that was closed without saving." : " Read from the document already open in Revit, which was left open and unchanged."),
                Data = data,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            return Fail(request, "Reading the family was cancelled.", "cancelled");
        }
        catch (Exception ex)
        {
            return Fail(request, $"Reading the family failed: {ex.Message}", "failed", warnings);
        }
        finally
        {
            if (owned)
            {
                try { familyDoc?.Close(false); } catch { /* already closed */ }
            }
        }
    }

    // ── Opening ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A family loaded in the project, opened as an independent copy (Document.EditFamily). When Revit refuses because
    /// the family is already open in the Family Editor, or the project does not have it but a family document of that
    /// name is open, that open document is read instead and left open.
    /// </summary>
    private sealed class LoadedFamilyDocument
    {
        public Document? Document { get; set; }
        public bool Owned { get; set; }
        public string Source { get; set; } = "project";
        public string Name { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string Status { get; set; } = "failed";
    }

    private static LoadedFamilyDocument OpenLoaded(UIApplication uiapp, Document project, string name)
    {
        var wanted = name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(name) : name;
        var result = new LoadedFamilyDocument { Name = wanted };
        var loaded = FamilyInventory.LoadedByName(project);
        var openDoc = OpenFamilyDocuments(uiapp).FirstOrDefault(d => string.Equals(TitleName(d), wanted, StringComparison.OrdinalIgnoreCase));

        LoadedFamilyDocument UseOpen(string because)
        {
            if (openDoc == null) { result.Error = because; return result; }
            result.Document = openDoc;
            result.Source = "openDocument";
            result.Name = TitleName(openDoc);
            return result;
        }

        if (!loaded.TryGetValue(wanted, out var family))
        {
            var close = FamilyContentsView.Suggest(wanted, loaded.Keys);
            result.Status = "not_found";
            return UseOpen($"'{wanted}' is not a family loaded in '{project.Title}'" +
                           $"{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}?" : ".")} " +
                           "revit_list_families lists them; pass path for a .rfa file.");
        }
        result.Name = family.Name;

        if (family.IsInPlace)
        {
            result.Status = "invalid_arguments";
            result.Error = $"'{family.Name}' is an in-place family; it has no family document to read.";
            return result;
        }
        if (!family.IsEditable)
        {
            result.Status = "invalid_arguments";
            return UseOpen($"Revit does not let '{family.Name}' be opened for editing (a system or protected family), so its contents cannot be read.");
        }
        if (project.IsModifiable)
        {
            result.Error = $"'{project.Title}' has an open transaction; finish it before reading a family.";
            return result;
        }

        try
        {
            var doc = project.EditFamily(family);
            if (doc == null) return UseOpen($"Revit could not open '{family.Name}'.");
            result.Document = doc;
            result.Owned = true;
            return result;
        }
        catch (Exception ex)
        {
            // Revit refuses a family that is already open in the Family Editor: read that document instead.
            return UseOpen($"Revit could not open '{family.Name}': {ex.Message}");
        }
    }

    /// <summary>A .rfa file: the document already open in Revit when there is one, else opened in the background.</summary>
    private static Document? OpenFile(UIApplication uiapp, string rawPath, out bool owned, out string? fullPath, out string? error, out string status)
    {
        owned = false;
        fullPath = null;
        error = null;
        status = "invalid_arguments";
        var policyError = new FilePathPolicy().ValidateRead(rawPath);
        if (policyError != null) { error = $"path: {policyError}"; return null; }
        var (full, normalizeError) = FilePathPolicy.NormalizePath(rawPath);
        if (full == null) { error = $"path: {normalizeError}"; return null; }
        if (!full.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) { error = $"path '{full}' is not a family file (.rfa)."; return null; }
        if (!File.Exists(full)) { error = $"'{full}' does not exist."; status = "not_found"; return null; }
        fullPath = full;

        var normalized = DocumentTargetMatcher.NormalizePath(full);
        var open = OpenFamilyDocuments(uiapp).FirstOrDefault(d =>
            !string.IsNullOrEmpty(d.PathName) && DocumentTargetMatcher.NormalizePath(d.PathName) == normalized);
        if (open != null) return open;

        try
        {
            var doc = uiapp.Application.OpenDocumentFile(full);
            owned = true;
            return doc;
        }
        catch (Exception ex)
        {
            error = $"Revit could not open '{full}': {ex.Message}";
            status = "failed";
            return null;
        }
    }

    private static IEnumerable<Document> OpenFamilyDocuments(UIApplication uiapp)
    {
        foreach (Document doc in uiapp.Application.Documents)
            if (doc.IsFamilyDocument) yield return doc;
    }

    private static string TitleName(Document doc) =>
        doc.Title.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(doc.Title) : doc.Title;

    private static List<string> PatternNames<T>(Document doc) where T : Element =>
        new FilteredElementCollector(doc).OfClass(typeof(T)).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ── Reading ──────────────────────────────────────────────────────────────

    private static FamilyContentsSnapshot Read(Document doc, string familyName, bool readNested, List<string> warnings,
        CancellationToken cancellationToken, out View? view)
    {
        var snapshot = new FamilyContentsSnapshot { FamilyName = familyName };
        var category = Safe(() => doc.OwnerFamily?.FamilyCategory, null, warnings, "family category");
        snapshot.Category = category?.Name ?? string.Empty;
        snapshot.IsDetailItem = category != null && category.Id.Value == (long)BuiltInCategory.OST_DetailComponents;
        snapshot.IsGenericAnnotation = category != null && category.Id.Value == (long)BuiltInCategory.OST_GenericAnnotation;
        view = FindSketchView(doc);

        var manager = doc.FamilyManager;
        var styles = new Dictionary<long, FamilyContentsLineStyle>();

        ReadParameters(doc, manager, snapshot, warnings);
        cancellationToken.ThrowIfCancellationRequested();

        var regionSketchIds = new HashSet<long>();
        ReadRegions(doc, manager, snapshot, styles, regionSketchIds, warnings, cancellationToken);
        ReadCurves(doc, manager, snapshot, styles, regionSketchIds, warnings, cancellationToken);
        foreach (var text in Capped(new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>(), "text notes", warnings))
        {
            var read = ReadText(text, warnings);
            if (read == null) continue;
            read.VisibleParameter = VisibleParameter(manager, text);
            snapshot.TextNotes.Add(read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ReadNested(doc, manager, snapshot, readNested, warnings, cancellationToken);

        snapshot.LineStyles.AddRange(styles.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase));
        return snapshot;
    }

    private static void ReadParameters(Document doc, FamilyManager manager, FamilyContentsSnapshot snapshot, List<string> warnings)
    {
        var parameters = new List<(FamilyParameter Parameter, string? Friendly, ForgeTypeId? Spec)>();
        foreach (var fp in manager.GetParameters())
        {
            try
            {
                var definition = fp.Definition;
                var spec = Safe(() => definition.GetDataType(), null, warnings, $"data type of '{definition.Name}'");
                var friendly = ParameterRevitTypes.FriendlyOf(spec);
                var group = Safe(() => definition.GetGroupTypeId(), null, warnings, $"group of '{definition.Name}'");
                snapshot.Parameters.Add(new FamilyContentsParameter
                {
                    Name = definition.Name,
                    DataType = friendly ?? ParameterRevitTypes.SpecLabel(spec),
                    Friendly = friendly,
                    Group = ParameterRevitTypes.GroupLabel(group),
                    IsInstance = fp.IsInstance,
                    IsShared = fp.IsShared,
                    Guid = fp.IsShared ? Safe(() => fp.GUID.ToString(), string.Empty, warnings, $"guid of '{definition.Name}'") : string.Empty,
                    Formula = fp.Formula ?? string.Empty,
                    IsReporting = fp.IsReporting,
                    IsBuiltIn = fp.Id.Value < 0
                });
                parameters.Add((fp, friendly, spec));
            }
            catch (Exception ex)
            {
                warnings.Add($"A family parameter could not be read: {ex.Message}");
            }
        }

        snapshot.CurrentType = Safe(() => manager.CurrentType?.Name ?? string.Empty, string.Empty, warnings, "current type");
        foreach (FamilyType type in manager.Types)
        {
            var item = new FamilyContentsType { Name = type.Name };
            foreach (var (fp, friendly, spec) in parameters)
            {
                try { item.Values[fp.Definition.Name] = TypeValue(doc, type, fp, friendly, spec); }
                catch (Exception ex) { warnings.Add($"Type '{type.Name}', parameter '{fp.Definition.Name}': {ex.Message}"); }
            }
            snapshot.Types.Add(item);
        }
    }

    /// <summary>The type's value: lengths mm, areas m², volumes m³, angles degrees, yes/no bool, element ids by name; other units as the family displays them.</summary>
    private static object? TypeValue(Document doc, FamilyType type, FamilyParameter fp, string? friendly, ForgeTypeId? spec)
    {
        if (!type.HasValue(fp)) return null;
        switch (fp.StorageType)
        {
            case StorageType.Double:
            {
                var value = type.AsDouble(fp);
                if (value == null) return null;
                if (friendly is "length" or "area" or "volume" or "angle") return FamilyContentsGeometry.FromInternal(friendly, value.Value);
                if (spec != null && !string.IsNullOrEmpty(spec.TypeId) && UnitUtils.IsMeasurableSpec(spec))
                {
                    try { return UnitUtils.ConvertFromInternalUnits(value.Value, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId()); }
                    catch { return value.Value; }
                }
                return value.Value;
            }
            case StorageType.Integer:
            {
                var value = type.AsInteger(fp);
                if (value == null) return null;
                return friendly == "yesNo" ? value.Value != 0 : (object)(long)value.Value;
            }
            case StorageType.String:
                return type.AsString(fp);
            case StorageType.ElementId:
            {
                var id = type.AsElementId(fp);
                if (id == null || id == ElementId.InvalidElementId) return null;
                return doc.GetElement(id)?.Name ?? id.Value.ToString();
            }
            default:
                return type.AsValueString(fp);
        }
    }

    private static void ReadRegions(Document doc, FamilyManager manager, FamilyContentsSnapshot snapshot,
        Dictionary<long, FamilyContentsLineStyle> styles, HashSet<long> sketchIds, List<string> warnings, CancellationToken cancellationToken)
    {
        var types = new Dictionary<long, FamilyContentsRegionType>();
        foreach (var region in Capped(new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>(), "filled regions", warnings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var item = new FamilyContentsRegion { Id = region.Id.Value, VisibleParameter = VisibleParameter(manager, region) };
                foreach (var loop in region.GetBoundaries())
                {
                    var read = new FamilyContentsLoop();
                    foreach (var curve in loop) read.Segments.Add(Segment(curve));
                    item.Loops.Add(read);
                }

                if (doc.GetElement(region.GetTypeId()) is FilledRegionType type)
                {
                    item.Type = type.Name;
                    if (!types.ContainsKey(type.Id.Value)) types[type.Id.Value] = RegionType(doc, type);
                }

                // The boundary's line style is the style of its sketch lines: the API has no getter on the region.
                var boundaryStyles = new List<string>();
                foreach (var id in Safe(() => region.GetDependentElements(new ElementClassFilter(typeof(CurveElement))), new List<ElementId>(), warnings,
                             $"boundary of filled region {region.Id.Value}"))
                {
                    sketchIds.Add(id.Value);
                    if (doc.GetElement(id) is CurveElement edge && edge.LineStyle is GraphicsStyle gs)
                    {
                        var name = StyleName(gs, styles);
                        if (name.Length > 0) boundaryStyles.Add(name);
                    }
                }
                var distinct = boundaryStyles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                item.LineStyle = distinct.FirstOrDefault() ?? string.Empty;
                item.MixedLineStyles = distinct.Count > 1;
                snapshot.FilledRegions.Add(item);
            }
            catch (Exception ex)
            {
                warnings.Add($"Filled region {region.Id.Value} could not be read: {ex.Message}");
            }
        }
        snapshot.FilledRegionTypes.AddRange(types.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static FamilyContentsRegionType RegionType(Document doc, FilledRegionType type) => new()
    {
        Name = type.Name,
        ForegroundPattern = PatternLabel(doc, type.ForegroundPatternId),
        ForegroundColor = Hex(type.ForegroundPatternColor),
        BackgroundPattern = PatternLabel(doc, type.BackgroundPatternId),
        BackgroundColor = Hex(type.BackgroundPatternColor),
        LineWeight = type.LineWeight,
        IsMasking = type.IsMasking
    };

    private static void ReadCurves(Document doc, FamilyManager manager, FamilyContentsSnapshot snapshot,
        Dictionary<long, FamilyContentsLineStyle> styles, HashSet<long> sketchIds, List<string> warnings, CancellationToken cancellationToken)
    {
        var curves = new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>()
            .Where(c => !sketchIds.Contains(c.Id.Value) && IsDrawnCurve(c));
        foreach (var element in Capped(curves, "curves", warnings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var curve = element.GeometryCurve;
                if (curve == null) continue;
                var item = Curve(curve);
                item.Id = element.Id.Value;
                if (element.LineStyle is GraphicsStyle gs) item.LineStyle = StyleName(gs, styles);
                item.VisibleParameter = VisibleParameter(manager, element);
                snapshot.Curves.Add(item);
            }
            catch (Exception ex)
            {
                warnings.Add($"Curve {element.Id.Value} could not be read: {ex.Message}");
            }
        }
    }

    /// <summary>Detail, symbolic and model lines drawn in the family; not sketch lines of a region or reference lines.</summary>
    private static bool IsDrawnCurve(CurveElement curve)
    {
        try
        {
            if (curve.CurveElementType is not (CurveElementType.DetailCurve or CurveElementType.SymbolicCurve or CurveElementType.ModelCurve)) return false;
            var category = curve.Category?.Id.Value;
            return category != (long)BuiltInCategory.OST_SketchLines && category != (long)BuiltInCategory.OST_ReferenceLines;
        }
        catch
        {
            return false;
        }
    }

    private static void ReadNested(Document doc, FamilyManager manager, FamilyContentsSnapshot snapshot, bool readNested,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var annotationFamilies = new Dictionary<long, Family>();
        foreach (var instance in Capped(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>(), "nested instances", warnings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var symbol = instance.Symbol;
                var family = symbol?.Family;
                var familyCategory = Safe(() => family?.FamilyCategory, null, warnings, "nested family category");
                var item = new FamilyContentsNested
                {
                    Id = instance.Id.Value,
                    Family = family?.Name ?? string.Empty,
                    Type = symbol == null ? string.Empty : FamilyTypeSupport.SafeName(symbol),
                    Category = familyCategory?.Name ?? instance.Category?.Name ?? string.Empty,
                    IsGenericAnnotation = familyCategory != null && familyCategory.Id.Value == (long)BuiltInCategory.OST_GenericAnnotation,
                    Mirrored = instance.Mirrored,
                    VisibleParameter = VisibleParameter(manager, instance)
                };
                if (instance.Location is LocationPoint location)
                {
                    item.Position = Point(location.Point);
                    item.RotationDeg = FamilyContentsGeometry.NormalizeRotation(location.Rotation * 180 / Math.PI);
                }
                AddLinks(manager, instance, "instance", item.LinkedParameters);
                if (symbol != null) AddLinks(manager, symbol, "type", item.LinkedParameters);
                snapshot.NestedInstances.Add(item);
                if (item.IsGenericAnnotation && family != null) annotationFamilies[family.Id.Value] = family;
            }
            catch (Exception ex)
            {
                warnings.Add($"Nested instance {instance.Id.Value} could not be read: {ex.Message}");
            }
        }

        foreach (var family in annotationFamilies.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var nested = new FamilyContentsNestedFamily { Name = family.Name };
            snapshot.NestedFamilies.Add(nested);
            if (!readNested) { nested.Reason = "readNested=false"; continue; }
            if (snapshot.NestedFamilies.Count > MaxNestedFamiliesOpened) { nested.Reason = $"only the first {MaxNestedFamiliesOpened} nested annotation families are opened"; continue; }
            if (!family.IsEditable) { nested.Reason = "Revit does not let it be opened"; continue; }
            cancellationToken.ThrowIfCancellationRequested();
            Document? nestedDoc = null;
            try
            {
                nestedDoc = doc.EditFamily(family);
                if (nestedDoc == null) { nested.Reason = "Revit could not open it"; continue; }
                foreach (var text in new FilteredElementCollector(nestedDoc).OfClass(typeof(TextNote)).Cast<TextNote>().Take(20))
                {
                    var read = ReadText(text, warnings);
                    if (read != null) nested.Texts.Add(read);
                }
                nested.OtherElements =
                    new FilteredElementCollector(nestedDoc).OfClass(typeof(CurveElement)).Cast<CurveElement>().Count(IsDrawnCurve) +
                    new FilteredElementCollector(nestedDoc).OfClass(typeof(FilledRegion)).GetElementCount() +
                    new FilteredElementCollector(nestedDoc).OfClass(typeof(FamilyInstance)).GetElementCount();
                nested.Opened = true;
            }
            catch (Exception ex)
            {
                nested.Reason = $"Revit could not open it: {ex.Message}";
            }
            finally
            {
                try { nestedDoc?.Close(false); } catch { /* already closed */ }
            }
        }
    }

    /// <summary>Parameters of <paramref name="element"/> linked to a family parameter (its Visible parameter is reported apart).</summary>
    private static void AddLinks(FamilyManager manager, Element element, string scope, List<FamilyContentsLink> links)
    {
        foreach (Parameter parameter in element.Parameters)
        {
            try
            {
                if (parameter.Definition is InternalDefinition { BuiltInParameter: BuiltInParameter.IS_VISIBLE_PARAM }) continue;
                var associated = manager.GetAssociatedFamilyParameter(parameter);
                if (associated == null) continue;
                links.Add(new FamilyContentsLink { Parameter = parameter.Definition.Name, FamilyParameter = associated.Definition.Name, Scope = scope });
            }
            catch
            {
                // A parameter Revit cannot associate is simply not linked.
            }
        }
    }

    private static string VisibleParameter(FamilyManager manager, Element element)
    {
        try
        {
            var visible = element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            return visible == null ? string.Empty : manager.GetAssociatedFamilyParameter(visible)?.Definition.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static FamilyContentsText? ReadText(TextNote text, List<string> warnings)
    {
        try
        {
            var type = text.TextNoteType;
            var size = type?.get_Parameter(BuiltInParameter.TEXT_SIZE);
            var direction = text.BaseDirection;
            return new FamilyContentsText
            {
                Id = text.Id.Value,
                Text = (text.Text ?? string.Empty).TrimEnd('\r', '\n'),
                Position = Point(text.Coord),
                TextType = type?.Name ?? string.Empty,
                SizeMm = size != null && size.HasValue ? size.AsDouble() * MmPerFoot : null,
                RotationDeg = direction == null ? 0 : FamilyContentsGeometry.NormalizeRotation(Math.Atan2(direction.Y, direction.X) * 180 / Math.PI),
                Alignment = text.HorizontalAlignment switch
                {
                    HorizontalTextAlignment.Center => "center",
                    HorizontalTextAlignment.Right => "right",
                    _ => "left"
                }
            };
        }
        catch (Exception ex)
        {
            warnings.Add($"Text note {text.Id.Value} could not be read: {ex.Message}");
            return null;
        }
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    private static DetailPoint Point(XYZ p) => new(p.X * MmPerFoot, p.Y * MmPerFoot);

    private static FamilyContentsCurve Curve(Curve curve)
    {
        switch (curve)
        {
            case Line line:
                return new FamilyContentsCurve { Kind = "line", Start = Point(line.GetEndPoint(0)), End = Point(line.GetEndPoint(1)) };
            case Arc arc:
            {
                var center = Point(arc.Center);
                var radius = arc.Radius * MmPerFoot;
                if (!arc.IsBound || arc.Length >= 2 * Math.PI * arc.Radius - 1e-9)
                    return new FamilyContentsCurve { Kind = "circle", Center = center, Radius = radius };
                var start = Point(arc.GetEndPoint(0));
                var end = Point(arc.GetEndPoint(1));
                var (startAngle, endAngle) = FamilyContentsGeometry.ArcAngles(center, start, end, arc.Normal.Z < 0);
                return new FamilyContentsCurve
                {
                    Kind = "arc", Start = start, End = end, Mid = Point(arc.Evaluate(0.5, true)),
                    Center = center, Radius = radius, StartAngle = startAngle, EndAngle = endAngle
                };
            }
            default:
                return new FamilyContentsCurve
                {
                    Kind = "other",
                    CurveType = curve.GetType().Name,
                    Start = curve.IsBound ? Point(curve.GetEndPoint(0)) : default,
                    End = curve.IsBound ? Point(curve.GetEndPoint(1)) : default
                };
        }
    }

    private static FamilyContentsSegment Segment(Curve curve)
    {
        switch (curve)
        {
            case Line line:
                return new FamilyContentsSegment { Kind = "line", Start = Point(line.GetEndPoint(0)), End = Point(line.GetEndPoint(1)) };
            case Arc { IsBound: true } arc:
                return new FamilyContentsSegment
                {
                    Kind = "arc", Start = Point(arc.GetEndPoint(0)), End = Point(arc.GetEndPoint(1)), Mid = Point(arc.Evaluate(0.5, true)),
                    Center = Point(arc.Center), Radius = arc.Radius * MmPerFoot
                };
            case Arc arc:
                return new FamilyContentsSegment { Kind = "arc", Center = Point(arc.Center), Radius = arc.Radius * MmPerFoot };
            default:
                return new FamilyContentsSegment
                {
                    Kind = "other",
                    CurveType = curve.GetType().Name,
                    Start = curve.IsBound ? Point(curve.GetEndPoint(0)) : default,
                    End = curve.IsBound ? Point(curve.GetEndPoint(1)) : default
                };
        }
    }

    // ── Styles and patterns ──────────────────────────────────────────────────

    /// <summary>
    /// The style's name, recorded in <paramref name="styles"/>; empty for the category's own line (a top-level category,
    /// such as Detail Items itself), which is the family's default.
    /// </summary>
    private static string StyleName(GraphicsStyle gs, Dictionary<long, FamilyContentsLineStyle> styles)
    {
        var category = gs.GraphicsStyleCategory;
        if (category == null || category.Parent == null) return string.Empty;
        if (!styles.ContainsKey(category.Id.Value))
        {
            var doc = gs.Document;
            var patternId = category.GetLinePatternId(GraphicsStyleType.Projection);
            var pattern = patternId == null || patternId == ElementId.InvalidElementId || patternId == LinePatternElement.GetSolidPatternId()
                ? null
                : doc.GetElement(patternId) as LinePatternElement;
            styles[category.Id.Value] = new FamilyContentsLineStyle
            {
                Name = category.Name,
                Parent = category.Parent.Name,
                Weight = category.GetLineWeight(GraphicsStyleType.Projection),
                Color = Hex(category.LineColor),
                Pattern = pattern?.Name ?? string.Empty,
                BuiltIn = category.Id.Value < 0
            };
        }
        return category.Name;
    }

    private static string PatternLabel(Document doc, ElementId id) =>
        id == null || id == ElementId.InvalidElementId ? "none"
        : doc.GetElement(id) is FillPatternElement element ? (element.GetFillPattern().IsSolidFill ? "solid" : element.Name)
        : "none";

    private static string Hex(Color? color) =>
        color == null || !color.IsValid ? string.Empty : $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private static View? FindSketchView(Document doc)
    {
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        return views.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
               ?? views.FirstOrDefault(v => v.ViewType == ViewType.DraftingView)
               ?? views.FirstOrDefault(v => v.ViewType is ViewType.CeilingPlan or ViewType.EngineeringPlan);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>At most <see cref="FamilyContentsView.MaxLimit"/> items of a list, with a warning when there are more.</summary>
    private static IEnumerable<T> Capped<T>(IEnumerable<T> items, string what, List<string> warnings)
    {
        var count = 0;
        foreach (var item in items)
        {
            if (++count > FamilyContentsView.MaxLimit)
            {
                warnings.Add($"Only the first {FamilyContentsView.MaxLimit} {what} were read.");
                yield break;
            }
            yield return item;
        }
    }

    private static T Safe<T>(Func<T> read, T fallback, List<string> warnings, string what)
    {
        try { return read(); }
        catch (Exception ex)
        {
            warnings.Add($"Could not read the {what}: {ex.Message}");
            return fallback;
        }
    }

    private static McpToolResult Invalid(McpToolRequest request, List<string> errors) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = "invalid_arguments",
        Message = string.Join(" ", errors),
        Errors = errors
    };

    private static McpToolResult Fail(McpToolRequest request, string message, string status, List<string>? warnings = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = status,
        Message = message,
        Errors = [message],
        Warnings = warnings ?? new List<string>()
    };
}
