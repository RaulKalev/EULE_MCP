using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Addin.RoomDevices;

namespace RevitMCP.Addin.Families;

/// <summary>A loaded family or a system family, as revit_list_families reports it.</summary>
internal sealed class FamilyRow
{
    public long? FamilyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    /// <summary>loadable | system | inPlace.</summary>
    public string Kind { get; set; } = "loadable";
    public int InstanceCount { get; set; }
    public bool IsEditable { get; set; }
    public bool? IsUserCreated { get; set; }
    public List<(long Id, string Name, int Instances)> Types { get; } = new();
}

/// <summary>Everything a purge pass decides from.</summary>
internal sealed class PurgeSnapshot
{
    public List<PurgeTypeInfo> Types { get; } = new();
    public List<PurgeFamilyInfo> Families { get; } = new();
    public List<PurgeOtherInfo> Others { get; } = new();
    /// <summary>False when Document.GetUnusedElements failed at run time; the instance-count method is then the only one.</summary>
    public bool RevitPurgeAvailable { get; set; }
    public string? RevitPurgeProblem { get; set; }
}

/// <summary>
/// Revit-side collectors of the family tools (#102): the family list with type and instance counts (one pass over the
/// model, through <see cref="FamilyTypeSupport.CountInstancesByType"/>), loaded families by name, and the purge snapshot
/// with its protections.
/// </summary>
internal static class FamilyInventory
{
    public static List<Family> Families(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();

    /// <summary>Loaded families by name (case-insensitive). Revit keeps family names unique per document.</summary>
    public static Dictionary<string, Family> LoadedByName(Document doc)
    {
        var map = new Dictionary<string, Family>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Families(doc))
        {
            var name = FamilyTypeSupport.SafeName(family);
            if (name.Length > 0 && !map.ContainsKey(name)) map[name] = family;
        }
        return map;
    }

    public static string CategoryOf(Family family)
    {
        try { return family.FamilyCategory?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static List<FamilySymbol> Symbols(Document doc, Family family)
    {
        var symbols = new List<FamilySymbol>();
        try
        {
            foreach (var id in family.GetFamilySymbolIds())
                if (doc.GetElement(id) is FamilySymbol symbol) symbols.Add(symbol);
        }
        catch { /* an in-place or damaged family can refuse */ }
        return symbols.OrderBy(FamilyTypeSupport.SafeName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<FamilyRow> Rows(Document doc, Dictionary<long, int> countsByType, bool includeSystem)
    {
        var rows = new List<FamilyRow>();
        foreach (var family in Families(doc))
        {
            var row = new FamilyRow
            {
                FamilyId = family.Id.Value,
                Name = FamilyTypeSupport.SafeName(family),
                Category = CategoryOf(family),
                Kind = SafeBool(() => family.IsInPlace) ? "inPlace" : "loadable",
                IsEditable = SafeBool(() => family.IsEditable),
                IsUserCreated = SafeNullable(() => family.IsUserCreated)
            };
            foreach (var symbol in Symbols(doc, family))
            {
                var count = countsByType.TryGetValue(symbol.Id.Value, out var c) ? c : 0;
                row.Types.Add((symbol.Id.Value, FamilyTypeSupport.SafeName(symbol), count));
                row.InstanceCount += count;
            }
            rows.Add(row);
        }

        if (includeSystem)
        {
            var systemTypes = FamilyTypeSupport.CollectTypes(doc).Where(t => t is not FamilySymbol)
                .GroupBy(t => (Category: t.Category?.Name ?? string.Empty, Family: FamilyTypeSupport.SafeFamilyName(t)));
            foreach (var group in systemTypes)
            {
                var row = new FamilyRow
                {
                    Name = group.Key.Family.Length > 0 ? group.Key.Family : group.Key.Category,
                    Category = group.Key.Category,
                    Kind = "system"
                };
                foreach (var type in group.OrderBy(FamilyTypeSupport.SafeName, StringComparer.OrdinalIgnoreCase))
                {
                    var count = countsByType.TryGetValue(type.Id.Value, out var c) ? c : 0;
                    row.Types.Add((type.Id.Value, FamilyTypeSupport.SafeName(type), count));
                    row.InstanceCount += count;
                }
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>
    /// The purge snapshot: every family and loadable type with its instance count, Revit's own unused list
    /// (Document.GetUnusedElements, available in the Revit 2024 and 2026 APIs), and the protections — company device
    /// map types, title block types on sheets, view templates, in-place families.
    /// </summary>
    public static PurgeSnapshot Snapshot(Document doc, bool includeOther)
    {
        var snapshot = new PurgeSnapshot();
        var counts = FamilyTypeSupport.CountInstancesByType(doc);

        HashSet<long>? unused = null;
        try
        {
            unused = new HashSet<long>(doc.GetUnusedElements(new HashSet<ElementId>()).Select(id => id.Value));
            snapshot.RevitPurgeAvailable = true;
        }
        catch (Exception ex)
        {
            snapshot.RevitPurgeProblem = $"Document.GetUnusedElements failed ({ex.Message}); only types without instances are " +
                                         "considered, and other unused elements cannot be listed.";
        }

        var deviceMap = DeviceMapTypes(doc);
        var titleBlocksOnSheets = new HashSet<long>();
        try
        {
            foreach (var element in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                var typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId) titleBlocksOnSheets.Add(typeId.Value);
            }
        }
        catch { /* the instance counts protect them as well */ }

        foreach (var family in Families(doc))
        {
            var familyName = FamilyTypeSupport.SafeName(family);
            var inPlace = SafeBool(() => family.IsInPlace);
            var category = CategoryOf(family);
            snapshot.Families.Add(new PurgeFamilyInfo
            {
                FamilyId = family.Id.Value,
                Name = familyName,
                Category = category,
                IsInPlace = inPlace,
                UnusedPerRevit = unused == null ? null : unused.Contains(family.Id.Value)
            });
            foreach (var symbol in Symbols(doc, family))
            {
                var typeName = FamilyTypeSupport.SafeName(symbol);
                snapshot.Types.Add(new PurgeTypeInfo
                {
                    TypeId = symbol.Id.Value,
                    TypeName = typeName,
                    FamilyId = family.Id.Value,
                    FamilyName = familyName,
                    Category = symbol.Category?.Name ?? category,
                    InstanceCount = counts.TryGetValue(symbol.Id.Value, out var c) ? c : 0,
                    IsInPlace = inPlace,
                    UnusedPerRevit = unused == null ? null : unused.Contains(symbol.Id.Value),
                    Protection = deviceMap.Contains(Key(familyName, typeName)) ? "deviceMap"
                        : titleBlocksOnSheets.Contains(symbol.Id.Value) ? "titleBlockOnSheet"
                        : null
                });
            }
        }

        if (includeOther && unused != null)
        {
            foreach (var value in unused)
            {
                var element = doc.GetElement(new ElementId(value));
                if (element == null || element is Family || element is FamilySymbol) continue;
                snapshot.Others.Add(new PurgeOtherInfo
                {
                    Id = value,
                    Name = FamilyTypeSupport.SafeName(element),
                    Category = SafeCategory(element),
                    Kind = element.GetType().Name,
                    Protection = OtherProtection(element, deviceMap)
                });
            }
        }
        return snapshot;
    }

    private static string? OtherProtection(Element element, HashSet<string> deviceMap)
    {
        if (element is View view && SafeBool(() => view.IsTemplate)) return "viewTemplate";
        if (element is ElementType type && deviceMap.Contains(Key(FamilyTypeSupport.SafeFamilyName(type), FamilyTypeSupport.SafeName(type))))
            return "deviceMap";
        return null;
    }

    /// <summary>"family|type" keys of the company device map (deviceCodes in the project config), including their source types.</summary>
    public static HashSet<string> DeviceMapTypes(Document doc)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (config, _, _) = ProjectConfigLocator.Read(doc, string.Empty);
            if (config?[DeviceCodeMap.SectionName] is not JsonObject section) return keys;
            foreach (var code in DeviceCodeMap.Parse(section, new List<string>()).Values)
            {
                keys.Add(Key(code.Family, code.Type));
                if (!string.IsNullOrWhiteSpace(code.SourceType))
                {
                    var (family, type) = code.SourceFamilyAndType();
                    keys.Add(Key(family, type));
                }
            }
        }
        catch { /* no config: nothing to protect */ }
        return keys;
    }

    private static string Key(string family, string type) => family.Trim() + "|" + type.Trim();

    private static string SafeCategory(Element element)
    {
        try { return element.Category?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try { return read(); }
        catch { return false; }
    }

    private static bool? SafeNullable(Func<bool> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
