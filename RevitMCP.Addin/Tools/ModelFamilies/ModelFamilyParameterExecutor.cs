using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Parameters;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The body of revit_preview_edit_model_family_parameters and revit_edit_model_family_parameters (#172). Order, as
/// documented: dissociations, removeParameters, parameters (created and edited, formulas cleared), types (values per
/// type), formulas set, associations. Everything that can be checked without Revit is checked first by
/// <see cref="ModelFamilyParameterPlanner"/>; the rest is checked here before anything is changed; Revit's own
/// refusals roll the whole edit back with the failing step named.
/// </summary>
internal static class ModelFamilyParameterExecutor
{
    public static object? Run(ModelFamilyContext context)
    {
        var problems = context.Problems;
        var spec = ModelFamilyParameterSpecParser.Parse(context.Args, problems);
        if (spec.IsEmpty && problems.Count == 0)
            problems.Add("Nothing to do: give removeParameters, parameters, types, formulas or associations (see the tool description for their forms).");

        // The shared parameter file, only when a shared parameter is asked for.
        List<ModelFamilySharedDefinition>? definitions = null;
        var fileLabel = "the shared parameter file";
        string? filePath = null;
        string? fileSource = null;
        var needsFile = spec.Parameters.Any(p => p.Shared);
        if (needsFile)
        {
            var choice = SharedParameterFileReader.Choose(context.Uiapp.Application, context.Project ?? context.FamilyDoc, spec.SharedParameterFile);
            fileSource = choice.Source;
            if (choice.Error != null) problems.Add(choice.Error);
            else
            {
                filePath = choice.Path;
                fileLabel = $"'{choice.Path}'";
                if (!System.IO.File.Exists(choice.Path))
                    problems.Add($"The shared parameter file {fileLabel} ({choice.Source}) does not exist. This tool never creates one.");
                else
                {
                    definitions = SharedParameterFileReader.WithFile(context.Uiapp.Application, choice.Path!,
                        file => file == null ? null : SharedParameterFileReader.ReadDefinitions(file).Select(r => ModelFamilyParameterRead.Definition(r.Info)).ToList(),
                        context.Warnings);
                    if (definitions == null) problems.Add($"Revit could not read the shared parameter file {fileLabel}.");
                }
            }
        }

        var planContext = ModelFamilyParameterRead.Context(context.FamilyDoc, definitions, fileLabel);
        var plan = ModelFamilyParameterPlanner.Plan(planContext, spec);
        problems.AddRange(plan.Problems);
        context.Warnings.AddRange(plan.Warnings);

        // What only Revit can check: data types, groups, element parameters and their data types.
        var groups = ResolveGroups(context, plan, problems);
        var associations = PrepareAssociations(context, spec, plan, problems);

        var result = new JObject
        {
            ["sharedParameterFile"] = filePath == null ? null : new JObject { ["path"] = filePath, ["source"] = fileSource },
            ["impact"] = plan.Impact
        };
        if (problems.Count > 0)
        {
            result["plan"] = DescribePlan(plan);
            return result;
        }

        // ── Execute ──────────────────────────────────────────────────────────
        var doc = context.FamilyDoc;
        var manager = doc.FamilyManager;
        var report = new Report();

        EnsureType(context, plan, spec);
        Dissociate(context, associations, report);
        Remove(context, plan, report);
        Apply(context, plan, groups, definitions, filePath, report);
        var typeReport = ApplyTypes(context, spec, plan);
        SetFormulas(context, plan);
        Associate(context, associations, plan, report);

        // Read back what the family looks like now.
        var current = ModelFamilyParameterRead.Parameters(doc);
        var finalRows = new JArray(current.Where(p => p.Id.Value >= 0).Select(ModelFamilyParameterRead.Row));
        foreach (var item in report.Created.Concat(report.Changed).Concat(report.Replaced).Select(i => i.Name).Distinct(StringComparer.Ordinal))
            VerifyFormula(context, plan, item);

        result["parameters"] = new JObject
        {
            ["created"] = new JArray(report.Created.Select(r => r.Json)),
            ["changed"] = new JArray(report.Changed.Select(r => r.Json)),
            ["removed"] = new JArray(report.Removed),
            ["replacedWithShared"] = new JArray(report.Replaced.Select(r => r.Json)),
            ["all"] = finalRows
        };
        result["types"] = typeReport;
        result["associations"] = new JArray(report.Associations);
        context.Summary = Summarize(report, typeReport);
        return result;
    }

    // ── Validation that needs Revit ──────────────────────────────────────────

    /// <summary>The Revit group of every parameter the edit creates; unknown groups and data types are problems.</summary>
    private static Dictionary<string, ForgeTypeId> ResolveGroups(ModelFamilyContext context, ModelFamilyParameterPlan plan, List<string> problems)
    {
        var result = new Dictionary<string, ForgeTypeId>(StringComparer.Ordinal);
        foreach (var item in plan.Parameters.Where(i => i.Action == "create"))
        {
            var edit = item.Edit;
            string? key;
            if (edit.Group.Length > 0)
            {
                key = ParameterRevitTypes.ResolveGroup(edit.Group);
                if (key == null)
                {
                    problems.Add($"{edit.Source}.group: '{edit.Group}' is not a parameter group (for example Data, Text, Dimensions, Identity Data, Graphics, Electrical, Materials and Finishes).");
                    continue;
                }
            }
            else
            {
                key = DetailFamilySpecParser.DefaultGroupKey(item.Friendly ?? (item.Definition == null ? null : item.Definition.Friendly));
            }
            result[item.FinalName] = ParameterRevitTypes.GroupOf(key) ?? new ForgeTypeId();

            if (item.Definition == null && edit.DataType.Length > 0 && ParameterRevitTypes.ResolveSpec(edit.DataType) == null)
                problems.Add($"{edit.Source}.dataType: this Revit does not know the data type '{edit.DataType}'.");
        }
        return result;
    }

    private sealed class PreparedAssociation
    {
        public ModelFamilyAssociationEdit Edit { get; init; } = null!;
        public Element Element { get; init; } = null!;
        public Parameter ElementParameter { get; init; } = null!;
    }

    /// <summary>
    /// Finds the element and its parameter for every association, and checks up front that the parameter can be driven
    /// and that its data type matches the family parameter's. A name Revit does not know lists what can be associated.
    /// </summary>
    private static List<PreparedAssociation> PrepareAssociations(ModelFamilyContext context, ModelFamilyParameterSpec spec,
        ModelFamilyParameterPlan plan, List<string> problems)
    {
        var result = new List<PreparedAssociation>();
        foreach (var edit in spec.Associations)
        {
            var element = context.FamilyDoc.GetElement(new ElementId(edit.ElementId));
            if (element == null)
            {
                problems.Add($"{edit.Source}: the family has no element {edit.ElementId}. revit_get_model_family lists the ids of reference planes, dimensions, forms and nested components.");
                continue;
            }
            var parameter = ModelFamilyAssociations.ElementParameter(element, edit.Parameter);
            if (parameter == null || !ModelFamilyAssociations.CanAssociate(context.FamilyDoc, parameter))
            {
                var associable = ModelFamilyAssociations.Associable(context.FamilyDoc, element);
                var names = associable.OfType<JObject>().Select(r => (string?)r["builtIn"] ?? (string?)r["name"]).Where(n => !string.IsNullOrEmpty(n)).Take(14).ToList();
                problems.Add($"{edit.Source}.parameter: {(parameter == null ? $"{ModelFamilyLookup.Describe(element)} has no parameter '{edit.Parameter}'" : $"'{edit.Parameter}' of {ModelFamilyLookup.Describe(element)} cannot be driven by a family parameter")}. " +
                             (names.Count > 0 ? $"It can be associated with: {string.Join(", ", names)}." : "None of its parameters can be associated.") +
                             " revit_get_model_family section associableParameters (elementIds) lists them all.");
                continue;
            }

            if (!edit.Dissociate)
            {
                var target = plan.FindFinal(edit.FamilyParameter!);
                var elementSpec = SafeType(parameter);
                if (target != null && elementSpec != null && (target.ForgeId != null || target.DataType != null) &&
                    !ModelFamilyParameterPlanner.SameDataType(ParameterRevitTypes.FriendlyOf(elementSpec), elementSpec.TypeId, target.DataType, target.ForgeId))
                {
                    problems.Add($"{edit.Source}: '{edit.Parameter}' of {ModelFamilyLookup.Describe(element)} is {ParameterRevitTypes.SpecLabel(elementSpec)}, " +
                                 $"but the family parameter '{target.Name}' is {ModelFamilyParameterPlanner.DescribeType(target)}. Associate a parameter of the same data type " +
                                 "(a yes/no parameter drives visibility, a length drives a dimension, a material parameter drives a material).");
                    continue;
                }
            }
            result.Add(new PreparedAssociation { Edit = edit, Element = element, ElementParameter = parameter });
        }
        return result;
    }

    private static ForgeTypeId? SafeType(Parameter parameter)
    {
        try
        {
            var spec = parameter.Definition.GetDataType();
            return spec != null && spec.TypeId.Length > 0 ? spec : null;
        }
        catch { return null; }
    }

    // ── Steps ────────────────────────────────────────────────────────────────

    private sealed class ReportRow
    {
        public string Name { get; init; } = string.Empty;
        public JObject Json { get; init; } = new();
    }

    private sealed class Report
    {
        public List<ReportRow> Created { get; } = new();
        public List<ReportRow> Changed { get; } = new();
        public List<ReportRow> Replaced { get; } = new();
        public List<JToken> Removed { get; } = new();
        public List<JToken> Associations { get; } = new();
    }

    /// <summary>A family needs a type before a formula or a value can be set (Revit refuses a formula without one).</summary>
    private static void EnsureType(ModelFamilyContext context, ModelFamilyParameterPlan plan, ModelFamilyParameterSpec spec)
    {
        var manager = context.FamilyDoc.FamilyManager;
        if (manager.Types.Size > 0 && manager.CurrentType != null) return;
        if (plan.FormulaChanges.Count == 0 && plan.CurrentTypeValues.Count == 0 && spec.Types.All(t => t.Delete)) return;
        var name = context.FamilyName.Length > 0 ? context.FamilyName : "Type 1";
        context.Transact("Create the family's first type", () => manager.CurrentType = manager.NewType(name));
        context.Warnings.Add($"The family had no type; '{name}' was created so values and formulas can be set.");
    }

    private static void Dissociate(ModelFamilyContext context, List<PreparedAssociation> associations, Report report)
    {
        foreach (var prepared in associations.Where(a => a.Edit.Dissociate))
        {
            var before = ModelFamilyAssociations.Associated(context.FamilyDoc, prepared.ElementParameter)?.Definition.Name;
            if (before == null)
            {
                context.Warnings.Add($"{prepared.Edit.Source}: '{prepared.Edit.Parameter}' of element {prepared.Edit.ElementId} was not associated; nothing to release.");
                report.Associations.Add(AssociationRow(prepared, null, null, "notAssociated", null));
                continue;
            }
            context.Transact($"Dissociate {prepared.Edit.Parameter} of element {prepared.Edit.ElementId}", () =>
            {
                var error = ModelFamilyAssociations.Associate(context.FamilyDoc, prepared.ElementParameter, null);
                if (error != null) throw new InvalidOperationException(error);
            });
            var after = ModelFamilyAssociations.Associated(context.FamilyDoc, prepared.ElementParameter)?.Definition.Name;
            report.Associations.Add(AssociationRow(prepared, before, after, "dissociated", null));
        }
    }

    private static void Remove(ModelFamilyContext context, ModelFamilyParameterPlan plan, Report report)
    {
        foreach (var removal in plan.Removals)
        {
            var parameter = ModelFamilyLookup.Parameter(context.FamilyDoc, removal.Name);
            if (parameter == null) continue;
            context.Transact($"Remove parameter '{removal.Name}'", () => context.Manager.RemoveParameter(parameter));
            if (ModelFamilyLookup.Parameter(context.FamilyDoc, removal.Name) != null)
                throw new ModelFamilyStepException($"Remove parameter '{removal.Name}'", $"Remove parameter '{removal.Name}': Revit did not remove it.", new List<ModelFamilyFailure>());
            report.Removed.Add(ModelFamilyParameterRead.Row(removal));
        }
    }

    private static void Apply(ModelFamilyContext context, ModelFamilyParameterPlan plan, Dictionary<string, ForgeTypeId> groups,
        List<ModelFamilySharedDefinition>? definitions, string? filePath, Report report)
    {
        if (plan.Parameters.Count == 0 && plan.FormulaChanges.All(c => c.Formula != null)) return;
        var needsFile = plan.Parameters.Any(i => i.Definition != null && i.Action is "create" or "replaceWithShared") && filePath != null;
        if (needsFile)
        {
            SharedParameterFileReader.WithFile(context.Uiapp.Application, filePath!, file =>
            {
                ApplyParameters(context, plan, groups, file, report);
                return true;
            }, context.Warnings);
        }
        else
        {
            ApplyParameters(context, plan, groups, null, report);
        }
    }

    private static void ApplyParameters(ModelFamilyContext context, ModelFamilyParameterPlan plan, Dictionary<string, ForgeTypeId> groups,
        DefinitionFile? file, Report report)
    {
        var doc = context.FamilyDoc;
        var manager = doc.FamilyManager;
        foreach (var item in plan.Parameters)
        {
            var edit = item.Edit;
            var step = $"Parameter '{(item.Existing?.Name ?? item.FinalName)}'";
            FamilyParameter? parameter = null;

            if (item.Action == "create")
            {
                context.Transact(step, () =>
                {
                    var instance = edit.Instance ?? false;
                    if (item.Definition != null)
                    {
                        var info = new SharedDefinitionInfo
                        {
                            Group = item.Definition.Group, Name = item.Definition.Name, Guid = item.Definition.Guid, DataTypeId = item.Definition.DataTypeId
                        };
                        var external = file == null ? null : SharedParameterFileReader.Find(file, info)
                            ?? throw new InvalidOperationException($"the shared parameter definition '{info.Name}' is no longer in the file");
                        parameter = manager.AddParameter(external!, groups.TryGetValue(item.FinalName, out var g) ? g : new ForgeTypeId(), instance);
                        VerifyGuid(parameter, item.Definition);
                    }
                    else
                    {
                        var spec = ParameterRevitTypes.ResolveSpec(edit.DataType) ?? throw new InvalidOperationException($"Revit does not know the data type '{edit.DataType}'");
                        parameter = manager.AddParameter(item.FinalName, groups.TryGetValue(item.FinalName, out var g) ? g : new ForgeTypeId(), new ForgeTypeId(spec), instance);
                    }
                    if (edit.Reporting == true) manager.MakeReporting(parameter);
                    if (edit.Tooltip != null) manager.SetDescription(parameter, edit.Tooltip);
                });
                parameter = ModelFamilyLookup.Parameter(doc, item.FinalName);
                if (parameter == null)
                    throw new ModelFamilyStepException(step, $"{step}: Revit did not add the parameter.", new List<ModelFamilyFailure>());
                if (edit.Instance == true && !parameter.IsInstance)
                    throw new ModelFamilyStepException(step, $"{step}: Revit made it a type parameter instead of an instance parameter.", new List<ModelFamilyFailure>());
                report.Created.Add(new ReportRow { Name = item.FinalName, Json = ModelFamilyParameterRead.Row(parameter) });
                continue;
            }

            parameter = ModelFamilyLookup.Parameter(doc, item.Existing!.Name) ?? ModelFamilyLookup.Parameter(doc, item.FinalName);
            if (parameter == null)
                throw new ModelFamilyStepException(step, $"{step}: the parameter is no longer in the family.", new List<ModelFamilyFailure>());
            var current = parameter;
            context.Transact(step, () =>
            {
                if (item.Action == "replaceWithShared")
                {
                    var info = new SharedDefinitionInfo
                    {
                        Group = item.Definition!.Group, Name = item.Definition.Name, Guid = item.Definition.Guid, DataTypeId = item.Definition.DataTypeId
                    };
                    var external = file == null ? null : SharedParameterFileReader.Find(file, info)
                        ?? throw new InvalidOperationException($"the shared parameter definition '{info.Name}' is no longer in the file");
                    var group = current.Definition.GetGroupTypeId();
                    current = manager.ReplaceParameter(current, external!, group, edit.Instance ?? current.IsInstance);
                    VerifyGuid(current, item.Definition);
                    // Instance/type was passed to ReplaceParameter; the rest is applied below like any edit.
                }
                else if (edit.Instance != null && edit.Instance.Value != current.IsInstance)
                {
                    if (edit.Instance.Value) manager.MakeInstance(current);
                    else manager.MakeType(current);
                }
                if (edit.Reporting != null && edit.Reporting.Value != current.IsReporting)
                {
                    if (edit.Reporting.Value) manager.MakeReporting(current);
                    else manager.MakeNonReporting(current);
                }
                if (edit.Rename != null && !string.Equals(edit.Rename, current.Definition.Name, StringComparison.Ordinal))
                    manager.RenameParameter(current, edit.Rename);
                if (edit.Tooltip != null) manager.SetDescription(current, edit.Tooltip);
            });

            var read = ModelFamilyLookup.Parameter(doc, item.FinalName)
                       ?? throw new ModelFamilyStepException(step, $"{step}: the parameter is not in the family after the edit.", new List<ModelFamilyFailure>());
            if (edit.Instance != null && read.IsInstance != edit.Instance.Value)
                throw new ModelFamilyStepException(step, $"{step}: Revit left it {(read.IsInstance ? "an instance" : "a type")} parameter.", new List<ModelFamilyFailure>());
            if (edit.Rename != null && !string.Equals(read.Definition.Name, edit.Rename, StringComparison.Ordinal))
                throw new ModelFamilyStepException(step, $"{step}: Revit did not rename it to '{edit.Rename}'.", new List<ModelFamilyFailure>());
            var row = ModelFamilyParameterRead.Row(read);
            row["changes"] = new JArray(item.Changes);
            (item.Action == "replaceWithShared" ? report.Replaced : report.Changed).Add(new ReportRow { Name = item.FinalName, Json = row });
        }

        // Formulas that clear come before the values (a parameter with a formula takes no value).
        foreach (var change in plan.FormulaChanges.Where(c => c.Formula == null))
        {
            var parameter = ModelFamilyLookup.Parameter(doc, change.Parameter);
            if (parameter == null || parameter.Formula == null) continue;
            context.Transact($"Clear the formula of '{change.Parameter}'", () => manager.SetFormula(parameter, null));
        }
    }

    /// <summary>The shared GUID must survive: compare what Revit stored with the definition's.</summary>
    private static void VerifyGuid(FamilyParameter parameter, ModelFamilySharedDefinition definition)
    {
        if (!parameter.IsShared)
            throw new InvalidOperationException($"Revit added '{definition.Name}' as a family parameter, not a shared one");
        if (!string.Equals(parameter.GUID.ToString(), definition.Guid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"the GUID of '{definition.Name}' changed from {definition.Guid} to {parameter.GUID}");
    }

    private static void SetFormulas(ModelFamilyContext context, ModelFamilyParameterPlan plan)
    {
        foreach (var change in plan.FormulaChanges.Where(c => c.Formula != null))
        {
            var parameter = ModelFamilyLookup.Parameter(context.FamilyDoc, change.Parameter)
                            ?? throw new ModelFamilyStepException($"Formula of '{change.Parameter}'", $"Formula of '{change.Parameter}': the parameter is not in the family.", new List<ModelFamilyFailure>());
            context.Transact($"Formula of '{change.Parameter}'", () => context.Manager.SetFormula(parameter, change.Formula));
        }
    }

    /// <summary>Reads the formula back: a silent refusal (a formula Revit dropped) is reported.</summary>
    private static void VerifyFormula(ModelFamilyContext context, ModelFamilyParameterPlan plan, string name)
    {
        var change = plan.FormulaChanges.FirstOrDefault(c => string.Equals(c.Parameter, name, StringComparison.Ordinal));
        if (change?.Formula == null) return;
        var parameter = ModelFamilyLookup.Parameter(context.FamilyDoc, name);
        if (parameter != null && string.IsNullOrEmpty(parameter.Formula))
            context.Warnings.Add($"The formula of '{name}' reads back empty after it was set; Revit may have dropped it.");
    }

    // ── Types ────────────────────────────────────────────────────────────────

    private static JObject ApplyTypes(ModelFamilyContext context, ModelFamilyParameterSpec spec, ModelFamilyParameterPlan plan)
    {
        var doc = context.FamilyDoc;
        var manager = doc.FamilyManager;
        var created = new JArray();
        var renamed = new JArray();
        var deleted = new JArray();
        var values = new JArray();

        FamilyType? Find(string name) => manager.Types.Cast<FamilyType>().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        var originalName = manager.CurrentType?.Name;
        var currentName = originalName;
        string? makeCurrent = null;

        void SetValues(string typeName, IEnumerable<(string Parameter, JToken Value, string Source)> items, string step)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            context.Transact(step, () =>
            {
                var type = Find(typeName) ?? throw new InvalidOperationException($"the family has no type '{typeName}'");
                manager.CurrentType = type;
                foreach (var (parameterName, value, source) in list)
                {
                    var state = plan.FindFinal(parameterName)!;
                    var parameter = ModelFamilyLookup.Parameter(doc, state.Name)
                                    ?? throw new InvalidOperationException($"the family has no parameter '{parameterName}'");
                    var friendly = ParameterRevitTypes.FriendlyOf(parameter.Definition.GetDataType()) ?? state.DataType;
                    var (converted, error) = ModelFamilyParameterValues.ToInternal(friendly, value);
                    if (converted == null) throw new InvalidOperationException($"{source}: {error}");
                    var before = ModelFamilyParameterRead.ReadValue(doc, type, parameter, friendly);
                    SetValue(doc, manager, parameter, converted);
                    var after = ModelFamilyParameterRead.ReadValue(doc, type, parameter, friendly);
                    values.Add(new JObject { ["type"] = typeName, ["parameter"] = state.Name, ["before"] = before, ["after"] = after });
                }
            });
        }

        SetValues(manager.CurrentType?.Name ?? string.Empty,
            plan.CurrentTypeValues.Select(v => (v.Parameter, v.Value, v.Source + ".value")), "Values of the current type");

        foreach (var type in spec.Types)
        {
            var existing = Find(type.Name);
            var typeName = type.Name;
            if (type.Delete)
            {
                context.Transact($"Delete type '{type.Name}'", () =>
                {
                    manager.CurrentType = existing ?? throw new InvalidOperationException($"the family has no type '{type.Name}'");
                    manager.DeleteCurrentType();
                });
                if (Find(type.Name) != null)
                    throw new ModelFamilyStepException($"Delete type '{type.Name}'", $"Delete type '{type.Name}': Revit did not delete it.", new List<ModelFamilyFailure>());
                deleted.Add(type.Name);
                if (string.Equals(currentName, type.Name, StringComparison.OrdinalIgnoreCase)) currentName = null;
                continue;
            }
            if (type.Rename != null)
            {
                context.Transact($"Rename type '{type.Name}'", () =>
                {
                    manager.CurrentType = existing ?? throw new InvalidOperationException($"the family has no type '{type.Name}'");
                    manager.RenameCurrentType(type.Rename);
                });
                if (Find(type.Rename) == null)
                    throw new ModelFamilyStepException($"Rename type '{type.Name}'", $"Rename type '{type.Name}': Revit did not rename it to '{type.Rename}'.", new List<ModelFamilyFailure>());
                renamed.Add(new JObject { ["from"] = type.Name, ["to"] = type.Rename });
                if (string.Equals(currentName, type.Name, StringComparison.OrdinalIgnoreCase)) currentName = type.Rename;
                typeName = type.Rename;
            }
            else if (existing == null)
            {
                context.Transact($"Create type '{type.Name}'", () => manager.NewType(type.Name));
                if (Find(type.Name) == null)
                    throw new ModelFamilyStepException($"Create type '{type.Name}'", $"Create type '{type.Name}': Revit did not add it.", new List<ModelFamilyFailure>());
                created.Add(type.Name);
            }
            SetValues(typeName, type.Values.Select(p => (p.Key, p.Value, $"{type.Source}.values.{p.Key}")), $"Values of type '{typeName}'");
            if (type.MakeCurrent) makeCurrent = typeName;
        }

        // Put the current type back (or where makeCurrent says); Revit left the last one it touched current.
        var target = makeCurrent ?? currentName ?? manager.Types.Cast<FamilyType>().FirstOrDefault()?.Name;
        if (target != null && !string.Equals(manager.CurrentType?.Name, target, StringComparison.OrdinalIgnoreCase) && Find(target) != null)
            context.Transact("Restore the current type", () => manager.CurrentType = Find(target));

        return new JObject
        {
            ["created"] = created,
            ["renamed"] = renamed,
            ["deleted"] = deleted,
            ["values"] = values,
            ["currentType"] = manager.CurrentType?.Name,
            ["units"] = "lengths in mm, angles in degrees, area in m2, volume in m3, yes/no as true/false, materials by name"
        };
    }

    private static void SetValue(Document doc, FamilyManager manager, FamilyParameter parameter, ModelFamilyConvertedValue value)
    {
        if (value.IsMaterial)
        {
            var id = ElementId.InvalidElementId;
            if (value.MaterialName != null)
            {
                var material = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                    .FirstOrDefault(m => string.Equals(m.Name, value.MaterialName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"the family has no material '{value.MaterialName}'");
                id = material.Id;
            }
            else if (value.MaterialId != null)
            {
                id = doc.GetElement(new ElementId(value.MaterialId.Value)) is Material found ? found.Id
                    : throw new InvalidOperationException($"element {value.MaterialId} is not a material of the family");
            }
            manager.Set(parameter, id);
            return;
        }
        if (value.NeedsDocumentUnit && value.Internal is double number)
        {
            var unit = ModelFamilyParameterRead.DisplayUnit(doc, parameter.Definition.GetDataType());
            manager.Set(parameter, unit == null ? number : UnitUtils.ConvertToInternalUnits(number, unit));
            return;
        }
        switch (value.Internal)
        {
            case string text: manager.Set(parameter, text); break;
            case int whole: manager.Set(parameter, whole); break;
            case double real: manager.Set(parameter, real); break;
            default: throw new InvalidOperationException("the value has no form Revit can store");
        }
    }

    // ── Associations ─────────────────────────────────────────────────────────

    private static void Associate(ModelFamilyContext context, List<PreparedAssociation> associations, ModelFamilyParameterPlan plan, Report report)
    {
        foreach (var prepared in associations.Where(a => !a.Edit.Dissociate))
        {
            var before = ModelFamilyAssociations.Associated(context.FamilyDoc, prepared.ElementParameter)?.Definition.Name;
            var target = plan.FindFinal(prepared.Edit.FamilyParameter!)!;
            var familyParameter = ModelFamilyLookup.Parameter(context.FamilyDoc, target.Name)
                                  ?? throw new ModelFamilyStepException("Associate", $"{prepared.Edit.Source}: the family has no parameter '{target.Name}'.", new List<ModelFamilyFailure>());
            context.Transact($"Associate {prepared.Edit.Parameter} of element {prepared.Edit.ElementId} with '{target.Name}'", () =>
            {
                var error = ModelFamilyAssociations.Associate(context.FamilyDoc, prepared.ElementParameter, familyParameter);
                if (error != null) throw new InvalidOperationException(error);
            });
            var after = ModelFamilyAssociations.Associated(context.FamilyDoc, prepared.ElementParameter)?.Definition.Name;
            report.Associations.Add(AssociationRow(prepared, before, after, "associated", null));
        }
    }

    private static JObject AssociationRow(PreparedAssociation prepared, string? before, string? after, string status, string? reason) => new()
    {
        ["element"] = ModelFamilyLookup.Describe(prepared.Element),
        ["elementId"] = prepared.Edit.ElementId,
        ["parameter"] = prepared.Edit.Parameter,
        ["familyParameter"] = prepared.Edit.FamilyParameter,
        ["before"] = before,
        ["after"] = after,
        ["status"] = status,
        ["reason"] = reason
    };

    // ── Result ───────────────────────────────────────────────────────────────

    private static JObject DescribePlan(ModelFamilyParameterPlan plan) => new()
    {
        ["remove"] = new JArray(plan.Removals.Select(r => r.Name)),
        ["parameters"] = new JArray(plan.Parameters.Select(p => new JObject
        {
            ["name"] = p.FinalName,
            ["action"] = p.Action,
            ["changes"] = new JArray(p.Changes),
            ["guid"] = p.Definition?.Guid
        })),
        ["formulas"] = new JArray(plan.FormulaChanges.Select(c => new JObject { ["parameter"] = c.Parameter, ["formula"] = c.Formula }))
    };

    private static string Summarize(Report report, JObject types)
    {
        var parts = new List<string>();
        if (report.Created.Count > 0) parts.Add($"{report.Created.Count} parameter(s) created");
        if (report.Changed.Count > 0) parts.Add($"{report.Changed.Count} changed");
        if (report.Replaced.Count > 0) parts.Add($"{report.Replaced.Count} replaced with shared");
        if (report.Removed.Count > 0) parts.Add($"{report.Removed.Count} removed");
        var createdTypes = ((JArray?)types["created"])?.Count ?? 0;
        var values = ((JArray?)types["values"])?.Count ?? 0;
        if (createdTypes > 0) parts.Add($"{createdTypes} type(s) added");
        if (values > 0) parts.Add($"{values} value(s) set");
        var associated = report.Associations.Count;
        if (associated > 0) parts.Add($"{associated} association(s)");
        return parts.Count > 0 ? string.Join(", ", parts) : "no change";
    }
}
