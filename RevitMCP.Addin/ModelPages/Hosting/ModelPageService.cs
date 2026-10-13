using System.IO;
using System.Text;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.ModelPages.Hosting;

/// <summary>
/// Owns the model page store and its loopback listener for this Revit session. The listener starts
/// when the first page is published, or at startup when earlier pages exist, so links from a previous
/// session keep working. <c>pages.enabled=false</c> in the user config switches the feature off,
/// <c>pages.allowEdits=false</c> keeps pages read-only.
/// </summary>
public sealed class ModelPageService : IDisposable
{
    private const string ScriptResourceName = "RevitMCP.Addin.ModelPages.Viewer.pages.js";
    private const string StylesResourceName = "RevitMCP.Addin.ModelPages.Viewer.pages.css";

    private readonly ModelPageServer _server;
    private readonly Action<string> _log;
    private string? _script;
    private string? _styles;

    private ModelPageService(ModelPageRevitHandler handler, Action<string> log)
    {
        _log = log;
        Store = new ModelPageStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "Pages"));
        _server = new ModelPageServer(Store, handler, () => _script ??= Resource(ScriptResourceName), ModelPageServer.DefaultPort, log,
            () => _styles ??= Resource(StylesResourceName));
    }

    public static ModelPageService? Current { get; private set; }

    public ModelPageStore Store { get; }

    public string? Url => _server.IsListening ? _server.BaseUrl : null;

    /// <summary>Must be called in a valid Revit API context (App.OnStartup): it creates the ExternalEvent.</summary>
    public static ModelPageService Create(Action<string> log, ExternalEventService dispatcher, ApprovalService approvals, ActivityLogger? logger)
    {
        var handler = ModelPageRevitHandler.Create();
        handler.SetWritePath(dispatcher, approvals, logger);
        handler.EditsEnabled = () => Switch("allowEdits");
        var service = new ModelPageService(handler, log);
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

    private static string Resource(string name)
    {
        using var stream = typeof(ModelPageService).Assembly.GetManifestResourceStream(name);
        if (stream == null) return string.Empty;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static bool Enabled() => Switch("enabled");

    /// <summary>A pages.&lt;key&gt; switch in the user config; on unless it says false.</summary>
    private static bool Switch(string key)
    {
        try
        {
            var (path, _) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser);
            if (path == null || !File.Exists(path)) return true;
            var (config, _) = new JsonConfigService().Read(path);
            var node = config?["pages"]?[key];
            return node == null || !bool.TryParse(node.ToString(), out var on) || on;
        }
        catch
        {
            return true;
        }
    }
}
