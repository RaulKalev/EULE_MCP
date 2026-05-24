using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Saves a human-readable proposal requesting that the company skill master be updated,
/// based on a project override. Writes only to the user-scoped proposals folder;
/// NEVER modifies any company master skill file.
/// Tool name: revit_propose_master_skill_update
/// </summary>
public class ProposeSkillMasterUpdateTool : IRevitMcpTool
{
    public string Name        => "revit_propose_master_skill_update";
    public string Description => "Proposes updating the company master skill based on a project override. Writes a proposal JSON to the local proposals folder. Does NOT modify any company master files.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Skills;

    private static readonly string ProposalsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RKTools", "RevitMCP", "SkillProposals");

    private static readonly SkillOverrideService _overrideService = new();
    private static readonly SkillLoader          _loader          = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var skillId   = ToolArguments.GetString(request.Arguments, "skillId");
        var projectId = ToolArguments.GetString(request.Arguments, "projectId");
        var notes     = ToolArguments.GetString(request.Arguments, "notes");

        if (string.IsNullOrWhiteSpace(skillId) || string.IsNullOrWhiteSpace(projectId))
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = "skillId and projectId are required."
            });
        }

        // Load master skill (existence check)
        var allSkills = _loader.LoadAll();
        var master = allSkills.FirstOrDefault(s => s.Id.Equals(skillId, StringComparison.OrdinalIgnoreCase));
        if (master == null)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = $"Master skill '{skillId}' not found. Cannot propose update for unknown skill."
            });
        }

        // Load override
        var overrideData = _overrideService.Load(skillId, projectId);
        if (overrideData == null)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = $"No project override exists for skill '{skillId}' in project '{projectId}'. Create an override first."
            });
        }

        // Write proposal file — safe, local, user-scoped only
        Directory.CreateDirectory(ProposalsDir);

        var proposal = new SkillMasterUpdateProposal
        {
            SkillId              = skillId,
            ProjectId            = projectId,
            ProposedAt           = DateTime.UtcNow,
            BasedOnMasterVersion = master.Version,
            OverrideBaseVersion  = overrideData.BaseSkillVersion,
            Notes                = notes,
            OverrideData         = overrideData.Overrides,
        };

        var safeSkill   = SanitiseName(skillId);
        var safeProject = SanitiseName(projectId);
        var fileName    = $"{safeSkill}__{safeProject}_{DateTime.UtcNow:yyyyMMddHHmmss}.skill.proposal.json";
        var filePath    = Path.Combine(ProposalsDir, fileName);

        var json = JsonConvert.SerializeObject(proposal, Formatting.Indented);
        File.WriteAllText(filePath, json);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Proposal saved. Share '{filePath}' with the skill administrator to request a master update.",
            Data       = new { proposalFile = filePath, skillId, projectId },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string SanitiseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}

// ─── Proposal model ───────────────────────────────────────────────────────────

public class SkillMasterUpdateProposal
{
    [JsonProperty("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonProperty("skillId")]
    public string SkillId { get; set; } = string.Empty;

    [JsonProperty("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonProperty("proposedAt")]
    public DateTime ProposedAt { get; set; }

    [JsonProperty("basedOnMasterVersion")]
    public string BasedOnMasterVersion { get; set; } = string.Empty;

    [JsonProperty("overrideBaseVersion")]
    public string OverrideBaseVersion { get; set; } = string.Empty;

    [JsonProperty("notes")]
    public string? Notes { get; set; }

    [JsonProperty("overrideData")]
    public SkillOverrideData OverrideData { get; set; } = new();
}
