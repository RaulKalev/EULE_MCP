using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Logging;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Logging;

public class ActivityLogger
{
    private static readonly TimeSpan SettingsCacheTime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProjectIdCacheTime = TimeSpan.FromMinutes(10);

    private readonly string _logDir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Usage.UsageShare _share;
    private readonly ConcurrentDictionary<string, (string? Id, DateTimeOffset At)> _configProjectIds = new(StringComparer.OrdinalIgnoreCase);
    private Usage.UsageSettings? _usage;
    private DateTimeOffset _usageReadAt;

    public ActivityLogger()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RKTools", "RevitMCP", "Logs");
        Directory.CreateDirectory(_logDir);
        _share = new Usage.UsageShare(_logDir);

        // Usage reporting (docs/usage-reporting.md): copy days whose shared append failed last time. Background, never throws.
        _ = Task.Run(() =>
        {
            try
            {
                var shared = UsageSettings().SharedFolder;
                if (shared != null) _share.CatchUp(shared, Environment.UserName, Environment.MachineName);
            }
            catch { }
        });
    }

    /// <summary>The permission a tool declares (the request does not carry it); set at startup.</summary>
    public Func<string, ToolPermission?>? PermissionOf { get; set; }

    public async Task WriteAsync(McpToolRequest request, McpToolResult result, RevitDocumentContext? context = null, long? responseSizeBytes = null)
    {
        // Project Village observer (Village\): O(1), never throws, no-op when disabled. Does not alter the entry.
        Village.Hosting.VillageHooks.ToolCompleted(request, result, context);
        // Shared memory (Memory\): hint feedback and the workflow observer. O(1) enqueue, never throws.
        Memory.Hosting.MemoryHooks.ToolCompleted(request, result, context);

        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.Now,
            User = Environment.UserName,
            Machine = Environment.MachineName,
            Client = request.ClientName,
            Tool = request.ToolName,
            Permission = (PermissionOf?.Invoke(request.ToolName) ?? request.Permission).ToString(),
            ApprovalStatus = ApprovalStatusOf(request, result),
            Status = result.Success ? "Success" : "Failed",
            DurationMs = result.DurationMs,
            ResponseSizeBytes = responseSizeBytes,
            Message = result.Message,
            Warnings = result.Warnings,
            Errors = result.Errors
        };

        if (context != null)
        {
            entry.RevitVersion = context.RevitVersion;
            entry.Model = context.ModelTitle;
            entry.CentralPath = context.CentralPath;
            entry.ActiveView = context.ActiveViewName;
            entry.IsWorkshared = context.IsWorkshared;
            entry.RevitUsername = context.RevitUsername;
        }

        entry.QuerySafety = TryExtractQuerySafety(result);

        // A hot reload can keep an older RevitMCP.Core without these fields; the helper then fails to JIT and is skipped.
        try { SetUsageFields(entry, result, context); }
        catch (MissingMethodException) { }
        catch (Exception) { /* usage fields never fail a log line */ }
        try { SetPlanFields(entry, request); }
        catch (MissingMethodException) { }
        catch (Exception) { /* plan fields never fail a log line */ }

        await AppendEntryAsync(entry);
    }

    /// <summary>NotRequired, Pending (approval asked), Approved (the approved execution), Rejected or Expired.</summary>
    private static string ApprovalStatusOf(McpToolRequest request, McpToolResult result) => result.Status switch
    {
        "approval_required" => "Pending",
        "approval_rejected" => "Rejected",
        "approval_expired" => "Expired",
        _ => request.IsApproved ? "Approved" : "NotRequired"
    };

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void SetUsageFields(LogEntry entry, McpToolResult result, RevitDocumentContext? context)
    {
        entry.AffectedCount = RevitMCP.Village.VillageAffectedCount.TryExtract(result.Data);
        if (context == null || (string.IsNullOrEmpty(context.ModelTitle) && string.IsNullOrEmpty(context.CentralPath))) return;
        var modelPath = string.IsNullOrEmpty(context.CentralPath) ? context.LocalPath : context.CentralPath;
        entry.ProjectId = Usage.UsageProjectId.Resolve(ConfigProjectId(modelPath), ProjectKeyOf(context), context.ModelTitle);
    }

    /// <summary>Plan id and check outcome (#99), so an unattended session can be audited from the log alone.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetPlanFields(LogEntry entry, McpToolRequest request)
    {
        var check = Plans.PlanService.Current?.CheckOf(request.RequestId);
        var planId = check?.PlanId;
        if (planId == null && request.Arguments != null && request.Arguments.TryGetValue("planId", out var raw) && raw != null)
            planId = raw.ToString();
        if (string.IsNullOrWhiteSpace(planId)) return;
        entry.PlanId = planId;
        entry.PlanCheck = check?.Check;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string? ProjectKeyOf(RevitDocumentContext context)
    {
        try { return context.ProjectKey; }
        catch (MissingMethodException) { return null; }
    }

    /// <summary>The project config's projectId above the model, cached for a few minutes per model.</summary>
    private string? ConfigProjectId(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        if (_configProjectIds.TryGetValue(modelPath!, out var cached) && DateTimeOffset.UtcNow - cached.At < ProjectIdCacheTime) return cached.Id;
        var id = Usage.UsageProjectId.FromProjectConfig(modelPath);
        _configProjectIds[modelPath!] = (id, DateTimeOffset.UtcNow);
        return id;
    }

    private Usage.UsageSettings UsageSettings()
    {
        var now = DateTimeOffset.UtcNow;
        if (_usage == null || now - _usageReadAt > SettingsCacheTime)
        {
            _usage = Usage.UsageSettings.Load();
            _usageReadAt = now;
        }
        return _usage;
    }

    /// <summary>
    /// Inspects <paramref name="result"/>.Data for known pagination/summary fields and
    /// builds a structured <see cref="QuerySafetyLogEntry"/>. Returns null when the result
    /// carries no query-safety relevant data.
    /// </summary>
    private static QuerySafetyLogEntry? TryExtractQuerySafety(McpToolResult result)
    {
        try
        {
            if (result.Data == null && result.Status != ToolErrorCodes.ResponseTooLarge)
                return null;

            var entry = new QuerySafetyLogEntry
            {
                Truncated = result.Status == ToolErrorCodes.ResponseTooLarge
            };

            if (result.Data != null)
            {
                var token = JToken.FromObject(result.Data);
                if (token is JObject obj)
                {
                    entry.CandidateCount = obj["totalMatched"]?.Value<int?>();
                    entry.ReturnedCount = obj["returned"]?.Value<int?>();
                    entry.HasMore = obj["hasMore"]?.Value<bool?>() ?? false;
                    entry.SummaryMode = obj["summary"] is JObject sumObj && sumObj.HasValues;
                }
            }

            // Extract response size from ResponseGuard warning message when truncated
            if (entry.Truncated)
            {
                var guardWarning = result.Warnings.FirstOrDefault(w => w.Contains("[ResponseGuard]"));
                if (guardWarning != null)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(guardWarning, @"([\d,]+) bytes");
                    if (m.Success && long.TryParse(m.Groups[1].Value.Replace(",", ""), out var bytes))
                        entry.ResponseSizeBytes = bytes;
                }
            }

            // Only write a log entry if there is something meaningful to record
            return entry.CandidateCount.HasValue || entry.Truncated ? entry : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task WriteRawAsync(string message)    {
        var entry = new LogEntry
        {
            Tool = "system",
            Status = "Info",
            Message = message
        };
        await AppendEntryAsync(entry);
    }

    /// <summary>
    /// Fired on the thread-pool after each entry is written to disk.
    /// Subscribers must marshal to the UI thread themselves.
    /// </summary>
    public event Action<LogEntry>? EntryLogged;

    private async Task AppendEntryAsync(LogEntry entry)
    {
        var filePath = Path.Combine(_logDir, $"{DateTime.Today:yyyy-MM-dd}.jsonl");
        var line = JsonConvert.SerializeObject(entry, Formatting.None) + Environment.NewLine;

        await _lock.WaitAsync();
        try
        {
#if REVIT2024
            await System.Threading.Tasks.Task.Run(() => File.AppendAllText(filePath, line));
#else
            await File.AppendAllTextAsync(filePath, line);
#endif
            // The shared copy for usage reporting; best-effort, a failure is caught up from the local file later.
            var shared = SharedFolderOrNull();
            if (shared != null)
                await System.Threading.Tasks.Task.Run(() => _share.Append(shared, Environment.UserName, Environment.MachineName, DateTime.Today, line));
        }
        finally
        {
            _lock.Release();
        }

        // Fire after releasing the lock so subscribers never deadlock the writer.
        EntryLogged?.Invoke(entry);
    }

    public string LogDirectory => _logDir;

    private string? SharedFolderOrNull()
    {
        try { return UsageSettings().SharedFolder; }
        catch { return null; }
    }
}
