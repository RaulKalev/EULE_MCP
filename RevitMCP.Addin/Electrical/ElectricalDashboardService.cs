using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Aggregates electrical model data for the dashboard summary tool.
/// Must be called on the Revit API thread inside an ExternalEvent.
/// </summary>
public static class ElectricalDashboardService
{
    private sealed class PanelData
    {
        public long PanelId;
        public int CircuitCount;
        public int IssueCount;
        public Dictionary<string, int> IssueCounts = new();
        public double TotalApparentLoad;
    }

    public sealed class DashboardResult
    {
        public required object Model { get; init; }
        public required object Summary { get; init; }
        public List<object> IssueBreakdown { get; init; } = [];
        public List<object> PanelsWithMostIssues { get; init; } = [];
        public List<object> SystemTypeSummary { get; init; } = [];
        public object LoadSummary { get; init; } = new { };
        public List<string> Warnings { get; init; } = [];
    }

    public static DashboardResult Build(
        Document doc,
        bool includePanels,
        bool includeSystemTypes,
        bool includeTopIssues,
        bool includeUncircuitedSummary,
        bool includeLoadSummary,
        int limit,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        // ── Model info ────────────────────────────────────────────────────
        string title = doc.Title ?? "";
        bool isWorkshared = false;
        string centralPath = "", revitUser = "";
        try
        {
            isWorkshared = doc.IsWorkshared;
            if (isWorkshared)
                centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath());
        }
        catch { }
        try { revitUser = doc.Application?.Username ?? ""; } catch { }

        var modelInfo = new { title, isWorkshared, centralPath, revitUser };

        // ── Circuit data ──────────────────────────────────────────────────
        var circuits = CircuitQueryService.GetAll(doc).Take(limit).ToList();
        int circuitCount = circuits.Count;

        // Duplicate circuit number detection
        var circNumMap = new Dictionary<string, List<long>>();
        foreach (var c in circuits)
        {
            var num = c.CircuitNumber ?? "";
            if (string.IsNullOrWhiteSpace(num)) continue;
            var panel = CircuitDtoBuilder.TryGetPanel(c);
            var key = $"{panel?.Id?.Value ?? 0}|{num}";
            if (!circNumMap.ContainsKey(key)) circNumMap[key] = [];
            circNumMap[key].Add(c.Id.Value);
        }

        var issueTotals = new Dictionary<string, int>
        {
            ["MissingPanel"] = 0, ["EmptyCircuitNumber"] = 0,
            ["DuplicateCircuitNumbers"] = 0, ["MissingCableType"] = 0,
            ["MissingWireType"] = 0, ["MissingLoadName"] = 0, ["NoConnectedElements"] = 0
        };

        var panelMap = new Dictionary<string, PanelData>();
        var systemTypeCircuits = new Dictionary<string, int>();
        var systemTypeIssues = new Dictionary<string, int>();

        int circuitsWithIssues = 0, circuitsMissingPanel = 0;
        int circuitsMissingCableType = 0, circuitsMissingWireType = 0, circuitsMissingLoadName = 0;
        int circuitsWithoutConnectedElements = 0;

        foreach (var c in circuits)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var panel = CircuitDtoBuilder.TryGetPanel(c);
            var pName = panel?.Name ?? "(No Panel)";
            var pId = panel?.Id?.Value ?? 0L;
            var circNum = c.CircuitNumber ?? "";
            var wt = CircuitDtoBuilder.GetWireTypeName(doc, c);
            bool hasCableType = false;
            try
            {
                var cableId = c.CableType;
                hasCableType = cableId != null && cableId != ElementId.InvalidElementId;
            }
            catch { }
            string loadName = "";
            try { loadName = c.LookupParameter("Load Name")?.AsString() ?? ""; } catch { }
            var stName = c.SystemType.ToString();

            // Panel tracking
            if (!panelMap.ContainsKey(pName))
                panelMap[pName] = new PanelData { PanelId = pId };
            var pd = panelMap[pName];
            pd.CircuitCount++;
            if (includeLoadSummary) { try { pd.TotalApparentLoad += c.ApparentLoad; } catch { } }

            // System type tracking
            systemTypeCircuits[stName] = systemTypeCircuits.TryGetValue(stName, out int sc) ? sc + 1 : 1;

            // Issue checks
            var circIssues = new List<string>();
            if (panel == null) { circIssues.Add("MissingPanel"); circuitsMissingPanel++; }
            if (string.IsNullOrWhiteSpace(circNum)) { circIssues.Add("EmptyCircuitNumber"); }
            else
            {
                var key = $"{pId}|{circNum}";
                if (circNumMap.TryGetValue(key, out var dups) && dups.Count > 1)
                    circIssues.Add("DuplicateCircuitNumbers");
            }
            if (!hasCableType) { circIssues.Add("MissingCableType"); circuitsMissingCableType++; }
            if (string.IsNullOrEmpty(wt)) { circIssues.Add("MissingWireType"); circuitsMissingWireType++; }
            if (string.IsNullOrWhiteSpace(loadName)) { circIssues.Add("MissingLoadName"); circuitsMissingLoadName++; }

            int elemSize = 0;
            try { elemSize = c.Elements?.Size ?? 0; } catch { }
            if (elemSize == 0) { circIssues.Add("NoConnectedElements"); circuitsWithoutConnectedElements++; }

            foreach (var issue in circIssues)
            {
                issueTotals[issue]++;
                pd.IssueCount++;
                pd.IssueCounts[issue] = pd.IssueCounts.TryGetValue(issue, out int iv) ? iv + 1 : 1;
                systemTypeIssues[stName] = systemTypeIssues.TryGetValue(stName, out int si) ? si + 1 : 1;
            }
            if (circIssues.Count > 0) circuitsWithIssues++;
        }

        int duplicateCircuitNumberGroups = circNumMap.Count(kv => kv.Value.Count > 1);

        // ── Uncircuited elements (optional — accesses circuit.Elements) ────────
        int uncircuitedElementCount = 0;
        int connectedElementCount = 0;
        if (includeUncircuitedSummary)
        {
            try
            {
                var circuitedIds = new HashSet<long>();
                foreach (var c in circuits)
                {
                    try
                    {
                        if (c.Elements == null) continue;
                        foreach (Element e in c.Elements)
                        {
                            circuitedIds.Add(e.Id.Value);
                        }
                    }
                    catch { }
                }
                connectedElementCount = circuitedIds.Count;

                uncircuitedElementCount = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Count(e =>
                    {
                        if (e is not FamilyInstance fi) return false;
                        try { return fi.MEPModel != null && !circuitedIds.Contains(e.Id.Value); }
                        catch { return false; }
                    });
            }
            catch (Exception ex)
            {
                warnings.Add($"Uncircuited element scan unavailable: {ex.Message}");
            }
        }

        // ── Load summary (optional) ───────────────────────────────────────────
        double totalLoad = 0;
        int circuitsWithLoad = 0, circuitsMissingLoad = 0;
        if (includeLoadSummary)
        {
            foreach (var pd in panelMap.Values) totalLoad += pd.TotalApparentLoad;
            foreach (var c in circuits)
            {
                double load = 0; try { load = c.ApparentLoad; } catch { }
                if (load > 0) circuitsWithLoad++; else circuitsMissingLoad++;
            }
        }

        // ── Assemble result ───────────────────────────────────────────────────
        var summary = new
        {
            panelCount = panelMap.Count,
            circuitCount,
            connectedElementCount,
            uncircuitedElementCount,
            circuitsWithIssues,
            totalIssueCount = issueTotals.Values.Sum(),
            circuitsMissingPanel,
            circuitsMissingCableType,
            circuitsMissingWireType,
            circuitsMissingLoadName,
            duplicateCircuitNumberGroups,
            circuitsWithoutConnectedElements
        };

        var issueBreakdown = includeTopIssues
            ? issueTotals.Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => (object)new { issueType = kv.Key, count = kv.Value })
                .ToList()
            : [];

        var panelsWithMostIssues = includePanels
            ? panelMap.OrderByDescending(kv => kv.Value.IssueCount).Take(10)
                .Select(kv => (object)new
                {
                    panelName = kv.Key,
                    panelElementId = kv.Value.PanelId,
                    issueCount = kv.Value.IssueCount,
                    circuitCount = kv.Value.CircuitCount,
                    missingCableTypeCount = kv.Value.IssueCounts.TryGetValue("MissingCableType", out int mc) ? mc : 0,
                    missingLoadNameCount = kv.Value.IssueCounts.TryGetValue("MissingLoadName", out int ml) ? ml : 0,
                    duplicateCircuitNumberCount = kv.Value.IssueCounts.TryGetValue("DuplicateCircuitNumbers", out int dn) ? dn : 0,
                    totalApparentLoadDisplay = $"{kv.Value.TotalApparentLoad / 1000:F1} kVA"
                })
                .ToList()
            : [];

        var systemTypeSummary = includeSystemTypes
            ? systemTypeCircuits.OrderBy(kv => kv.Key)
                .Select(kv => (object)new
                {
                    systemType = kv.Key,
                    circuitCount = kv.Value,
                    issueCount = systemTypeIssues.TryGetValue(kv.Key, out int si) ? si : 0
                })
                .ToList()
            : [];

        var loadSummary = includeLoadSummary
            ? (object)new
            {
                totalApparentLoadDisplay = $"{totalLoad / 1000:F1} kVA",
                circuitsWithLoadData = circuitsWithLoad,
                circuitsMissingLoadData = circuitsMissingLoad
            }
            : new { };

        return new DashboardResult
        {
            Model = modelInfo,
            Summary = summary,
            IssueBreakdown = issueBreakdown,
            PanelsWithMostIssues = panelsWithMostIssues,
            SystemTypeSummary = systemTypeSummary,
            LoadSummary = loadSummary,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Returns per-panel issue detail data for GetPanelIssueSummaryTool.
    /// </summary>
    public static List<object> BuildPanelIssueSummary(
        Document doc,
        string panelNameFilter,
        bool includeCircuitDetails,
        bool includeIssueDetails,
        int limit,
        CancellationToken cancellationToken)
    {
        var circuits = CircuitQueryService.GetAll(doc);
        if (!string.IsNullOrWhiteSpace(panelNameFilter))
            circuits = CircuitQueryService.Filter(circuits, panelNameFilter, null, null);
        circuits = circuits.Take(limit).ToList();

        var circNumMap = new Dictionary<string, List<long>>();
        foreach (var c in circuits)
        {
            var num = c.CircuitNumber ?? "";
            if (string.IsNullOrWhiteSpace(num)) continue;
            var panel = CircuitDtoBuilder.TryGetPanel(c);
            var key = $"{panel?.Id?.Value ?? 0}|{num}";
            if (!circNumMap.ContainsKey(key)) circNumMap[key] = [];
            circNumMap[key].Add(c.Id.Value);
        }

        var panelMap = new Dictionary<string, PanelData>();
        var panelCircuits = new Dictionary<string, List<object>>();

        foreach (var c in circuits)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var panel = CircuitDtoBuilder.TryGetPanel(c);
            var pName = panel?.Name ?? "(No Panel)";
            var pId = panel?.Id?.Value ?? 0L;
            var circNum = c.CircuitNumber ?? "";
            var wt = CircuitDtoBuilder.GetWireTypeName(doc, c);
            bool hasCableType = false;
            try { var id = c.CableType; hasCableType = id != null && id != ElementId.InvalidElementId; } catch { }
            string ln = ""; try { ln = c.LookupParameter("Load Name")?.AsString() ?? ""; } catch { }

            if (!panelMap.ContainsKey(pName))
            {
                panelMap[pName] = new PanelData { PanelId = pId };
                panelCircuits[pName] = [];
            }
            var pd = panelMap[pName];
            pd.CircuitCount++;
            try { pd.TotalApparentLoad += c.ApparentLoad; } catch { }

            var circIssues = new List<string>();
            if (panel == null) { circIssues.Add("MissingPanel"); pd.IssueCounts["MissingPanel"] = pd.IssueCounts.TryGetValue("MissingPanel", out int v0) ? v0 + 1 : 1; }
            if (string.IsNullOrWhiteSpace(circNum)) { circIssues.Add("EmptyCircuitNumber"); pd.IssueCounts["EmptyCircuitNumber"] = pd.IssueCounts.TryGetValue("EmptyCircuitNumber", out int v1) ? v1 + 1 : 1; }
            else
            {
                var key = $"{pId}|{circNum}";
                if (circNumMap.TryGetValue(key, out var dups) && dups.Count > 1)
                { circIssues.Add("DuplicateCircuitNumbers"); pd.IssueCounts["DuplicateCircuitNumbers"] = pd.IssueCounts.TryGetValue("DuplicateCircuitNumbers", out int v2) ? v2 + 1 : 1; }
            }
            if (!hasCableType) { circIssues.Add("MissingCableType"); pd.IssueCounts["MissingCableType"] = pd.IssueCounts.TryGetValue("MissingCableType", out int v3) ? v3 + 1 : 1; }
            if (string.IsNullOrWhiteSpace(ln)) { circIssues.Add("MissingLoadName"); pd.IssueCounts["MissingLoadName"] = pd.IssueCounts.TryGetValue("MissingLoadName", out int v4) ? v4 + 1 : 1; }
            if (circIssues.Count > 0) pd.IssueCount++;

            if (includeCircuitDetails)
                panelCircuits[pName].Add(new
                {
                    circuitId = c.Id.Value,
                    circuitNumber = circNum,
                    loadName = ln,
                    wireType = wt,
                    issues = circIssues
                });
        }

        return panelMap.Select(kv =>
        {
            var pd = kv.Value;
            var issuesObj = includeIssueDetails
                ? pd.IssueCounts.Where(iv => iv.Value > 0)
                    .Select(iv => (object)new { issueType = iv.Key, count = iv.Value }).ToList()
                : null;
            return (object)new
            {
                panelName = kv.Key,
                panelElementId = pd.PanelId,
                circuitCount = pd.CircuitCount,
                issueCount = pd.IssueCount,
                missingCableTypeCount = pd.IssueCounts.TryGetValue("MissingCableType", out int mc) ? mc : 0,
                missingLoadNameCount = pd.IssueCounts.TryGetValue("MissingLoadName", out int ml) ? ml : 0,
                duplicateCircuitNumberCount = pd.IssueCounts.TryGetValue("DuplicateCircuitNumbers", out int dn) ? dn : 0,
                totalApparentLoadDisplay = $"{pd.TotalApparentLoad / 1000:F1} kVA",
                issues = issuesObj,
                circuits = includeCircuitDetails ? panelCircuits[kv.Key] : null
            };
        }).ToList();
    }
}
