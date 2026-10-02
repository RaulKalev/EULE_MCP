using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Graph;

public sealed class GraphExtractionResult
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();
    public Dictionary<string, long> NodesByKind { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> EdgesByRel { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = new();
    public int DanglingEdgesDropped { get; set; }
    public bool ElementLimitReached { get; set; }
    public long ElapsedMs { get; set; }

    /// <summary>Subset extraction only: requested ids that no longer exist in the document.</summary>
    public List<long> Missing { get; } = new();

    /// <summary>Linked models seen by a full build (#62), loaded or not.</summary>
    public List<GraphLinkSummary> Links { get; } = new();
    public bool LinkElementLimitReached { get; set; }
}

public sealed class GraphLinkSummary
{
    public string InstanceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Document { get; set; } = string.Empty;
    public bool Loaded { get; set; }
    public int Elements { get; set; }
    public int Spaces { get; set; }
}

/// <summary>
/// Walks the open document and produces graph nodes and edges. Reads only identity and structure
/// (ids, names, category, level, workset, host, room, circuit, type, tag, sheet) — never parameter
/// values. Every edge records its owner: the element whose state produced it (#61). Must run on the
/// Revit API thread.
/// </summary>
public static class RevitGraphExtractor
{
    /// <summary>
    /// Full extraction of the document. <paramref name="linkElementLimit"/> &gt; 0 also indexes the
    /// elements of loaded linked models (#62), up to that many in total; 0 leaves links out.
    /// </summary>
    public static GraphExtractionResult Extract(Document doc, int elementLimit, int linkElementLimit = 0,
        IReadOnlyList<RoutingParameterSpec>? routingParameters = null)
    {
        var builder = new Builder(doc, elementLimit, subset: false)
        {
            LinkElementLimit = linkElementLimit,
            RoutingParameters = routingParameters ?? Array.Empty<RoutingParameterSpec>()
        };
        return builder.Run();
    }

    /// <summary>
    /// Re-extracts only <paramref name="ids"/> (incremental update, #61): their nodes and owned
    /// edges, plus panel nodes for the panels of re-extracted circuits. Edges are not checked for
    /// dangling endpoints here — the graph database does that against the existing nodes.
    /// </summary>
    public static GraphExtractionResult ExtractSubset(Document doc, IEnumerable<long> ids,
        IReadOnlyList<RoutingParameterSpec>? routingParameters = null)
    {
        var builder = new Builder(doc, int.MaxValue, subset: true)
        {
            RoutingParameters = routingParameters ?? Array.Empty<RoutingParameterSpec>()
        };
        return builder.RunSubset(ids);
    }

    private sealed class Builder
    {
        private readonly Document _doc;
        private readonly int _elementLimit;
        private readonly bool _subset;
        private readonly GraphExtractionResult _result = new();
        private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
        private readonly HashSet<(string Src, string Dst, string Rel, string Owner)> _edges = new();
        private readonly Dictionary<long, string> _levelNames = new();
        private readonly Dictionary<int, string> _worksetNames = new();
        private readonly HashSet<string> _userWorksetIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FamilyInstance> _panels = new(StringComparer.Ordinal);
        private readonly bool _isWorkshared;
        private int _linkedCount;

        public int LinkElementLimit { get; set; }
        public IReadOnlyList<RoutingParameterSpec> RoutingParameters { get; set; } = Array.Empty<RoutingParameterSpec>();

        public Builder(Document doc, int elementLimit, bool subset)
        {
            _doc = doc;
            _elementLimit = elementLimit;
            _subset = subset;
            try { _isWorkshared = doc.IsWorkshared; } catch { _isWorkshared = false; }
        }

        public GraphExtractionResult Run()
        {
            var sw = Stopwatch.StartNew();
            Step("levels", CollectLevels);
            Step("worksets", CollectWorksets);
            Step("sheets", CollectSheets);
            Step("views", CollectViews);
            Step("spaces", CollectSpaces);
            Step("circuits", CollectCircuits);
            Step("panels", CollectPanels);
            Step("elements", CollectElements);
            Step("tags", CollectTags);
            if (LinkElementLimit > 0) Step("links", CollectLinks);
            Finish();
            sw.Stop();
            _result.ElapsedMs = sw.ElapsedMilliseconds;
            return _result;
        }

        public GraphExtractionResult RunSubset(IEnumerable<long> ids)
        {
            var sw = Stopwatch.StartNew();
            // Lookups the per-element methods rely on (level names, user worksets) — no nodes.
            Step("lookups", PrepareLookups);
            foreach (var raw in ids.Distinct())
            {
                Element? element;
                try { element = _doc.GetElement(new ElementId(raw)); } catch { element = null; }
                if (element == null)
                {
                    _result.Missing.Add(raw);
                    continue;
                }
                Step($"element {raw}", () => AddAny(element));
            }
            // Panels of re-extracted circuits (a circuit can move to a new panel).
            foreach (var panel in _panels.Values.ToList())
                if (!_nodes.ContainsKey(Id(panel.Id))) Step("panel", () => AddPanel(panel));
            Finish();
            sw.Stop();
            _result.ElapsedMs = sw.ElapsedMilliseconds;
            return _result;
        }

        private void Step(string label, Action action)
        {
            try { action(); }
            catch (Exception ex) { _result.Warnings.Add($"Graph step '{label}' failed: {ex.Message}"); }
        }

        private void PrepareLookups()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(Level)))
                if (element is Level level) _levelNames[level.Id.Value] = SafeName(level);
            if (!_isWorkshared) return;
            foreach (var workset in new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset))
            {
                _worksetNames[workset.Id.IntegerValue] = workset.Name;
                _userWorksetIds.Add(GraphSchema.WorksetIdPrefix + workset.Id.IntegerValue.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Subset dispatch: one element of any kind the graph knows.</summary>
        private void AddAny(Element element)
        {
            switch (element)
            {
                case Level level: AddLevel(level); return;
                case ViewSheet sheet: AddSheet(sheet); return;
                case View view: AddView(view); return;
                case SpatialElement spatial: AddSpace(spatial); return;
                case ElectricalSystem circuit: AddCircuit(circuit); return;
                case IndependentTag tag: AddTag(tag); return;
                case SpatialElementTag spatialTag: AddSpatialTag(spatialTag); return;
                case ElementType type: EnsureTypeNode(type, force: true); return;
                case RevitLinkInstance link: AddLinkInstance(link); return;
            }
            if (element is FamilyInstance fi && IsPanel(fi)) { AddPanel(fi); return; }
            AddModelElement(element);
        }

        // ─── Levels / worksets ─────────────────────────────────────────────

        private void CollectLevels()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(Level)))
                if (element is Level level) AddLevel(level);
        }

        private void AddLevel(Level level)
        {
            var name = SafeName(level);
            _levelNames[level.Id.Value] = name;
            AddNode(new GraphNode
            {
                Id = Id(level.Id),
                Kind = GraphSchema.Kinds.Level,
                Name = name,
                Category = level.Category?.Name ?? "Levels",
                Level = name,
                Workset = WorksetName(level),
                Extra = Extra(("elevationMm", Round(level.Elevation * 304.8)))
            });
        }

        private void CollectWorksets()
        {
            if (!_isWorkshared) return;
            foreach (var workset in new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset))
            {
                var id = GraphSchema.WorksetIdPrefix + workset.Id.IntegerValue.ToString(CultureInfo.InvariantCulture);
                _worksetNames[workset.Id.IntegerValue] = workset.Name;
                _userWorksetIds.Add(id);
                AddNode(new GraphNode
                {
                    Id = id,
                    Kind = GraphSchema.Kinds.Workset,
                    Name = workset.Name,
                    Category = "Worksets",
                    Workset = workset.Name,
                    Extra = Extra(("owner", workset.Owner), ("isOpen", workset.IsOpen))
                });
            }
        }

        // ─── Sheets / views ────────────────────────────────────────────────

        private void CollectSheets()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)))
                if (element is ViewSheet sheet) AddSheet(sheet);
        }

        private void AddSheet(ViewSheet sheet)
        {
            if (sheet.IsTemplate) return;
            var id = Id(sheet.Id);
            AddNode(new GraphNode
            {
                Id = id,
                Kind = GraphSchema.Kinds.Sheet,
                Name = $"{sheet.SheetNumber} - {SafeName(sheet)}".Trim(' ', '-'),
                Category = sheet.Category?.Name ?? "Sheets",
                Workset = WorksetName(sheet),
                Extra = Extra(("sheetNumber", sheet.SheetNumber), ("sheetName", SafeName(sheet)))
            });

            try
            {
                foreach (var viewId in sheet.GetAllPlacedViews())
                    AddEdge(Id(viewId), id, GraphSchema.Rels.OnSheet, owner: id);
            }
            catch { }
        }

        private void CollectViews()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(View)))
                if (element is View view && view is not ViewSheet) AddView(view);
        }

        private void AddView(View view)
        {
            bool isTemplate;
            try { isTemplate = view.IsTemplate; } catch { return; }
            if (isTemplate) return;

            switch (view.ViewType)
            {
                case ViewType.Internal:
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.DrawingSheet:
                case ViewType.Undefined:
                    return;
            }

            var id = Id(view.Id);
            string levelName = string.Empty;
            ElementId? levelId = null;
            if (view is ViewPlan plan)
            {
                try
                {
                    var genLevel = plan.GenLevel;
                    if (genLevel != null)
                    {
                        levelId = genLevel.Id;
                        levelName = SafeName(genLevel);
                    }
                }
                catch { }
            }

            AddNode(new GraphNode
            {
                Id = id,
                Kind = GraphSchema.Kinds.View,
                Name = SafeName(view),
                Category = view.Category?.Name ?? "Views",
                Level = levelName,
                Workset = WorksetName(view),
                Extra = Extra(("viewType", view.ViewType.ToString()), ("isSchedule", view is ViewSchedule ? true : (object?)null))
            });
            if (levelId != null) AddEdge(id, Id(levelId), GraphSchema.Rels.OnLevel, owner: id);
        }

        // ─── Rooms and MEP spaces ──────────────────────────────────────────

        private void CollectSpaces()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(SpatialElement)))
                if (element is SpatialElement spatial) AddSpace(spatial);
        }

        private void AddSpace(SpatialElement spatial)
        {
            if (spatial is not Room && spatial is not Space) return;
            try { if (spatial.Location == null) return; } catch { return; } // unplaced

            var id = Id(spatial.Id);
            var number = SafeString(() => spatial.Number);
            var name = SafeString(() => spatial.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString());
            if (string.IsNullOrEmpty(name)) name = SafeName(spatial);

            string levelName = string.Empty;
            ElementId? levelId = null;
            try
            {
                var level = spatial.Level;
                if (level != null) { levelId = level.Id; levelName = SafeName(level); }
            }
            catch { }

            AttachParams(AddNode(new GraphNode
            {
                Id = id,
                Kind = GraphSchema.Kinds.Space,
                Name = $"{number} {name}".Trim(),
                Category = spatial.Category?.Name ?? (spatial is Room ? "Rooms" : "Spaces"),
                Level = levelName,
                Workset = WorksetName(spatial),
                Extra = Extra(("number", number), ("name", name), ("spatialType", spatial is Room ? "Room" : "Space"))
            }), spatial);
            if (levelId != null) AddEdge(id, Id(levelId), GraphSchema.Rels.OnLevel, owner: id);
            AddWorksetEdge(spatial, id);
        }

        // ─── Panels / circuits ─────────────────────────────────────────────

        private void CollectPanels()
        {
            // Electrical Equipment also contains transformers and other equipment that cannot serve
            // as a circuit panel. Circuits identify actual panels through BaseEquipment, so only
            // instances observed there are promoted from ordinary elements to panel nodes.
            foreach (var panel in _panels.Values) AddPanel(panel);
        }

        private void AddPanel(FamilyInstance panel)
        {
            var id = Id(panel.Id);
            var (levelName, levelId) = LevelOf(panel);
            AttachParams(AddNode(new GraphNode
            {
                Id = id,
                Kind = GraphSchema.Kinds.Panel,
                Name = SafeName(panel),
                Category = panel.Category?.Name ?? "Electrical Equipment",
                Level = levelName,
                Workset = WorksetName(panel),
                Extra = Extra(
                    ("family", SafeString(() => panel.Symbol?.Family?.Name)),
                    ("type", SafeString(() => panel.Symbol?.Name)),
                    ("panelName", SafeString(() => panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString())))
            }), panel);
            if (levelId != null) AddEdge(id, Id(levelId), GraphSchema.Rels.OnLevel, owner: id);
            AddWorksetEdge(panel, id);
            AddInstanceEdges(panel, id);
        }

        /// <summary>Subset mode: is this instance the BaseEquipment of any circuit?</summary>
        private static bool IsPanel(FamilyInstance fi)
        {
            try
            {
                var systems = fi.MEPModel?.GetAssignedElectricalSystems();
                return systems != null && systems.Any(s => s?.BaseEquipment?.Id == fi.Id);
            }
            catch
            {
                return false;
            }
        }

        private void CollectCircuits()
        {
            var failed = 0;
            var circuits = new FilteredElementCollector(_doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>();
            foreach (var circuit in circuits)
                if (!AddCircuit(circuit)) failed++;
            if (failed > 0)
                _result.Warnings.Add($"{failed} circuit(s) did not expose their element set; their fed_by edges are missing.");
        }

        /// <summary>Circuit node, its fed_by edge to the panel and its members' fed_by edges (all owned by the circuit).</summary>
        private bool AddCircuit(ElectricalSystem circuit)
        {
            var id = Id(circuit.Id);
            FamilyInstance? panel;
            try { panel = circuit.BaseEquipment; } catch { panel = null; }
            var panelName = panel != null ? SafeName(panel) : SafeString(() => circuit.PanelName);
            var circuitNumber = SafeString(() => circuit.CircuitNumber);
            var loadName = SafeString(() => circuit.LoadName);
            var label = $"{panelName}/{circuitNumber}".Trim('/');
            if (!string.IsNullOrEmpty(loadName)) label = $"{label} {loadName}".Trim();

            AttachParams(AddNode(new GraphNode
            {
                Id = id,
                Kind = GraphSchema.Kinds.Circuit,
                Name = label,
                Category = circuit.Category?.Name ?? "Electrical Circuits",
                Level = panel != null ? LevelOf(panel).Name : string.Empty,
                Workset = WorksetName(circuit),
                Extra = Extra(
                    ("circuitNumber", circuitNumber),
                    ("loadName", loadName),
                    ("panel", panelName),
                    ("panelId", panel != null ? Id(panel.Id) : null),
                    ("systemType", SafeString(() => circuit.SystemType.ToString())))
            }), circuit);
            AddWorksetEdge(circuit, id);
            if (panel != null)
            {
                var panelId = Id(panel.Id);
                _panels[panelId] = panel;
                AddEdge(id, panelId, GraphSchema.Rels.FedBy, owner: id);
            }

            try
            {
                var elements = circuit.Elements;
                if (elements != null)
                {
                    foreach (Element e in elements)
                        if (e != null) AddEdge(Id(e.Id), id, GraphSchema.Rels.FedBy, owner: id);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ─── Model elements ────────────────────────────────────────────────

        private void CollectElements()
        {
            var count = 0;
            var collector = new FilteredElementCollector(_doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            foreach (var element in collector)
            {
                if (element == null) continue;
                if (count >= _elementLimit)
                {
                    _result.ElementLimitReached = true;
                    _result.Warnings.Add($"Element limit of {_elementLimit} reached; remaining model elements were not added. Raise elementLimit to include them.");
                    break;
                }
                if (AddModelElement(element)) count++;
            }
        }

        /// <summary>An ordinary model element (not a level/space/view/circuit/panel already added). Returns true when added.</summary>
        private bool AddModelElement(Element element)
        {
            if (element is ElementType) return false;
            try { if (element.ViewSpecific) return false; } catch { return false; }

            Category? category;
            try { category = element.Category; } catch { return false; }
            if (category == null) return false;
            bool isModel;
            try { isModel = category.CategoryType == CategoryType.Model && !category.IsTagCategory; } catch { return false; }
            if (!isModel) return false;

            var id = Id(element.Id);
            if (_nodes.ContainsKey(id)) return false; // levels, spaces, panels, circuits already added
            if (element is Level || element is SpatialElement || element is View || element is ElectricalSystem) return false;
            if (element is RevitLinkInstance link)
            {
                AddLinkInstance(link);
                return true;
            }

            var (levelName, levelId) = LevelOf(element);
            string? extra;
            if (element is FamilyInstance fi)
            {
                extra = Extra(
                    ("family", SafeString(() => fi.Symbol?.Family?.Name)),
                    ("type", SafeString(() => fi.Symbol?.Name)));
            }
            else
            {
                var typeElem = TypeOf(element);
                extra = Extra(("type", typeElem != null ? SafeName(typeElem) : null));
            }

            AttachParams(AddNode(new GraphNode
            {
                Id = id,
                Kind = GraphSchema.Kinds.Element,
                Name = SafeName(element),
                Category = category.Name,
                Level = levelName,
                Workset = WorksetName(element),
                Extra = extra
            }), element);

            if (levelId != null) AddEdge(id, Id(levelId), GraphSchema.Rels.OnLevel, owner: id);
            AddWorksetEdge(element, id);
            if (element is FamilyInstance instance)
                AddInstanceEdges(instance, id);
            else
                AddTypeEdge(element, id);
            return true;
        }

        /// <summary>type_of, hosted_on and located_in edges shared by panels and ordinary family instances.</summary>
        private void AddInstanceEdges(FamilyInstance fi, string id)
        {
            AddTypeEdge(fi, id);

            try
            {
                var host = fi.Host;
                if (host != null && host is not Level && host.Id != ElementId.InvalidElementId)
                    AddEdge(id, Id(host.Id), GraphSchema.Rels.HostedOn, owner: id);
            }
            catch { }

            ElementId? spaceId = null;
            try { spaceId = fi.Room?.Id; } catch { }
            if (spaceId == null)
            {
                try { spaceId = fi.Space?.Id; } catch { }
            }
            if (spaceId != null && spaceId != ElementId.InvalidElementId)
                AddEdge(id, Id(spaceId), GraphSchema.Rels.LocatedIn, owner: id);
        }

        private void AddTypeEdge(Element element, string id)
        {
            var typeElem = TypeOf(element);
            if (typeElem == null) return;
            var typeId = EnsureTypeNode(typeElem);
            AddEdge(id, typeId, GraphSchema.Rels.TypeOf, owner: id);
        }

        private string EnsureTypeNode(ElementType type, bool force = false)
        {
            var id = Id(type.Id);
            if (_nodes.ContainsKey(id) && !force) return id;

            var family = SafeString(() => type.FamilyName);
            var typeName = SafeName(type);
            AddNode(new GraphNode
            {
                Id = id,
                Kind = GraphSchema.Kinds.Type,
                Name = string.IsNullOrEmpty(family) ? typeName : $"{family}: {typeName}",
                Category = type.Category?.Name ?? string.Empty,
                Workset = WorksetName(type),
                Extra = Extra(("family", family), ("type", typeName))
            });
            return id;
        }

        // ─── Linked models (#62) ───────────────────────────────────────────

        /// <summary>The link instance itself: a host element, so its id stays the plain host id.</summary>
        private void AddLinkInstance(RevitLinkInstance link)
        {
            var id = Id(link.Id);
            var linkDoc = LinkDocument(link);
            var (levelName, levelId) = LevelOf(link);
            AddNode(new GraphNode
            {
                Id = id,
                Kind = GraphSchema.Kinds.Link,
                Name = SafeName(link),
                Category = SafeString(() => link.Category?.Name),
                Level = levelName,
                Workset = WorksetName(link),
                Extra = Extra(
                    ("document", linkDoc != null ? SafeString(() => linkDoc.Title) : null),
                    ("path", linkDoc != null ? SafeString(() => linkDoc.PathName) : null),
                    ("loaded", linkDoc != null))
            });
            if (levelId != null) AddEdge(id, Id(levelId), GraphSchema.Rels.OnLevel, owner: id);
            AddWorksetEdge(link, id);
            AddTypeEdge(link, id);
        }

        private static Document? LinkDocument(RevitLinkInstance link)
        {
            try { return link.GetLinkDocument(); }
            catch { return null; }
        }

        private void CollectLinks()
        {
            var hostHasSpaces = _nodes.Values.Any(n => n.Kind == GraphSchema.Kinds.Space);
            var links = new FilteredElementCollector(_doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            foreach (var link in links)
            {
                var instanceId = Id(link.Id);
                if (!_nodes.ContainsKey(instanceId)) AddLinkInstance(link);
                var linkDoc = LinkDocument(link);
                var summary = new GraphLinkSummary
                {
                    InstanceId = instanceId,
                    Name = SafeName(link),
                    Document = linkDoc != null ? SafeString(() => linkDoc.Title) : string.Empty,
                    Loaded = linkDoc != null
                };
                _result.Links.Add(summary);
                if (linkDoc == null)
                {
                    _result.Warnings.Add($"Link '{summary.Name}' ({instanceId}) is not loaded; its elements are not in the graph.");
                    continue;
                }
                if (_result.LinkElementLimitReached) continue;

                Transform? transform;
                try { transform = link.GetTotalTransform(); } catch { transform = null; }
                var scope = new LinkScope(link.Id.Value, summary.Name, summary.Document, linkDoc, transform);
                Step($"link {instanceId}", () => CollectLinkContent(scope, summary, hostHasSpaces));
            }
        }

        private sealed class LinkScope
        {
            public LinkScope(long instanceId, string name, string document, Document doc, Transform? transform)
            {
                InstanceId = instanceId;
                InstanceNodeId = instanceId.ToString(CultureInfo.InvariantCulture);
                Name = name;
                DocumentTitle = document;
                Doc = doc;
                Transform = transform;
            }

            public long InstanceId { get; }
            public string InstanceNodeId { get; }
            public string Name { get; }
            public string DocumentTitle { get; }
            public Document Doc { get; }
            public Transform? Transform { get; }
            public Dictionary<long, string> LevelNames { get; } = new();

            public string IdOf(ElementId id) => GraphLinkIds.Make(InstanceId, id.Value);
        }

        private void CollectLinkContent(LinkScope link, GraphLinkSummary summary, bool hostHasSpaces)
        {
            // Rooms and MEP spaces of the linked model.
            foreach (var element in new FilteredElementCollector(link.Doc).OfClass(typeof(SpatialElement)))
            {
                if (element is not (Room or Space)) continue;
                var spatial = (SpatialElement)element;
                try { if (spatial.Location == null) continue; } catch { continue; }
                if (!TakeLinkSlot()) return;

                var id = link.IdOf(spatial.Id);
                var number = SafeString(() => spatial.Number);
                var name = SafeString(() => spatial.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString());
                if (string.IsNullOrEmpty(name)) name = SafeName(spatial);
                AttachParams(AddNode(new GraphNode
                {
                    Id = id,
                    Kind = GraphSchema.Kinds.Space,
                    Name = $"{number} {name}".Trim(),
                    Category = SafeString(() => spatial.Category?.Name),
                    Level = LinkedLevelName(link, spatial),
                    Extra = Extra(("number", number), ("name", name), ("spatialType", spatial is Room ? "Room" : "Space"),
                        ("linkName", link.Name), ("linkDocument", link.DocumentTitle))
                }), spatial, link.Doc);
                AddEdge(id, link.InstanceNodeId, GraphSchema.Rels.InLink, owner: id);
                summary.Spaces++;
            }

            // Model elements of the linked model (same rules as the host; nested links are not followed).
            var collector = new FilteredElementCollector(link.Doc).WhereElementIsNotElementType().WhereElementIsViewIndependent();
            foreach (var element in collector)
            {
                if (element == null || element is Level || element is SpatialElement || element is View ||
                    element is ElectricalSystem || element is RevitLinkInstance || element is BasePoint) continue;
                Category? category;
                try { category = element.Category; } catch { continue; }
                if (category == null) continue;
                bool isModel;
                try { isModel = category.CategoryType == CategoryType.Model && !category.IsTagCategory; } catch { continue; }
                if (!isModel) continue;
                // Physical elements only: materials, legend components, cameras etc. have no bounding box.
                BoundingBoxXYZ? box;
                try { box = element.get_BoundingBox(null); } catch { box = null; }
                if (box == null) continue;
                if (!TakeLinkSlot()) return;

                var id = link.IdOf(element.Id);
                ElementType? type;
                try { type = link.Doc.GetElement(element.GetTypeId()) as ElementType; } catch { type = null; }
                var fi = element as FamilyInstance;
                AttachParams(AddNode(new GraphNode
                {
                    Id = id,
                    Kind = GraphSchema.Kinds.Element,
                    Name = SafeName(element),
                    Category = category.Name,
                    Level = LinkedLevelName(link, element),
                    Extra = Extra(
                        ("family", fi != null ? SafeString(() => fi.Symbol?.Family?.Name) : type != null ? SafeString(() => type.FamilyName) : null),
                        ("type", type != null ? SafeName(type) : null),
                        ("linkName", link.Name), ("linkDocument", link.DocumentTitle))
                }), element, link.Doc);
                AddEdge(id, link.InstanceNodeId, GraphSchema.Rels.InLink, owner: id);
                summary.Elements++;

                if (type != null)
                {
                    var typeId = link.IdOf(type.Id);
                    if (!_nodes.ContainsKey(typeId))
                    {
                        var family = SafeString(() => type.FamilyName);
                        var typeName = SafeName(type);
                        AddNode(new GraphNode
                        {
                            Id = typeId,
                            Kind = GraphSchema.Kinds.Type,
                            Name = string.IsNullOrEmpty(family) ? typeName : $"{family}: {typeName}",
                            Category = SafeString(() => type.Category?.Name),
                            Extra = Extra(("family", family), ("type", typeName), ("linkName", link.Name), ("linkDocument", link.DocumentTitle))
                        });
                        AddEdge(typeId, link.InstanceNodeId, GraphSchema.Rels.InLink, owner: typeId);
                    }
                    AddEdge(id, typeId, GraphSchema.Rels.TypeOf, owner: id);
                }

                // Room/space inside the linked model.
                if (fi != null)
                {
                    ElementId? linkedSpace = null;
                    try { linkedSpace = fi.Room?.Id; } catch { }
                    if (linkedSpace == null) { try { linkedSpace = fi.Space?.Id; } catch { } }
                    if (linkedSpace != null && linkedSpace != ElementId.InvalidElementId)
                        AddEdge(id, link.IdOf(linkedSpace), GraphSchema.Rels.LocatedIn, owner: id);
                }

                // Host room/space containing the element (geometric, through the link transform).
                if (hostHasSpaces && link.Transform != null)
                {
                    var hostSpace = HostSpaceAt(link.Transform, ContainmentPoint(fi, box));
                    if (hostSpace != null) AddEdge(id, hostSpace, GraphSchema.Rels.LocatedIn, owner: id);
                }
            }
        }

        private const double MaxContainmentExtentFt = 3000 / 304.8; // 3 m: larger elements span rooms
        private const double CeilingDropFt = 1000 / 304.8;

        /// <summary>
        /// The point that decides which room a linked element is in: a family instance's insertion point,
        /// otherwise the bounding-box centre of a small element (IFC DirectShapes have no location).
        /// Large elements (walls, slabs, long runs) get none — "the room it is in" is meaningless for them.
        /// </summary>
        private static XYZ? ContainmentPoint(FamilyInstance? fi, BoundingBoxXYZ box)
        {
            try
            {
                if (fi?.Location is LocationPoint lp) return lp.Point;
                var size = box.Max - box.Min;
                if (Math.Max(size.X, Math.Max(size.Y, size.Z)) > MaxContainmentExtentFt) return null;
                var center = (box.Min + box.Max) / 2;
                return box.Transform != null ? box.Transform.OfPoint(center) : center;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Host room/space at a linked-model point; retried 1 m lower for ceiling-mounted items above the room's upper limit.</summary>
        private string? HostSpaceAt(Transform transform, XYZ? linkPoint)
        {
            if (linkPoint == null) return null;
            try
            {
                var point = transform.OfPoint(linkPoint);
                foreach (var p in new[] { point, point - new XYZ(0, 0, CeilingDropFt) })
                {
                    Element? space = _doc.GetRoomAtPoint(p);
                    space ??= _doc.GetSpaceAtPoint(p);
                    if (space != null) return Id(space.Id);
                }
            }
            catch { }
            return null;
        }

        private bool TakeLinkSlot()
        {
            if (_linkedCount >= LinkElementLimit)
            {
                if (!_result.LinkElementLimitReached)
                    _result.Warnings.Add($"Linked element limit of {LinkElementLimit} reached; remaining linked elements were not added. Raise linkElementLimit to include them.");
                _result.LinkElementLimitReached = true;
                return false;
            }
            _linkedCount++;
            return true;
        }

        private static string LinkedLevelName(LinkScope link, Element element)
        {
            ElementId? levelId = null;
            try { levelId = element.LevelId; } catch { }
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.RBS_START_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM })
                {
                    try
                    {
                        var p = element.get_Parameter(bip);
                        if (p == null || p.StorageType != StorageType.ElementId) continue;
                        var candidate = p.AsElementId();
                        if (candidate != null && candidate != ElementId.InvalidElementId) { levelId = candidate; break; }
                    }
                    catch { }
                }
            }
            if (levelId == null || levelId == ElementId.InvalidElementId) return string.Empty;
            if (!link.LevelNames.TryGetValue(levelId.Value, out var name))
            {
                try { name = (link.Doc.GetElement(levelId) as Level)?.Name ?? string.Empty; } catch { name = string.Empty; }
                link.LevelNames[levelId.Value] = name;
            }
            return name;
        }

        // ─── Tags ──────────────────────────────────────────────────────────

        private void CollectTags()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(IndependentTag)))
                if (element is IndependentTag tag) AddTag(tag);
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(SpatialElementTag)))
                if (element is SpatialElementTag tag) AddSpatialTag(tag);
        }

        private void AddTag(IndependentTag tag)
        {
            var viewId = OwnerView(tag);
            if (viewId == null) return;

            ICollection<ElementId> taggedIds;
            try { taggedIds = tag.GetTaggedLocalElementIds(); }
            catch { return; }

            var owner = Id(tag.Id);
            foreach (var taggedId in taggedIds)
            {
                if (taggedId == null || taggedId == ElementId.InvalidElementId) continue;
                AddEdge(Id(taggedId), viewId, GraphSchema.Rels.TaggedIn, owner);
            }
        }

        private void AddSpatialTag(SpatialElementTag tag)
        {
            var viewId = OwnerView(tag);
            if (viewId == null) return;

            ElementId? taggedId = null;
            try
            {
                taggedId = tag switch
                {
                    RoomTag roomTag => roomTag.Room?.Id,
                    SpaceTag spaceTag => spaceTag.Space?.Id,
                    _ => null
                };
            }
            catch { }
            if (taggedId == null || taggedId == ElementId.InvalidElementId) return;
            AddEdge(Id(taggedId), viewId, GraphSchema.Rels.TaggedIn, Id(tag.Id));
        }

        private static string? OwnerView(Element tag)
        {
            try
            {
                var viewId = tag.OwnerViewId;
                return viewId == null || viewId == ElementId.InvalidElementId ? null : Id(viewId);
            }
            catch
            {
                return null;
            }
        }

        // ─── Finish ────────────────────────────────────────────────────────

        private void Finish()
        {
            _result.Nodes.AddRange(_nodes.Values);
            foreach (var node in _nodes.Values)
                Increment(_result.NodesByKind, node.Kind);

            var counted = new HashSet<(string, string, string)>();
            foreach (var (src, dst, rel, owner) in _edges)
            {
                // Full build: drop edges to elements that are not in the graph. Subset: the database
                // checks endpoints against the existing nodes instead.
                if (!_subset && (!_nodes.ContainsKey(src) || !_nodes.ContainsKey(dst)))
                {
                    _result.DanglingEdgesDropped++;
                    continue;
                }
                _result.Edges.Add(new GraphEdge(src, dst, rel, owner));
                if (counted.Add((src, dst, rel))) Increment(_result.EdgesByRel, rel);
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        private GraphNode AddNode(GraphNode node)
        {
            _nodes[node.Id] = node;
            return node;
        }

        /// <summary>Reads the allowlisted routing parameters (#63) of an element onto its node (instance first, then type).</summary>
        private void AttachParams(GraphNode node, Element element, Document? owner = null)
        {
            if (RoutingParameters.Count == 0) return;
            Dictionary<string, string>? values = null;
            foreach (var spec in RoutingParameters)
            {
                if (!spec.AppliesTo(node.Category)) continue;
                var value = GraphRoutingParameters.Clean(ReadParameter(element, spec, owner ?? _doc));
                if (value == null) continue;
                (values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[spec.Name] = value;
            }
            node.Params = values;
        }

        private static string? ReadParameter(Element element, RoutingParameterSpec spec, Document doc)
        {
            var text = ParameterText(Lookup(element, spec));
            if (text != null) return text;
            try
            {
                var typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return null;
                var type = doc.GetElement(typeId);
                return type == null ? null : ParameterText(Lookup(type, spec));
            }
            catch
            {
                return null;
            }
        }

        private static Parameter? Lookup(Element element, RoutingParameterSpec spec)
        {
            try { return spec.Guid != null ? element.get_Parameter(spec.Guid.Value) : element.LookupParameter(spec.Name); }
            catch { return null; }
        }

        private static string? ParameterText(Parameter? p)
        {
            if (p == null) return null;
            try
            {
                if (!p.HasValue) return null;
                return p.StorageType switch
                {
                    StorageType.String => p.AsString(),
                    StorageType.Integer => p.AsValueString() ?? p.AsInteger().ToString(CultureInfo.InvariantCulture),
                    _ => p.AsValueString()
                };
            }
            catch
            {
                return null;
            }
        }

        private void AddEdge(string src, string dst, string rel, string owner)
        {
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst) || src == dst) return;
            _edges.Add((src, dst, rel, owner));
        }

        private void AddWorksetEdge(Element element, string id)
        {
            if (!_isWorkshared) return;
            try
            {
                var worksetId = GraphSchema.WorksetIdPrefix + element.WorksetId.IntegerValue.ToString(CultureInfo.InvariantCulture);
                if (_userWorksetIds.Contains(worksetId))
                    AddEdge(id, worksetId, GraphSchema.Rels.InWorkset, owner: id);
            }
            catch { }
        }

        private string WorksetName(Element element)
        {
            if (!_isWorkshared) return string.Empty;
            try
            {
                var worksetId = element.WorksetId;
                if (worksetId == null) return string.Empty;
                if (_worksetNames.TryGetValue(worksetId.IntegerValue, out var cached)) return cached;
                var workset = _doc.GetWorksetTable().GetWorkset(worksetId);
                var name = workset?.Name ?? string.Empty;
                _worksetNames[worksetId.IntegerValue] = name;
                return name;
            }
            catch
            {
                return string.Empty;
            }
        }

        private (string Name, ElementId? Id) LevelOf(Element element)
        {
            ElementId? levelId = null;
            try { levelId = element.LevelId; } catch { }
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.RBS_START_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.LEVEL_PARAM })
                {
                    try
                    {
                        var p = element.get_Parameter(bip);
                        if (p == null || p.StorageType != StorageType.ElementId) continue;
                        var candidate = p.AsElementId();
                        if (candidate != null && candidate != ElementId.InvalidElementId) { levelId = candidate; break; }
                    }
                    catch { }
                }
            }
            if (levelId == null || levelId == ElementId.InvalidElementId) return (string.Empty, null);

            if (!_levelNames.TryGetValue(levelId.Value, out var name))
            {
                try { name = (_doc.GetElement(levelId) as Level)?.Name ?? string.Empty; } catch { name = string.Empty; }
                _levelNames[levelId.Value] = name;
            }
            return (name, _levelNames.ContainsKey(levelId.Value) && name.Length > 0 ? levelId : null);
        }

        private ElementType? TypeOf(Element element)
        {
            try
            {
                var typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return null;
                return _doc.GetElement(typeId) as ElementType;
            }
            catch
            {
                return null;
            }
        }

        private static string Id(ElementId id) => id.Value.ToString(CultureInfo.InvariantCulture);

        private static string SafeName(Element element)
        {
            try { return element.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeString(Func<string?> read)
        {
            try { return read() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static double Round(double value) => Math.Round(value, 1);

        /// <summary>Small JSON object; blank strings and nulls are dropped so rows stay compact.</summary>
        private static string? Extra(params (string Key, object? Value)[] pairs)
        {
            var obj = new JObject();
            foreach (var (key, value) in pairs)
            {
                if (value == null) continue;
                if (value is string s && s.Length == 0) continue;
                obj[key] = JToken.FromObject(value);
            }
            return obj.Count == 0 ? null : obj.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void Increment(Dictionary<string, long> map, string key)
        {
            map.TryGetValue(key, out var current);
            map[key] = current + 1;
        }
    }
}
