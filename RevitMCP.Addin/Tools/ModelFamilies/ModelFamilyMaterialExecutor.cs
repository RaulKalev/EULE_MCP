using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Parameters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The body of revit_preview_edit_model_family_materials and revit_edit_model_family_materials (#173): materials,
/// direct assignments, associations with material family parameters, then per-type defaults, in that order. The
/// session rolls everything back for a preview and when a problem was reported.
/// </summary>
internal static class ModelFamilyMaterialExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken) =>
        ModelFamilySession.Edit(uiapp, request, apply, "edit materials", Body, cancellationToken);

    public static string Summary(McpToolRequest request)
    {
        var plan = ModelFamilyMaterialArgs.Parse(request.Arguments ?? new Dictionary<string, object?>(), new List<string>());
        return $"Edit family materials ({plan.Describe()}) in {ModelFamilySession.DescribeTarget(request)}.";
    }

    private static object? Body(ModelFamilyContext context)
    {
        var plan = ModelFamilyMaterialArgs.Parse(context.Args, context.Problems);
        var result = new JObject();
        if (context.Problems.Count > 0) return result;
        if (plan.IsEmpty)
        {
            context.Problems.Add("Nothing to do: pass materials, assign, associate or defaults (see the tool description for the order they run in).");
            return result;
        }

        var run = new Run(context, result);
        if (plan.Materials.Count > 0) run.Materials(plan.Materials);
        // Everything below names materials, so a failed creation would only add noise.
        if (context.Problems.Count == 0 && plan.Assign.Count > 0) run.Assign(plan.Assign);
        if (context.Problems.Count == 0 && plan.Associate.Count > 0) run.Associate(plan.Associate);
        if (context.Problems.Count == 0 && plan.Defaults.Count > 0) run.Defaults(plan.Defaults);
        if (context.Problems.Count == 0) run.FinalStates();

        context.Summary = plan.Describe();
        return result;
    }

    private sealed class Run
    {
        private readonly ModelFamilyContext _context;
        private readonly JObject _result;
        private readonly Document _doc;
        private readonly List<long> _touched = new();

        public Run(ModelFamilyContext context, JObject result)
        {
            _context = context;
            _result = result;
            _doc = context.FamilyDoc;
        }

        private JArray Rows(string key)
        {
            if (_result[key] is JArray existing) return existing;
            var rows = new JArray();
            _result[key] = rows;
            return rows;
        }

        // ---------------------------------------------------------------- materials

        public void Materials(List<ModelFamilyMaterialOp> ops)
        {
            _context.Transact("Materials", () =>
            {
                foreach (var op in ops) ApplyMaterial(op);
            });
        }

        private void ApplyMaterial(ModelFamilyMaterialOp op)
        {
            var catalog = ModelFamilyMaterialRevit.Catalog(_doc);
            var existing = catalog.Materials.Where(m => string.Equals(m.Name, op.Name, StringComparison.Ordinal)).ToList();
            Material? material;
            string status;
            JObject? before = null;
            string? from = null;

            if (existing.Count > 0)
            {
                material = ModelFamilyMaterialRevit.MaterialById(_doc, existing[0].Id);
                status = "edited";
                if (op.DuplicateFrom.Length > 0 || op.CopyFromProject)
                    _context.Warnings.Add($"{op.Source}: the family already has '{op.Name}', so it was not duplicated or copied again; only the properties you gave were applied.");
                if (material != null) before = ModelFamilyMaterialRevit.Snapshot(material);
            }
            else
            {
                if (ModelFamilyMaterialCatalog.NearDuplicateWarning(op.Source, op.Name, catalog.NearDuplicates(op.Name)) is { } near)
                    _context.Warnings.Add(near);

                if (op.CopyFromProject)
                {
                    material = CopyFromProject(op, out from);
                    status = "copied";
                }
                else if (op.DuplicateFrom.Length > 0)
                {
                    material = Duplicate(op, catalog, out from);
                    status = "duplicated";
                }
                else
                {
                    material = Create(op);
                    status = "created";
                }
            }
            if (material == null) return;

            var changed = ApplyProperties(op, material) || status != "edited";

            var row = new JObject
            {
                ["id"] = material.Id.Value,
                ["name"] = material.Name,
                ["status"] = status,
                ["changed"] = changed,
                ["after"] = ModelFamilyMaterialRevit.Snapshot(material)
            };
            if (from != null) row["from"] = from;
            if (before != null) row["before"] = before;
            Rows("materials").Add(row);
        }

        private Material? Create(ModelFamilyMaterialOp op)
        {
            try
            {
                var id = Material.Create(_doc, op.Name);
                return _doc.GetElement(id) as Material;
            }
            catch (Exception ex)
            {
                _context.Problems.Add($"{op.Source}.name: Revit refused to create '{op.Name}': {ex.Message.TrimEnd('.')}.");
                return null;
            }
        }

        private Material? Duplicate(ModelFamilyMaterialOp op, ModelFamilyMaterialCatalog catalog, out string? from)
        {
            from = null;
            var source = $"{op.Source}.duplicateFrom";
            var resolution = catalog.Resolve(op.DuplicateFrom);
            if (ModelFamilyMaterialCatalog.ProblemOf(resolution, op.DuplicateFrom, source) is { } problem)
            {
                _context.Problems.Add(problem);
                return null;
            }
            if (ModelFamilyMaterialCatalog.WarningOf(resolution, op.DuplicateFrom, source) is { } warning) _context.Warnings.Add(warning);
            var original = ModelFamilyMaterialRevit.MaterialById(_doc, resolution.Id!.Value);
            if (original == null)
            {
                _context.Problems.Add($"{source}: material {resolution.Id} could not be read.");
                return null;
            }
            try
            {
                var copy = original.Duplicate(op.Name);
                if (copy == null) _context.Problems.Add($"{op.Source}: Revit could not duplicate '{original.Name}' as '{op.Name}'.");
                from = original.Name;
                return copy;
            }
            catch (Exception ex)
            {
                _context.Problems.Add($"{op.Source}.name: Revit refused to duplicate '{original.Name}' as '{op.Name}': {ex.Message.TrimEnd('.')}.");
                return null;
            }
        }

        private Material? CopyFromProject(ModelFamilyMaterialOp op, out string? from)
        {
            from = null;
            var project = _context.Project;
            if (project == null)
            {
                _context.Problems.Add($"{op.Source}.copyFromProject: no project document is open; pass the document argument, or create the material instead.");
                return null;
            }
            var source = $"{op.Source}.copyFromProject";
            var resolution = ModelFamilyMaterialRevit.Catalog(project).Resolve(op.Name);
            if (ModelFamilyMaterialCatalog.ProblemOf(resolution, op.Name, source, $"the project '{project.Title}'") is { } problem)
            {
                _context.Problems.Add(problem);
                return null;
            }
            if (ModelFamilyMaterialCatalog.WarningOf(resolution, op.Name, source) is { } warning) _context.Warnings.Add(warning);
            try
            {
                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new ModelFamilyMaterialKeepDestination());
                var copied = ElementTransformUtils.CopyElements(project, new List<ElementId> { new(resolution.Id!.Value) }, _doc, Transform.Identity, options);
                var material = copied.Select(id => _doc.GetElement(id)).OfType<Material>().FirstOrDefault();
                if (material == null)
                {
                    _context.Problems.Add($"{source}: Revit copied no material from '{project.Title}'; the family may already have one of that name.");
                    return null;
                }
                from = project.Title;
                if (!string.Equals(material.Name, op.Name, StringComparison.Ordinal))
                {
                    try { material.Name = op.Name; }
                    catch { _context.Warnings.Add($"{source}: the copy is called '{material.Name}'; Revit would not rename it to '{op.Name}'."); }
                }
                return material;
            }
            catch (Exception ex)
            {
                _context.Problems.Add($"{source}: Revit could not copy '{resolution.Name}' from '{project.Title}': {ex.Message.TrimEnd('.')}.");
                return null;
            }
        }

        /// <summary>Sets the properties the operation gives; true when one changed.</summary>
        private bool ApplyProperties(ModelFamilyMaterialOp op, Material material)
        {
            var changed = false;
            try
            {
                if (op.Color is { } c)
                {
                    var current = material.Color;
                    if (current == null || !current.IsValid || current.Red != c[0] || current.Green != c[1] || current.Blue != c[2])
                    {
                        material.Color = new Color((byte)c[0], (byte)c[1], (byte)c[2]);
                        changed = true;
                    }
                }
                if (op.Transparency is { } t && material.Transparency != t) { material.Transparency = t; changed = true; }
                if (op.Shininess is { } sh && material.Shininess != sh) { material.Shininess = sh; changed = true; }
                if (op.Smoothness is { } sm && material.Smoothness != sm) { material.Smoothness = sm; changed = true; }
                if (op.MaterialClass is { } cls && !string.Equals(material.MaterialClass, cls, StringComparison.Ordinal))
                {
                    material.MaterialClass = cls;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _context.Problems.Add($"{op.Source}: Revit refused a property of '{material.Name}': {ex.Message.TrimEnd('.')}.");
            }
            return changed;
        }

        // ---------------------------------------------------------------- shared lookups

        /// <summary>The element id a material reference means; false (with a problem) when it does not resolve.</summary>
        private bool ResolveMaterial(ModelFamilyMaterialRef reference, string source, out ElementId id)
        {
            id = ElementId.InvalidElementId;
            if (reference.IsNone) return true;
            if (reference.Id != null)
            {
                if (ModelFamilyMaterialRevit.MaterialById(_doc, reference.Id.Value) is { } byId)
                {
                    id = byId.Id;
                    return true;
                }
                var element = _doc.GetElement(new ElementId(reference.Id.Value));
                _context.Problems.Add(element == null
                    ? $"{source}: the family has no element {reference.Id}."
                    : $"{source}: element {reference.Id} is a {element.GetType().Name}, not a material.");
                return false;
            }
            var resolution = ModelFamilyMaterialRevit.Catalog(_doc).Resolve(reference.Name!);
            if (ModelFamilyMaterialCatalog.ProblemOf(resolution, reference.Name!, source) is { } problem)
            {
                _context.Problems.Add(problem);
                return false;
            }
            if (ModelFamilyMaterialCatalog.WarningOf(resolution, reference.Name!, source) is { } warning) _context.Warnings.Add(warning);
            id = new ElementId(resolution.Id!.Value);
            return true;
        }

        /// <summary>An element with a Material property, or null (with a problem).</summary>
        private (Element Element, Parameter Parameter)? MaterialElement(long id, string source)
        {
            var element = _doc.GetElement(new ElementId(id));
            if (element == null)
            {
                _context.Problems.Add($"{source}: the family has no element {id}. revit_get_model_family section materialAssignments lists the forms that have a material.");
                return null;
            }
            var parameter = ModelFamilyMaterialRevit.MaterialParameter(element);
            if (parameter == null)
            {
                _context.Problems.Add(ModelFamilyMaterialState.NoMaterialParameter(source, id, element.GetType().Name));
                return null;
            }
            return (element, parameter);
        }

        private void Touch(long id)
        {
            if (!_touched.Contains(id)) _touched.Add(id);
        }

        // ---------------------------------------------------------------- assign

        public void Assign(List<ModelFamilyMaterialAssignOp> ops)
        {
            _context.Transact("Assign materials", () =>
            {
                foreach (var op in ops)
                {
                    if (!ResolveMaterial(op.Material, $"{op.Source}.material", out var materialId)) continue;
                    foreach (var elementId in op.Elements)
                    {
                        if (MaterialElement(elementId, $"{op.Source}.elements") is not { } found) continue;
                        var (element, parameter) = found;
                        var before = ModelFamilyMaterialRevit.Brief(_doc, element);

                        if (ModelFamilyAssociations.Associated(_doc, parameter) is { } driving)
                        {
                            if (!op.ReplaceAssociation)
                            {
                                _context.Problems.Add($"{op.Source}.elements: element {elementId}'s material is driven by the family parameter '{driving.Definition.Name}'. " +
                                                      "Pass replaceAssociation=true to disconnect it and set the material directly, or change the parameter's value with defaults.");
                                continue;
                            }
                            if (ModelFamilyAssociations.Associate(_doc, parameter, null) is { } dissociateError)
                            {
                                _context.Problems.Add($"{op.Source}.elements: element {elementId}: {dissociateError}");
                                continue;
                            }
                        }
                        if (parameter.IsReadOnly)
                        {
                            _context.Problems.Add($"{op.Source}.elements: the Material property of element {elementId} is read-only.");
                            continue;
                        }
                        try
                        {
                            if (!parameter.Set(materialId))
                                _context.Problems.Add($"{op.Source}.elements: Revit did not accept the material for element {elementId}.");
                        }
                        catch (Exception ex)
                        {
                            _context.Problems.Add($"{op.Source}.elements: element {elementId}: {ex.Message.TrimEnd('.')}.");
                            continue;
                        }
                        if (ModelFamilyMaterialRevit.SafeId(parameter) != (materialId == ElementId.InvalidElementId ? -1 : materialId.Value))
                            _context.Problems.Add($"{op.Source}.elements: element {elementId} did not take the material (it reads {ModelFamilyMaterialRevit.SafeId(parameter)} after the change).");

                        Touch(elementId);
                        Rows("assigned").Add(new JObject
                        {
                            ["element"] = elementId,
                            ["description"] = ModelFamilyLookup.Describe(element),
                            ["before"] = before,
                            ["after"] = ModelFamilyMaterialRevit.Brief(_doc, element)
                        });
                    }
                }
            });
        }

        // ---------------------------------------------------------------- associate

        public void Associate(List<ModelFamilyMaterialAssociateOp> ops)
        {
            _context.Transact("Associate materials", () =>
            {
                foreach (var op in ops)
                {
                    FamilyParameter? familyParameter = null;
                    if (op.Parameter != null && (familyParameter = ResolveParameter(op)) == null) continue;

                    foreach (var elementId in op.Elements)
                    {
                        if (MaterialElement(elementId, $"{op.Source}.elements") is not { } found) continue;
                        var (element, parameter) = found;
                        var before = ModelFamilyAssociations.Associated(_doc, parameter)?.Definition.Name;
                        if (ModelFamilyAssociations.Associate(_doc, parameter, familyParameter) is { } error)
                        {
                            _context.Problems.Add($"{op.Source}.elements: element {elementId}: {error}");
                            continue;
                        }
                        Touch(elementId);
                        Rows("associated").Add(new JObject
                        {
                            ["element"] = elementId,
                            ["description"] = ModelFamilyLookup.Describe(element),
                            ["parameterBefore"] = before,
                            ["parameterAfter"] = ModelFamilyAssociations.Associated(_doc, parameter)?.Definition.Name,
                            ["materialAfter"] = ModelFamilyMaterialRevit.State(_doc, element)?["material"]
                        });
                    }
                }
            });
        }

        /// <summary>The material family parameter an associate entry names, adding it when asked to and it is missing.</summary>
        private FamilyParameter? ResolveParameter(ModelFamilyMaterialAssociateOp op)
        {
            var source = $"{op.Source}.parameter";
            var existing = ModelFamilyLookup.Parameter(_doc, op.Parameter!);
            if (existing != null)
            {
                if (ModelFamilyMaterialRevit.IsMaterialParameter(existing)) return existing;
                _context.Problems.Add($"{source}: '{existing.Definition.Name}' is not a material parameter (its type is {SpecLabel(existing)}); " +
                                      "a Material property can only be driven by a parameter of type Material. Pass another name, or add a material parameter with create.");
                return null;
            }
            if (op.Create == null)
            {
                var close = ModelFamilyJson.Suggest(op.Parameter!, ModelFamilyLookup.Parameters(_doc).Where(ModelFamilyMaterialRevit.IsMaterialParameter).Select(p => p.Definition.Name));
                _context.Problems.Add($"{source}: the family has no parameter '{op.Parameter}'" +
                                      (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ".") +
                                      " Pass create (for example {\"instance\": true}) to add a material parameter of that name.");
                return null;
            }

            var groupKey = ParameterRevitTypes.ResolveGroup(op.Create.Group);
            if (groupKey == null)
            {
                _context.Problems.Add($"{op.Source}.create.group: '{op.Create.Group}' is not a parameter group Revit knows (for example \"Materials and Finishes\", \"Identity Data\", \"Data\").");
                return null;
            }
            try
            {
                var group = ParameterRevitTypes.GroupOf(groupKey) ?? new ForgeTypeId();
                var created = _doc.FamilyManager.AddParameter(op.Parameter!, group, SpecTypeId.Reference.Material, op.Create.Instance);
                Rows("createdParameters").Add(new JObject
                {
                    ["name"] = created.Definition.Name,
                    ["instance"] = created.IsInstance,
                    ["group"] = op.Create.Group
                });
                return created;
            }
            catch (Exception ex)
            {
                _context.Problems.Add($"{op.Source}.create: Revit refused to add the material parameter '{op.Parameter}': {ex.Message.TrimEnd('.')}.");
                return null;
            }
        }

        private static string SpecLabel(FamilyParameter parameter)
        {
            try { return LabelUtils.GetLabelForSpec(parameter.Definition.GetDataType()); }
            catch { return "another type"; }
        }

        // ---------------------------------------------------------------- defaults

        public void Defaults(List<ModelFamilyMaterialDefaultOp> ops)
        {
            var manager = _doc.FamilyManager;
            _context.Transact("Material defaults", () =>
            {
                var original = manager.CurrentType;
                try
                {
                    foreach (var op in ops) ApplyDefault(manager, op);
                }
                finally
                {
                    if (original != null)
                    {
                        try { manager.CurrentType = original; }
                        catch { /* the transaction is rolled back or committed with the type Revit left */ }
                    }
                }
            });
        }

        private void ApplyDefault(FamilyManager manager, ModelFamilyMaterialDefaultOp op)
        {
            var source = op.Source;
            var parameter = ModelFamilyLookup.Parameter(_doc, op.Parameter, $"{source}.parameter", _context.Problems);
            if (parameter == null) return;
            if (!ModelFamilyMaterialRevit.IsMaterialParameter(parameter))
            {
                _context.Problems.Add($"{source}.parameter: '{parameter.Definition.Name}' is not a material parameter (its type is {SpecLabel(parameter)}); use revit_edit_model_family_parameters for other values.");
                return;
            }
            if (parameter.IsDeterminedByFormula)
            {
                _context.Problems.Add($"{source}.parameter: '{parameter.Definition.Name}' is determined by a formula, so its value cannot be set per type.");
                return;
            }
            if (!ResolveMaterial(op.Material, $"{source}.material", out var materialId)) return;

            var types = manager.Types.Cast<FamilyType>().ToList();
            if (types.Count == 0)
            {
                _context.Problems.Add($"{source}: the family has no types to give a default to. Creating types is the family parameters tool (#172, revit_edit_model_family_parameters).");
                return;
            }
            var targets = types;
            if (op.Type.Length > 0)
            {
                var named = types.Where(t => string.Equals(t.Name, op.Type, StringComparison.Ordinal)).ToList();
                if (named.Count == 0) named = types.Where(t => string.Equals(t.Name, op.Type, StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count == 0)
                {
                    _context.Problems.Add($"{source}.type: the family has no type '{op.Type}'. Types: {string.Join(", ", types.Select(t => $"'{t.Name}'"))}. " +
                                          "Creating types is the family parameters tool (#172, revit_edit_model_family_parameters).");
                    return;
                }
                targets = named;
            }

            var expected = materialId == ElementId.InvalidElementId ? -1 : materialId.Value;
            foreach (var type in targets)
            {
                try
                {
                    manager.CurrentType = type;
                    var before = type.HasValue(parameter) ? type.AsElementId(parameter).Value : -1;
                    manager.Set(parameter, materialId);
                    var after = type.HasValue(parameter) ? type.AsElementId(parameter).Value : -1;
                    if (after != expected)
                        _context.Problems.Add($"{source}: type '{type.Name}' did not take the value (it reads {after} after the change).");
                    Rows("defaults").Add(new JObject
                    {
                        ["type"] = type.Name,
                        ["parameter"] = parameter.Definition.Name,
                        ["instanceParameter"] = parameter.IsInstance,
                        ["before"] = before > 0 ? ModelFamilyMaterialRevit.MaterialName(_doc, before) : null,
                        ["after"] = after > 0 ? ModelFamilyMaterialRevit.MaterialName(_doc, after) : null
                    });
                }
                catch (Exception ex)
                {
                    _context.Problems.Add($"{source}: type '{type.Name}': {ex.Message.TrimEnd('.')}.");
                }
            }
        }

        // ---------------------------------------------------------------- final state

        /// <summary>The state of every element the call touched, read after the last transaction.</summary>
        public void FinalStates()
        {
            if (_touched.Count == 0) return;
            var rows = new JArray();
            foreach (var id in _touched)
                if (_doc.GetElement(new ElementId(id)) is { } element && ModelFamilyMaterialRevit.State(_doc, element) is { } state)
                    rows.Add(state);
            _result["elements"] = rows;
        }
    }
}
