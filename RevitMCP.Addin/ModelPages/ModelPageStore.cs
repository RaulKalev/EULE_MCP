using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Addin.ModelPages;

/// <summary>What is known about one published page; the HTML itself stays on disk.</summary>
public sealed class ModelPageInfo
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;

    /// <summary>The model the page was generated from; element ids mean nothing in another one.</summary>
    [JsonProperty("model_id")] public string ModelId { get; set; } = string.Empty;
    [JsonProperty("project_name")] public string ProjectName { get; set; } = string.Empty;
    [JsonProperty("client", NullValueHandling = NullValueHandling.Ignore)] public string? Client { get; set; }
    [JsonProperty("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonProperty("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonProperty("bytes")] public int Bytes { get; set; }
}

/// <summary>
/// Model pages on disk: one <c>&lt;id&gt;.html</c> and one <c>&lt;id&gt;.json</c> per page in a local folder,
/// so a page survives a Revit restart. Pure — no Revit API, unit tested in RevitMCP.Tests.
/// </summary>
public sealed class ModelPageStore
{
    public const int MaxHtmlBytes = 2 * 1024 * 1024;
    public const int MaxPages = 200;
    public const int MaxIdLength = 48;
    public const int MaxTitleLength = 120;

    private readonly string _folder;
    private readonly object _gate = new();

    public ModelPageStore(string folder) => _folder = folder;

    public string Folder => _folder;

    /// <summary>Lowercase letters, digits and hyphens only: an id is a file name and a URL segment.</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id!.Length > MaxIdLength) return false;
        foreach (var c in id)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
        return id[0] != '-';
    }

    /// <summary>A readable id from the title plus a random suffix, e.g. "kk-48-kontroll-3fa9c1".</summary>
    public static string NewId(string? title)
    {
        var slug = new StringBuilder();
        foreach (var raw in (title ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(raw);
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) slug.Append(c);
            else if (slug.Length > 0 && slug[slug.Length - 1] != '-') slug.Append('-');
            if (slug.Length >= MaxIdLength - 7) break;
        }
        var stem = slug.ToString().Trim('-');
        if (stem.Length == 0) stem = "page";
        // "memory-" ids belong to the memory review page, the one page allowed to change shared memory.
        if (stem == "memory" || stem.StartsWith("memory-", StringComparison.Ordinal)) stem = "page-" + stem;

        var bytes = new byte[3];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return stem + "-" + string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Saves a page. With <paramref name="id"/> of an existing page that page is replaced (its link stays valid);
    /// otherwise a new one is created. Throws <see cref="ArgumentException"/> with a message for the caller.
    /// </summary>
    public ModelPageInfo Save(string? id, string title, string html, string modelId, string projectName, string? client, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(html)) throw new ArgumentException("The page has no HTML.");
        var bytes = Encoding.UTF8.GetByteCount(html);
        if (bytes > MaxHtmlBytes) throw new ArgumentException($"The page is {bytes / 1024:N0} KB; the limit is {MaxHtmlBytes / 1024:N0} KB.");
        if (!string.IsNullOrEmpty(id) && !IsValidId(id)) throw new ArgumentException("pageId may hold lowercase letters, digits and hyphens only.");

        title = (title ?? string.Empty).Trim();
        if (title.Length == 0) title = "Model page";
        if (title.Length > MaxTitleLength) title = title.Substring(0, MaxTitleLength);

        lock (_gate)
        {
            Directory.CreateDirectory(_folder);
            var existing = string.IsNullOrEmpty(id) ? null : Find(id!);
            var info = new ModelPageInfo
            {
                Id = string.IsNullOrEmpty(id) ? NewId(title) : id!,
                Title = title,
                ModelId = modelId,
                ProjectName = projectName,
                Client = client,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
                Bytes = bytes
            };
            File.WriteAllText(HtmlPath(info.Id), html, new UTF8Encoding(false));
            File.WriteAllText(MetaPath(info.Id), JsonConvert.SerializeObject(info, Formatting.Indented), new UTF8Encoding(false));
            Prune(info.Id);
            return info;
        }
    }

    /// <summary>Every page, most recently updated first. Unreadable entries are skipped.</summary>
    public List<ModelPageInfo> List()
    {
        var pages = new List<ModelPageInfo>();
        lock (_gate)
        {
            if (!Directory.Exists(_folder)) return pages;
            foreach (var file in Directory.GetFiles(_folder, "*.json"))
            {
                var info = ReadMeta(file);
                if (info != null && File.Exists(HtmlPath(info.Id))) pages.Add(info);
            }
        }
        pages.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return pages;
    }

    public ModelPageInfo? Find(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_gate)
        {
            var meta = MetaPath(id);
            return File.Exists(meta) && File.Exists(HtmlPath(id)) ? ReadMeta(meta) : null;
        }
    }

    public string? ReadHtml(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_gate)
        {
            try { return File.Exists(HtmlPath(id)) ? File.ReadAllText(HtmlPath(id), Encoding.UTF8) : null; }
            catch { return null; }
        }
    }

    public bool Remove(string id)
    {
        if (!IsValidId(id)) return false;
        lock (_gate)
        {
            var found = File.Exists(MetaPath(id)) || File.Exists(HtmlPath(id));
            try { File.Delete(MetaPath(id)); } catch { }
            try { File.Delete(HtmlPath(id)); } catch { }
            return found;
        }
    }

    private string HtmlPath(string id) => Path.Combine(_folder, id + ".html");
    private string MetaPath(string id) => Path.Combine(_folder, id + ".json");

    private static ModelPageInfo? ReadMeta(string file)
    {
        try
        {
            var info = JsonConvert.DeserializeObject<ModelPageInfo>(File.ReadAllText(file, Encoding.UTF8));
            // The file name is the id the server looks up; a mismatching or unsafe one is not a page.
            return info != null && IsValidId(info.Id) && info.Id == Path.GetFileNameWithoutExtension(file) ? info : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Keeps the newest <see cref="MaxPages"/>; the page just saved always stays.</summary>
    private void Prune(string keepId)
    {
        var pages = new List<ModelPageInfo>();
        foreach (var file in Directory.GetFiles(_folder, "*.json"))
        {
            var info = ReadMeta(file);
            if (info != null) pages.Add(info);
        }
        if (pages.Count <= MaxPages) return;
        pages.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        foreach (var old in pages.Skip(MaxPages))
        {
            if (old.Id == keepId) continue;
            try { File.Delete(MetaPath(old.Id)); } catch { }
            try { File.Delete(HtmlPath(old.Id)); } catch { }
        }
    }
}
