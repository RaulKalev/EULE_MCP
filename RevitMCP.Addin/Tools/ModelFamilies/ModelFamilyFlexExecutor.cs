using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Parameters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// revit_flex_model_family (#176): exercises family types and parameter ranges, regenerating each case, checks
/// expectations and restores every value. Runs inside <see cref="ModelFamilySession.Trial"/>, so the session also rolls
/// the whole transaction group back: two safety nets, the explicit restore after every case and the group rollback.
/// </summary>
internal static class ModelFamilyFlexExecutor
{
    public const string What = "flex the family types";

    /// <summary>The tool: the trial, then (reopenCopy) the same type cases on a saved and reopened copy.</summary>
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var result = ModelFamilySession.Trial(uiapp, request, What, Body, cancellationToken);
        var reopen = ModelFamilyJson.Token(request.Arguments, ModelFamilyFlexArgs.ReopenCopy);
        var wanted = reopen?.Type == JTokenType.Boolean ? (bool)reopen
            : reopen?.Type == JTokenType.String && bool.TryParse(((string?)reopen)?.Trim(), out var parsed) && parsed;
        if (!wanted || !result.Success || result.Data is not JObject envelope || envelope["result"] is not JObject data) return result;
        data["reopen"] = ModelFamilyFlexReopen.Run(uiapp, request, data, cancellationToken);
        if (data["reopen"]?["equal"]?.Type == JTokenType.Boolean && !(bool)data["reopen"]!["equal"]!)
            result.Warnings.Add("The reopened copy does not behave like the family: see result.reopen.");
        return result;
    }

    /// <summary>A raw value of a family type, as Revit stores it, so a restore puts back exactly what was there.</summary>
    private sealed class RawValue
    {
        public bool Has { get; init; }
        public double? Double { get; init; }
        public int? Integer { get; init; }
        public string? Text { get; init; }
        public long? Id { get; init; }

        public static RawValue Read(FamilyType type, FamilyParameter parameter)
        {
            if (!type.HasValue(parameter)) return new RawValue();
            return parameter.StorageType switch
            {
                StorageType.Double => new RawValue { Has = true, Double = type.AsDouble(parameter) },
                StorageType.Integer => new RawValue { Has = true, Integer = type.AsInteger(parameter) },
                StorageType.String => new RawValue { Has = true, Text = type.AsString(parameter) },
                StorageType.ElementId => new RawValue { Has = true, Id = type.AsElementId(parameter)?.Value ?? ElementId.InvalidElementId.Value },
                _ => new RawValue()
            };
        }

        public void Write(FamilyManager manager, FamilyParameter parameter)
        {
            if (!Has) return;
            switch (parameter.StorageType)
            {
                case StorageType.Double when Double != null: manager.Set(parameter, Double.Value); break;
                case StorageType.Integer when Integer != null: manager.Set(parameter, Integer.Value); break;
                case StorageType.String: manager.Set(parameter, Text ?? string.Empty); break;
                case StorageType.ElementId when Id != null: manager.Set(parameter, new ElementId(Id.Value)); break;
            }
        }

        public bool SameAs(RawValue other) =>
            Has == other.Has &&
            (Double == null && other.Double == null || Double != null && other.Double != null && Math.Abs(Double.Value - other.Double.Value) <= 1e-9 * Math.Max(1, Math.Abs(Double.Value))) &&
            Integer == other.Integer &&
            string.Equals(Text ?? string.Empty, other.Text ?? string.Empty, StringComparison.Ordinal) &&
            Id == other.Id;
    }

    public static object? Body(ModelFamilyContext context)
    {
        var doc = context.FamilyDoc;
        var manager = doc.FamilyManager;
        var problems = context.Problems;

        var spec = ModelFamilyFlexSpec.Parse(context.Args, problems);
        var typeNames = ModelFamilyParameterRead.TypeNames(doc);
        var originalType = manager.CurrentType;
        var plan = ModelFamilyFlexCases.Build(spec, typeNames, originalType?.Name, problems);
        var parameters = ModelFamilyParameterRead.Parameters(doc)
            .Select(p => (Parameter: p, State: ModelFamilyParameterRead.State(p)))
            .ToList();
        var inputs = ValidateInputs(context, plan, parameters.ToDictionary(p => p.Parameter.Id.Value, p => p.State));
        var targets = ModelFamilyFlexMeasure.Resolve(doc, spec.Checks, problems);
        if (originalType == null && plan.Cases.Any(c => c.Values.Count > 0))
            problems.Add("The family has no type, so no value can be set: add a type with revit_edit_model_family_parameters, or flex without ranges and cases.");
        if (problems.Count > 0)
            return new JObject { ["plannedCases"] = plan.Cases.Count, ["types"] = new JArray(plan.Types) };

        // What the checks read, so each case reports those values.
        var readNames = spec.Checks.SelectMany(c => c.ReferencedParameters())
            .Concat(plan.Cases.SelectMany(c => c.Values.Select(v => v.Key)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 1. Snapshot: the current type and every value a case will set, per type, exactly as stored.
        FamilyType? Find(string name) => name.Length == 0 ? originalType
            : manager.Types.Cast<FamilyType>().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        var snapshot = new Dictionary<string, Dictionary<long, RawValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in plan.Cases.Where(c => c.Values.Count > 0).GroupBy(c => c.Type, StringComparer.OrdinalIgnoreCase))
        {
            var type = Find(group.Key);
            if (type == null) continue;
            var values = new Dictionary<long, RawValue>();
            foreach (var name in group.SelectMany(c => c.Values.Select(v => v.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
                if (inputs.TryGetValue(name, out var parameter)) values[parameter.Id.Value] = RawValue.Read(type, parameter);
            snapshot[group.Key] = values;
        }

        // 2. The cases, each in its own transaction: a case Revit refuses is rolled back alone.
        var results = new List<ModelFamilyFlexCaseResult>();
        var restored = true;
        string? restoreError = null;
        foreach (var flexCase in plan.Cases)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var result = new ModelFamilyFlexCaseResult { Case = flexCase };
            var failureStart = context.Failures.Count;
            result.Committed = context.TryTransact($"Flex case '{flexCase.Name}'", () =>
            {
                var type = Find(flexCase.Type) ?? throw new InvalidOperationException($"the family has no type '{flexCase.Type}'");
                manager.CurrentType = type;
                foreach (var (name, value) in flexCase.Values.Select(v => (v.Key, v.Value)))
                {
                    var parameter = inputs[name];
                    var friendly = ParameterRevitTypes.FriendlyOf(parameter.Definition.GetDataType());
                    var (converted, error) = ModelFamilyParameterValues.ToInternal(friendly, value);
                    if (converted == null) throw new InvalidOperationException($"{name}: {error}");
                    SetValue(doc, manager, parameter, converted);
                }
            }, out var caseError);
            result.Error = caseError;
            var caseFailures = context.Failures.Skip(failureStart).ToList();
            context.Failures.RemoveRange(failureStart, context.Failures.Count - failureStart);
            result.RevitFailures = ModelFamilyFlexMeasure.DescribeFailures(doc, caseFailures);

            if (result.Committed)
            {
                var values = ReadValues(doc, parameters);
                foreach (var name in readNames)
                    if (ModelFamilyFlexExpr.TryGet(values, name, out var value)) result.Parameters[name] = value?.DeepClone();
                foreach (var check in spec.Checks)
                {
                    var observation = ModelFamilyFlexMeasure.Measure(doc, check, targets[check.Id], values);
                    result.Observations[check.Id] = observation;
                    result.Failures.AddRange(ModelFamilyFlexEvaluator.Evaluate(check, observation, values));
                }
            }
            results.Add(result);

            // 3. Restore the type's values and read them back; a failed restore stops the run.
            if (flexCase.Values.Count > 0 && snapshot.TryGetValue(flexCase.Type, out var originals) && Find(flexCase.Type) is { } touched)
            {
                if (result.Committed)
                {
                    context.TryTransact($"Restore type '{touched.Name}'", () =>
                    {
                        manager.CurrentType = touched;
                        foreach (var pair in originals)
                            if (inputs.Values.FirstOrDefault(p => p.Id.Value == pair.Key) is { } parameter) pair.Value.Write(manager, parameter);
                    }, out var error);
                    if (error != null) restoreError = $"Restoring type '{touched.Name}' after case '{flexCase.Name}' failed: {error}";
                }
                if (restoreError == null)
                {
                    var touchedType = Find(flexCase.Type)!;
                    foreach (var pair in originals)
                    {
                        var parameter = inputs.Values.FirstOrDefault(p => p.Id.Value == pair.Key);
                        if (parameter != null && !RawValue.Read(touchedType, parameter).SameAs(pair.Value))
                        {
                            restoreError = $"After case '{flexCase.Name}', '{parameter.Definition.Name}' of type '{touchedType.Name}' did not read back as its original value.";
                            break;
                        }
                    }
                }
                if (restoreError != null)
                {
                    restored = false;
                    problems.Add(restoreError + " The run stopped; the session rolls every change back, so the family is unchanged. Check it with revit_get_model_family include=[\"types\"].");
                    break;
                }
            }
        }

        // 4. Put the original current type back.
        if (restored && originalType != null && !string.Equals(manager.CurrentType?.Name, originalType.Name, StringComparison.Ordinal))
        {
            var ok = context.TryTransact("Restore the current type", () => manager.CurrentType = originalType, out var error);
            if (!ok || !string.Equals(manager.CurrentType?.Name, originalType.Name, StringComparison.Ordinal))
            {
                restored = false;
                problems.Add($"The current type could not be set back to '{originalType.Name}'{(error != null ? $": {error}" : string.Empty)}. The session rolls every change back.");
            }
        }

        ModelFamilyFlexRun.ApplyFixed(results, spec.Checks);
        var summary = ModelFamilyFlexRun.Summarize(results, spec.Checks);
        context.Summary = $"{results.Count} case(s): {summary["passed"]} passed, {summary["failed"]} failed checks, {summary["refused"]} refused by Revit; " +
                          (restored ? "every value restored and the trial rolled back" : "a restore failed; the trial was rolled back");
        return new JObject
        {
            ["types"] = new JArray(plan.Types.Select(t => t.Length == 0 ? null : t)),
            ["originalType"] = originalType?.Name,
            ["casesRequested"] = plan.Requested,
            ["duplicatesDropped"] = plan.Duplicates,
            ["cases"] = new JArray(results.Select(r => r.ToJson())),
            ["summary"] = summary,
            ["restored"] = restored,
            ["units"] = "lengths in mm in family coordinates, angles in degrees, yes/no as true/false, materials by name, electrical values in the document's display unit",
            ["note"] = "Each case ran in its own transaction and its values were set back and read back afterwards; the whole trial was then rolled back, so the family is unchanged."
        };
    }

    /// <summary>
    /// The parameters the cases set, by name: each must exist and be writable (no formula, not reporting, not
    /// built-in), and each value must convert (#172's wire units). Problems name the range or case.
    /// </summary>
    private static Dictionary<string, FamilyParameter> ValidateInputs(ModelFamilyContext context, ModelFamilyFlexPlan plan,
        Dictionary<long, ModelFamilyParameterState> states)
    {
        var doc = context.FamilyDoc;
        var result = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materials = ModelFamilyParameterRead.MaterialNames(doc);
        foreach (var (name, source, value) in ModelFamilyFlexCases.Inputs(plan))
        {
            void Problem(string message)
            {
                if (reported.Add(message)) context.Problems.Add(message);
            }
            var parameter = ModelFamilyLookup.Parameter(doc, name);
            if (parameter == null)
            {
                if (reported.Add($"missing:{name}")) ModelFamilyLookup.Parameter(doc, name, source, context.Problems);
                continue;
            }
            var state = states.TryGetValue(parameter.Id.Value, out var known) ? known : ModelFamilyParameterRead.State(parameter);
            if (state.Formula.Length > 0) { Problem($"{source}: '{state.Name}' is driven by the formula '{state.Formula}'; flex the parameters the formula reads instead."); continue; }
            if (state.Reporting) { Problem($"{source}: '{state.Name}' is a reporting parameter (it measures the geometry); it cannot be set."); continue; }
            if (state.BuiltIn) { Problem($"{source}: '{state.Name}' is a built-in parameter; flex family parameters."); continue; }
            var readOnly = false;
            try { readOnly = parameter.IsReadOnly || parameter.IsDeterminedByFormula; }
            catch { /* treated as writable; Revit refuses the case otherwise */ }
            if (readOnly) { Problem($"{source}: '{state.Name}' is read-only in this family."); continue; }
            var (converted, error) = ModelFamilyParameterValues.ToInternal(state.DataType, value);
            if (converted == null) { Problem($"{source}: '{state.Name}': {error}"); continue; }
            if (converted.IsMaterial && converted.MaterialName != null && !materials.Contains(converted.MaterialName, StringComparer.OrdinalIgnoreCase))
            {
                var close = ModelFamilyJson.Suggest(converted.MaterialName, materials);
                Problem($"{source}: the family has no material '{converted.MaterialName}'" +
                        (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : "; create it with revit_edit_model_family_materials."));
                continue;
            }
            result[name] = parameter;
        }
        return result;
    }

    /// <summary>The current type's value of every parameter, in the wire form (formula results included).</summary>
    private static Dictionary<string, JToken?> ReadValues(Document doc, List<(FamilyParameter Parameter, ModelFamilyParameterState State)> parameters)
    {
        var values = new Dictionary<string, JToken?>(StringComparer.OrdinalIgnoreCase);
        var type = doc.FamilyManager.CurrentType;
        if (type == null) return values;
        foreach (var (parameter, state) in parameters)
            if (!values.ContainsKey(state.Name)) values[state.Name] = ModelFamilyParameterRead.ReadValue(doc, type, parameter, state.DataType);
        return values;
    }

    /// <summary>Sets a converted value on the current type (the conversion rules of #172's parameter executor).</summary>
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
}
