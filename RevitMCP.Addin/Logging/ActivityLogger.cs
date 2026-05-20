using System.IO;
using Newtonsoft.Json;
using RevitMCP.Core.Logging;
using RevitMCP.Core.Models;

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

    public async Task WriteAsync(McpToolRequest request, McpToolResult result)
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
            Message = result.Message,
            Warnings = result.Warnings,
            Errors = result.Errors
        };

        await AppendEntryAsync(entry);
    }

    public async Task WriteRawAsync(string message)
    {
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
            await File.AppendAllTextAsync(filePath, line);
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
