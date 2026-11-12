using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Family contents (docs/family-contents.md): read what is inside a family, and optionally a spec that copies it.</summary>
internal sealed partial class RevitMcpTools
{
    [McpServerTool(Name = "revit_get_family_contents", ReadOnly = true),
     Description("Reads what is inside a family without changing it: family (loaded in the project, opened as a copy) or path (a .rfa file). Never saves; closes what it opened. Millimetres on the family's reference level, degrees. Returns category, view, parameters (dataType, group, instance, shared+guid, formula, isReporting), types with values, curves (line|arc|circle, lineStyle, visibleParameter), filledRegions (loops, type, lineStyle), textNotes, nestedInstances (position, rotation, linkedParameters), lineStyles and filledRegionTypes used. asSpec=true adds spec: arguments of revit_create_detail_family that copy it (add savePath); specOmitted lists what does not fit.")]
    public async Task<string> GetFamilyContents(
        [Description("Name of a family loaded in the project. Give this or path.")] string? family = null,
        [Description("Path of a .rfa file. Give this or family.")] string? path = null,
        [Description("Sections to return: parameters, types, curves, filledRegions, textNotes, nestedInstances, lineStyles, filledRegionTypes. Default all.")] string[]? include = null,
        [Description("Most items per list (default 500); longer lists are cut and named in truncated.")] int limit = 500,
        [Description("Also return spec, the revit_create_detail_family arguments that copy the family. Default false.")] bool asSpec = false,
        [Description("Open nested Generic Annotation families to read their text (default true).")] bool readNested = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["family"] = family ?? string.Empty,
            ["path"] = path ?? string.Empty,
            ["include"] = ToJToken(include),
            ["limit"] = limit,
            ["asSpec"] = asSpec,
            ["readNested"] = readNested
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_get_family_contents", args, cancellationToken));
    }
}
