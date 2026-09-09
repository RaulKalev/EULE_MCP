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
}

/// <summary>
/// Walks the open document once and produces graph nodes and edges. Reads only identity and
/// structure (ids, names, category, level, workset, host, room, circuit, type, tag, sheet) —
/// never parameter values. Must run on the Revit API thread.
/// </summary>
public static class RevitGraphExtractor
{
    public static GraphExtractionResult Extract(Document doc, int elementLimit)
    {
        var builder = new Builder(doc, elementLimit);
        return builder.Run();
    }

    private sealed class Builder
    {
        private readonly Document _doc;
        private readonly int _elementLimit;
        private readonly GraphExtractionResult _result = new();
        private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
        private readonly HashSet<(string, string, string)> _edges = new();
        private readonly Dictionary<long, string> _levelNames = new();
        private readonly Dictionary<int, string> _worksetNames = new();
        private readonly HashSet<string> _userWorksetIds = new(StringComparer.Ordinal);
        private readonly bool _isWorkshared;

        public Builder(Document doc, int elementLimit)
        {
            _doc = doc;
            _elementLimit = elementLimit;
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
            Step("panels", CollectPanels);
            Step("circuits", CollectCircuits);
            Step("elements", CollectElements);
            Step("tags", CollectTags);
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

        // ─── Levels / worksets ─────────────────────────────────────────────

        private void CollectLevels()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(Level)))
            {
                if (element is not Level level) continue;
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
            {
                if (element is not ViewSheet sheet || sheet.IsTemplate) continue;
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
                        AddEdge(Id(viewId), id, GraphSchema.Rels.OnSheet);
                }
                catch { }
            }
        }

        private void CollectViews()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(View)))
            {
                if (element is not View view || view is ViewSheet) continue;
                bool isTemplate;
                try { isTemplate = view.IsTemplate; } catch { continue; }
                if (isTemplate) continue;

                switch (view.ViewType)
                {
                    case ViewType.Internal:
                    case ViewType.ProjectBrowser:
                    case ViewType.SystemBrowser:
                    case ViewType.DrawingSheet:
                    case ViewType.Undefined:
                        continue;
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
                if (levelId != null) AddEdge(id, Id(levelId), GraphSchema.Rels.OnLevel);
            }
        }

        // ─── Rooms and MEP spaces ──────────────────────────────────────────

        private void CollectSpaces()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(SpatialElement)))
            {
                if (element is not SpatialElement spatial) continue;
                if (spatial is not Room && spatial is not Space) continue;
                try { if (spatial.Location == null) continue; } catch { continue; } // unplaced

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

                AddNode(new GraphNode
                {
                    Id = id,
                    Kind = GraphSchema.Kinds.Space,
                    Name = $"{number} {name}".Trim(),
                    Category = spatial.Category?.Name ?? (spatial is Room ? "Rooms" : "Spaces"),
                    Level = levelName,
                    Workset = WorksetName(spatial),
                    Extra = Extra(("number", number), ("name", name), ("spatialType", spatial is Room ? "Room" : "Space"))
                });
                if (levelId != null) AddEdge(id, Id(levelId), GraphSchema.Rels.OnLevel);
                AddWorksetEdge(spatial, id);
            }
        }

        // ─── Panels / circuits ─────────────────────────────────────────────

        private void CollectPanels()
        {
            var panels = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .Cast<FamilyInstance>();
            foreach (var panel in panels)
            {
                var id = Id(panel.Id);
                var (levelName, levelId) = LevelOf(panel);
                AddNode(new GraphNode
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
                });
                if (levelId != null) AddEdge(id, Id(levelId), GraphSchema.Rels.OnLevel);
                AddWorksetEdge(panel, id);
                AddInstanceEdges(panel, id);
            }
        }

        private void CollectCircuits()
        {
            var failed = 0;
            var circuits = new FilteredElementCollector(_doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>();
            foreach (var circuit in circuits)
            {
                var id = Id(circuit.Id);
                FamilyInstance? panel;
                try { panel = circuit.BaseEquipment; } catch { panel = null; }
                var panelName = panel != null ? SafeName(panel) : SafeString(() => circuit.PanelName);
                var circuitNumber = SafeString(() => circuit.CircuitNumber);
                var loadName = SafeString(() => circuit.LoadName);
                var label = $"{panelName}/{circuitNumber}".Trim('/');
                if (!string.IsNullOrEmpty(loadName)) label = $"{label} {loadName}".Trim();

                AddNode(new GraphNode
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
                });
                AddWorksetEdge(circuit, id);
                if (panel != null) AddEdge(id, Id(panel.Id), GraphSchema.Rels.FedBy);

                try
                {
                    var elements = circuit.Elements;
                    if (elements != null)
                    {
                        foreach (Element e in elements)
                            if (e != null) AddEdge(Id(e.Id), id, GraphSchema.Rels.FedBy);
                    }
                }
                catch
                {
                    failed++;
                }
            }
            if (failed > 0)
                _result.Warnings.Add($"{failed} circuit(s) did not expose their element set; their fed_by edges are missing.");
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

                Category? category;
                try { category = element.Category; } catch { continue; }
                if (category == null) continue;
                bool isModel;
                try { isModel = category.CategoryType == CategoryType.Model && !category.IsTagCategory; } catch { continue; }
                if (!isModel) continue;

                var id = Id(element.Id);
                if (_nodes.ContainsKey(id)) continue; // levels, spaces, panels, circuits already added
                if (element is Level || element is SpatialElement || element is View || element is ElectricalSystem) continue;

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

                AddNode(new GraphNode
                {
                    Id = id,
                    Kind = GraphSchema.Kinds.Element,
                    Name = SafeName(element),
                    Category = category.Name,
                    Level = levelName,
                    Workset = WorksetName(element),
                    Extra = extra
                });
                count++;

                if (levelId != null) AddEdge(id, Id(levelId), GraphSchema.Rels.OnLevel);
                AddWorksetEdge(element, id);
                if (element is FamilyInstance instance)
                    AddInstanceEdges(instance, id);
                else
                    AddTypeEdge(element, id);
            }
        }

        /// <summary>type_of, hosted_on and located_in edges shared by panels and ordinary family instances.</summary>
        private void AddInstanceEdges(FamilyInstance fi, string id)
        {
            AddTypeEdge(fi, id);

            try
            {
                var host = fi.Host;
                if (host != null && host is not Level && host.Id != ElementId.InvalidElementId)
                    AddEdge(id, Id(host.Id), GraphSchema.Rels.HostedOn);
            }
            catch { }

            ElementId? spaceId = null;
            try { spaceId = fi.Room?.Id; } catch { }
            if (spaceId == null)
            {
                try { spaceId = fi.Space?.Id; } catch { }
            }
            if (spaceId != null && spaceId != ElementId.InvalidElementId)
                AddEdge(id, Id(spaceId), GraphSchema.Rels.LocatedIn);
        }

        private void AddTypeEdge(Element element, string id)
        {
            var typeElem = TypeOf(element);
            if (typeElem == null) return;
            var typeId = EnsureTypeNode(typeElem);
            AddEdge(id, typeId, GraphSchema.Rels.TypeOf);
        }

        private string EnsureTypeNode(ElementType type)
        {
            var id = Id(type.Id);
            if (_nodes.ContainsKey(id)) return id;

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

        // ─── Tags ──────────────────────────────────────────────────────────

        private void CollectTags()
        {
            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(IndependentTag)))
            {
                if (element is not IndependentTag tag) continue;
                var viewId = OwnerView(tag);
                if (viewId == null) continue;

                ICollection<ElementId> taggedIds;
                try { taggedIds = tag.GetTaggedLocalElementIds(); }
                catch { continue; }

                foreach (var taggedId in taggedIds)
                {
                    if (taggedId == null || taggedId == ElementId.InvalidElementId) continue;
                    AddEdge(Id(taggedId), viewId, GraphSchema.Rels.TaggedIn);
                }
            }

            foreach (var element in new FilteredElementCollector(_doc).OfClass(typeof(SpatialElementTag)))
            {
                if (element is not SpatialElementTag tag) continue;
                var viewId = OwnerView(tag);
                if (viewId == null) continue;

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
                if (taggedId == null || taggedId == ElementId.InvalidElementId) continue;
                AddEdge(Id(taggedId), viewId, GraphSchema.Rels.TaggedIn);
            }
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

            foreach (var (src, dst, rel) in _edges)
            {
                if (!_nodes.ContainsKey(src) || !_nodes.ContainsKey(dst))
                {
                    _result.DanglingEdgesDropped++;
                    continue;
                }
                _result.Edges.Add(new GraphEdge(src, dst, rel));
                Increment(_result.EdgesByRel, rel);
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        private void AddNode(GraphNode node) => _nodes[node.Id] = node;

        private void AddEdge(string src, string dst, string rel)
        {
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst) || src == dst) return;
            _edges.Add((src, dst, rel));
        }

        private void AddWorksetEdge(Element element, string id)
        {
            if (!_isWorkshared) return;
            try
            {
                var worksetId = GraphSchema.WorksetIdPrefix + element.WorksetId.IntegerValue.ToString(CultureInfo.InvariantCulture);
                if (_userWorksetIds.Contains(worksetId))
                    AddEdge(id, worksetId, GraphSchema.Rels.InWorkset);
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
