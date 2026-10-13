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
    public const string StylesPath = "/pages.css";

    /// <summary>
    /// The default stylesheet, and the stored light/dark choice applied before the first paint
    /// (the helper script that offers the toggle only loads at the end of the body).
    /// </summary>
    internal const string HeadAssets =
        "<link rel=\"stylesheet\" href=\"" + StylesPath + "\">" +
        "<script>try{var t=localStorage.getItem('revit-page-theme');if(t==='light'||t==='dark')document.documentElement.classList.add(t)}catch(e){}</script>";

    private static readonly Regex HeadTag = new(@"<head\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new(@"<html\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExternalResource = new(
        @"<(?:script|img|iframe|link|video|audio|source)\b[^>]*\b(?:src|href)\s*=\s*[""']?\s*(?:https?:)?//",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ElementIdAttribute = new(@"\bdata-revit-id\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The page as served. The default stylesheet is linked first in the head, so the page's own styles win;
    /// the helper script and its settings go before the last <c>&lt;/body&gt;</c>, or at the end of a fragment.
    /// The settings are JSON-encoded, with "&lt;" escaped so they cannot close the script.
    /// </summary>
    public static string Inject(string html, string pageId, string token, string? title = null)
    {
        const string styles = HeadAssets;
        var head = HeadTag.Match(html);
        var anchor = head.Success ? head : HtmlTag.Match(html);
        html = anchor.Success
            ? html.Substring(0, anchor.Index + anchor.Length) + styles + html.Substring(anchor.Index + anchor.Length)
            : styles + html;

        var settings = JsonConvert.SerializeObject(new { id = pageId, token, title }).Replace("<", "\\u003c");
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

    /// <summary>The list of published pages shown at "/", grouped by model, in the default look.</summary>
    public static string Index(IReadOnlyList<ModelPageInfo> pages)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>Revit model pages</title>").Append(HeadAssets);
        sb.Append("<style>main{max-width:900px}.brand{font-weight:800;letter-spacing:.08em;color:var(--accent);margin:36px 0 0}");
        sb.Append("a.card{display:block;color:inherit;margin:0 0 10px;transition:border-color .12s,transform .05s}");
        sb.Append("a.card:hover{border-color:var(--accent);text-decoration:none}a.card:active{transform:scale(.995)}");
        sb.Append("a.card b{display:block;font-size:16px}</style></head><body><main>");
        sb.Append("<p class=\"brand\">EULE</p><h1>Revit model pages</h1>");
        sb.Append("<p class=\"muted\">Pages an AI agent generated from a model. They work on this computer while Revit is running.</p>");
        if (pages.Count == 0)
        {
            sb.Append("<div class=\"card muted\">No pages yet. Ask the agent to publish its report as a model page.</div>");
        }
        else
        {
            foreach (var group in pages.GroupBy(p => p.ProjectName))
            {
                sb.Append("<h2>").Append(WebUtility.HtmlEncode(group.Key)).Append("</h2>");
                foreach (var page in group)
                {
                    sb.Append("<a class=\"card\" href=\"/p/").Append(page.Id).Append("\"><b>").Append(WebUtility.HtmlEncode(page.Title)).Append("</b><small>");
                    sb.Append(page.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                    if (!string.IsNullOrEmpty(page.Client)) sb.Append(" · ").Append(WebUtility.HtmlEncode(page.Client));
                    sb.Append("</small></a>");
                }
            }
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}
