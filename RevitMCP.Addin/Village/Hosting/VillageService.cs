using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Graph;

namespace RevitMCP.Addin.Village.Hosting;

/// <summary>
/// Owns the village for one Revit process: options from the user/company config, the state hub,
/// the loopback SSE server, the graph reader and the instance registration. Created once in
/// <c>App.OnStartup</c>; starting the listener is opt-in (<c>village.enabled</c>) and can be
/// toggled from the connector window. Every public member is guarded — a failure here is logged
/// and otherwise invisible to the connector. Contains no Revit API calls.
/// </summary>
public sealed class VillageService : IDisposable
{
    private const string ViewerResourceName = "RevitMCP.Addin.Village.Viewer.village.html";
    private const string FallbackHtml =
        "<!doctype html><meta charset=\"utf-8\"><title>Project Village</title>" +
        "<p>The Project Village viewer resource is missing from this build. The read-only feed is still available at <code>/events</code> and <code>/snapshot</code>.</p>";

    private static readonly string DiagLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCP_startup.log");

    private readonly object _gate = new();
    private readonly string _revitVersion;
    private readonly int _processId;
    private readonly VillageInstanceRegistry _registry = new();
    private VillageSseServer? _server;
    private string? _html;

    /// <summary>The process-wide instance the hooks consult. Null until App.OnStartup created it.</summary>
    public static VillageService? Current { get; private set; }

    private VillageService(string revitVersion, int processId, VillageOptions options)
    {
        _revitVersion = revitVersion;
        _processId = processId;
        Options = options;

        var cacheRoot = Path.Combine(GraphPathResolver.DefaultLocalRoot(), "..", "Village", "graph-cache");
        var reader = new VillageGraphReader(
            Path.GetFullPath(cacheRoot),
            new VillageThemeScorer(VillageThemeConfig.FromJson(options.ThemesJson)));

        Hub = new VillageStateHub(
            options,
            graphReader: reader,
            graphSourceProvider: BuildGraphSource,
            instancesProvider: ListInstances,
            log: Log);
        Hub.MessagePublished += OnMessage;
    }

    public VillageOptions Options { get; private set; }
    public VillageStateHub Hub { get; }

    public bool IsRunning
    {
        get { lock (_gate) return _server != null && _server.IsListening && Hub.IsRunning; }
    }

    public string? Url
    {
        get { lock (_gate) return _server?.IsListening == true ? _server.BaseUrl : null; }
    }

    public int ViewerCount
    {
        get { lock (_gate) return _server?.ClientCount ?? 0; }
    }

    public string? LastError { get; private set; }

    /// <summary>Creates the service and publishes it as <see cref="Current"/>. Does not start anything.</summary>
    public static VillageService Create(string revitVersion, int processId)
    {
        var options = LoadOptions();
        var service = new VillageService(revitVersion, processId, options);
        Current = service;
        service.Log($"created (enabled={options.Enabled}, port={options.Port}, queue={options.QueueSize})");
        return service;
    }

    /// <summary>Starts the listener when <c>village.enabled</c> is true. Never throws.</summary>
    public bool StartIfEnabled()
    {
        try { return Options.Enabled && Start(); }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log("start failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>Starts the consumer and the loopback listener. Returns false when no port could be bound.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_server != null && _server.IsListening) return true;
            try
            {
                var server = new VillageSseServer(Options, () => Hub.SnapshotJson, LoadViewerHtml, () => Hub.InstancesJson, Log);
                if (!server.Start())
                {
                    LastError = $"No free loopback port between {Options.Port} and {Options.Port + VillageOptions.PortFallbackAttempts - 1}.";
                    Log(LastError);
                    server.Dispose();
                    return false;
                }
                _server = server;
                LastError = null;
                Hub.Start();
                RegisterInstance();
                Log("started at " + server.BaseUrl);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log("start failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _registry.Unregister(_processId); } catch { }
            try { _server?.Stop(); } catch { }
            _server = null;
            try { Hub.Stop(); } catch { }
            Log("stopped");
        }
    }

    /// <summary>Starts or stops the listener and persists <c>village.enabled</c> in the user config.</summary>
    public bool SetEnabled(bool enabled)
    {
        Options.Enabled = enabled;
        PersistEnabled(enabled);
        if (enabled) return Start();
        Stop();
        return true;
    }

    public void Dispose()
    {
        Stop();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    // ─── Wiring ─────────────────────────────────────────────────────────────

    private void OnMessage(string eventName, string json)
    {
        VillageSseServer? server;
        lock (_gate) server = _server;
        server?.Broadcast(eventName, json);
        if (eventName == "state") RefreshRegistration();
    }

    private VillageGraphSource BuildGraphSource(VillageProjectContext context, VillageGraphHint? hint)
    {
        var (configured, configuredSource) = GraphPathResolver.LoadConfiguredSharedFolder();
        var source = new VillageGraphSource
        {
            DatabasePath = hint?.DatabasePath,
            ModelName = context.ModelName,
            RootSource = configured != null ? configuredSource ?? "config" : "local fallback"
        };
        if (!string.IsNullOrWhiteSpace(configured)) source.Roots.Add(configured!);
        source.Roots.Add(GraphPathResolver.DefaultLocalRoot());
        return source;
    }

    private List<VillageInstanceLink> ListInstances() =>
        _registry.List(IsProcessAlive, _processId);

    private string? _registeredProject;

    private void RegisterInstance()
    {
        var url = _server?.BaseUrl;
        if (url == null) return;
        _registeredProject = Hub.Aggregator.ProjectName;
        _registry.Register(new VillageInstanceRecord
        {
            ProcessId = _processId,
            RevitVersion = _revitVersion,
            ProjectName = _registeredProject,
            Url = url
        });
    }

    private void RefreshRegistration()
    {
        try
        {
            var name = Hub.Aggregator.ProjectName;
            if (string.Equals(name, _registeredProject, StringComparison.Ordinal)) return;
            RegisterInstance();
        }
        catch { }
    }

    private string LoadViewerHtml()
    {
        var cached = _html;
        if (cached != null) return cached;
        try
        {
            using var stream = typeof(VillageService).Assembly.GetManifestResourceStream(ViewerResourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                cached = reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Log("viewer resource load failed: " + ex.Message);
        }
        _html = cached ?? FallbackHtml;
        return _html;
    }

    // ─── Configuration ──────────────────────────────────────────────────────

    public static VillageOptions LoadOptions()
    {
        try
        {
            var svc = new JsonConfigService();
            var user = ReadScope(svc, ConfigPathResolver.ScopeUser);
            var company = ReadScope(svc, ConfigPathResolver.ScopeCompany);
            return VillageOptions.FromConfig(user, company);
        }
        catch
        {
            return VillageOptions.Default;
        }
    }

    private static JsonObject? ReadScope(JsonConfigService svc, string scope)
    {
        var (path, _) = ConfigPathResolver.Resolve(scope);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var (config, _) = svc.Read(path!, createIfMissing: false);
        return config;
    }

    private void PersistEnabled(bool enabled)
    {
        try
        {
            var (path, error) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser);
            if (path == null) { Log("cannot persist village.enabled: " + error); return; }
            var svc = new JsonConfigService();
            var updates = new Dictionary<string, JsonNode?> { ["village.enabled"] = JsonValue.Create(enabled) };
            var (ok, err, _, _) = svc.Update(path, updates, backupBeforeOverwrite: false, createIfMissing: true);
            if (!ok) Log("cannot persist village.enabled: " + err);
        }
        catch (Exception ex)
        {
            Log("cannot persist village.enabled: " + ex.Message);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return true; }
    }

    private void Log(string message)
    {
        if (!Options.DiagnosticLogging && !message.StartsWith("start", StringComparison.Ordinal) &&
            !message.StartsWith("created", StringComparison.Ordinal) && !message.StartsWith("stopped", StringComparison.Ordinal) &&
            !message.StartsWith("No free", StringComparison.Ordinal))
            return;
        try { File.AppendAllText(DiagLogPath, $"[VILLAGE {DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}"); } catch { }
    }
}
