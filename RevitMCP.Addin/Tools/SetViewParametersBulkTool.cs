using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Tools;

public class SetViewParametersBulkTool : IRevitMcpTool
{
    public string Name => "revit_set_view_parameters_bulk";
    public string Description =>
        "Sets one or more parameters on multiple views in a single transaction. Requires approval. " +
        "Required: viewIds (long array) OR (viewTypes + nameFilter) to select views, " +
        "parameters (object — key=parameterName, value=value). " +
        "Optional: includeTemplates (bool, default false), limit (int, default 500).";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var viewIds          = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var viewTypes        = ToolArguments.GetStringArray(request.Arguments, "viewTypes");
        var nameFilter       = ToolArguments.GetString(request.Arguments, "nameFilter");
        var includeTemplates = ToolArguments.GetBool(request.Arguments, "includeTemplates", false);
        var limit            = ToolArguments.GetInt(request.Arguments, "limit", 500);
        var paramValues      = GetParamDict(request.Arguments);

        if (paramValues.Count == 0)
            return Task.FromResult(Fail(request, "parameters object is required."));
        if (viewIds.Length == 0 && viewTypes.Length == 0 && string.IsNullOrWhiteSpace(nameFilter))
            return Task.FromResult(Fail(request, "Provide viewIds, viewTypes, or nameFilter."));

        IEnumerable<View> views;
        if (viewIds.Length > 0)
        {
            views = viewIds.Select(id => doc.GetElement(new ElementId(id)) as View).Where(v => v != null)!;
        }
        else
        {
            views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => includeTemplates || !v.IsTemplate);
            if (viewTypes.Length > 0)
                views = views.Where(v => viewTypes.Any(vt =>
                    string.Equals(v.ViewType.ToString(), vt, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(nameFilter))
                views = views.Where(v => v.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        }
        views = views.Take(limit);

        int updated  = 0;
        var warnings = new List<string>();
        var results  = new List<object>();

        using var t = new Transaction(doc, "Revit MCP - Set View Parameters Bulk");
        t.Start();
        foreach (var v in views)
        {
            var setCount = 0;
            foreach (var (pName, pVal) in paramValues)
            {
                try
                {
                    var p = FindParameter(v, pName);
                    if (p == null) { warnings.Add($"View '{v.Name}': param '{pName}' not found."); continue; }
                    if (p.IsReadOnly) { warnings.Add($"View '{v.Name}': param '{pName}' is read-only."); continue; }
                    SetParam(p, pVal?.ToString() ?? "");
                    setCount++;
                }
                catch (Exception ex) { warnings.Add($"View '{v.Name}', param '{pName}': {ex.Message}"); }
            }
            if (setCount > 0) { updated++; results.Add(new { viewId = v.Id.Value, viewName = v.Name }); }
        }
        t.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = updated > 0,
            Message    = $"Updated parameters on {updated} view(s).",
            Data       = new { updated, results },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static void SetParam(Parameter p, string strVal)
    {
        switch (p.StorageType)
        {
            case StorageType.String:  p.Set(strVal); break;
            case StorageType.Integer when int.TryParse(strVal, out var iv): p.Set(iv); break;
            case StorageType.Double  when double.TryParse(strVal,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var dv): p.Set(dv); break;
        }
    }

    private static Dictionary<string, object?> GetParamDict(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue("parameters", out var val)) return [];
        JObject? jo = val switch
        {
            JObject j => j,
            JToken t  => t as JObject,
            string s  => TryParseJObject(s),
            _         => null
        };
        if (jo == null) return [];
        return jo.Properties().ToDictionary(p => p.Name, p => (object?)p.Value.ToObject<object>());
    }

    private static JObject? TryParseJObject(string s) { try { return JObject.Parse(s); } catch { return null; } }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };

    /// <summary>Looks up a parameter by exact name, then falls back to case-insensitive contains match.</summary>
    private static Parameter? FindParameter(Element element, string name)
    {
        var exact = element.LookupParameter(name);
        if (exact != null) return exact;
        Parameter? match = null;
        foreach (Parameter p in element.Parameters)
        {
            if (p.Definition.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                if (match != null) return null; // ambiguous — require exact name
                match = p;
            }
        }
        return match;
    }
}
