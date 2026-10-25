using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Parameters;

/// <summary>One entry of doc.ParameterBindings.</summary>
internal sealed class BindingRow
{
    public Definition Definition { get; set; } = null!;
    public ElementBinding Binding { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public long? ParameterElementId { get; set; }
    public bool IsShared { get; set; }
    public string? Guid { get; set; }
    public ForgeTypeId? DataType { get; set; }
    public ForgeTypeId? Group { get; set; }
    public bool IsInstance { get; set; }
    public List<string> Categories { get; } = new();
    public bool? VariesAcrossGroups { get; set; }

    public ExistingBinding ToExisting() => new()
    {
        Name = Name,
        Guid = Guid,
        IsShared = IsShared,
        IsInstance = IsInstance,
        DataTypeId = DataType?.TypeId,
        Categories = Categories.ToList()
    };

    public object ToPayload() => new
    {
        name = Name,
        parameterElementId = ParameterElementId,
        isShared = IsShared,
        guid = Guid,
        dataType = DataType?.TypeId,
        dataTypeLabel = ParameterRevitTypes.SpecLabel(DataType),
        dataTypeName = ParameterRevitTypes.FriendlyOf(DataType),
        group = Group?.TypeId ?? string.Empty,
        groupLabel = ParameterRevitTypes.GroupLabel(Group),
        binding = IsInstance ? "instance" : "type",
        categories = Categories,
        variesAcrossGroups = VariesAcrossGroups
    };
}

/// <summary>Reads the project's parameter bindings (#102): project and shared parameters with their categories.</summary>
internal static class ParameterBindingReader
{
    public static List<BindingRow> Read(Document doc)
    {
        var rows = new List<BindingRow>();
        var iterator = doc.ParameterBindings.ForwardIterator();
        iterator.Reset();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not Definition definition || iterator.Current is not ElementBinding binding) continue;
            var row = new BindingRow
            {
                Definition = definition,
                Binding = binding,
                Name = definition.Name,
                IsInstance = binding is InstanceBinding
            };
            try { row.DataType = definition.GetDataType(); } catch { /* left empty */ }
            try { row.Group = definition.GetGroupTypeId(); } catch { /* left empty */ }

            if (definition is InternalDefinition internalDefinition)
            {
                try { row.VariesAcrossGroups = internalDefinition.VariesAcrossGroups; } catch { /* not readable */ }
                try
                {
                    row.ParameterElementId = internalDefinition.Id.Value;
                    if (doc.GetElement(internalDefinition.Id) is SharedParameterElement shared)
                    {
                        row.IsShared = true;
                        row.Guid = shared.GuidValue.ToString();
                    }
                }
                catch { /* a built-in definition has no element */ }
            }

            try
            {
                foreach (Category category in binding.Categories)
                    row.Categories.Add(category.Name);
            }
            catch { /* an empty category set */ }
            row.Categories.Sort(StringComparer.OrdinalIgnoreCase);
            rows.Add(row);
        }
        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The binding of a parameter by name or GUID.</summary>
    public static BindingRow? Find(IEnumerable<BindingRow> rows, string name, string guid)
    {
        var list = rows.ToList();
        if (!string.IsNullOrWhiteSpace(guid))
            return list.FirstOrDefault(r => r.Guid != null && string.Equals(r.Guid, guid.Trim(), StringComparison.OrdinalIgnoreCase));
        return list.FirstOrDefault(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Categories by name → Revit category, only those that accept bound parameters.</summary>
    public static (Category? Category, string? Error) ResolveCategory(Document doc, string name)
    {
        var resolved = new RevitMCP.Addin.Query.CategoryResolver().Resolve(doc, name);
        if (resolved.Category == null)
            return (null, resolved.Suggestions.Count > 0 ? $"did you mean {string.Join(", ", resolved.Suggestions.Take(3))}?" : null);
        bool allows;
        try { allows = resolved.Category.AllowsBoundParameters; }
        catch { allows = false; }
        return allows ? (resolved.Category, null) : (null, "does not allow bound parameters");
    }

    public static CategorySet CategorySet(Document doc, IEnumerable<string> names)
    {
        var set = doc.Application.Create.NewCategorySet();
        foreach (var name in names)
        {
            var (category, _) = ResolveCategory(doc, name);
            if (category != null) set.Insert(category);
        }
        return set;
    }

    public static ElementBinding NewBinding(Document doc, bool instance, CategorySet categories) =>
        instance
            ? doc.Application.Create.NewInstanceBinding(categories)
            : doc.Application.Create.NewTypeBinding(categories);
}
