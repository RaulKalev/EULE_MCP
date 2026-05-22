using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CheckCircuitHealthTool : IRevitMcpTool
{
    public string Name => "revit_check_circuit_health";
    public string Description => "Central circuit QA tool. Runs configurable checks: MissingPanel, EmptyCircuitNumber, DuplicateCircuitNumbers, MissingCableType (strict: Revit 2026 CableType not set), MissingWireType (lenient: neither CableType nor legacy WireType resolves to a name), MissingLoadName, NoConnectedElements. Returns circuit IDs and details for each issue found.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly string[] AllChecks =
    [
        "MissingPanel", "EmptyCircuitNumber", "DuplicateCircuitNumbers",
        "MissingCableType", "MissingWireType", "MissingLoadName", "NoConnectedElements"
    ];

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var requestedChecks = ToolArguments.GetStringArray(request.Arguments, "checks");
        var includeElements = ToolArguments.GetBool(request.Arguments, "includeElements", true);
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 5000);

        var activeChecks = requestedChecks.Length > 0
            ? new HashSet<string>(requestedChecks, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(AllChecks, StringComparer.OrdinalIgnoreCase);

        var circuits = CircuitQueryService.GetAll(doc);
        if (!string.IsNullOrWhiteSpace(panelName) || !string.IsNullOrWhiteSpace(systemType))
            circuits = CircuitQueryService.Filter(circuits, panelName, null, systemType);

        var checked_ = circuits.Take(limit).ToList();

        // Pre-pass for duplicate detection
        var circuitNumMap = new Dictionary<string, List<long>>();
        if (activeChecks.Contains("DuplicateCircuitNumbers"))
        {
            foreach (var c in checked_)
            {
                var num = c.CircuitNumber ?? "";
                if (string.IsNullOrWhiteSpace(num)) continue;
                var panel = CircuitDtoBuilder.TryGetPanel(c);
                var key = $"{panel?.Id?.Value ?? 0}|{num}";
                if (!circuitNumMap.ContainsKey(key)) circuitNumMap[key] = new List<long>();
                circuitNumMap[key].Add(c.Id.Value);
            }
        }

        var issues = new List<object>();
        var counts = AllChecks.ToDictionary(k => k, _ => 0);

        foreach (var circuit in checked_)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var panel = CircuitDtoBuilder.TryGetPanel(circuit);
            var circNum = circuit.CircuitNumber ?? "";

            // CableType (Revit 2026 API)
            bool hasCableType = false;
            try
            {
                var id = circuit.CableType;
                hasCableType = id != null && id != ElementId.InvalidElementId;
            }
            catch { }

            // WireType: use the same resolver as the DTO builder (CableType first, deprecated WireType as fallback)
            // This prevents false positives when CableType is set but the deprecated WireType is null.
            // Cached so the resolved name can be included in the output for inline cross-checking.
            string resolvedWireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);
            bool hasWireType = !string.IsNullOrEmpty(resolvedWireType);

            string loadName = "";
            try { loadName = circuit.LookupParameter("Load Name")?.AsString() ?? ""; } catch { }

            var circIssues = new List<string>();

            if (activeChecks.Contains("MissingPanel") && panel == null)
            { circIssues.Add("MissingPanel"); counts["MissingPanel"]++; }

            if (activeChecks.Contains("EmptyCircuitNumber") && string.IsNullOrWhiteSpace(circNum))
            { circIssues.Add("EmptyCircuitNumber"); counts["EmptyCircuitNumber"]++; }

            if (activeChecks.Contains("DuplicateCircuitNumbers") && !string.IsNullOrWhiteSpace(circNum))
            {
                var key = $"{panel?.Id?.Value ?? 0}|{circNum}";
                if (circuitNumMap.TryGetValue(key, out var dups) && dups.Count > 1)
                { circIssues.Add("DuplicateCircuitNumbers"); counts["DuplicateCircuitNumbers"]++; }
            }

            if (activeChecks.Contains("MissingCableType") && !hasCableType)
            { circIssues.Add("MissingCableType"); counts["MissingCableType"]++; }

            if (activeChecks.Contains("MissingWireType") && !hasWireType)
            { circIssues.Add("MissingWireType"); counts["MissingWireType"]++; }

            if (activeChecks.Contains("MissingLoadName") && string.IsNullOrWhiteSpace(loadName))
            { circIssues.Add("MissingLoadName"); counts["MissingLoadName"]++; }

            List<object>? elements = null;
            if (activeChecks.Contains("NoConnectedElements") || (includeElements && circIssues.Count > 0))
            {
                bool noElems = false;
                try
                {
                    var es = circuit.Elements;
                    noElems = es == null || es.IsEmpty;
                    if (!noElems && includeElements && circIssues.Count > 0)
                    {
                        elements = new List<object>();
                        foreach (Element e in es!)
                            elements.Add(new { elementId = e.Id.Value, category = e.Category?.Name ?? "", type = e.Name });
                    }
                }
                catch { }

                if (activeChecks.Contains("NoConnectedElements") && noElems)
                { circIssues.Add("NoConnectedElements"); counts["NoConnectedElements"]++; }
            }

            if (circIssues.Count > 0)
                issues.Add(new
                {
                    circuitId = circuit.Id.Value,
                    circuitNumber = circNum,
                    loadName,
                    panelName = panel?.Name ?? "",
                    panelElementId = panel?.Id?.Value ?? 0L,
                    systemType = circuit.SystemType.ToString(),
                    wireType = resolvedWireType,   // same resolver as revit_get_circuit_info — empty means genuinely missing
                    issues = circIssues,
                    connectedElements = elements
                });
        }

        var activeCounts = counts.Where(kv => activeChecks.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Checked {checked_.Count} circuit(s). {issues.Count} circuit(s) have issues.",
            Data = new
            {
                checkedCount = checked_.Count,
                circuitsWithIssues = issues.Count,
                issueSummary = activeCounts,
                circuits = issues
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
