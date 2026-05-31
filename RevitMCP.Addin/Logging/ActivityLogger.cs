using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Logging;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Logging;

public class ActivityLogger
{
    private readonly string _logDir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ActivityLogger()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RKTools", "RevitMCP", "Logs");
        Directory.CreateDirectory(_logDir);
    }

    public async Task WriteAsync(McpToolRequest request, McpToolResult result, RevitDocumentContext? context = null, long? responseSizeBytes = null)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.Now,
            User = Environment.UserName,
            Machine = Environment.MachineName,
            Client = request.ClientName,
            Tool = request.ToolName,
            Permission = request.Permission.ToString(),
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

        await AppendEntryAsync(entry);
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
        }
        finally
        {
            _lock.Release();
        }

        // Fire after releasing the lock so subscribers never deadlock the writer.
        EntryLogged?.Invoke(entry);
    }

    public string LogDirectory => _logDir;
}
