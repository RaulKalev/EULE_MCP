using System.IO;
using System.Text;
using RevitMCP.Addin.Configuration;

namespace RevitMCP.Addin.ModelPages.Hosting;

/// <summary>
/// Owns the model page store and its loopback listener for this Revit session. The listener starts
/// when the first page is published, or at startup when earlier pages exist, so links from a previous
/// session keep working. <c>pages.enabled=false</c> in the user config switches the feature off.
/// </summary>
public sealed class ModelPageService : IDisposable
{
    private const string ScriptResourceName = "RevitMCP.Addin.ModelPages.Viewer.pages.js";

    private readonly ModelPageServer _server;
    private readonly Action<string> _log;
    private string? _script;

    private ModelPageService(ModelPageRevitHandler handler, Action<string> log)
    {
        _log = log;
        Store = new ModelPageStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "Pages"));
        _server = new ModelPageServer(Store, handler, Script, ModelPageServer.DefaultPort, log);
    }

    public static ModelPageService? Current { get; private set; }

    public ModelPageStore Store { get; }

    public string? Url => _server.IsListening ? _server.BaseUrl : null;

    /// <summary>Must be called in a valid Revit API context (App.OnStartup): it creates the ExternalEvent.</summary>
    public static ModelPageService Create(Action<string> log)
    {
        var service = new ModelPageService(ModelPageRevitHandler.Create(), log);
        Current = service;
        return service;
    }

    /// <summary>Starts the listener when pages from an earlier session exist. Never throws.</summary>
    public void StartIfPagesExist()
    {
        try
        {
            if (Enabled() && Store.List().Count > 0) _server.Start();
        }
        catch (Exception ex)
        {
            _log("[model-pages] start failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>The page's URL once the listener is up; null with the reason when it cannot be served.</summary>
    public string? UrlFor(string pageId, out string? problem)
    {
        problem = null;
        if (!Enabled())
        {
            problem = "Model pages are switched off (pages.enabled=false in the user configuration).";
            return null;
        }
        if (!_server.Start())
        {
            problem = $"No local port from {ModelPageServer.DefaultPort} up could be opened for the page.";
            return null;
        }
        return _server.PageUrl(pageId);
    }

    public void Dispose()
    {
        _server.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private string Script()
    {
        if (_script != null) return _script;
        using var stream = typeof(ModelPageService).Assembly.GetManifestResourceStream(ScriptResourceName);
        if (stream == null) return string.Empty;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return _script = reader.ReadToEnd();
    }

    /// <summary>pages.enabled in the user config; on unless it says false.</summary>
    private static bool Enabled()
    {
        try
        {
            var (path, _) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser);
            if (path == null || !File.Exists(path)) return true;
            var (config, _) = new JsonConfigService().Read(path);
            var node = config?["pages"]?["enabled"];
            return node == null || !bool.TryParse(node.ToString(), out var on) || on;
        }
        catch
        {
            return true;
        }
    }
}
