using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Revit-side support for revit_create_cable_type. Uses Autodesk.Revit.DB.Electrical.CableType via
/// reflection (it only exists from Revit 2026) and falls back to WireType where the class is absent.
/// </summary>
internal static class CableTypeDuplicator
{
    public sealed class Request
    {
        public string SourceTypeName { get; set; } = string.Empty;
        public long SourceTypeId { get; set; }
        public string IfExists { get; set; } = "skip";
        public List<CableTypeCreationItem> Items { get; set; } = [];
    }

    public sealed class Context
    {
        public Type TypeClass { get; set; } = typeof(WireType);
        /// <summary>"CableType" or "WireType".</summary>
        public string Kind { get; set; } = "WireType";
        public Element Source { get; set; } = null!;
        public List<Element> Existing { get; set; } = [];
        public List<PlannedCableType> Plan { get; set; } = [];
    }

    public static Request ParseRequest(Dictionary<string, object?> arguments)
    {
        var request = new Request
        {
            SourceTypeName = ToolArguments.GetString(arguments, "sourceTypeName").Trim(),
            SourceTypeId = ToolArguments.GetLong(arguments, "sourceTypeId"),
            IfExists = ToolArguments.GetString(arguments, "ifExists", "skip")
        };

        // Batch form: items=[{newName, parameters}]
        if (arguments.TryGetValue("items", out var rawItems) && rawItems != null)
        {
            Newtonsoft.Json.Linq.JArray? array;
            try
            {
                array = rawItems switch
                {
                    Newtonsoft.Json.Linq.JArray a => a,
                    string s => ToolArguments.TryParseJArray(s),
                    _ => Newtonsoft.Json.Linq.JArray.FromObject(rawItems)
                };
            }
            catch { array = null; }
            foreach (var token in array ?? [])
            {
                if (token is not Newtonsoft.Json.Linq.JObject obj) continue;
                var itemArgs = obj.Properties().ToDictionary(
                    p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);
                request.Items.Add(new CableTypeCreationItem
                {
                    NewName = ToolArguments.GetString(itemArgs, "newName").Trim(),
                    Parameters = ViewManagerToolSupport.GetStringDictionary(itemArgs, "parameters")
                });
            }
        }

        // Single form: newName + parameters. Shared parameters also apply to newNames.
        var shared = ViewManagerToolSupport.GetStringDictionary(arguments, "parameters");
        var newName = ToolArguments.GetString(arguments, "newName").Trim();
        if (newName.Length > 0)
            request.Items.Add(new CableTypeCreationItem { NewName = newName, Parameters = shared });
        foreach (var name in ToolArguments.GetStringArray(arguments, "newNames"))
        {
            request.Items.Add(new CableTypeCreationItem
            {
                NewName = name.Trim(),
                Parameters = new Dictionary<string, string>(shared, StringComparer.OrdinalIgnoreCase)
            });
        }

        return request;
    }

    /// <summary>Resolves the type class, the source type, and the plan. Null + error when the request is unusable.</summary>
    public static Context? Build(Document doc, Request request, List<string> warnings, out string? error)
    {
        error = null;

        var ifExists = CableTypeCreationPlanner.NormalizeIfExists(request.IfExists);
        if (ifExists == null)
        {
            error = $"ifExists must be 'skip' or 'error' (got '{request.IfExists}').";
            return null;
        }

        if (request.Items.Count == 0)
        {
            error = "Provide newName, newNames, or items=[{newName, parameters}].";
            return null;
        }
        if (request.Items.Count > CableTypeCreationPlanner.MaxItems)
        {
            error = $"At most {CableTypeCreationPlanner.MaxItems} cable types per call.";
            return null;
        }

        var context = new Context();
        var cableTypeClass = typeof(Element).Assembly.GetType("Autodesk.Revit.DB.Electrical.CableType");
        if (cableTypeClass != null)
        {
            context.TypeClass = cableTypeClass;
            context.Kind = "CableType";
        }
        else
        {
            warnings.Add("This Revit version has no CableType class; WireType is duplicated instead.");
        }

        try
        {
            context.Existing = new FilteredElementCollector(doc)
                .OfClass(context.TypeClass)
                .ToList();
        }
        catch (Exception ex)
        {
            error = $"Could not collect {context.Kind} elements: {ex.Message}";
            return null;
        }

        if (context.Existing.Count == 0)
        {
            error = $"The model has no {context.Kind} to duplicate from. Create one cable type in Revit first.";
            return null;
        }

        var source = ResolveSource(context, request, out error);
        if (source == null)
            return null;
        context.Source = source;

        context.Plan = CableTypeCreationPlanner.Plan(
            request.Items,
            context.Existing.Select(FamilyTypeSupport.SafeName),
            ifExists);
        return context;
    }

    public static Element? FindExisting(Context context, string name) =>
        context.Existing.FirstOrDefault(t =>
            string.Equals(FamilyTypeSupport.SafeName(t), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Duplicates the source and names the copy. CableType.Duplicate() takes no name, so the copy
    /// is renamed afterwards; WireType goes through ElementType.Duplicate(name). Needs an open transaction.
    /// </summary>
    public static Element Duplicate(Context context, string newName)
    {
        if (context.Kind == "CableType")
        {
            var method = context.TypeClass.GetMethod(
                "Duplicate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method != null)
            {
                object? copy;
                try { copy = method.Invoke(context.Source, null); }
                catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }

                if (copy is not Element copied)
                    throw new InvalidOperationException("Revit did not return the duplicated cable type.");
                copied.Name = newName;
                return copied;
            }
        }

        if (context.Source is not ElementType sourceType)
            throw new InvalidOperationException($"{context.Kind} cannot be duplicated in this Revit version.");
        return sourceType.Duplicate(newName)
               ?? throw new InvalidOperationException("Revit did not return the duplicated type.");
    }

    private static Element? ResolveSource(Context context, Request request, out string? error)
    {
        error = null;
        var available = string.Join(", ", context.Existing.Take(20).Select(FamilyTypeSupport.SafeName));

        if (request.SourceTypeId != 0)
        {
            var byId = context.Existing.FirstOrDefault(t => t.Id.Value == request.SourceTypeId);
            if (byId == null)
                error = $"sourceTypeId {request.SourceTypeId} is not a {context.Kind}. Available: {available}";
            return byId;
        }

        if (request.SourceTypeName.Length == 0)
        {
            if (context.Existing.Count == 1)
                return context.Existing[0];
            error = $"Provide sourceTypeName or sourceTypeId. Available {context.Kind}s: {available}";
            return null;
        }

        var exact = context.Existing.FirstOrDefault(t =>
            string.Equals(FamilyTypeSupport.SafeName(t), request.SourceTypeName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        var partial = context.Existing
            .Where(t => FamilyTypeSupport.SafeName(t).IndexOf(request.SourceTypeName, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        if (partial.Count == 1)
            return partial[0];

        error = partial.Count > 1
            ? $"Several {context.Kind}s match '{request.SourceTypeName}': " +
              $"{string.Join(", ", partial.Select(FamilyTypeSupport.SafeName))}. Use the exact name or sourceTypeId."
            : $"No {context.Kind} matches '{request.SourceTypeName}'. Available: {available}";
        return null;
    }
}
