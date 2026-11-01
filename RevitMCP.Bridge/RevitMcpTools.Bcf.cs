using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>BCF clash review and clash fixes: read a BCF file, match it to the model, propose and apply fixes (docs/bcf-clashes.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string BcfPathDescription = "Full path of the .bcf / .bcfzip file.";

    private const string ClashFixDirectionsDescription =
        "Allowed directions: up, down, side+, side- (square to a run), +x, -x, +y, -y, or the groups vertical / horizontal. Empty = all.";

    [McpServerTool(Name = "revit_bcf_read", ReadOnly = true),
     Description("Reads a BCF file (2.0, 2.1, 3.0) from a clash or issue tool (Navisworks, Solibri, BIMcollab, Revizto, ACC) without touching the model. Without topic: every topic {number, clashId BCF-0001…, guid, title, status, type, priority, assignedTo, labels, comment and viewpoint counts, elements its viewpoint selects} and counts by status, type and priority. With topic (number, BCF-0003, Index or guid): description, comments, viewpoints with camera (metres, IFC coordinates), clipping planes and the selected components {ifcGuid, authoringToolId}. extractSnapshots=true writes the snapshots to a temp folder and returns their paths (open one to see the clash). Then revit_bcf_check matches the topics to Revit elements.")]
    public async Task<string> BcfRead(
        [Description(BcfPathDescription)] string path,
        [Description("One topic to show in full: its number, BCF-0003, Index or guid. Empty = list all topics.")] string? topic = null,
        [Description("Extract the snapshot images to a temp folder and return their paths. Default false.")] bool extractSnapshots = false,
        [Description("Most topics to list. Default 200.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["topic"] = topic ?? string.Empty,
            ["extractSnapshots"] = extractSnapshots,
            ["limit"] = limit
        };
        return FormatResult(await pipeClient.SendAsync("revit_bcf_read", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_bcf_check", ReadOnly = true),
     Description("Matches BCF topics to elements of the host model and its loaded links and re-tests each clash with the live solids. Matching: IFC GlobalId (the stored IfcGUID parameter or Revit's export id) and the component's AuthoringToolId (Low confidence when only the id matched). Per topic check = still_clashing (with the intersection volume), cleared (both found, no longer intersecting), partial, not_found, single_element or no_components, plus the matched elements {elementId, linkInstanceId, category, matchedBy, confidence}. Topics with two matched elements are saved as the document's clash run (replacing the last one) under clash ids BCF-0001…, so revit_focus_clash, revit_select_clash_elements, revit_get_adjacent_clash, revit_export_clash_report_to_excel and revit_propose_clash_fixes work on them. Does not change the model. Publish the result for the user with revit_publish_page.")]
    public async Task<string> BcfCheck(
        [Description(BcfPathDescription)] string path,
        [Description("Topics to check: numbers, BCF-0003 ids or guids. Empty = all.")] string[]? topics = null,
        [Description("Intersections smaller than a cube of this edge do not count, mm. Default 5.")] double toleranceMm = 5,
        [Description("Save the matched topics as the document's clash run (BCF-xxxx ids). Default true.")] bool saveAsLastRun = true,
        [Description("Most topics to check. Default 500.")] int limit = 500,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["topics"] = topics ?? [],
            ["toleranceMm"] = toleranceMm,
            ["saveAsLastRun"] = saveAsLastRun,
            ["limit"] = limit,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_bcf_check", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_propose_clash_fixes", ReadOnly = true),
     Description("Proposes moves that clear clashes of the document's clash run (from revit_bcf_check or a clash detection), without changing the model. One element of each pair moves: a host-model run segment (cable tray, conduit, duct, pipe) or a family instance at a point. Building and structural elements, linked, pinned and grouped elements, fittings and flexible runs never move. A run moves up/down or sideways, a vertical run and an instance along X/Y, a hosted instance within its host's plane; a segment joined at its ends only along the runs it is joined to (they stretch) — otherwise status manual with a reroute suggestion. Each distance is the shortest move that clears the solids, plus clearanceMm, rounded up to stepMm, at most maxShiftMm; the moved solid is checked against the host and links for new clashes. Returns per clash {status proposed|manual|cleared|not_found, mover, obstacle, options[] {direction, travelMm, delta, newClashes, alsoClears, fix}, ruledOut[], notMoved[]} and fixes[] (the best option of each clash that creates no new clash) to pass to revit_apply_clash_fixes.")]
    public async Task<string> ProposeClashFixes(
        [Description("Clash ids from the clash run (BCF-0003, CL-0012). Empty = every Active/New clash, up to limit.")] string[]? clashIds = null,
        [Description("Move this element rather than letting the tool choose (must be one of the pair).")] long moveElementId = 0,
        [Description("Gap left to the obstacle, mm. Default 25.")] double clearanceMm = 25,
        [Description("Round each move up to a multiple of this, mm. Default 10.")] double stepMm = 10,
        [Description("Longest move to propose, mm. Default 1000.")] double maxShiftMm = 1000,
        [Description(ClashFixDirectionsDescription)] string[]? directions = null,
        [Description("Options per clash. Default 3.")] int maxOptions = 3,
        [Description("Intersections smaller than a cube of this edge do not count, mm. Default 5.")] double toleranceMm = 5,
        [Description("Most clashes per call. Default 20.")] int limit = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["clashIds"] = clashIds ?? [],
            ["moveElementId"] = moveElementId,
            ["clearanceMm"] = clearanceMm,
            ["stepMm"] = stepMm,
            ["maxShiftMm"] = maxShiftMm,
            ["directions"] = directions ?? [],
            ["maxOptions"] = maxOptions,
            ["toleranceMm"] = toleranceMm,
            ["limit"] = limit,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_propose_clash_fixes", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_apply_clash_fixes"),
     Description("Applies clash fixes by moving elements (requires approval), verifying each move in the model. fixes is the fixes array of revit_propose_clash_fixes (or options[].fix entries the user picked). Each fix runs in its own sub-transaction: after the move the element must clear the obstacle (else still_clashing), its joined runs must stay joined and keep their direction (else connections_broken, always undone), and it and the runs that followed it must not run into anything new in the host or loaded links (else new_clashes). A failed fix is rolled back unless keepIfStillClashing / keepIfNewClashes; the others stay. One transaction, one Revit undo. Fixed clashes are marked Fixed in the clash run. Run revit_bcf_check (or the detection) again to confirm.")]
    public async Task<string> ApplyClashFixes(
        [Description("JSON array of {elementId, deltaXmm, deltaYmm, deltaZmm, obstacleElementId, obstacleLinkInstanceId?, clashId?}.")] string fixes,
        [Description("Keep a move that still leaves an intersection with the obstacle. Default false.")] bool keepIfStillClashing = false,
        [Description("Keep a move that runs into other elements. Default false.")] bool keepIfNewClashes = false,
        [Description("Intersections smaller than a cube of this edge do not count, mm. Default 5.")] double toleranceMm = 5,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fixes))
            return FormatBridgeError("Provide 'fixes': the fixes array of revit_propose_clash_fixes.");
        if (!TryParseJsonArray(fixes, "fixes", out var parsedFixes, out var fixesError))
            return FormatBridgeError(fixesError!);

        var args = new Dictionary<string, object?>
        {
            ["fixes"] = parsedFixes,
            ["keepIfStillClashing"] = keepIfStillClashing,
            ["keepIfNewClashes"] = keepIfNewClashes,
            ["toleranceMm"] = toleranceMm,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_apply_clash_fixes", args, cancellationToken));
    }
}
