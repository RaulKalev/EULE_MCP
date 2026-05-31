using System.Diagnostics;
using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Skills.Tasks.Delivery;

/// <summary>
/// Task ID: delivery.scan.folder
/// Scans a delivery folder and stores the <see cref="DeliveryScanResult"/> in SharedData["deliveryScan"].
/// Setting "folderPath" is required.
/// </summary>
internal sealed class ScanDeliveryFolderTask : ISkillTask
{
    public string Id           => "delivery.scan.folder";
    public string Name         => "Scan Delivery Folder";
    public bool   ChangesModel => false;

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var folderPath = SkillSettings.GetString(taskDef.Settings, "folderPath");
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return new SkillTaskResult
                {
                    TaskId = Id, TaskName = Name, Success = false, CriticalFailure = true,
                    Message = "Setting 'folderPath' is required.", DurationMs = sw.ElapsedMilliseconds
                };
            }

            var recursive   = SkillSettings.GetBool(taskDef.Settings, "recursive", true);
            var maxResults  = SkillSettings.GetInt(taskDef.Settings, "maxResults", 5000);
            var extsRaw     = SkillSettings.GetString(taskDef.Settings, "includeExtensions", "pdf,dwg");
            var extensions  = extsRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
                                     .Where(e => e.Length > 0)
                                     .ToList();

            var policy  = new FilePathPolicy();
            var scanner = new DeliveryFileScanner(policy);
            var scan    = scanner.Scan(folderPath, recursive, extensions, maxResults);

            if (!scan.Success)
            {
                return new SkillTaskResult
                {
                    TaskId = Id, TaskName = Name, Success = false, CriticalFailure = true,
                    Message = scan.Error ?? "Scan failed.", DurationMs = sw.ElapsedMilliseconds
                };
            }

            ctx.SharedData["deliveryScan"] = scan;

            return new SkillTaskResult
            {
                TaskId    = Id, TaskName = Name, Success = true,
                Message   = $"Scanned delivery folder '{folderPath}'. Found {scan.Files.Count} matching files.",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId = Id, TaskName = Name, Success = false, CriticalFailure = true,
                Message = $"Folder scan failed: {ex.Message}", DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}
