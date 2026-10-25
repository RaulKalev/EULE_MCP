using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.RoomDevices;

namespace RevitMCP.Addin.ModelState;

/// <summary>
/// The model health audit's collectors (#109), one method per section. Every section is timed and wrapped, so one
/// failure leaves the rest of the report intact; lists are capped (<see cref="MaxRows"/>), and the whole-model scan
/// that feeds the summary, unused types, phases and worksets runs once and is capped too (<see cref="MaxScanElements"/>).
/// API thread only.
/// </summary>
public sealed class AuditCollectors
{
    public const int MaxRows = 500;
    public const int MaxScanElements = 400_000;
    public const int OwnershipSample = 2_000;
    public const int TopCategories = 30;

    private readonly UIApplication _uiapp;
    private readonly Document _doc;
    private readonly AuditRules _rules;
    private readonly CancellationToken _ct;
    private ElementScan? _scan;
    private List<FailureMessage>? _warnings;

    public AuditCollectors(UIApplication uiapp, Document doc, AuditRules rules, CancellationToken cancellationToken)
    {
        _uiapp = uiapp;
        _doc = doc;
        _rules = rules;
        _ct = cancellationToken;
    }

    /// <summary>Runs the requested sections (canonical names, page order) into a report.</summary>
    public AuditReport Run(IReadOnlyList<string> sections)
    {
        var sw = Stopwatch.StartNew();
        var report = new AuditReport
        {
            DocumentTitle = _doc.Title,
            LocalPath = NullIfEmpty(SafeString(() => _doc.PathName)),
            CentralPath = NullIfEmpty(RevitDocumentResolver.GetCentralPath(_doc)),
            RevitVersion = SafeString(() => _doc.Application.VersionNumber),
            GeneratedAt = DateTimeOffset.Now
        };
        foreach (var name in sections)
        {
            _ct.ThrowIfCancellationRequested();
            report.Sections.Add(RunSection(name));
        }
        sw.Stop();
        report.DurationMs = sw.ElapsedMilliseconds;
        return report;
    }

    private AuditSection RunSection(string name)
    {
        var section = new AuditSection { Name = name, Title = AuditSections.TitleOf(name) };
        var sw = Stopwatch.StartNew();
        try
        {
            switch (name)
            {
                case AuditSections.Summary: Summary(section); break;
                case AuditSections.Warnings: Warnings(section); break;
                case AuditSections.Types: UnusedTypes(section); break;
                case AuditSections.Views: Views(section); break;
                case AuditSections.Sheets: Sheets(section); break;
                case AuditSections.Rooms: Rooms(section); break;
                case AuditSections.Imports: Imports(section); break;
                case AuditSections.Families: InPlaceFamilies(section); break;
                case AuditSections.Groups: Groups(section); break;
                case AuditSections.Phases: Phases(section); break;
                case AuditSections.DesignOptions: DesignOptions(section); break;
                case AuditSections.Worksets: Worksets(section); break;
                case AuditSections.Links: Links(section); break;
                case AuditSections.Graph: GraphAndAgent(section); break;
                default: section.Status = AuditStatus.Skipped; section.Notes.Add("Unknown section."); break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            section.Error = $"{ex.GetType().Name}: {ex.Message}";
        }
        sw.Stop();
        section.DurationMs = sw.ElapsedMilliseconds;
        section.Finish(_rules);
        return section;
    }

    // ─── Shared whole-model scan ───────────────────────────────────────────

    private sealed class ElementScan
    {
        public int Scanned;
        public bool Truncated;
        public readonly Dictionary<string, int> Categories = new(StringComparer.Ordinal);
        public readonly HashSet<long> UsedTypeIds = new();
        public readonly Dictionary<long, int> Created = new();
        public readonly Dictionary<long, int> Demolished = new();
        public readonly Dictionary<int, int> Worksets = new();
        public readonly List<ElementId> OwnershipSample = new();
    }

    private ElementScan Scan()
    {
        if (_scan != null) return _scan;
        var scan = new ElementScan();
        var workshared = SafeBool(() => _doc.IsWorkshared);
        foreach (var element in new FilteredElementCollector(_doc).WhereElementIsNotElementType())
        {
            if (scan.Scanned >= MaxScanElements) { scan.Truncated = true; break; }
            scan.Scanned++;
            if ((scan.Scanned & 2047) == 0) _ct.ThrowIfCancellationRequested();
            try
            {
                var typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId) scan.UsedTypeIds.Add(typeId.Value);

                Category? category = null;
                try { category = element.Category; } catch { }
                if (category == null) continue;
                var name = category.Name ?? string.Empty;
                scan.Categories[name] = scan.Categories.TryGetValue(name, out var n) ? n + 1 : 1;

                var created = PhaseReader.IdOf(element, BuiltInParameter.PHASE_CREATED);
                if (created != null) scan.Created[created.Value] = scan.Created.TryGetValue(created.Value, out var c) ? c + 1 : 1;
                var demolished = PhaseReader.IdOf(element, BuiltInParameter.PHASE_DEMOLISHED);
                if (demolished != null) scan.Demolished[demolished.Value] = scan.Demolished.TryGetValue(demolished.Value, out var d) ? d + 1 : 1;

                if (workshared)
                {
                    var ws = element.WorksetId?.IntegerValue ?? -1;
                    scan.Worksets[ws] = scan.Worksets.TryGetValue(ws, out var w) ? w + 1 : 1;
                    if (scan.OwnershipSample.Count < OwnershipSample) scan.OwnershipSample.Add(element.Id);
                }
            }
            catch
            {
                // one unreadable element never sinks the scan
            }
        }
        _scan = scan;
        return scan;
    }

    private void ScanNote(AuditSection section, ElementScan scan)
    {
        if (!scan.Truncated) return;
        section.Sampled = true;
        section.Notes.Add($"Skaneeriti esimesed {MaxScanElements} elementi (mudel on suurem); arvud katavad ainult neid.");
    }

    private List<FailureMessage> RevitWarnings()
    {
        if (_warnings != null) return _warnings;
        try { _warnings = _doc.GetWarnings().ToList(); }
        catch { _warnings = new List<FailureMessage>(); }
        return _warnings;
    }

    // ─── Sections ──────────────────────────────────────────────────────────

    private void Summary(AuditSection s)
    {
        var scan = Scan();
        s.Count("summary.elements", "Elemente (tüübid välja arvatud)", scan.Scanned);
        s.Count("summary.categories", "Kategooriaid", scan.Categories.Count);
        var workshared = SafeBool(() => _doc.IsWorkshared);
        s.Count("summary.workshared", "Töökomplektidega (1 = jah)", workshared ? 1 : 0);

        var path = SafeString(() => _doc.PathName);
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var mb = new FileInfo(path).Length / (1024.0 * 1024.0);
            s.Count("file.sizeMb", "Faili suurus", Math.Round(mb, 1), "MB");
        }
        else
        {
            s.Notes.Add("Faili suurust ei saa lugeda: mudel pole salvestatud või asub pilves.");
        }

        var info = s.Table("model", "Mudel", ("property", "Omadus"), ("value", "Väärtus"));
        info.Add(null, "Pealkiri", _doc.Title);
        info.Add(null, "Kesk-mudel", RevitDocumentResolver.GetCentralPath(_doc));
        info.Add(null, "Fail", path);
        info.Add(null, "Revit", SafeString(() => _doc.Application.VersionNumber));
        info.Add(null, "Töökomplektid", workshared);
        info.Total = info.Rows.Count;

        var top = s.Table("categories", $"Elemendid kategooriate kaupa (top {TopCategories})", ("category", "Kategooria"), ("count", "Arv"));
        foreach (var pair in scan.Categories.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(TopCategories))
            top.Add(null, pair.Key, pair.Value);
        top.Total = scan.Categories.Count;
        s.Notes.Add("Elementide arvud tulevad auditi enda ühest mudeliläbimisest (sama, mis annab tüübid, faasid ja töökomplektid).");
        ScanNote(s, scan);
    }

    private void Warnings(AuditSection s)
    {
        var keys = RevitWarningReader.Read(_doc, out var unreadable);
        var groups = WarningDiff.Group(keys);
        s.Count("warnings.total", "Hoiatusi kokku", keys.Count);
        s.Count("warnings.kinds", "Erinevaid hoiatusi", groups.Count);
        s.Count("warnings.errors", "Neist vigu", keys.Count(WarningDiff.IsErrorSeverity));
        if (unreadable > 0) s.Notes.Add($"{unreadable} hoiatust ei õnnestunud lugeda.");

        var table = s.Table("warningGroups", "Hoiatused kirjelduse kaupa",
            ("description", "Kirjeldus"), ("severity", "Raskus"), ("count", "Arv"), ("elementIds", "Elemendid (esimesed)"));
        foreach (var g in groups.OrderByDescending(g => g.Count).Take(MaxRows))
            table.Add(null, g.Description, g.Severity, g.Count, string.Join(", ", g.ElementIds.Take(10)));
        table.Total = groups.Count;
        s.Notes.Add("Üksikasjad ja muudatused kontrollpunktist alates: revit_get_warnings.");
    }

    private void UnusedTypes(AuditSection s)
    {
        var scan = Scan();
        var byFamily = new Dictionary<long, (string Family, string Category, int Total, List<string> Unused)>();
        foreach (var symbol in new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
        {
            Family? family;
            try { family = symbol.Family; } catch { family = null; }
            if (family == null) continue;
            var key = family.Id.Value;
            if (!byFamily.TryGetValue(key, out var entry))
                entry = (SafeString(() => family.Name), SafeString(() => symbol.Category?.Name), 0, new List<string>());
            entry.Total++;
            if (!scan.UsedTypeIds.Contains(symbol.Id.Value)) entry.Unused.Add(SafeString(() => symbol.Name));
            byFamily[key] = entry;
        }
        var unused = byFamily.Values.Sum(e => e.Unused.Count);
        s.Count("types.unused", "Kasutamata perekonnatüüpe", unused);
        s.Count("types.total", "Perekonnatüüpe kokku", byFamily.Values.Sum(e => e.Total));
        s.Count("types.familiesWithUnused", "Perekondi, millel on kasutamata tüüpe", byFamily.Values.Count(e => e.Unused.Count > 0));

        var table = s.Table("unusedTypes", "Kasutamata tüübid perekondade kaupa",
            ("family", "Perekond"), ("category", "Kategooria"), ("unused", "Kasutamata"), ("total", "Tüüpe"), ("types", "Tüübid (esimesed)"));
        var families = byFamily.Values.Where(e => e.Unused.Count > 0).OrderByDescending(e => e.Unused.Count).ThenBy(e => e.Family, StringComparer.CurrentCulture).ToList();
        foreach (var e in families.Take(MaxRows))
            table.Add(null, e.Family, e.Category, e.Unused.Count, e.Total, string.Join(", ", e.Unused.Take(10)));
        table.Total = families.Count;
        s.Notes.Add("Kasutamata = ühtegi eksemplari ei ole (GetTypeId). Ainult tüübiparameetrites viidatud tüübid (pesastatud, profiilid, " +
                    "paneelid) võivad siin olla kasutamata, kuigi Revit Purge neid ei eemalda — kontrolli enne kustutamist.");
        ScanNote(s, scan);
    }

    private void Views(AuditSection s)
    {
        var placed = PlacedViewIds();
        var views = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v is not ViewSheet && v is not ViewSchedule && SafeBool(() => v.CanBePrinted))
            .ToList();
        var templates = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Count(v => v.IsTemplate);
        var unplaced = views.Where(v => !placed.Contains(v.Id.Value)).OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name, StringComparer.CurrentCulture).ToList();
        var withoutTemplate = views.Count(v => v.ViewTemplateId == ElementId.InvalidElementId);

        s.Count("views.total", "Vaateid (mallid, lehed ja tabelid välja arvatud)", views.Count);
        s.Count("views.unplaced", "Lehtedel paigutamata", unplaced.Count);
        s.Count("views.unplacedPercent", "Paigutamata osakaal", views.Count == 0 ? 0 : Math.Round(100.0 * unplaced.Count / views.Count, 1), "%");
        s.Count("views.withoutTemplate", "Vaatemallita", withoutTemplate);
        s.Count("views.templates", "Vaatemalle", templates);

        var byType = s.Table("viewsByType", "Vaated tüübi kaupa", ("viewType", "Tüüp"), ("total", "Kokku"), ("unplaced", "Paigutamata"), ("withoutTemplate", "Mallita"));
        foreach (var g in views.GroupBy(v => v.ViewType.ToString()).OrderByDescending(g => g.Count()))
            byType.Add(null, g.Key, g.Count(), g.Count(v => !placed.Contains(v.Id.Value)), g.Count(v => v.ViewTemplateId == ElementId.InvalidElementId));
        byType.Total = byType.Rows.Count;

        var table = s.Table("unplacedViews", "Lehtedel paigutamata vaated", ("id", "Id"), ("name", "Nimi"), ("viewType", "Tüüp"), ("template", "Mall"));
        foreach (var v in unplaced.Take(MaxRows))
            table.Add(v.Id.Value, v.Id.Value, SafeString(() => v.Name), v.ViewType.ToString(),
                v.ViewTemplateId == ElementId.InvalidElementId ? string.Empty : SafeString(() => _doc.GetElement(v.ViewTemplateId)?.Name));
        table.Total = unplaced.Count;
        s.Notes.Add("Sama loogika mis revit_find_unplaced_views; tabelid (schedules) on lehtede jaotises eraldi.");
    }

    private HashSet<long> PlacedViewIds()
    {
        var placed = new HashSet<long>();
        foreach (var sheet in new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
        {
            try { foreach (var id in sheet.GetAllPlacedViews()) placed.Add(id.Value); } catch { }
        }
        return placed;
    }

    private void Sheets(AuditSection s)
    {
        var sheets = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(v => !v.IsTemplate).ToList();
        var schedulesOnSheets = new FilteredElementCollector(_doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
            .Where(i => !SafeBool(() => i.IsTitleblockRevisionSchedule))
            .Select(i => i.OwnerViewId.Value).ToHashSet();
        var withoutViews = sheets.Where(sh => SafeCount(() => sh.GetAllPlacedViews().Count) == 0 && !schedulesOnSheets.Contains(sh.Id.Value))
            .OrderBy(sh => SafeString(() => sh.SheetNumber), StringComparer.CurrentCulture).ToList();
        var revisions = new FilteredElementCollector(_doc).OfClass(typeof(Revision)).Cast<Revision>().ToList();
        var withRevisions = sheets.Count(sh => SafeCount(() => sh.GetAllRevisionIds().Count) > 0);
        var placeholders = sheets.Count(sh => SafeBool(() => sh.IsPlaceholder));

        s.Count("sheets.total", "Lehti", sheets.Count);
        s.Count("sheets.withoutViews", "Lehti ilma vaate ja tabelita", withoutViews.Count(sh => !SafeBool(() => sh.IsPlaceholder)));
        s.Count("sheets.placeholders", "Kohatäitelehti", placeholders);
        s.Count("sheets.withRevisions", "Revisjoniga lehti", withRevisions);
        s.Count("sheets.revisions", "Revisjone", revisions.Count);
        s.Count("sheets.revisionsNotIssued", "Väljastamata revisjone", revisions.Count(r => !SafeBool(() => r.Issued)));

        var table = s.Table("sheetsWithoutViews", "Lehed ilma vaateta", ("id", "Id"), ("number", "Number"), ("name", "Nimi"));
        foreach (var sh in withoutViews.Where(sh => !SafeBool(() => sh.IsPlaceholder)).Take(MaxRows))
            table.Add(sh.Id.Value, sh.Id.Value, SafeString(() => sh.SheetNumber), SafeString(() => sh.Name));
        table.Total = withoutViews.Count(sh => !SafeBool(() => sh.IsPlaceholder));

        var rev = s.Table("revisions", "Revisjonid", ("sequence", "Jrk"), ("date", "Kuupäev"), ("description", "Kirjeldus"), ("issued", "Väljastatud"));
        foreach (var r in revisions.OrderBy(r => SafeCount(() => r.SequenceNumber)).Take(MaxRows))
            rev.Add(null, SafeCount(() => r.SequenceNumber), SafeString(() => r.RevisionDate), SafeString(() => r.Description), SafeBool(() => r.Issued));
        rev.Total = revisions.Count;
    }

    private void Rooms(AuditSection s)
    {
        var rooms = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
            .OfType<Room>().ToList();
        if (rooms.Count == 0)
        {
            s.Status = AuditStatus.Skipped;
            s.Notes.Add("Mudelis ei ole ruume.");
            return;
        }

        var redundantIds = new HashSet<long>();
        var notEnclosedIds = new HashSet<long>();
        foreach (var warning in RevitWarnings())
        {
            try
            {
                var definition = warning.GetFailureDefinitionId();
                var target = definition == BuiltInFailures.RoomFailures.RoomsInSameRegionRooms ? redundantIds
                    : definition == BuiltInFailures.RoomFailures.RoomNotEnclosed ? notEnclosedIds
                    : null;
                if (target == null) continue;
                foreach (var id in warning.GetFailingElements()) target.Add(id.Value);
            }
            catch { }
        }

        var unplaced = rooms.Where(r => r.Location == null).ToList();
        var zeroArea = rooms.Where(r => r.Location != null && SafeDouble(() => r.Area) <= 0).ToList();
        var redundant = zeroArea.Where(r => redundantIds.Contains(r.Id.Value)).ToList();
        var notEnclosed = zeroArea.Where(r => !redundantIds.Contains(r.Id.Value)).ToList();

        s.Count("rooms.total", "Ruume", rooms.Count);
        s.Count("rooms.unplaced", "Paigutamata", unplaced.Count);
        s.Count("rooms.notEnclosed", "Suletud piirideta (pind 0)", notEnclosed.Count);
        s.Count("rooms.redundant", "Üleliigseid (mitu ruumi ühes piirkonnas)", redundant.Count);

        var table = s.Table("roomProblems", "Probleemsed ruumid", ("id", "Id"), ("number", "Number"), ("name", "Nimi"), ("level", "Korrus"), ("problem", "Probleem"));
        var problems = unplaced.Select(r => (Room: r, Problem: "paigutamata"))
            .Concat(notEnclosed.Select(r => (Room: r, Problem: notEnclosedIds.Contains(r.Id.Value) ? "piirideta" : "pind 0")))
            .Concat(redundant.Select(r => (Room: r, Problem: "üleliigne")))
            .ToList();
        foreach (var (room, problem) in problems.Take(MaxRows))
            table.Add(room.Id.Value, room.Id.Value, SafeString(() => room.Number), SafeString(() => room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()),
                SafeString(() => room.Level?.Name), problem);
        table.Total = problems.Count;
    }

    private void Imports(AuditSection s)
    {
        var instances = new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        var imported = instances.Where(i => !SafeBool(() => i.IsLinked)).ToList();
        var linked = instances.Count - imported.Count;
        var dwg = instances.Count(i => TypeName(i).EndsWith(".dwg", StringComparison.OrdinalIgnoreCase));
        var patterns = new FilteredElementCollector(_doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
            .Count(p => SafeString(() => p.Name).StartsWith("IMPORT", StringComparison.OrdinalIgnoreCase));

        s.Count("imports.instances", "CAD eksemplare kokku", instances.Count);
        s.Count("imports.cadImports", "Imporditud (mitte lingitud)", imported.Count);
        s.Count("imports.cadLinks", "Lingitud CAD", linked);
        s.Count("imports.dwg", "DWG", dwg);
        s.Count("imports.viewSpecific", "Vaatepõhiseid", instances.Count(i => SafeBool(() => i.ViewSpecific)));
        s.Count("imports.importedLinePatterns", "Imporditud joonemustreid (IMPORT-*)", patterns);

        var table = s.Table("cadInstances", "CAD eksemplarid", ("id", "Id"), ("name", "Nimi"), ("kind", "Liik"), ("view", "Vaade"), ("pinned", "Kinnitatud"));
        foreach (var i in instances.OrderBy(i => SafeBool(() => i.IsLinked)).ThenBy(TypeName, StringComparer.CurrentCulture).Take(MaxRows))
        {
            var view = SafeBool(() => i.ViewSpecific) ? SafeString(() => _doc.GetElement(i.OwnerViewId)?.Name) : string.Empty;
            table.Add(i.Id.Value, i.Id.Value, TypeName(i), SafeBool(() => i.IsLinked) ? "link" : "import", view, SafeBool(() => i.Pinned));
        }
        table.Total = instances.Count;
        s.Notes.Add("Imporditud joonestiile ei loeta: Revit ei märgi neid eraldi. CAD-kihtide nähtavus: revit_list_cad_imports.");
    }

    private string TypeName(ImportInstance instance) => SafeString(() => _doc.GetElement(instance.GetTypeId())?.Name);

    private void InPlaceFamilies(AuditSection s)
    {
        var families = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().Where(f => SafeBool(() => f.IsInPlace)).ToList();
        var familyIds = families.Select(f => f.Id.Value).ToHashSet();
        var instances = new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(fi => { try { return fi.Symbol?.Family != null && familyIds.Contains(fi.Symbol.Family.Id.Value); } catch { return false; } })
            .ToList();
        s.Count("families.inPlace", "Kohapealseid perekondi", families.Count);
        s.Count("families.inPlaceInstances", "Nende eksemplare", instances.Count);

        var table = s.Table("inPlaceInstances", "Kohapealsed perekonnad", ("id", "Id"), ("family", "Perekond"), ("category", "Kategooria"), ("level", "Korrus"));
        foreach (var fi in instances.OrderBy(fi => SafeString(() => fi.Category?.Name), StringComparer.CurrentCulture).Take(MaxRows))
            table.Add(fi.Id.Value, fi.Id.Value, SafeString(() => fi.Symbol?.Family?.Name), SafeString(() => fi.Category?.Name),
                SafeString(() => _doc.GetElement(fi.LevelId)?.Name));
        table.Total = instances.Count;
    }

    private void Groups(AuditSection s)
    {
        var types = new FilteredElementCollector(_doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
        var groups = new FilteredElementCollector(_doc).OfClass(typeof(Group)).Cast<Group>().ToList();
        string KindOf(Element e)
        {
            var id = SafeLong(() => e.Category?.Id.Value ?? 0);
            if (id == (long)BuiltInCategory.OST_IOSModelGroups) return "model";
            if (id == (long)BuiltInCategory.OST_IOSAttachedDetailGroups) return "attached";
            if (id == (long)BuiltInCategory.OST_IOSDetailGroups) return "detail";
            return "other";
        }
        var countByType = groups.GroupBy(g => SafeLong(() => g.GetTypeId().Value)).ToDictionary(g => g.Key, g => g.Count());
        var unusedTypes = types.Where(t => !countByType.ContainsKey(t.Id.Value)).ToList();

        s.Count("groups.modelTypes", "Mudeligruppide tüüpe", types.Count(t => KindOf(t) == "model"));
        s.Count("groups.detailTypes", "Detailigruppide tüüpe", types.Count(t => KindOf(t) == "detail"));
        s.Count("groups.attachedDetailTypes", "Kinnitatud detailigruppide tüüpe (mudel + detail)", types.Count(t => KindOf(t) == "attached"));
        s.Count("groups.modelInstances", "Mudeligruppe", groups.Count(g => KindOf(g) == "model"));
        s.Count("groups.detailInstances", "Detailigruppe", groups.Count(g => KindOf(g) is "detail" or "attached"));
        s.Count("groups.unusedTypes", "Kasutamata grupitüüpe", unusedTypes.Count);

        var table = s.Table("groupTypes", "Grupitüübid", ("type", "Tüüp"), ("kind", "Liik"), ("instances", "Eksemplare"), ("members", "Liikmeid (esimeses)"));
        var rows = types.Select(t =>
            {
                var first = groups.FirstOrDefault(g => SafeLong(() => g.GetTypeId().Value) == t.Id.Value);
                return (Type: t, Count: countByType.TryGetValue(t.Id.Value, out var n) ? n : 0, Members: first == null ? 0 : SafeCount(() => first.GetMemberIds().Count));
            })
            .OrderByDescending(r => r.Count).ThenBy(r => SafeString(() => r.Type.Name), StringComparer.CurrentCulture).ToList();
        foreach (var r in rows.Take(MaxRows))
            table.Add(null, SafeString(() => r.Type.Name), KindOf(r.Type), r.Count, r.Members);
        table.Total = rows.Count;
    }

    private void Phases(AuditSection s)
    {
        var scan = Scan();
        var listing = PhaseReader.Read(_doc, countElements: false, scanCap: 0, _ct);
        s.Count("phases.count", "Faase", listing.Phases.Count);
        s.Count("phases.filters", "Faasifiltreid", listing.PhaseFilters.Count);
        s.Count("phases.demolishedElements", "Lammutatud elemente", scan.Demolished.Values.Sum());

        var table = s.Table("phases", "Faasid", ("sequence", "Jrk"), ("name", "Faas"), ("views", "Vaateid"), ("created", "Loodud"), ("demolished", "Lammutatud"));
        foreach (var p in listing.Phases)
            table.Add(null, p.SequenceNumber, p.Name, p.ViewCount,
                scan.Created.TryGetValue(p.Id, out var c) ? c : 0, scan.Demolished.TryGetValue(p.Id, out var d) ? d : 0);
        table.Total = listing.Phases.Count;

        var filters = s.Table("phaseFilters", "Faasifiltrid", ("name", "Filter"), ("new", "Uus"), ("existing", "Olemasolev"), ("demolished", "Lammutatud"), ("temporary", "Ajutine"), ("views", "Vaateid"));
        foreach (var f in listing.PhaseFilters)
            filters.Add(null, f.Name, f.NewStatus, f.ExistingStatus, f.DemolishedStatus, f.TemporaryStatus, f.ViewCount);
        filters.Total = listing.PhaseFilters.Count;
        ScanNote(s, scan);
    }

    private void DesignOptions(AuditSection s)
    {
        var sets = DesignOptionReader.Read(_doc, countElements: true, _ct);
        if (sets.Count == 0)
        {
            s.Count("designOptions.sets", "Variandikomplekte", 0);
            s.Notes.Add("Mudelis ei ole kavandivariante.");
            return;
        }
        s.Count("designOptions.sets", "Variandikomplekte", sets.Count);
        s.Count("designOptions.options", "Variante", sets.Sum(x => x.Options.Count));
        s.Count("designOptions.elements", "Elemente variantides", sets.Sum(x => x.Options.Sum(o => o.ElementCount ?? 0)));

        var table = s.Table("designOptions", "Kavandivariandid", ("set", "Komplekt"), ("option", "Variant"), ("primary", "Põhivariant"), ("elements", "Elemente"));
        foreach (var set in sets)
            foreach (var o in set.Options)
                table.Add(null, set.SetName, o.Name, o.IsPrimary, o.ElementCount);
        table.Total = table.Rows.Count;
        s.Notes.Add(DesignOptionReader.SetOnlyNote);
    }

    private void Worksets(AuditSection s)
    {
        if (!SafeBool(() => _doc.IsWorkshared))
        {
            s.Status = AuditStatus.Skipped;
            s.Notes.Add("Mudel ei kasuta töökomplekte.");
            return;
        }
        var scan = Scan();
        var me = SafeString(() => _doc.Application.Username);
        var worksets = new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset).ToList();
        var ownedByOthers = worksets.Where(w => !string.IsNullOrEmpty(w.Owner) && !string.Equals(w.Owner, me, StringComparison.OrdinalIgnoreCase)).ToList();

        var othersElements = 0;
        var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in scan.OwnershipSample)
        {
            var owner = ElementModelStateReader.OwnerOf(_doc, id, true);
            if (owner.Length == 0 || string.Equals(owner, me, StringComparison.OrdinalIgnoreCase)) continue;
            othersElements++;
            owners[owner] = owners.TryGetValue(owner, out var n) ? n + 1 : 1;
        }

        s.Count("worksets.user", "Kasutaja töökomplekte", worksets.Count);
        s.Count("worksets.ownedByOthers", "Teiste omanduses töökomplekte", ownedByOthers.Count);
        s.Count("worksets.elementsOwnedByOthers", "Teiste omanduses elemente (valim)", othersElements);
        s.Count("worksets.closed", "Suletud töökomplekte", worksets.Count(w => !w.IsOpen));
        s.Sampled = true;
        s.Notes.Add($"Elementide omandus on valimist: esimesed {scan.OwnershipSample.Count} elementi (kuni {OwnershipSample}). " +
                    "Täpse oleku annab revit_get_ownership.");

        var table = s.Table("worksets", "Töökomplektid", ("name", "Nimi"), ("elements", "Elemente"), ("owner", "Omanik"), ("open", "Avatud"), ("editable", "Muudetav"));
        foreach (var w in worksets.OrderBy(w => w.Name, StringComparer.CurrentCulture))
            table.Add(null, w.Name, scan.Worksets.TryGetValue(w.Id.IntegerValue, out var n) ? n : 0, w.Owner, w.IsOpen, SafeBool(() => w.IsEditable));
        table.Total = worksets.Count;

        if (owners.Count > 0)
        {
            var who = s.Table("owners", "Teiste omanduses elemendid (valim)", ("user", "Kasutaja"), ("elements", "Elemente"));
            foreach (var pair in owners.OrderByDescending(p => p.Value)) who.Add(null, pair.Key, pair.Value);
            who.Total = owners.Count;
        }
        ScanNote(s, scan);
    }

    private void Links(AuditSection s)
    {
        var links = LinkInventory.Collect(_doc);
        if (links.Count == 0)
        {
            s.Count("links.total", "Linke", 0);
            s.Notes.Add("Mudelis ei ole linke.");
            return;
        }
        string PathType(LinkEntry entry)
        {
            try { return entry.Type.GetExternalFileReference()?.PathType.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }
        bool Loaded(LinkEntry entry) => string.Equals(entry.Status, "Loaded", StringComparison.OrdinalIgnoreCase);

        var instances = links.SelectMany(l => l.Instances).ToList();
        s.Count("links.total", "Linke", links.Count);
        s.Count("links.rvt", "RVT", links.Count(l => l.Kind == LinkKind.Rvt));
        s.Count("links.ifc", "IFC", links.Count(l => l.Kind == LinkKind.Ifc));
        s.Count("links.dwg", "DWG/CAD", links.Count(l => l.Kind is LinkKind.Dwg or LinkKind.OtherCad));
        s.Count("links.notLoaded", "Laadimata", links.Count(l => !Loaded(l)));
        s.Count("links.absolutePath", "Absoluutse teega", links.Count(l => string.Equals(PathType(l), "Absolute", StringComparison.OrdinalIgnoreCase)));
        s.Count("links.unpinned", "Kinnitamata eksemplare", instances.Count(i => !SafeBool(() => i.Pinned)));

        var table = s.Table("links", "Lingid", ("id", "Eksemplar"), ("name", "Nimi"), ("kind", "Liik"), ("status", "Olek"), ("pathType", "Tee"), ("pinned", "Kinnitatud"), ("path", "Fail"));
        foreach (var link in links.Take(MaxRows))
        {
            var first = link.Instances.FirstOrDefault();
            table.Add(first?.Id.Value, first?.Id.Value, link.Name, link.Kind.ToString().ToUpperInvariant(), link.Status, PathType(link),
                link.Instances.Count == 0 ? "-" : link.Instances.All(i => SafeBool(() => i.Pinned)) ? "jah" : "ei", link.Path);
        }
        table.Total = links.Count;
        s.Notes.Add("Read valivad lingi esimese eksemplari. Laadimine ja eemaldamine: revit_reload_links_from, revit_remove_links.");
    }

    private void GraphAndAgent(AuditSection s)
    {
        try
        {
            var (location, signal, _) = RevitMCP.Addin.Tools.Graph.GraphToolSupport.Locate(_uiapp, _doc, new Dictionary<string, object?>());
            if (!File.Exists(location.DatabasePath))
            {
                s.Count("graph.stale", "Graaf puudub või on aegunud (1 = jah)", 1);
                s.Notes.Add("Mudeligraafi pole ehitatud: revit_graph_build.");
            }
            else
            {
                var handle = new GraphStore().OpenForRead(location.DatabasePath);
                GraphMeta meta;
                using (var db = GraphDatabase.OpenReadOnly(handle.ReadPath)) meta = db.ReadMeta();
                var freshness = GraphFreshness.Evaluate(meta, signal);
                s.Count("graph.stale", "Graaf puudub või on aegunud (1 = jah)", freshness.Stale ? 1 : 0);
                s.Count("graph.nodes", "Graafi sõlmi", meta.NodeCount ?? 0);
                if (freshness.Stale) s.Notes.Add("Graaf on aegunud: " + freshness.Reason);
                var table = s.Table("graph", "Mudeligraaf", ("property", "Omadus"), ("value", "Väärtus"));
                table.Add(null, "Ehitatud", meta.Get(GraphSchema.MetaKeys.BuiltAt));
                table.Add(null, "Skeemi versioon", meta.SchemaVersion);
                table.Add(null, "Fail", location.DatabasePath);
                table.Total = table.Rows.Count;
            }
        }
        catch (Exception ex)
        {
            s.Notes.Add($"Graafi olekut ei saanud lugeda: {ex.Message}");
        }

        var modelId = CheckpointChangeTracker.ModelIdOf(_doc);
        var checkpoints = CheckpointChangeTracker.Store?.ForModel(modelId) ?? new List<Checkpoint>();
        s.Count("agent.checkpoints", "Kontrollpunkte", checkpoints.Count);
        var plans = PlanService.Current?.Registry.All().Where(p => p.State == PlanState.Active && p.ModelId == modelId).ToList() ?? new List<Plan>();
        s.Count("agent.activePlans", "Aktiivseid plaane", plans.Count);
        if (plans.Count > 0)
        {
            var table = s.Table("plans", "Aktiivsed plaanid", ("id", "Plaan"), ("title", "Pealkiri"), ("calls", "Kutseid"));
            foreach (var p in plans) table.Add(null, p.Id, p.Title, p.Calls);
            table.Total = plans.Count;
        }
    }

    // ─── Thresholds from config ────────────────────────────────────────────

    /// <summary>The company defaults with <c>audit.thresholds</c> from the company, user and project config applied in that order.</summary>
    public static AuditRules LoadRules(Document doc, List<string> sources, List<string> warnings)
    {
        var rules = AuditRules.Default();
        sources.Add("ettevõtte vaikeväärtused");
        foreach (var (scope, path) in ConfigCandidates(doc))
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try
            {
                var (config, error) = new JsonConfigService().Read(path!, createIfMissing: false);
                if (config == null)
                {
                    if (error != null) warnings.Add($"{scope} config {path}: {error}");
                    continue;
                }
                var node = config[AuditRules.ConfigSection]?[AuditRules.ConfigKey];
                if (node == null) continue;
                rules = rules.WithOverrides(node.ToJsonString(), $"{scope} config", warnings);
                sources.Add($"{scope} config ({path})");
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read {AuditRules.ConfigSection}.{AuditRules.ConfigKey} from {path}: {ex.Message}");
            }
        }
        return rules;
    }

    private static IEnumerable<(string Scope, string? Path)> ConfigCandidates(Document doc)
    {
        yield return (ConfigPathResolver.ScopeCompany, ConfigPathResolver.Resolve(ConfigPathResolver.ScopeCompany).Path);
        yield return (ConfigPathResolver.ScopeUser, ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser).Path);
        var root = ProjectConfigLocator.FindProjectRoot(doc, string.Empty);
        if (root != null)
            yield return (ConfigPathResolver.ScopeProject, ConfigPathResolver.Resolve(ConfigPathResolver.ScopeProject, root).Path);
    }

    // ─── Safe readers ──────────────────────────────────────────────────────

    private static string SafeString(Func<string?> read)
    {
        try { return read() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try { return read(); }
        catch { return false; }
    }

    private static int SafeCount(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }

    private static long SafeLong(Func<long> read)
    {
        try { return read(); }
        catch { return 0; }
    }

    private static double SafeDouble(Func<double> read)
    {
        try { return read(); }
        catch { return 0; }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
