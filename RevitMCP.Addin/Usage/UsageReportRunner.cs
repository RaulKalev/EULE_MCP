using System.Globalization;
using System.IO;
using System.Text;

namespace RevitMCP.Addin.Usage;

public sealed class UsageReportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public UsageReportData? Data { get; set; }
    public string? HtmlPath { get; set; }
    public string? ExcelPath { get; set; }
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Runs the monthly report end to end: settings, month, aggregation, files in <c>&lt;reportFolder&gt;\&lt;yyyy-MM&gt;.html|xlsx</c>.
/// Shared by <c>revit_usage_report</c> and the connector window's button. Pure — no Revit API.
/// </summary>
public static class UsageReportRunner
{
    public const string FormatHtml = "html";
    public const string FormatXlsx = "xlsx";
    public const string FormatBoth = "both";

    /// <summary>The month to report: yyyy-MM as given, else the previous month (the usual monthly run).</summary>
    public static string? Month(string? requested, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(requested)) return now.ToLocalTime().AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return DateTime.TryParseExact(requested!.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m)
            ? m.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : null;
    }

    /// <param name="css">The model-page stylesheet embedded in the HTML file.</param>
    public static UsageReportResult Run(UsageSettings settings, string? month, string? sharedFolderOverride, string? format, DateTimeOffset now, string? css)
    {
        var result = new UsageReportResult();
        var shared = string.IsNullOrWhiteSpace(sharedFolderOverride) ? settings.SharedFolder : Environment.ExpandEnvironmentVariables(sharedFolderOverride!.Trim());
        if (string.IsNullOrWhiteSpace(shared))
        {
            result.Message = "No shared usage folder: set usage.sharedFolder in the company config (after telling staff, see docs/usage-reporting.md) or pass sharedFolder.";
            return result;
        }
        if (!Directory.Exists(shared))
        {
            result.Message = $"The shared usage folder does not exist or is unreachable: {shared}";
            return result;
        }
        var m = Month(month, now);
        if (m == null)
        {
            result.Message = "month must be yyyy-MM, e.g. 2026-09.";
            return result;
        }
        var fmt = (format ?? FormatBoth).Trim().ToLowerInvariant();
        if (fmt is not (FormatHtml or FormatXlsx or FormatBoth))
        {
            result.Message = "format must be html, xlsx or both.";
            return result;
        }

        var data = UsageAggregator.Run(shared!, m, settings, now);
        result.Data = data;
        result.Warnings.AddRange(data.Costs.Problems);
        if (data.Files == 0) result.Warnings.Add($"No usage files for {m} in {shared}.");

        var folder = settings.ReportFolder ?? Path.Combine(shared!, "reports");
        try
        {
            Directory.CreateDirectory(folder);
            if (fmt is FormatHtml or FormatBoth)
            {
                result.HtmlPath = Path.Combine(folder, m + ".html");
                File.WriteAllText(result.HtmlPath, UsageReportWriter.Html(data, css), new UTF8Encoding(false));
            }
            if (fmt is FormatXlsx or FormatBoth)
            {
                result.ExcelPath = Path.Combine(folder, m + ".xlsx");
                UsageReportWriter.Excel(data, result.ExcelPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Message = "The report could not be written to " + folder + ": " + ex.Message;
            return result;
        }

        result.Success = true;
        result.Message = $"Usage report {m}: {data.Total.Calls:N0} calls by {data.ByUser.Count} users in {data.ByProject.Count} projects; " +
                         $"time saved ≈ {data.Total.SavedSeconds / 3600:0.0} h (estimate).";
        return result;
    }
}
