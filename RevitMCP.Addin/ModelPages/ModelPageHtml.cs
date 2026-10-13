using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace RevitMCP.Addin.ModelPages;

/// <summary>
/// What the server adds to a page: the bootstrap that tells the helper script which page it is and
/// which token to send, the index of published pages, and the checks made when a page is published.
/// Pure — unit tested in RevitMCP.Tests.
/// </summary>
public static class ModelPageHtml
{
    public const string ScriptPath = "/pages.js";

    private static readonly Regex ExternalResource = new(
        @"<(?:script|img|iframe|link|video|audio|source)\b[^>]*\b(?:src|href)\s*=\s*[""']?\s*(?:https?:)?//",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ElementIdAttribute = new(@"\bdata-revit-id\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The page as served: the helper script and its settings go before the last <c>&lt;/body&gt;</c>, or at the
    /// end of a fragment. The id and token are JSON-encoded, with "&lt;" escaped so they cannot close the script.
    /// </summary>
    public static string Inject(string html, string pageId, string token)
    {
        var settings = JsonConvert.SerializeObject(new { id = pageId, token }).Replace("<", "\\u003c");
        var bootstrap = "<script>window.__revitPage=" + settings + ";</script><script src=\"" + ScriptPath + "\"></script>";
        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + bootstrap : html.Substring(0, at) + bootstrap + html.Substring(at);
    }

    /// <summary>Things the author should know about a page being published; empty when there is nothing to say.</summary>
    public static List<string> Review(string html)
    {
        var notes = new List<string>();
        if (ExternalResource.IsMatch(html))
            notes.Add("The page loads something from another site (script, style, image or frame). The page is served with a policy that blocks every other origin, so that will not load: put scripts, styles and images inline.");
        if (!ElementIdAttribute.IsMatch(html))
            notes.Add("No element carries data-revit-id, so nothing on the page selects elements in Revit. Add data-revit-id=\"<element id>\" to the rows or items that stand for model elements.");
        return notes;
    }

    /// <summary>The list of published pages shown at "/".</summary>
    public static string Index(IReadOnlyList<ModelPageInfo> pages)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>Revit model pages</title><style>");
        sb.Append("body{font:15px/1.5 system-ui,Segoe UI,sans-serif;margin:0;background:#f6f7f9;color:#1c2330}");
        sb.Append("main{max-width:860px;margin:0 auto;padding:32px 16px}h1{font-size:22px;margin:0 0 4px}");
        sb.Append("p.lead{margin:0 0 24px;color:#5b6676}ul{list-style:none;padding:0;margin:0}");
        sb.Append("li{background:#fff;border:1px solid #e1e5eb;border-radius:8px;margin:0 0 8px}");
        sb.Append("li a{display:block;padding:12px 16px;color:inherit;text-decoration:none}li a:hover{background:#f0f4fa}");
        sb.Append("b{display:block}small{color:#5b6676}p.empty{color:#5b6676}");
        sb.Append("@media(prefers-color-scheme:dark){body{background:#14181f;color:#e6e9ee}li{background:#1c222c;border-color:#2c3440}");
        sb.Append("li a:hover{background:#242c38}small,p.lead,p.empty{color:#9aa5b5}}");
        sb.Append("</style></head><body><main><h1>Revit model pages</h1>");
        sb.Append("<p class=\"lead\">Pages an AI agent generated from a model. They work on this computer while Revit is running.</p>");
        if (pages.Count == 0)
        {
            sb.Append("<p class=\"empty\">No pages yet. Ask the agent to publish its report as a model page.</p>");
        }
        else
        {
            sb.Append("<ul>");
            foreach (var page in pages)
            {
                sb.Append("<li><a href=\"/p/").Append(page.Id).Append("\"><b>").Append(WebUtility.HtmlEncode(page.Title)).Append("</b><small>");
                sb.Append(WebUtility.HtmlEncode(page.ProjectName));
                sb.Append(" · ").Append(page.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                sb.Append("</small></a></li>");
            }
            sb.Append("</ul>");
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}
