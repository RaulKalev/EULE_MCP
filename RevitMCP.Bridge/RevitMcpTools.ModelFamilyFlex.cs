using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Flex tests of model-family types and checks of placed instances (#176, docs/model-family-flex.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string ModelFamilyFlexTypesDescription = "Type names to exercise (default every type of the family).";

    private const string ModelFamilyFlexChecksDescription =
        "Expectations, each {id?, kind, …}; a number may be {parameter, factor?, offset?} (e.g. min.x = {\"parameter\":\"Width\",\"factor\":-0.5}). Kinds: " +
        "boundingBox {element?, size?:{x,y,z}, min?, max?:[x,y,z], tolerance?}, centred {element?, axis, about?}, plane {plane, at?:{axis, value}, fixed?}, " +
        "face {element, face, at?, fixed?, onPlane?}, parameter {parameter, equals?, min?, max?}, material {element, equals}, " +
        "connector {connector, origin?, originFixed?, onFace?:{element, face}, direction?, systemType?, domain?, values?}, symbol {instance, at?, fixed?}, " +
        "exists {elements}, cut {void, solid}.";

    private const string ModelFamilyInstanceChecksDescription =
        "Expectations in family coordinates, each {id?, kind, …}; a number may be {parameter, factor?, offset?} read from the instance or its type. Kinds: " +
        "boundingBox {size?, min?, max?, tolerance?}, centred {axis, about?}, parameter {parameter, equals?, min?, max?}, material {parameter?, equals}, " +
        "connector {connector: id|description|systemType, origin?, direction?, systemType?, domain?, values?:{\"RBS_ELEC_VOLTAGE\":…}, connected?, system?}, " +
        "hosted {hosted?: false}, count {type, min?, max?}.";

    [McpServerTool(Name = "revit_flex_model_family", ReadOnly = true),
     Description("Flex-tests a model family: runs every type (or types), each value of ranges (min/max of writable parameters, steps 2-5) and explicit cases, " +
                 "regenerates each case, evaluates checks and reports per case status ok|failed|refused, failedChecks (expected, actual, difference), " +
                 "Revit's failures with the dimensions and alignments it names, and measurements. Rolls each case back and reads every value back, " +
                 "and rolls the whole trial back: changes nothing. reopenCopy repeats the type cases on a saved and reopened temporary copy and compares. " +
                 ModelFamilyUnitsNote)]
    public async Task<string> FlexModelFamily(
        [Description(ModelFamilyReadTargetDescription)] object? target = null,
        [Description(ModelFamilyFlexTypesDescription)] string[]? types = null,
        [Description("Writable parameters to flex: [{parameter, min, max, steps?: 2-5 (default 2 = min and max), types?}]; numbers in wire units (mm, degrees) or true/false.")] object[]? ranges = null,
        [Description("Explicit combinations: [{name?, type? (default the current type), values: {\"<parameter>\": value}}] in mm, degrees, true/false, text or a material name.")] object[]? cases = null,
        [Description("oneAtATime (default: each range value alone on each type) or extremes (also all-min and all-max per type).")] string? combine = null,
        [Description("Cap on the number of cases (default 60, at most 300); more is a problem that says how many were requested.")] int? maxCases = null,
        [Description(ModelFamilyFlexChecksDescription)] object[]? checks = null,
        [Description("Also save a temporary copy, reopen it and run the type cases again; reports per type whether it behaves the same.")] bool? reopenCopy = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ModelFamilyArgs(target, document);
        args["types"] = ToJToken(types);
        args["ranges"] = ToJToken(ranges);
        args["cases"] = ToJToken(cases);
        args["combine"] = combine;
        args["maxCases"] = maxCases;
        args["checks"] = ToJToken(checks);
        args["reopenCopy"] = reopenCopy;
        return FormatResult(await pipeClient.SendAsync("revit_flex_model_family", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_check_model_family_instances", ReadOnly = true),
     Description("Checks placed instances of a loaded family in a project: bounding box and centring in the instance's own (family) coordinates, " +
                 "parameters, materials, connectors (origin, direction, domain, system type, electrical values, system membership), hosting and " +
                 "counts per type. Returns per instance id, type, level, location, status, failedChecks and measurements, a summary and missingTypes. Reads only.")]
    public async Task<string> CheckModelFamilyInstances(
        [Description("Name of the loaded family whose instances to check.")] string? family = null,
        [Description("Instances to check (default every instance of family).")] long[]? elementIds = null,
        [Description("Only instances of these types; also the types missingTypes looks for.")] string[]? types = null,
        [Description(ModelFamilyInstanceChecksDescription)] object[]? checks = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["family"] = family,
            ["elementIds"] = ToJToken(elementIds),
            ["types"] = ToJToken(types),
            ["checks"] = ToJToken(checks),
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_check_model_family_instances", args, cancellationToken));
    }
}
