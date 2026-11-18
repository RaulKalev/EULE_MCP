using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// Reads and writes the visibility of family elements (#175): the Visible flag and the yes/no parameter that drives it
/// (every element), and detail levels and view display (forms, model lines and symbolic lines, through Revit's
/// <c>FamilyElementVisibility</c>). A nested instance has no visibility object in the API: for it only Visible and its
/// parameter can be set, and a request for more is refused instead of dropped.
/// </summary>
internal static class ModelFamilyComponentVisibilityRevit
{
    /// <summary>The detail-level and view flags of an element, or null when the API gives none (nested instances).</summary>
    public static ComponentVisibilityFlags? ReadFlags(Element element)
    {
        try
        {
            var visibility = element switch
            {
                GenericForm form => form.GetVisibility(),
                SymbolicCurve symbolic => symbolic.GetVisibility(),
                ModelCurve model => model.GetVisibility(),
                _ => null
            };
            if (visibility == null) return null;
            return new ComponentVisibilityFlags
            {
                ViewSpecific = visibility.VisibilityType == FamilyElementVisibilityType.ViewSpecific,
                Coarse = visibility.IsShownInCoarse,
                Medium = visibility.IsShownInMedium,
                Fine = visibility.IsShownInFine,
                PlanRcp = visibility.IsShownInTopBottom,
                FrontBack = visibility.IsShownInFrontBack,
                LeftRight = visibility.IsShownInLeftRight,
                PlanRcpCut = visibility.IsShownInPlanRCPCut,
                OnlyWhenCut = visibility.IsShownOnlyWhenCut
            };
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFlags(Element element, ComponentVisibilityFlags current, ComponentVisibilityFlags flags)
    {
        var visibility = element switch
        {
            GenericForm form => form.GetVisibility(),
            SymbolicCurve symbolic => symbolic.GetVisibility(),
            ModelCurve model => model.GetVisibility(),
            _ => throw new InvalidOperationException($"{ModelFamilyLookup.Describe(element)} has no detail-level or view settings in the Revit API.")
        };
        // Only what changes, levels turned on before levels turned off (see ComponentVisibilityFlags.Writes).
        foreach (var (flag, value) in ComponentVisibilityFlags.Writes(current, flags))
        {
            switch (flag)
            {
                case "Coarse": visibility.IsShownInCoarse = value; break;
                case "Medium": visibility.IsShownInMedium = value; break;
                case "Fine": visibility.IsShownInFine = value; break;
                case "OnlyWhenCut": visibility.IsShownOnlyWhenCut = value; break;
                case "TopBottom": visibility.IsShownInTopBottom = value; break;
                case "FrontBack": visibility.IsShownInFrontBack = value; break;
                case "LeftRight": visibility.IsShownInLeftRight = value; break;
                case "PlanRCPCut": visibility.IsShownInPlanRCPCut = value; break;
            }
        }
        switch (element)
        {
            case GenericForm form: form.SetVisibility(visibility); break;
            case SymbolicCurve symbolic: symbolic.SetVisibility(visibility); break;
            case ModelCurve model: model.SetVisibility(visibility); break;
        }
    }

    public static Parameter? VisibleParameter(Element element)
    {
        try { return element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM); }
        catch { return null; }
    }

    public static bool? IsVisible(Element element)
    {
        var parameter = VisibleParameter(element);
        return parameter != null && parameter.StorageType == StorageType.Integer ? parameter.AsInteger() != 0 : null;
    }

    /// <summary>The family parameter that drives the element's Visible flag, or null.</summary>
    public static string? DrivenBy(Document familyDoc, Element element)
    {
        var parameter = VisibleParameter(element);
        return parameter == null ? null : ModelFamilyAssociations.Associated(familyDoc, parameter)?.Definition.Name;
    }

    /// <summary>
    /// Applies a request to one element, inside the caller's transaction. Throws with a plain message when Revit
    /// refuses or the element cannot do it; the caller's transaction then rolls back. Reads every setting back.
    /// </summary>
    public static JObject Apply(Document familyDoc, Element element, ComponentVisibilityRequest request, string source, List<string> warnings)
    {
        var label = ModelFamilyLookup.Describe(element);

        if (request.Visible != null || request.VisibleParameterGiven)
        {
            var parameter = VisibleParameter(element)
                            ?? throw new InvalidOperationException($"{source}: {label} has no Visible property.");
            if (request.VisibleParameterGiven)
            {
                FamilyParameter? driver = null;
                if (request.VisibleParameter != null)
                {
                    var problems = new List<string>();
                    driver = ModelFamilyLookup.Parameter(familyDoc, request.VisibleParameter, $"{source}.visibleParameter", problems);
                    if (driver == null) throw new InvalidOperationException(problems.FirstOrDefault() ?? $"{source}: unknown family parameter.");
                }
                var error = ModelFamilyAssociations.Associate(familyDoc, parameter, driver);
                if (error != null) throw new InvalidOperationException($"{source}: {label}: {error} A Visible property needs a Yes/No family parameter.");
            }
            if (request.Visible != null && ModelFamilyAssociations.Associated(familyDoc, parameter) == null)
            {
                if (parameter.IsReadOnly) throw new InvalidOperationException($"{source}: the Visible flag of {label} is read-only.");
                parameter.Set(request.Visible.Value ? 1 : 0);
                if (parameter.AsInteger() != (request.Visible.Value ? 1 : 0))
                    throw new InvalidOperationException($"{source}: Revit did not keep visible={request.Visible.Value.ToString().ToLowerInvariant()} on {label}.");
            }
            else if (request.Visible != null)
                warnings.Add($"{source}: visible was not set on {label} because a family parameter drives it; change that parameter's value instead.");
        }

        if (request.HasFlags)
        {
            var current = ReadFlags(element);
            if (current == null)
                throw new InvalidOperationException(
                    $"{source}: {label} is a nested instance, and the Revit API has no detail-level or view settings for one (only Visible and its yes/no parameter). " +
                    "Set them on the nested family's own geometry, or on the forms and lines of this family.");
            var merged = ComponentVisibilityFlags.Merge(current, request, source, warnings, out var mergeError)
                         ?? throw new InvalidOperationException(mergeError);
            if (!merged.SameAs(current))
            {
                var written = new HashSet<string>(ComponentVisibilityFlags.Writes(current, merged).Select(w => w.Flag));
                WriteFlags(element, current, merged);
                var after = ReadFlags(element);
                // Only what was written is checked: a flag Revit ties to it (the cut flag of a category cut in the
                // instance follows planRcp) may change along with it.
                if (after == null || ComponentVisibilityFlags.Writes(after, merged).Any(w => written.Contains(w.Flag)))
                    warnings.Add($"{source}: Revit kept different detail-level or view settings than asked on {label}; now {after?.ToJson().ToString(Newtonsoft.Json.Formatting.None)}.");
            }
        }
        return Describe(familyDoc, element);
    }

    /// <summary>One compact row: id, description, visible, the parameter driving it, detail levels and view flags.</summary>
    public static JObject Describe(Document familyDoc, Element element)
    {
        var row = ModelFamilyComponentSupport.Row(element);
        row["visible"] = IsVisible(element);
        row["visibleParameter"] = DrivenBy(familyDoc, element);
        var flags = ReadFlags(element);
        if (flags != null)
        {
            var json = flags.ToJson();
            row["visibilityKind"] = json["kind"];
            row["detailLevels"] = json["detailLevels"];
            row["views"] = json["views"];
        }
        return row;
    }
}
