using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Parameters;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>A family parameter as the planner sees it: read from the family, or planned.</summary>
public sealed class ModelFamilyParameterState
{
    public string Name { get; set; } = string.Empty;
    /// <summary>The friendly data type name (text, length, yesNo, …) when the map knows it.</summary>
    public string? DataType { get; set; }
    /// <summary>The data type's forge id, for the types the friendly map does not name.</summary>
    public string? ForgeId { get; set; }
    public string DataTypeLabel { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public bool Instance { get; set; }
    public bool Shared { get; set; }
    public string? Guid { get; set; }
    public bool Reporting { get; set; }
    public bool BuiltIn { get; set; }
    public string Formula { get; set; } = string.Empty;

    public ModelFamilyParameterState Clone() => (ModelFamilyParameterState)MemberwiseClone();
}

/// <summary>A definition of the shared parameter file.</summary>
public sealed class ModelFamilySharedDefinition
{
    public string Group { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string DataTypeId { get; set; } = string.Empty;
    public string? Friendly { get; set; }
}

/// <summary>An element parameter driven by a family parameter.</summary>
public sealed class ModelFamilyAssociatedRef
{
    public long ElementId { get; set; }
    public string Element { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
    public string? BuiltIn { get; set; }
}

/// <summary>What the planner needs to know about the family before the edit.</summary>
public sealed class ModelFamilyParameterContext
{
    public List<ModelFamilyParameterState> Parameters { get; } = new();
    /// <summary>parameter name → ids of the dimensions labelled with it.</summary>
    public Dictionary<string, List<long>> LabelsByParameter { get; } = new(StringComparer.Ordinal);
    /// <summary>parameter name → the element parameters it drives.</summary>
    public Dictionary<string, List<ModelFamilyAssociatedRef>> AssociatedByParameter { get; } = new(StringComparer.Ordinal);
    public List<string> TypeNames { get; } = new();
    /// <summary>Definitions of the shared parameter file; null when none was read (the caller then already reported why).</summary>
    public List<ModelFamilySharedDefinition>? SharedDefinitions { get; set; }
    public string SharedFileLabel { get; set; } = "the shared parameter file";
    /// <summary>The family's material names; null skips the check (and a missing material is then a Revit-side problem).</summary>
    public List<string>? MaterialNames { get; set; }
}

/// <summary>What will happen to one entry of <c>parameters</c>.</summary>
public sealed class ModelFamilyPlannedParameter
{
    public ModelFamilyParameterEdit Edit { get; set; } = new();
    /// <summary>The parameter before the edit; null for a new one.</summary>
    public ModelFamilyParameterState? Existing { get; set; }
    /// <summary>create | edit | replaceWithShared.</summary>
    public string Action { get; set; } = "create";
    public ModelFamilySharedDefinition? Definition { get; set; }
    public string FinalName { get; set; } = string.Empty;
    public string? Friendly { get; set; }
    public List<string> Changes { get; } = new();
}

/// <summary>A formula to set (or clear) on a parameter, by its name after the edit.</summary>
public sealed class ModelFamilyFormulaChange
{
    public string Source { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
    /// <summary>Null clears the formula.</summary>
    public string? Formula { get; set; }
}

/// <summary>A value of the current type given on a <c>parameters</c> entry.</summary>
public sealed class ModelFamilyCurrentValue
{
    public string Source { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
    public JToken Value { get; set; } = JValue.CreateNull();
}

/// <summary>The checked plan of a parameter edit: what to do, what it affects, and what stops it.</summary>
public sealed class ModelFamilyParameterPlan
{
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<ModelFamilyParameterState> Removals { get; } = new();
    public List<ModelFamilyPlannedParameter> Parameters { get; } = new();
    public List<ModelFamilyFormulaChange> FormulaChanges { get; } = new();
    public List<ModelFamilyCurrentValue> CurrentTypeValues { get; } = new();
    /// <summary>The parameters after the edit.</summary>
    public List<ModelFamilyParameterState> Final { get; } = new();
    public Dictionary<string, string> RenamedFrom { get; } = new(StringComparer.OrdinalIgnoreCase);
    public JObject Impact { get; } = new() { ["removed"] = new JArray(), ["changed"] = new JArray() };

    /// <summary>A parameter after the edit by its new name, or by the name it had before a rename.</summary>
    public ModelFamilyParameterState? FindFinal(string name)
    {
        var found = ModelFamilyParameterPlanner.Find(Final, name);
        if (found != null) return found;
        return RenamedFrom.TryGetValue(name, out var renamed) ? ModelFamilyParameterPlanner.Find(Final, renamed) : null;
    }
}

/// <summary>
/// Checks an edit of a family's parameters against the family as it is (#172) before Revit is asked: conflicts with
/// shared definitions, data types, formula cycles, instance and type rules, and what a change or a removal affects. Pure.
/// </summary>
public static class ModelFamilyParameterPlanner
{
    public static ModelFamilyParameterState? Find(IEnumerable<ModelFamilyParameterState> parameters, string name)
    {
        var list = parameters as IList<ModelFamilyParameterState> ?? parameters.ToList();
        var trimmed = name.Trim();
        return list.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.Ordinal))
               ?? list.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when two data types are the same: equal friendly names, or the same forge id with versions ignored.</summary>
    public static bool SameDataType(string? friendlyA, string? forgeA, string? friendlyB, string? forgeB)
    {
        if (friendlyA != null && friendlyB != null) return string.Equals(friendlyA, friendlyB, StringComparison.OrdinalIgnoreCase);
        if (forgeA != null && forgeB != null) return ParameterSpecMap.SameType(forgeA, forgeB);
        if (friendlyA != null && forgeB != null) return string.Equals(friendlyA, ParameterSpecMap.FriendlyOfForgeId(forgeB), StringComparison.OrdinalIgnoreCase);
        if (friendlyB != null && forgeA != null) return string.Equals(friendlyB, ParameterSpecMap.FriendlyOfForgeId(forgeA), StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public static string DescribeType(ModelFamilyParameterState state) =>
        state.DataTypeLabel.Length > 0 ? state.DataTypeLabel : state.DataType ?? state.ForgeId ?? "unknown";

    public static ModelFamilyParameterPlan Plan(ModelFamilyParameterContext context, ModelFamilyParameterSpec spec)
    {
        var plan = new ModelFamilyParameterPlan();
        var problems = plan.Problems;
        var warnings = plan.Warnings;

        var before = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in context.Parameters) before.TryAdd(parameter.Name, parameter.Formula.Length > 0 ? parameter.Formula : null);
        var usedByBefore = ModelFamilyFormulaGraph.UsedBy(ModelFamilyFormulaGraph.Build(before));

        // Dissociations run first: they free a parameter for removal.
        var released = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var association in spec.Associations.Where(a => a.Dissociate))
            released.Add($"{association.ElementId}|{association.Parameter}");
        List<ModelFamilyAssociatedRef> StillAssociated(string parameter) =>
            context.AssociatedByParameter.TryGetValue(parameter, out var list)
                ? list.Where(a => !released.Contains($"{a.ElementId}|{a.Parameter}") &&
                                  !(a.BuiltIn != null && released.Contains($"{a.ElementId}|{a.BuiltIn}"))).ToList()
                : new List<ModelFamilyAssociatedRef>();

        // 2. Removals.
        foreach (var name in spec.RemoveParameters)
        {
            var state = Find(context.Parameters, name);
            if (state == null)
            {
                var close = ModelFamilyJson.Suggest(name, context.Parameters.Select(p => p.Name));
                problems.Add($"removeParameters: the family has no parameter '{name}'" +
                             (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ". revit_get_model_family section parameters lists them."));
                continue;
            }
            if (state.BuiltIn)
            {
                problems.Add($"removeParameters: '{state.Name}' is a built-in family parameter and cannot be removed.");
                continue;
            }
            if (!plan.Removals.Contains(state)) plan.Removals.Add(state);
        }
        foreach (var state in context.Parameters)
            if (!plan.Removals.Contains(state)) plan.Final.Add(state.Clone());

        // 3. Parameters: create or edit.
        foreach (var edit in spec.Parameters) PlanParameter(plan, context, edit, usedByBefore, StillAssociated);

        // 5. Formulas: the entries of parameters first, then the formulas list; the last change to a parameter wins.
        foreach (var item in spec.Formulas)
        {
            var target = plan.FindFinal(item.Parameter);
            if (target == null)
            {
                var close = ModelFamilyJson.Suggest(item.Parameter, plan.Final.Select(p => p.Name));
                problems.Add($"{item.Source}.parameter: the family has no parameter '{item.Parameter}'" +
                             (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : "."));
                continue;
            }
            plan.FormulaChanges.RemoveAll(c => string.Equals(c.Parameter, target.Name, StringComparison.Ordinal));
            plan.FormulaChanges.Add(new ModelFamilyFormulaChange { Source = item.Source, Parameter = target.Name, Formula = item.Formula });
        }
        foreach (var change in plan.FormulaChanges)
            if (Find(plan.Final, change.Parameter) is { } target) target.Formula = change.Formula ?? string.Empty;

        CheckFormulas(plan, context, usedByBefore, StillAssociated);
        CheckValues(plan, context, spec);
        CheckAssociations(plan, spec);
        return plan;
    }

    private static void PlanParameter(ModelFamilyParameterPlan plan, ModelFamilyParameterContext context, ModelFamilyParameterEdit edit,
        Dictionary<string, List<string>> usedByBefore, Func<string, List<ModelFamilyAssociatedRef>> stillAssociated)
    {
        var problems = plan.Problems;
        var existing = edit.Name.Length > 0 ? plan.FindFinal(edit.Name) : null;
        if (existing == null && edit.Name.Length == 0 && edit.Guid.Length > 0)
            existing = plan.Final.FirstOrDefault(p => p.Shared && string.Equals(p.Guid, edit.Guid, StringComparison.OrdinalIgnoreCase));
        var item = new ModelFamilyPlannedParameter { Edit = edit, Existing = existing };

        if (existing != null)
        {
            item.Action = "edit";
            PlanExisting(plan, context, item, existing, usedByBefore, stillAssociated);
        }
        else
        {
            PlanNew(plan, context, item);
        }
        plan.Parameters.Add(item);
    }

    // ── Existing parameters ──────────────────────────────────────────────────

    private static void PlanExisting(ModelFamilyParameterPlan plan, ModelFamilyParameterContext context, ModelFamilyPlannedParameter item,
        ModelFamilyParameterState existing, Dictionary<string, List<string>> usedByBefore, Func<string, List<ModelFamilyAssociatedRef>> stillAssociated)
    {
        var edit = item.Edit;
        var source = edit.Source;
        var problems = plan.Problems;
        var originalName = existing.Name;
        item.FinalName = existing.Name;

        if (edit.Spec != null && !SameDataType(edit.Spec.Friendly, edit.Spec.ForgeId, existing.DataType, existing.ForgeId))
            problems.Add($"{source}.dataType: the data type of '{existing.Name}' ({DescribeType(existing)}) cannot be changed. " +
                         $"Remove it (removeParameters) and add it again with the new data type; formulas, labels and associations that use it must be redone.");

        if (edit.Shared || edit.SharedFlag == false)
            PlanSharedOnExisting(plan, context, item, existing);

        if (existing.BuiltIn && (edit.Instance != null || edit.Reporting != null || edit.Rename != null || edit.Shared || edit.Tooltip != null))
            problems.Add($"{source}: '{existing.Name}' is a built-in family parameter; its instance/type, reporting, name, sharing and tooltip cannot be changed.");

        if (edit.Instance != null && edit.Instance.Value != existing.Instance)
        {
            item.Changes.Add(edit.Instance.Value ? "instance" : "type");
            existing.Instance = edit.Instance.Value;
        }
        if (edit.Reporting != null && edit.Reporting.Value != existing.Reporting)
        {
            item.Changes.Add(edit.Reporting.Value ? "reporting" : "nonReporting");
            existing.Reporting = edit.Reporting.Value;
        }
        if (edit.Reporting == true && !existing.Instance)
            problems.Add($"{source}: '{existing.Name}' is a reporting parameter, which must be an instance parameter. Add instance: true.");

        if (edit.Rename != null && !string.Equals(edit.Rename, existing.Name, StringComparison.Ordinal))
        {
            if (existing.Shared)
                problems.Add($"{source}.rename: '{existing.Name}' is a shared parameter; its name belongs to the shared parameter file and cannot be changed here.");
            else if (Find(plan.Final, edit.Rename) is { } clash && !ReferenceEquals(clash, existing))
                problems.Add($"{source}.rename: the family already has a parameter '{edit.Rename}'.");
            else
            {
                plan.RenamedFrom[existing.Name] = edit.Rename;
                existing.Name = edit.Rename;
                item.FinalName = edit.Rename;
                item.Changes.Add("rename");
            }
        }
        if (edit.Tooltip != null) item.Changes.Add("tooltip");
        if (edit.Group.Length > 0 && ParameterSpecMap.Normalize(edit.Group) != ParameterSpecMap.Normalize(existing.Group))
            plan.Warnings.Add($"{source}.group: the group of an existing parameter cannot be changed through the Revit API; '{existing.Name}' keeps '{existing.Group}'.");

        item.Friendly = existing.DataType;
        RecordFormulaAndValue(plan, item, existing);

        if (item.Changes.Count > 0 || edit.FormulaGiven)
            AddChangedImpact(plan, context, originalName, item, usedByBefore, stillAssociated);
    }

    private static void PlanSharedOnExisting(ModelFamilyParameterPlan plan, ModelFamilyParameterContext context, ModelFamilyPlannedParameter item,
        ModelFamilyParameterState existing)
    {
        var edit = item.Edit;
        var source = edit.Source;
        var problems = plan.Problems;

        if (edit.SharedFlag == false)
        {
            if (existing.Shared)
                problems.Add($"{source}.shared: '{existing.Name}' is a shared parameter and cannot be turned into a family parameter. Remove it and add it again as a family parameter.");
            return;
        }

        if (existing.Shared)
        {
            if (edit.Guid.Length > 0 && !string.Equals(edit.Guid, existing.Guid, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{source}.guid: the family has a shared parameter '{existing.Name}' with GUID {existing.Guid}, but {edit.Guid} was requested. " +
                             "Same name, different GUID: remove the family's parameter first, or ask for its own GUID.");
                return;
            }
            if (edit.Guid.Length == 0 && context.SharedDefinitions != null)
            {
                var candidates = context.SharedDefinitions.Where(d => string.Equals(d.Name, existing.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count > 0 && !candidates.Any(d => string.Equals(d.Guid, existing.Guid, StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"{source}: the family's shared parameter '{existing.Name}' has GUID {existing.Guid}, but {context.SharedFileLabel} defines '{existing.Name}' " +
                                 $"as {string.Join(", ", candidates.Select(d => d.Guid))}. Same name, different GUID: the family's parameter is not the company's.");
            }
            return;
        }

        // A family parameter asked to become shared: ReplaceParameter keeps formulas, labels and associations.
        var definition = ResolveDefinition(plan, context, edit, existing.Name, source);
        if (definition == null) return;
        if (!string.Equals(definition.Name, existing.Name, StringComparison.Ordinal))
        {
            problems.Add($"{source}: the family parameter '{existing.Name}' would be replaced by the shared parameter '{definition.Name}'; replacing keeps the name. " +
                         $"Rename the family parameter first (rename), or ask for the definition that is called '{existing.Name}'.");
            return;
        }
        if (!SameDataType(existing.DataType, existing.ForgeId, definition.Friendly, definition.DataTypeId))
        {
            problems.Add($"{source}: '{existing.Name}' is {DescribeType(existing)}, but the shared definition is {definition.Friendly ?? definition.DataTypeId}. " +
                         "A shared parameter replaces a family parameter only when the data types match; otherwise remove it and add the shared one.");
            return;
        }
        item.Action = "replaceWithShared";
        item.Definition = definition;
        item.Changes.Add("replacedWithShared");
        existing.Shared = true;
        existing.Guid = definition.Guid;
    }

    // ── New parameters ───────────────────────────────────────────────────────

    private static void PlanNew(ModelFamilyParameterPlan plan, ModelFamilyParameterContext context, ModelFamilyPlannedParameter item)
    {
        var edit = item.Edit;
        var source = edit.Source;
        var problems = plan.Problems;
        item.Action = "create";
        item.FinalName = edit.Name;

        if (edit.Rename != null)
            problems.Add($"{source}.rename: '{edit.Name}' is not a parameter of the family, so there is nothing to rename. Create it under the new name instead.");
        var state = new ModelFamilyParameterState
        {
            Name = edit.Name,
            Instance = edit.Instance ?? false,
            Reporting = edit.Reporting ?? false,
            Group = edit.Group
        };

        if (edit.Shared)
        {
            if (context.SharedDefinitions == null)
            {
                // The shared parameter file problem was reported by the caller; keep the name so later checks do not pile on.
                state.Shared = true;
                state.Name = edit.Name;
                if (state.Name.Length > 0) plan.Final.Add(state);
                return;
            }
            var definition = ResolveDefinition(plan, context, edit, edit.Name, source);
            if (definition == null)
            {
                if (state.Name.Length > 0) plan.Final.Add(state);
                return;
            }
            if (edit.Spec != null && !SameDataType(edit.Spec.Friendly, edit.Spec.ForgeId, definition.Friendly, definition.DataTypeId))
            {
                problems.Add($"{source}.dataType: '{edit.DataType}' differs from the data type of the shared definition '{definition.Name}' ({definition.Friendly ?? definition.DataTypeId}). " +
                             "A shared parameter always has its definition's data type: leave dataType out, or ask for the definition you mean (guid).");
                return;
            }
            if (Find(plan.Final, definition.Name) is { } clash)
            {
                problems.Add(clash.Shared && string.Equals(clash.Guid, definition.Guid, StringComparison.OrdinalIgnoreCase)
                    ? $"{source}: the family already has the shared parameter '{definition.Name}' (GUID {definition.Guid}); edit it (it is an existing parameter) instead of adding it."
                    : $"{source}: the family already has a parameter '{definition.Name}'" +
                      (clash.Shared ? $" with GUID {clash.Guid}, but the definition is {definition.Guid}. Same name, different GUID: remove the family's parameter first."
                          : " that is not shared. Ask for shared: true on it to replace it with the definition (same data type), or remove it first."));
                return;
            }
            item.Definition = definition;
            item.FinalName = definition.Name;
            item.Friendly = definition.Friendly;
            state.Name = definition.Name;
            state.Shared = true;
            state.Guid = definition.Guid;
            state.DataType = definition.Friendly;
            state.ForgeId = definition.DataTypeId;
            state.DataTypeLabel = definition.Friendly ?? definition.DataTypeId;
        }
        else
        {
            if (edit.Spec == null)
            {
                if (edit.DataType.Length == 0)
                    problems.Add($"{source}: '{edit.Name}' is not a parameter of the family, so it is created, and a new parameter needs a dataType " +
                                 $"({string.Join(", ", ParameterSpecMap.FriendlySpecNames.Take(10))}, …).");
                return;
            }
            state.DataType = edit.Spec.Friendly;
            state.ForgeId = edit.Spec.ForgeId;
            state.DataTypeLabel = edit.Spec.Friendly ?? edit.Spec.ForgeId ?? edit.DataType;
            item.Friendly = edit.Spec.Friendly;
        }

        if (edit.Reporting == true && !state.Instance)
            problems.Add($"{source}: '{state.Name}' is a reporting parameter, which must be an instance parameter. Add instance: true.");
        plan.Final.Add(state);
        item.Changes.Add("created");
        RecordFormulaAndValue(plan, item, state);
    }

    /// <summary>
    /// The definition of the shared parameter file an entry names: by guid, else by name. A name that two definitions
    /// share with different GUIDs is ambiguous and needs the guid.
    /// </summary>
    private static ModelFamilySharedDefinition? ResolveDefinition(ModelFamilyParameterPlan plan, ModelFamilyParameterContext context,
        ModelFamilyParameterEdit edit, string name, string source)
    {
        var definitions = context.SharedDefinitions;
        if (definitions == null) return null;
        var file = context.SharedFileLabel;
        if (edit.Guid.Length > 0)
        {
            var byGuid = definitions.FirstOrDefault(d => string.Equals(d.Guid, edit.Guid, StringComparison.OrdinalIgnoreCase));
            if (byGuid == null)
            {
                plan.Problems.Add($"{source}.guid: {file} has no definition with GUID {edit.Guid}. Shared parameters are never created by this tool: " +
                                  "add the definition in Revit (Manage > Shared Parameters), or use another shared parameter file (sharedParameterFile).");
                return null;
            }
            if (name.Length > 0 && !string.Equals(byGuid.Name, name, StringComparison.Ordinal))
            {
                plan.Problems.Add($"{source}: GUID {edit.Guid} is '{byGuid.Name}' in {file}, not '{name}'. Same GUID, different name: use the definition's name or leave the name out.");
                return null;
            }
            return byGuid;
        }

        var candidates = definitions.Where(d => string.Equals(d.Name, name, StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0) candidates = definitions.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Select(c => c.Guid.ToLowerInvariant()).Distinct().Count() > 1)
        {
            plan.Problems.Add($"{source}: {file} has {candidates.Count} definitions named '{name}' with different GUIDs " +
                              $"({string.Join("; ", candidates.Select(c => $"{c.Guid} in group '{c.Group}', {c.Friendly ?? c.DataTypeId}"))}). " +
                              "That is ambiguous: pass guid to say which one, and fix the duplicate in the file.");
            return null;
        }
        if (candidates.Count >= 1) return candidates[0];

        var close = ModelFamilyJson.Suggest(name, definitions.Select(d => d.Name).Distinct());
        plan.Problems.Add($"{source}: {file} has no shared parameter '{name}'" +
                          (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ".") +
                          " Shared parameters are never created by this tool: add the definition in Revit (Manage > Shared Parameters), or make it a family parameter (shared omitted, with a dataType).");
        return null;
    }

    private static void RecordFormulaAndValue(ModelFamilyParameterPlan plan, ModelFamilyPlannedParameter item, ModelFamilyParameterState state)
    {
        var edit = item.Edit;
        if (edit.FormulaGiven)
            plan.FormulaChanges.Add(new ModelFamilyFormulaChange { Source = edit.Source, Parameter = state.Name, Formula = edit.Formula });
        if (edit.Value != null)
            plan.CurrentTypeValues.Add(new ModelFamilyCurrentValue { Source = edit.Source, Parameter = state.Name, Value = edit.Value });
    }

    private static void AddChangedImpact(ModelFamilyParameterPlan plan, ModelFamilyParameterContext context, string originalName, ModelFamilyPlannedParameter item,
        Dictionary<string, List<string>> usedByBefore, Func<string, List<ModelFamilyAssociatedRef>> stillAssociated)
    {
        var dependents = ModelFamilyFormulaGraph.Dependents(usedByBefore, originalName);
        var labels = context.LabelsByParameter.TryGetValue(originalName, out var found) ? found : new List<long>();
        plan.Impact.Value<JArray>("changed")!.Add(ImpactRow(originalName, dependents, labels, stillAssociated(originalName), item.Changes));
    }

    private static JObject ImpactRow(string parameter, List<string> dependents, List<long> labels, List<ModelFamilyAssociatedRef> associated, IEnumerable<string>? changes = null)
    {
        var row = new JObject
        {
            ["parameter"] = parameter,
            ["usedByFormulas"] = new JArray(dependents),
            ["dimensionLabels"] = new JArray(labels),
            ["associated"] = new JArray(associated.Select(a => new JObject
            {
                ["elementId"] = a.ElementId, ["element"] = a.Element, ["parameter"] = a.Parameter
            }))
        };
        if (changes != null) row["changes"] = new JArray(changes);
        return row;
    }

    // ── Formulas ─────────────────────────────────────────────────────────────

    private static void CheckFormulas(ModelFamilyParameterPlan plan, ModelFamilyParameterContext context,
        Dictionary<string, List<string>> usedByBefore, Func<string, List<ModelFamilyAssociatedRef>> stillAssociated)
    {
        var problems = plan.Problems;
        var finalNames = plan.Final.Select(p => p.Name).ToList();
        var removedNames = plan.Removals.Select(r => r.Name).ToList();
        // Names the formulas may use: the family after the edit, what is being removed, and what is being renamed.
        var known = finalNames.Concat(removedNames).Concat(plan.RenamedFrom.Keys).Distinct(StringComparer.Ordinal).ToList();

        var dependsOn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unknownReported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in plan.Final)
        {
            var refs = ModelFamilyFormula.References(state.Formula, known);
            var mapped = refs.Parameters.Select(r => plan.RenamedFrom.TryGetValue(r, out var renamed) && !finalNames.Contains(r, StringComparer.Ordinal) ? renamed : r).Distinct(StringComparer.Ordinal).ToList();
            dependsOn[state.Name] = mapped.Where(r => finalNames.Contains(r, StringComparer.Ordinal)).ToList();

            var changed = plan.FormulaChanges.FirstOrDefault(c => string.Equals(c.Parameter, state.Name, StringComparison.Ordinal));
            foreach (var gone in mapped.Where(r => removedNames.Contains(r, StringComparer.Ordinal)))
            {
                if (!finalNames.Contains(gone, StringComparer.Ordinal))
                    problems.Add($"removeParameters: '{gone}' cannot be removed: the formula of '{state.Name}' ({state.Formula}) uses it. " +
                                 $"Revit would delete that formula along with the parameter. Change or clear the formula in this call " +
                                 $"(parameters: [{{\"name\": \"{state.Name}\", \"formula\": null}}]), or remove '{state.Name}' too.");
                // Removed and added again: Revit deletes the formula with the old parameter, so an unchanged one would be lost.
                else if (changed == null)
                    problems.Add($"removeParameters: '{gone}' is removed and added again, but Revit deletes the formula of '{state.Name}' ({state.Formula}) " +
                                 $"together with the old '{gone}'. Set it again in this call (formulas: [{{\"parameter\": \"{state.Name}\", \"formula\": \"{state.Formula}\"}}]), " +
                                 "or clear it (formula: null).");
            }
            if (changed?.Formula != null && unknownReported.Add(state.Name))
            {
                foreach (var unknown in refs.Unknown)
                    plan.Warnings.Add($"{changed.Source}: the formula of '{state.Name}' uses '{unknown}', which is not a parameter of the family. Revit decides whether that is valid.");
            }
        }

        var cycle = ModelFamilyFormulaGraph.FindCycle(dependsOn);
        if (cycle != null)
            problems.Add($"formulas: circular reference {ModelFamilyFormulaGraph.DescribeCycle(cycle)}. A formula cannot depend on itself, directly or through other parameters; change one of them.");

        // Revit: a type parameter's formula cannot use an instance parameter.
        foreach (var state in plan.Final.Where(s => s.Formula.Length > 0 && !s.Instance))
        {
            foreach (var used in dependsOn[state.Name])
            {
                var target = Find(plan.Final, used);
                if (target != null && target.Instance)
                    problems.Add($"formulas: '{state.Name}' is a type parameter, but its formula uses the instance parameter '{used}'. " +
                                 $"Revit does not allow that: make '{state.Name}' an instance parameter (instance: true), or use a type parameter in the formula.");
            }
        }
        foreach (var state in plan.Final.Where(s => s.Reporting && !s.Instance))
            problems.Add($"parameters: '{state.Name}' is a reporting parameter, which must be an instance parameter. Add instance: true.");

        // What a removal affects.
        foreach (var removal in plan.Removals)
        {
            var users = usedByBefore.TryGetValue(removal.Name, out var list) ? list.Where(n => !removedNames.Contains(n, StringComparer.Ordinal)).ToList() : new List<string>();
            var labels = context.LabelsByParameter.TryGetValue(removal.Name, out var l) ? l : new List<long>();
            var associated = stillAssociated(removal.Name);
            plan.Impact.Value<JArray>("removed")!.Add(ImpactRow(removal.Name, users, labels, associated));
            if (labels.Count > 0)
                plan.Warnings.Add($"removeParameters: removing '{removal.Name}' releases the label of dimension {string.Join(", ", labels.Take(10))}.");
            if (associated.Count > 0)
                plan.Warnings.Add($"removeParameters: removing '{removal.Name}' releases {associated.Count} association(s) (element {string.Join(", ", associated.Select(a => $"{a.ElementId} {a.Parameter}").Take(10))}).");
        }
    }

    // ── Type values ──────────────────────────────────────────────────────────

    private static void CheckValues(ModelFamilyParameterPlan plan, ModelFamilyParameterContext context, ModelFamilyParameterSpec spec)
    {
        foreach (var value in plan.CurrentTypeValues)
            CheckValue(plan, context, $"{value.Source}.value", value.Parameter, value.Value);

        var names = new List<string>(context.TypeNames);
        var makeCurrent = spec.Types.Where(t => t.MakeCurrent).ToList();
        if (makeCurrent.Count > 1)
            plan.Problems.Add($"types: makeCurrent is set on {makeCurrent.Count} types ({string.Join(", ", makeCurrent.Select(t => $"'{t.Name}'"))}); only one can be current.");

        foreach (var type in spec.Types)
        {
            var exists = names.Any(n => string.Equals(n, type.Name, StringComparison.OrdinalIgnoreCase));
            if (type.Delete)
            {
                if (!exists)
                    plan.Problems.Add($"{type.Source}: the family has no type '{type.Name}'" + SuggestType(type.Name, names));
                else
                {
                    names.RemoveAll(n => string.Equals(n, type.Name, StringComparison.OrdinalIgnoreCase));
                    if (names.Count == 0)
                        plan.Problems.Add($"{type.Source}: '{type.Name}' is the last type of the family and Revit does not delete the last type. Rename it, or add another type first.");
                }
                continue;
            }
            if (type.Rename != null)
            {
                if (!exists)
                    plan.Problems.Add($"{type.Source}: the family has no type '{type.Name}' to rename" + SuggestType(type.Name, names));
                else if (!string.Equals(type.Rename, type.Name, StringComparison.OrdinalIgnoreCase) && names.Any(n => string.Equals(n, type.Rename, StringComparison.OrdinalIgnoreCase)))
                    plan.Problems.Add($"{type.Source}.rename: the family already has a type '{type.Rename}'.");
                else
                {
                    names.RemoveAll(n => string.Equals(n, type.Name, StringComparison.OrdinalIgnoreCase));
                    names.Add(type.Rename);
                }
            }
            else if (!exists)
            {
                names.Add(type.Name);
            }
            foreach (var pair in type.Values)
                CheckValue(plan, context, $"{type.Source}.values.{pair.Key}", pair.Key, pair.Value);
        }
    }

    private static string SuggestType(string wanted, IEnumerable<string> names)
    {
        var close = ModelFamilyJson.Suggest(wanted, names);
        return close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ".";
    }

    private static void CheckValue(ModelFamilyParameterPlan plan, ModelFamilyParameterContext context, string source, string parameter, JToken value)
    {
        var state = plan.FindFinal(parameter);
        if (state == null)
        {
            var close = ModelFamilyJson.Suggest(parameter, plan.Final.Select(p => p.Name));
            plan.Problems.Add($"{source}: the family has no parameter '{parameter}'" +
                              (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : "."));
            return;
        }
        if (state.Formula.Length > 0)
        {
            plan.Problems.Add($"{source}: '{state.Name}' is driven by the formula {state.Formula}; a value cannot be set on it. Clear the formula first (formula: null) or leave the value out.");
            return;
        }
        var friendly = state.DataType ?? (state.ForgeId != null ? ParameterSpecMap.FriendlyOfForgeId(state.ForgeId) : null);
        if (friendly == null && state.Shared && state.DataType == null && state.ForgeId == null) return; // unresolved shared definition
        var (converted, error) = ModelFamilyParameterValues.ToInternal(friendly, value);
        if (error != null)
        {
            plan.Problems.Add($"{source}: '{state.Name}' ({DescribeType(state)}): {error} Expected {ModelFamilyParameterValues.ExpectedForm(friendly)}.");
            return;
        }
        if (converted is { IsMaterial: true, MaterialName: { } material } && context.MaterialNames != null &&
            !context.MaterialNames.Contains(material, StringComparer.OrdinalIgnoreCase))
        {
            var close = ModelFamilyJson.Suggest(material, context.MaterialNames);
            plan.Problems.Add($"{source}: the family has no material '{material}'" +
                              (close.Count > 0 ? $"; close matches: {string.Join(", ", close.Select(n => $"'{n}'"))}." : $"; its materials are {(context.MaterialNames.Count > 0 ? string.Join(", ", context.MaterialNames.Take(8).Select(n => $"'{n}'")) : "none")}.") +
                              " A material must exist in the family before a parameter can use it (revit_edit_model_family_materials creates them).");
        }
    }

    // ── Associations ─────────────────────────────────────────────────────────

    private static void CheckAssociations(ModelFamilyParameterPlan plan, ModelFamilyParameterSpec spec)
    {
        foreach (var association in spec.Associations.Where(a => !a.Dissociate))
        {
            if (plan.FindFinal(association.FamilyParameter!) != null) continue;
            var close = ModelFamilyJson.Suggest(association.FamilyParameter!, plan.Final.Select(p => p.Name));
            plan.Problems.Add($"{association.Source}.familyParameter: the family has no parameter '{association.FamilyParameter}'" +
                              (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ". Create it in this call (parameters) or check its name."));
        }
    }
}
