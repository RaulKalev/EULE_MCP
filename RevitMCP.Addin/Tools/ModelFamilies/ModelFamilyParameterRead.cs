using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Parameters;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// Reads a family's parameters, formula graph, dimension labels, associations and type values for the parameter tools
/// and the <c>parameters</c>, <c>types</c> and <c>associableParameters</c> sections of revit_get_model_family (#172).
/// The friendly data type names and group labels come from the project parameter tools' helpers, so both agree.
/// </summary>
internal static class ModelFamilyParameterRead
{
    public static List<FamilyParameter> Parameters(Document familyDoc) => ModelFamilyLookup.Parameters(familyDoc);

    /// <summary>One family parameter as the planner sees it.</summary>
    public static ModelFamilyParameterState State(FamilyParameter parameter)
    {
        var state = new ModelFamilyParameterState { Name = parameter.Definition.Name };
        try { state.Instance = parameter.IsInstance; } catch { /* unreadable: type */ }
        try { state.Reporting = parameter.IsReporting; } catch { /* not reporting */ }
        try { state.BuiltIn = parameter.Id.Value < 0; } catch { /* a family parameter */ }
        try { state.Formula = parameter.Formula ?? string.Empty; } catch { /* no formula */ }
        try
        {
            if (parameter.IsShared)
            {
                state.Shared = true;
                state.Guid = parameter.GUID.ToString();
            }
        }
        catch { /* GUID is only available for shared parameters */ }
        try
        {
            var spec = parameter.Definition.GetDataType();
            state.ForgeId = spec != null && spec.TypeId.Length > 0 ? spec.TypeId : null;
            state.DataType = ParameterRevitTypes.FriendlyOf(spec);
            state.DataTypeLabel = ParameterRevitTypes.SpecLabel(spec);
        }
        catch { /* a built-in parameter without a data type */ }
        try { state.Group = ParameterRevitTypes.GroupLabel(parameter.Definition.GetGroupTypeId()); }
        catch { state.Group = string.Empty; }
        return state;
    }

    /// <summary>The dimensions of the family by the name of the parameter that labels them.</summary>
    public static Dictionary<string, List<long>> LabelsByParameter(Document familyDoc)
    {
        var result = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var dimension in new FilteredElementCollector(familyDoc).OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            try
            {
                var label = dimension.FamilyLabel;
                if (label == null) continue;
                var name = label.Definition.Name;
                if (!result.TryGetValue(name, out var list)) result[name] = list = new List<long>();
                list.Add(dimension.Id.Value);
            }
            catch
            {
                // A dimension that cannot carry a label (a multi-segment one without an equality constraint).
            }
        }
        return result;
    }

    /// <summary>The element parameters a family parameter drives, with the element they belong to.</summary>
    public static List<ModelFamilyAssociatedRef> Associated(Document familyDoc, FamilyParameter parameter)
    {
        var result = new List<ModelFamilyAssociatedRef>();
        try
        {
            foreach (Parameter linked in parameter.AssociatedParameters)
            {
                try
                {
                    var element = linked.Element;
                    result.Add(new ModelFamilyAssociatedRef
                    {
                        ElementId = element?.Id.Value ?? 0,
                        Element = element == null ? string.Empty : ModelFamilyLookup.Describe(element),
                        Parameter = linked.Definition?.Name ?? string.Empty,
                        BuiltIn = (linked.Definition as InternalDefinition)?.BuiltInParameter is { } bip && bip != BuiltInParameter.INVALID ? bip.ToString() : null
                    });
                }
                catch
                {
                    // An association Revit cannot describe is left out.
                }
            }
        }
        catch
        {
            // Built-in parameters may not list associations.
        }
        return result;
    }

    public static List<string> TypeNames(Document familyDoc) =>
        familyDoc.FamilyManager.Types.Cast<FamilyType>().Select(t => t.Name).ToList();

    public static List<string> MaterialNames(Document familyDoc) =>
        new FilteredElementCollector(familyDoc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The planner's picture of the family. <paramref name="definitions"/> are those of the shared parameter file, when read.</summary>
    public static ModelFamilyParameterContext Context(Document familyDoc, List<ModelFamilySharedDefinition>? definitions, string fileLabel)
    {
        var context = new ModelFamilyParameterContext { SharedDefinitions = definitions, SharedFileLabel = fileLabel };
        foreach (var parameter in Parameters(familyDoc))
        {
            var state = State(parameter);
            context.Parameters.Add(state);
            var associated = Associated(familyDoc, parameter);
            if (associated.Count > 0) context.AssociatedByParameter[state.Name] = associated;
        }
        foreach (var pair in LabelsByParameter(familyDoc)) context.LabelsByParameter[pair.Key] = pair.Value;
        context.TypeNames.AddRange(TypeNames(familyDoc));
        context.MaterialNames = MaterialNames(familyDoc);
        return context;
    }

    public static ModelFamilySharedDefinition Definition(SharedDefinitionInfo info) => new()
    {
        Group = info.Group,
        Name = info.Name,
        Guid = info.Guid,
        DataTypeId = info.DataTypeId,
        Friendly = ParameterRevitTypes.FriendlyOf(ParameterRevitTypes.SpecOf(info.DataTypeId))
    };

    /// <summary>The unit the document shows a data type in (volts, amperes, watts, slope); null when Revit has none.</summary>
    public static ForgeTypeId? DisplayUnit(Document doc, ForgeTypeId? spec)
    {
        if (spec == null || spec.TypeId.Length == 0) return null;
        try { return doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId(); }
        catch { return null; }
    }

    /// <summary>A type's value of a parameter in the wire form (mm, degrees, names), or null when it has none.</summary>
    public static JToken? ReadValue(Document familyDoc, FamilyType type, FamilyParameter parameter, string? friendly)
    {
        try
        {
            if (!type.HasValue(parameter)) return null;
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                {
                    var value = type.AsDouble(parameter);
                    if (value == null) return null;
                    if (friendly != null && ModelFamilyParameterValues.DocumentUnitTypes.Contains(friendly))
                    {
                        var unit = DisplayUnit(familyDoc, parameter.Definition.GetDataType());
                        return unit == null ? value.Value : Math.Round(UnitUtils.ConvertFromInternalUnits(value.Value, unit), 6);
                    }
                    return ModelFamilyParameterValues.ToWireNumber(friendly, value.Value);
                }
                case StorageType.Integer:
                {
                    var value = type.AsInteger(parameter);
                    if (value == null) return null;
                    return friendly == "yesNo" ? value.Value != 0 : value.Value;
                }
                case StorageType.String:
                    return type.AsString(parameter);
                case StorageType.ElementId:
                {
                    var id = type.AsElementId(parameter);
                    if (id == null || id == ElementId.InvalidElementId) return null;
                    var element = familyDoc.GetElement(id);
                    return element != null ? element.Name : id.Value;
                }
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A parameter as a result row.</summary>
    public static JObject Row(FamilyParameter parameter)
    {
        var state = State(parameter);
        return Row(state, parameter.Id.Value);
    }

    public static JObject Row(ModelFamilyParameterState state, long? id = null) => new()
    {
        ["id"] = id,
        ["name"] = state.Name,
        ["dataType"] = state.DataType ?? state.DataTypeLabel,
        ["dataTypeLabel"] = state.DataTypeLabel,
        ["group"] = state.Group,
        ["instance"] = state.Instance,
        ["shared"] = state.Shared,
        ["guid"] = state.Guid,
        ["reporting"] = state.Reporting,
        ["formula"] = state.Formula.Length > 0 ? state.Formula : null
    };

    // ── Inspection sections ──────────────────────────────────────────────────

    public static JToken ParametersSection(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var parameters = Parameters(doc);
        var states = parameters.Select(State).ToList();
        var formulas = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var state in states) formulas.TryAdd(state.Name, state.Formula.Length > 0 ? state.Formula : null);
        var dependsOn = ModelFamilyFormulaGraph.Build(formulas);
        var usedBy = ModelFamilyFormulaGraph.UsedBy(dependsOn);
        var labels = LabelsByParameter(doc);

        var rows = new JArray();
        foreach (var parameter in parameters.Take(Math.Max(1, limit)))
        {
            var state = State(parameter);
            var row = Row(state, parameter.Id.Value);
            row["builtIn"] = state.BuiltIn;
            row["dependsOn"] = new JArray(dependsOn.TryGetValue(state.Name, out var deps) ? deps : new List<string>());
            row["usedBy"] = new JArray(usedBy.TryGetValue(state.Name, out var users) ? users : new List<string>());
            row["labels"] = new JArray(labels.TryGetValue(state.Name, out var dims) ? dims : new List<long>());
            row["associated"] = new JArray(Associated(doc, parameter).Select(a => new JObject
            {
                ["elementId"] = a.ElementId, ["element"] = a.Element, ["parameter"] = a.Parameter, ["builtIn"] = a.BuiltIn
            }));
            rows.Add(row);
        }
        var cycle = ModelFamilyFormulaGraph.FindCycle(dependsOn);
        return new JObject
        {
            ["count"] = parameters.Count,
            ["truncated"] = parameters.Count > limit,
            ["formulaCycle"] = cycle == null ? null : ModelFamilyFormulaGraph.DescribeCycle(cycle),
            ["items"] = rows
        };
    }

    public static JToken TypesSection(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var manager = doc.FamilyManager;
        var parameters = Parameters(doc).Where(p => p.Id.Value >= 0).ToList();
        var friendly = parameters.ToDictionary(p => p.Id.Value, p => State(p).DataType);
        var types = new JArray();
        foreach (var type in manager.Types.Cast<FamilyType>().Take(Math.Max(1, limit)))
        {
            var values = new JObject();
            foreach (var parameter in parameters)
            {
                var value = ReadValue(doc, type, parameter, friendly[parameter.Id.Value]);
                if (value != null) values[parameter.Definition.Name] = value;
            }
            types.Add(new JObject { ["name"] = type.Name, ["values"] = values });
        }
        return new JObject
        {
            ["currentType"] = manager.CurrentType?.Name,
            ["count"] = manager.Types.Size,
            ["units"] = "lengths in mm, angles in degrees, area in m2, volume in m3, yes/no as true/false, materials by name",
            ["items"] = types
        };
    }

    public static JToken AssociableSection(ModelFamilyContext context, int limit)
    {
        var ids = ToolArguments.GetLongArray(context.Args, "elementIds");
        if (ids.Length == 0)
            return new JObject { ["note"] = "Pass elementIds (element ids of the family, from revit_get_model_family) to list which of their parameters a family parameter can drive." };
        var rows = new JArray();
        foreach (var id in ids.Take(Math.Max(1, limit)))
        {
            var element = context.FamilyDoc.GetElement(new ElementId(id));
            if (element == null)
            {
                rows.Add(new JObject { ["elementId"] = id, ["error"] = "The family has no element with this id." });
                continue;
            }
            rows.Add(new JObject
            {
                ["elementId"] = id,
                ["element"] = ModelFamilyLookup.Describe(element),
                ["parameters"] = ModelFamilyAssociations.Associable(context.FamilyDoc, element)
            });
        }
        return new JObject { ["note"] = "name is the parameter's name, builtIn its BuiltInParameter name (language independent); associatedWith is the family parameter that drives it now.", ["items"] = rows };
    }
}
