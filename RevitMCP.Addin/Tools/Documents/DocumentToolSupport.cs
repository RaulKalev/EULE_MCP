using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>Shared argument parsing and guards for the multi-document / link tools (#90).</summary>
internal static class DocumentToolSupport
{
    public static McpToolResult Fail(McpToolRequest request, string message, string? status = null, object? data = null)
    {
        var result = new McpToolResult { RequestId = request.RequestId, Success = false, Message = message, Data = data };
        if (status != null) result.Status = status;
        return result;
    }

    /// <summary>Resolves the <c>document</c> argument; null with <paramref name="failure"/> set when it does not resolve.</summary>
    public static RevitDocumentResolver.DocumentTarget? ResolveTarget(
        UIApplication uiapp, McpToolRequest request, out McpToolResult? failure)
    {
        var target = RevitDocumentResolver.Resolve(uiapp, request, out var error);
        failure = target == null ? Fail(request, error ?? "Document was not found.", "validation_failed") : null;
        return target;
    }

    /// <summary>Link loads, saves and syncs cannot run while a transaction is open on the document.</summary>
    public static string? CheckNoOpenTransaction(Document doc)
    {
        try
        {
            if (doc.IsModifiable)
                return $"'{doc.Title}' has an open transaction (an edit is in progress). Finish or cancel it in Revit and retry.";
            if (doc.IsReadOnly)
                return $"'{doc.Title}' is read-only.";
        }
        catch (Exception ex)
        {
            return $"Could not read the state of '{doc.Title}': {ex.Message}";
        }
        return null;
    }

    public static object DescribeDocument(UIApplication uiapp, Document doc) => new
    {
        title = doc.Title,
        path = doc.PathName ?? string.Empty,
        isActive = RevitDocumentResolver.IsActive(uiapp, doc)
    };

    /// <summary>Parses an argument holding an array of objects (JArray, JSON string or CLR array).</summary>
    public static JArray? GetObjectArray(Dictionary<string, object?> args, string key, List<string> errors)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null)
            return null;
        try
        {
            return raw switch
            {
                JArray array => array,
                string json when string.IsNullOrWhiteSpace(json) => null,
                string json => JToken.Parse(json) as JArray,
                _ => JArray.FromObject(raw)
            };
        }
        catch (Exception ex)
        {
            errors.Add($"{key} could not be parsed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves link selectors (name or type/instance id) to exactly one link each. A selector matching
    /// several links is reported instead of acting on all of them.
    /// </summary>
    public static List<LinkEntry> ResolveLinks(IReadOnlyList<LinkEntry> links, IEnumerable<string> selectors, List<string> errors)
    {
        var result = new List<LinkEntry>();
        foreach (var selector in selectors.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var matches = LinkInventory.Find(links, selector, out var error);
            if (error != null)
            {
                errors.Add(error);
                continue;
            }
            if (matches.Count > 1)
            {
                errors.Add($"'{selector}' matches {matches.Count} links: {string.Join(", ", matches.Select(m => $"'{m.Name}' (type {m.TypeId})"))}. Pass the exact name or type id.");
                continue;
            }
            if (result.All(r => r.TypeId != matches[0].TypeId))
                result.Add(matches[0]);
        }
        return result;
    }
}
