using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Tools.Families;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// Nested families of a model family (#175): loading them into the family document, placing instances, driving their
/// parameters and type from family parameters, and locking them to reference planes.
/// </summary>
internal static class ModelFamilyComponentNested
{
    public static List<Family> NestedFamilyList(Document familyDoc) =>
        new FilteredElementCollector(familyDoc).OfClass(typeof(Family)).Cast<Family>()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A loaded family as a row: id, name, category, placement type, whether it is shared, its types.</summary>
    public static JObject DescribeFamily(Document doc, Family family)
    {
        var shared = false;
        try { shared = family.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1; }
        catch { /* not readable for this family */ }
        var placement = string.Empty;
        try { placement = family.FamilyPlacementType.ToString(); }
        catch { /* an in-place family */ }
        return new JObject
        {
            ["id"] = family.Id.Value,
            ["name"] = FamilyTypeSupport.SafeName(family),
            ["category"] = FamilyInventory.CategoryOf(family),
            ["placementType"] = placement,
            ["shared"] = shared,
            ["types"] = new JArray(FamilyInventory.Symbols(doc, family).Select(s => new JObject
            {
                ["id"] = s.Id.Value,
                ["name"] = FamilyTypeSupport.SafeName(s)
            }))
        };
    }

    // ── nestedFamilies ───────────────────────────────────────────────────────

    public static JArray LoadFamilies(ModelFamilyContext context, List<ComponentNestedFamilySpec> specs)
    {
        var rows = new JArray();
        var doc = context.FamilyDoc;
        var policy = new FilePathPolicy();
        foreach (var spec in specs)
        {
            var name = spec.Path.Length > 0 ? Path.GetFileNameWithoutExtension(spec.Path) : spec.FromProject;
            if (string.Equals(name, context.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                context.Problems.Add($"{spec.Source}: '{name}' is the family being edited; a family cannot be nested in itself.");
                continue;
            }
            var existing = NestedFamilyList(doc).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && spec.IfExists == FamilyIfExists.Skip)
            {
                var skipped = DescribeFamily(doc, existing);
                skipped["status"] = "skipped";
                skipped["note"] = "Already loaded in the family; ifExists=skip left it as it is. Pass ifExists=overwrite to load the file's version.";
                rows.Add(skipped);
                continue;
            }

            string? loadPath = null;
            string? tempFolder = null;
            try
            {
                if (spec.Path.Length > 0)
                {
                    if (policy.ValidateRead(spec.Path) is { } readError) { context.Problems.Add($"{spec.Source}.path: {readError}"); continue; }
                    if (!File.Exists(spec.Path)) { context.Problems.Add($"{spec.Source}.path: '{spec.Path}' does not exist."); continue; }
                    loadPath = spec.Path;
                }
                else
                {
                    loadPath = SaveProjectFamilyToTemp(context, spec, out tempFolder);
                    if (loadPath == null) continue;
                }

                Family? loaded = null;
                var options = new FamilyLoadOptions(spec.IfExists);
                var path = loadPath;
                var ok = context.TryTransact($"Load nested family {name}", () =>
                {
                    if (!doc.LoadFamily(path, options, out loaded) || loaded == null)
                        throw new InvalidOperationException($"Revit did not load '{name}' into the family. Its category may not be allowed as a nested component of this family's category.");
                }, out var error);
                if (!ok) { context.Problems.Add($"{spec.Source}: {error}"); continue; }
                var row = DescribeFamily(doc, loaded!);
                row["status"] = existing != null ? "reloaded" : "loaded";
                row["source"] = spec.Path.Length > 0 ? spec.Path : $"project family '{spec.FromProject}'";
                if (options.SharedFamiliesFound.Count > 0) row["sharedFamiliesFound"] = new JArray(options.SharedFamiliesFound);
                rows.Add(row);
            }
            finally
            {
                if (tempFolder != null)
                {
                    try { Directory.Delete(tempFolder, true); }
                    catch { /* a temporary copy; the system temp folder is cleaned up anyway */ }
                }
            }
        }
        return rows;
    }

    /// <summary>
    /// Saves the project's family to a temporary .rfa so it can be loaded into the family document from a path. Opens
    /// the family document (and closes it again) only when it was not already open; a family open in the user's Family
    /// Editor is never touched. Null, with a problem recorded, when that is not possible.
    /// </summary>
    private static string? SaveProjectFamilyToTemp(ModelFamilyContext context, ComponentNestedFamilySpec spec, out string? tempFolder)
    {
        tempFolder = null;
        if (context.Project == null)
        {
            context.Problems.Add($"{spec.Source}.fromProject: no project is open (pass document), so there is no family to copy; give path instead.");
            return null;
        }
        if (!FamilyInventory.LoadedByName(context.Project).TryGetValue(spec.FromProject, out var family))
        {
            var close = ModelFamilyJson.Suggest(spec.FromProject, FamilyInventory.LoadedByName(context.Project).Keys);
            context.Problems.Add($"{spec.Source}.fromProject: '{spec.FromProject}' is not a family loaded in '{context.Project.Title}'" +
                                 (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ". Use revit_list_families, or pass path."));
            return null;
        }
        if (family.IsInPlace || !family.IsEditable)
        {
            context.Problems.Add($"{spec.Source}.fromProject: '{spec.FromProject}' cannot be opened for editing (an in-place or system family); only loadable families can be nested.");
            return null;
        }
        Document? source = null;
        var owned = false;
        try
        {
            source = FamilyDocumentGuard.EditFamily(context.Project, family, out owned);
            if (source == null)
            {
                context.Problems.Add($"{spec.Source}.fromProject: Revit did not open '{spec.FromProject}'.");
                return null;
            }
            if (!owned)
            {
                context.Problems.Add($"{spec.Source}.fromProject: {FamilyDocumentGuard.OpenInEditorMessage(spec.FromProject)}");
                return null;
            }
            var folder = Path.Combine(Path.GetTempPath(), "RevitMcp_nested_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            tempFolder = folder;
            var file = Path.Combine(folder, spec.FromProject + ".rfa");
            source.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = true });
            return file;
        }
        catch (Exception ex)
        {
            context.Problems.Add($"{spec.Source}.fromProject: could not copy '{spec.FromProject}' out of the project: {ex.Message.TrimEnd('.')}.");
            return null;
        }
        finally
        {
            FamilyDocumentGuard.CloseIfOwned(source, owned);
        }
    }

    // ── instances ────────────────────────────────────────────────────────────

    public static JArray PlaceInstances(ModelFamilyContext context, List<ComponentInstanceSpec> specs, Dictionary<string, List<long>> created)
    {
        var rows = new JArray();
        var doc = context.FamilyDoc;
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var problems = new List<string>();
            var family = FindFamily(doc, spec.Family, spec.Source, problems);
            FamilySymbol? symbol = null;
            if (family != null)
            {
                var symbols = FamilyInventory.Symbols(doc, family);
                if (symbols.Count == 0) problems.Add($"{spec.Source}.family: '{family.Name}' has no types.");
                else if (spec.Type.Length == 0) symbol = symbols[0];
                else
                {
                    symbol = symbols.FirstOrDefault(s => string.Equals(FamilyTypeSupport.SafeName(s), spec.Type, StringComparison.OrdinalIgnoreCase));
                    if (symbol == null)
                    {
                        var close = ModelFamilyJson.Suggest(spec.Type, symbols.Select(FamilyTypeSupport.SafeName));
                        problems.Add($"{spec.Source}.type: '{family.Name}' has no type '{spec.Type}'. Types: {string.Join(", ", symbols.Take(10).Select(s => $"'{FamilyTypeSupport.SafeName(s)}'"))}" +
                                     (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : "."));
                    }
                }
            }
            var host = family != null && symbol != null ? ResolveHost(doc, family, spec, problems) : null;
            if (problems.Count > 0 || family == null || symbol == null || host == null)
            {
                context.Problems.AddRange(problems);
                continue;
            }

            JObject? row = null;
            var ids = new List<long>();
            var ok = context.TryTransact($"Place nested instance {i + 1}", () =>
            {
                row = PlaceOne(context, spec, family, symbol, host, ids);
            }, out var error);
            if (!ok)
            {
                context.Problems.Add($"{spec.Source}: {error}");
                continue;
            }
            created[ModelFamilyComponentRefs.Format("instances", i)] = ids;
            rows.Add(row!);
        }
        return rows;
    }

    private static Family? FindFamily(Document doc, JToken? token, string source, List<string> problems)
    {
        var all = NestedFamilyList(doc);
        if (token == null) return null;
        if (token.Type == JTokenType.Integer && ModelFamilyJson.AsId(token) is { } id)
        {
            var byId = all.FirstOrDefault(f => f.Id.Value == id);
            if (byId == null) problems.Add($"{source}.family: the family has no nested family with id {id}.");
            return byId;
        }
        var name = (token.Type == JTokenType.String ? (string?)token : token.ToString())?.Trim() ?? string.Empty;
        var found = all.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;
        var close = ModelFamilyJson.Suggest(name, all.Select(f => f.Name));
        problems.Add($"{source}.family: no nested family '{name}' is loaded in the family" +
                     (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : all.Count > 0 ? $". Loaded: {string.Join(", ", all.Take(10).Select(f => $"'{f.Name}'"))}." : ".") +
                     " Load it with nestedFamilies first.");
        return null;
    }

    /// <summary>Where an instance goes, worked out before anything is created.</summary>
    private sealed class Host
    {
        public FamilyPlacementType Placement { get; init; }
        public View? View { get; init; }
        public ReferencePlane? ReferencePlane { get; init; }
        public string Description { get; init; } = string.Empty;
    }

    private static Host? ResolveHost(Document doc, Family family, ComponentInstanceSpec spec, List<string> problems)
    {
        var placement = family.FamilyPlacementType;
        switch (placement)
        {
            case FamilyPlacementType.ViewBased:
            {
                if (spec.WorkPlane != null)
                    problems.Add($"{spec.Source}.workPlane: '{family.Name}' is view based (an annotation, detail or symbol family); name the view instead.");
                var view = ModelFamilyLookup.View(doc, spec.View, $"{spec.Source}.view", problems);
                if (view == null) return null;
                if (view is View3D || view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Elevation or ViewType.Section or ViewType.Detail or ViewType.DraftingView))
                {
                    problems.Add($"{spec.Source}.view: '{view.Name}' cannot hold a view-based instance; use a plan or an elevation.");
                    return null;
                }
                return new Host { Placement = placement, View = view, Description = $"view '{view.Name}'" };
            }
            case FamilyPlacementType.OneLevelBased:
            {
                if (spec.View != null)
                    problems.Add($"{spec.Source}.view: '{family.Name}' is a model family (placement {placement}); it is placed in 3D, not in a view. Leave view out.");
                if (spec.WorkPlane is { } token && !(token.Type == JTokenType.String && string.Equals((string?)token, "level", StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"{spec.Source}.workPlane: '{family.Name}' is level based; it sits at its position. Use \"level\" or leave workPlane out.");
                return problems.Count > 0 ? null : new Host { Placement = placement, Description = "the family's reference level" };
            }
            case FamilyPlacementType.WorkPlaneBased:
            {
                if (spec.View != null)
                    problems.Add($"{spec.Source}.view: '{family.Name}' is work plane based; give workPlane (a reference plane), not view.");
                if (spec.WorkPlane == null || spec.WorkPlane.Type == JTokenType.String && string.Equals((string?)spec.WorkPlane, "level", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{spec.Source}.workPlane: '{family.Name}' is work plane based; give the reference plane it sits on (name or element id).");
                    return null;
                }
                var plane = ModelFamilyLookup.ReferencePlane(doc, spec.WorkPlane, $"{spec.Source}.workPlane", problems);
                return plane == null || problems.Count > 0 ? null : new Host { Placement = placement, ReferencePlane = plane, Description = $"reference plane '{plane.Name}'" };
            }
            default:
                problems.Add($"{spec.Source}.family: '{family.Name}' has placement type {placement}, which this tool does not place (supported: OneLevelBased, WorkPlaneBased, ViewBased).");
                return null;
        }
    }

    private static JObject PlaceOne(ModelFamilyContext context, ComponentInstanceSpec spec, Family family, FamilySymbol symbol, Host host, List<long> ids)
    {
        var doc = context.FamilyDoc;
        if (!symbol.IsActive)
        {
            symbol.Activate();
            doc.Regenerate();
        }
        var position = ModelFamilyLookup.Xyz(spec.Position);
        FamilyInstance instance;
        XYZ axis;
        switch (host.Placement)
        {
            case FamilyPlacementType.ViewBased:
                instance = doc.FamilyCreate.NewFamilyInstance(position, symbol, host.View!);
                axis = host.View!.RightDirection.CrossProduct(host.View.UpDirection);
                break;
            case FamilyPlacementType.WorkPlaneBased:
            {
                var plane = host.ReferencePlane!.GetPlane();
                var onPlane = position - plane.Normal.Multiply((position - plane.Origin).DotProduct(plane.Normal));
                instance = doc.FamilyCreate.NewFamilyInstance(host.ReferencePlane.GetReference(), onPlane, plane.XVec, symbol)
                           ?? throw new InvalidOperationException($"Revit did not place '{family.Name}' on {host.Description}.");
                axis = plane.Normal;
                break;
            }
            default:
                instance = doc.FamilyCreate.NewFamilyInstance(position, symbol, StructuralType.NonStructural);
                axis = XYZ.BasisZ;
                break;
        }
        ids.Add(instance.Id.Value);
        if (Math.Abs(spec.RotationDegrees) > 1e-9)
            ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(position, position + axis), spec.RotationDegrees * Math.PI / 180);

        foreach (var property in spec.Parameters.Properties())
            SetParameter(instance, symbol, property.Name, property.Value, $"{spec.Source}.parameters.{property.Name}");
        doc.Regenerate();

        var associations = new JArray();
        foreach (var pair in spec.Associate)
        {
            var at = $"{spec.Source}.associate.{pair.Key}";
            var parameter = ModelFamilyAssociations.ElementParameter(instance, pair.Key);
            if (parameter == null)
            {
                if (ModelFamilyAssociations.ElementParameter(symbol, pair.Key) != null)
                    throw new InvalidOperationException($"{at}: '{pair.Key}' is a type parameter of '{family.Name}'. Revit associates only the instance parameters of a nested instance; make it an instance parameter in the nested family, or drive the nested type with typeParameter.");
                throw new InvalidOperationException($"{at}: the nested instance has no parameter '{pair.Key}'. Its associable parameters: {string.Join(", ", ModelFamilyAssociations.Associable(doc, instance).Select(r => r["name"]).Take(12))}.");
            }
            var problems = new List<string>();
            var familyParameter = ModelFamilyLookup.Parameter(doc, pair.Value, at, problems)
                                  ?? throw new InvalidOperationException(problems.FirstOrDefault() ?? $"{at}: unknown family parameter.");
            var error = ModelFamilyAssociations.Associate(doc, parameter, familyParameter);
            if (error != null) throw new InvalidOperationException($"{at}: {error}");
            associations.Add(new JObject { ["parameter"] = pair.Key, ["familyParameter"] = familyParameter.Definition.Name });
        }

        JObject? typeParameter = null;
        if (spec.TypeParameter.Length > 0) typeParameter = DriveType(context, spec, instance, symbol);

        JObject? visibility = null;
        if (spec.Visibility != null)
            visibility = ModelFamilyComponentVisibilityRevit.Apply(doc, instance, spec.Visibility, spec.Source, context.Warnings);

        var locks = new JArray();
        foreach (var lockSpec in spec.LockTo) locks.Add(LockInstance(context, instance, lockSpec));

        var row = Describe(doc, instance);
        if (spec.Name.Length > 0) row["name"] = spec.Name;
        row["host"] = host.Description;
        if (associations.Count > 0) row["associated"] = associations;
        if (typeParameter != null) row["typeParameter"] = typeParameter;
        if (visibility != null) row["visibility"] = visibility;
        if (locks.Count > 0) row["locks"] = locks;
        return row;
    }

    // ── parameters ───────────────────────────────────────────────────────────

    private static void SetParameter(FamilyInstance instance, FamilySymbol symbol, string name, JToken value, string source)
    {
        var parameter = ModelFamilyAssociations.ElementParameter(instance, name);
        if (parameter == null)
        {
            if (ModelFamilyAssociations.ElementParameter(symbol, name) != null)
                throw new InvalidOperationException($"{source}: '{name}' is a type parameter of '{symbol.Family.Name}'; a nested instance only takes instance parameters here.");
            throw new InvalidOperationException($"{source}: the nested instance has no parameter '{name}'.");
        }
        if (parameter.IsReadOnly) throw new InvalidOperationException($"{source}: '{name}' is read-only.");
        bool ok;
        switch (parameter.StorageType)
        {
            case StorageType.Double:
            {
                var number = ModelFamilyJson.AsNumber(value) ?? throw new InvalidOperationException($"{source}: give a number.");
                var type = SafeDataType(parameter);
                var internalValue = type == SpecTypeId.Length ? ModelFamilyJson.ToFeet(number)
                    : type == SpecTypeId.Angle ? number * Math.PI / 180
                    : number;
                ok = parameter.Set(internalValue);
                break;
            }
            case StorageType.Integer:
            {
                int number;
                if (value.Type == JTokenType.Boolean) number = (bool)value ? 1 : 0;
                else number = (int)Math.Round(ModelFamilyJson.AsNumber(value) ?? throw new InvalidOperationException($"{source}: give a whole number or true/false."));
                ok = parameter.Set(number);
                break;
            }
            case StorageType.String:
                ok = parameter.Set(value.Type == JTokenType.String ? (string?)value ?? string.Empty : value.ToString(Newtonsoft.Json.Formatting.None));
                break;
            case StorageType.ElementId:
                ok = parameter.Set(new ElementId(ModelFamilyJson.AsId(value) ?? throw new InvalidOperationException($"{source}: give an element id.")));
                break;
            default:
                throw new InvalidOperationException($"{source}: '{name}' has a storage type this tool does not set.");
        }
        if (!ok) throw new InvalidOperationException($"{source}: Revit did not accept the value for '{name}'.");
    }

    private static ForgeTypeId? SafeDataType(Parameter parameter)
    {
        try { return parameter.Definition.GetDataType(); }
        catch { return null; }
    }

    // ── nested type selection ────────────────────────────────────────────────

    /// <summary>
    /// Drives the instance's type from a family-type parameter: associates the instance's type property with a family
    /// parameter of data type "Family Type" for the nested family's category, creating that parameter when it is missing.
    /// </summary>
    private static JObject DriveType(ModelFamilyContext context, ComponentInstanceSpec spec, FamilyInstance instance, FamilySymbol symbol)
    {
        var doc = context.FamilyDoc;
        var at = $"{spec.Source}.typeParameter";
        var candidates = new[] { BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM, BuiltInParameter.ELEM_TYPE_PARAM }
            .Select(b => { try { return instance.get_Parameter(b); } catch { return null; } })
            .Where(p => p != null && ModelFamilyAssociations.CanAssociate(doc, p)).Cast<Parameter>().ToList();
        var category = symbol.Family.FamilyCategory;
        if (candidates.Count == 0)
            throw new InvalidOperationException($"{at}: Revit does not let the type of a '{category?.Name}' nested instance be driven by a family parameter " +
                                                "(its type property cannot be associated). Not supported for this nested family; place one instance per type instead.");

        var parameter = ModelFamilyLookup.Parameter(doc, spec.TypeParameter);
        var created = false;
        if (parameter == null)
        {
            if (category == null) throw new InvalidOperationException($"{at}: the nested family has no category, so a family-type parameter cannot be created for it.");
            try
            {
                parameter = doc.FamilyManager.AddParameter(spec.TypeParameter, GroupTypeId.General, category, spec.TypeParameterInstance);
                created = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{at}: Revit refused to create the family-type parameter '{spec.TypeParameter}' for category '{category.Name}': {ex.Message.TrimEnd('.')}. " +
                                                    "Driving the nested type is not supported for that category.");
            }
        }

        string? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                doc.FamilyManager.AssociateElementParameterToFamilyParameter(candidate, parameter);
                var now = ModelFamilyAssociations.Associated(doc, candidate);
                if (now != null && now.Id == parameter.Id)
                    return new JObject
                    {
                        ["familyParameter"] = parameter.Definition.Name,
                        ["created"] = created,
                        ["instance"] = parameter.IsInstance,
                        ["drives"] = (candidate.Definition as InternalDefinition)?.BuiltInParameter.ToString()
                    };
                lastError = $"'{candidate.Definition.Name}' did not take the association.";
            }
            catch (Exception ex)
            {
                lastError = ex.Message.TrimEnd('.') + ".";
            }
        }
        throw new InvalidOperationException($"{at}: Revit would not associate the nested type with '{spec.TypeParameter}': {lastError} " +
                                            $"The parameter must be a family-type parameter for category '{category?.Name}'.");
    }

    // ── locking ──────────────────────────────────────────────────────────────

    /// <summary>The named references the instance's family defines, as FamilyInstanceReferenceType names.</summary>
    public static List<string> NamedReferences(FamilyInstance instance)
    {
        var names = new List<string>();
        foreach (var name in ModelFamilyComponentPlan.InstanceReferenceNames)
        {
            if (!Enum.TryParse<FamilyInstanceReferenceType>(name, out var type)) continue;
            try { if (instance.GetReferences(type).Count > 0) names.Add(name); }
            catch { /* the reference type does not apply to this family */ }
        }
        return names;
    }

    /// <summary>
    /// Creates a locked alignment between a reference plane and a reference of the instance. Revit does not move
    /// anything for an alignment, so the two must already be coplanar; when they are not this says by how much.
    /// </summary>
    private static JObject LockInstance(ModelFamilyContext context, FamilyInstance instance, ComponentLockSpec lockSpec)
    {
        var doc = context.FamilyDoc;
        var problems = new List<string>();
        var plane = ModelFamilyLookup.ReferencePlane(doc, lockSpec.Reference, $"{lockSpec.Source}.reference", problems)
                    ?? throw new InvalidOperationException(problems.FirstOrDefault() ?? $"{lockSpec.Source}.reference: unknown reference plane.");
        if (!Enum.TryParse<FamilyInstanceReferenceType>(lockSpec.InstanceReference, out var type))
            throw new InvalidOperationException($"{lockSpec.Source}.instanceReference: '{lockSpec.InstanceReference}' is not a reference type.");
        IList<Reference> references;
        try { references = instance.GetReferences(type); }
        catch { references = new List<Reference>(); }
        if (references.Count == 0)
        {
            var available = NamedReferences(instance);
            throw new InvalidOperationException($"{lockSpec.Source}.instanceReference: the nested family defines no '{lockSpec.InstanceReference}' reference" +
                                                (available.Count > 0 ? $"; it has {string.Join(", ", available)}." : ". Set \"Is Reference\" on a reference plane inside the nested family and reload it."));
        }

        var geometry = plane.GetPlane();
        var distanceMm = DistanceMm(instance, type, geometry, out var parallel);
        if (!parallel)
            throw new InvalidOperationException($"{lockSpec.Source}: the instance's {lockSpec.InstanceReference} reference is not parallel to reference plane '{plane.Name}'; rotate the instance (rotation) so they are.");
        if (distanceMm is { } distance && Math.Abs(distance) > 0.05)
            throw new InvalidOperationException($"{lockSpec.Source}: the instance's {lockSpec.InstanceReference} reference is {Math.Round(Math.Abs(distance), 2)} mm from reference plane '{plane.Name}'; Revit aligns only references that already coincide. " +
                                                "Place the instance on the plane (position), then lock it.");

        var viewProblems = new List<string>();
        var view = Math.Abs(geometry.Normal.Z) > 0.9
            ? ModelFamilyLookup.View(doc, new JValue("front"), $"{lockSpec.Source}", viewProblems)
            : ModelFamilyLookup.View(doc, null, $"{lockSpec.Source}", viewProblems);
        if (view == null) throw new InvalidOperationException(viewProblems.FirstOrDefault() ?? $"{lockSpec.Source}: no view to create the alignment in.");
        doc.Regenerate();
        var alignment = doc.FamilyCreate.NewAlignment(view, plane.GetReference(), references[0]);
        if (alignment != null && !alignment.IsLocked) alignment.IsLocked = true;
        return new JObject
        {
            ["reference"] = plane.Name,
            ["referenceId"] = plane.Id.Value,
            ["instanceReference"] = lockSpec.InstanceReference,
            ["alignmentId"] = alignment?.Id.Value,
            ["locked"] = alignment?.IsLocked ?? false,
            ["distanceMm"] = distanceMm == null ? null : Math.Round(distanceMm.Value, 3)
        };
    }

    /// <summary>
    /// Distance in millimetres from the plane to the instance's reference, for the centre references (which pass
    /// through the instance origin in the usual templates); null when it cannot be worked out, in which case Revit's
    /// own check of the alignment decides. <paramref name="parallel"/> is false when the reference is turned away.
    /// </summary>
    private static double? DistanceMm(FamilyInstance instance, FamilyInstanceReferenceType type, Plane plane, out bool parallel)
    {
        parallel = true;
        Transform transform;
        try { transform = instance.GetTransform(); }
        catch { return null; }
        XYZ? normal = type switch
        {
            FamilyInstanceReferenceType.CenterLeftRight => transform.BasisX,
            FamilyInstanceReferenceType.CenterFrontBack => transform.BasisY,
            FamilyInstanceReferenceType.CenterElevation => transform.BasisZ,
            _ => null
        };
        if (normal == null) return null;
        if (Math.Abs(normal.Normalize().DotProduct(plane.Normal)) < 0.999)
        {
            parallel = false;
            return null;
        }
        return ModelFamilyJson.ToMm((transform.Origin - plane.Origin).DotProduct(plane.Normal));
    }

    // ── rows ─────────────────────────────────────────────────────────────────

    /// <summary>A nested instance as a row: id, family, type, position, rotation, visibility and the references it offers.</summary>
    public static JObject Describe(Document doc, FamilyInstance instance)
    {
        var row = new JObject
        {
            ["id"] = instance.Id.Value,
            ["family"] = instance.Symbol?.Family?.Name,
            ["familyId"] = instance.Symbol?.Family?.Id.Value,
            ["type"] = instance.Symbol == null ? null : FamilyTypeSupport.SafeName(instance.Symbol),
            ["placementType"] = SafePlacement(instance)
        };
        if (instance.Location is LocationPoint point)
        {
            row["position"] = ModelFamilyLookup.Mm(point.Point);
            row["rotation"] = Math.Round(point.Rotation * 180 / Math.PI, 4);
        }
        try { if (instance.OwnerViewId != ElementId.InvalidElementId) row["view"] = doc.GetElement(instance.OwnerViewId)?.Name; }
        catch { /* not view specific */ }
        row["visible"] = ModelFamilyComponentVisibilityRevit.IsVisible(instance);
        row["visibleParameter"] = ModelFamilyComponentVisibilityRevit.DrivenBy(doc, instance);
        row["references"] = new JArray(NamedReferences(instance));
        return row;
    }

    private static string? SafePlacement(FamilyInstance instance)
    {
        try { return instance.Symbol?.Family?.FamilyPlacementType.ToString(); }
        catch { return null; }
    }
}
