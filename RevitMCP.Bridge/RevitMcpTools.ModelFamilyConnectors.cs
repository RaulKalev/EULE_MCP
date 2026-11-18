using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>MEP connectors of a model family (#174, docs/model-family-connectors.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string ModelFamilyConnectorsRemoveDescription =
        "Connector element ids to delete (runs first). A connector linked to a removed one loses its link (warning).";

    private const string ModelFamilyConnectorsCreateDescription =
        "Connectors to create (after remove): [{name? (your label; also the connector description), domain: electrical|duct|pipe|conduit|cableTray, " +
        "host: {element: <form id>, face: left|right|front|back|top|bottom|[nx,ny,nz]} | {reference: \"<stable>\"} | \"<reference plane>\", " +
        "systemType (electrical: powerBalanced, powerUnBalanced, data, telephone, security, fireAlarm, nurseCall, controls, communication; duct: supplyAir, returnAir, exhaustAir, otherAir, fitting, global; " +
        "pipe: supplyHydronic, returnHydronic, sanitary, domesticHotWater, domesticColdWater, fireProtect*, vent, otherPipe, fitting, global; none for conduit/cableTray), " +
        "shape? round|rectangular|oval (duct), diameter?, width?, height?, position? [x,y,z] (moved within the face), flip?, angle?, primary?, " +
        "linkTo? <id> | {created: \"connectors[i]\"}, properties? {\"<name or BuiltInParameter>\": value | {value, unit}} (V, VA, W, A, L/s, mm by default), " +
        "associations? {\"<connector parameter>\": \"<family parameter>\"}}].";

    private const string ModelFamilyConnectorsEditDescription =
        "Connectors to change (last): [{connector: <id>, systemType?, shape?, diameter?, width?, height?, position?, flip?, angle?, primary?, " +
        "linkTo? <id> | {created: \"connectors[i]\"} | null, properties?, associations? {\"<parameter>\": \"<family parameter>\" | null}}].";

    [McpServerTool(Name = "revit_preview_edit_model_family_connectors", ReadOnly = true),
     Description("Previews revit_edit_model_family_connectors: runs the same remove/create/edit of MEP connectors on the family and rolls it back, so it reports what Revit would create, store and refuse (ready, problems, the read-back rows). Changes nothing. " + ModelFamilyUnitsNote)]
    public Task<string> PreviewEditModelFamilyConnectors(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyConnectorsRemoveDescription)] long[]? remove = null,
        [Description(ModelFamilyConnectorsCreateDescription)] object[]? connectors = null,
        [Description(ModelFamilyConnectorsEditDescription)] object[]? edit = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyConnectors("revit_preview_edit_model_family_connectors", target, remove, connectors, edit, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_model_family_connectors"),
     Description("Creates, edits and removes MEP connectors of a model family: electrical (power and ELV), duct, pipe, conduit, cable tray. Requires approval. Order: remove, connectors, edit, then links. Each connector is read back: id, description, domain, systemType, shape, size, origin, direction, host, isPrimary, linkedConnector, properties, associations (revit_get_model_family section connectors lists them all). Run revit_preview_edit_model_family_connectors first. " + ModelFamilyUnitsNote)]
    public Task<string> EditModelFamilyConnectors(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyConnectorsRemoveDescription)] long[]? remove = null,
        [Description(ModelFamilyConnectorsCreateDescription)] object[]? connectors = null,
        [Description(ModelFamilyConnectorsEditDescription)] object[]? edit = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyConnectors("revit_edit_model_family_connectors", target, remove, connectors, edit, document, cancellationToken);

    private async Task<string> SendEditModelFamilyConnectors(string toolName, object? target, long[]? remove, object[]? connectors, object[]? edit,
        string? document, CancellationToken cancellationToken)
    {
        var args = ModelFamilyArgs(target, document);
        args["remove"] = ToJToken(remove);
        args["connectors"] = ToJToken(connectors);
        args["edit"] = ToJToken(edit);
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}
