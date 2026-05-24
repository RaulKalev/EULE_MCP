# Skills System

The EULE MCP skill system lets you run multi-step QA workflows against a live Revit model using a single tool call. Each **skill** is a named sequence of tasks with configurable settings. Skills can be company-wide (master) or project-specific (override).

---

## Where Company Skills Are Stored

**Company master skills** (read-only for agents):

```
%ProgramData%\RKTools\MCP\Skills\
```

Built-in default skill files are written here automatically when the skill loader starts for the first time. If the path is inaccessible (e.g. no admin rights), skills fall back to:

```
%AppData%\RKTools\RevitMCP\SkillCache\
```

**Project overrides** (per-project, user-scoped):

```
%AppData%\RKTools\RevitMCP\ProjectSkillOverrides\{projectId}\{skillId}.override.skill.json
```

**Skill proposals** (safe local-only, never auto-applied):

```
%AppData%\RKTools\RevitMCP\SkillProposals\
```

---

## Built-In Company Skills

| Skill ID | Name | Purpose |
|----------|------|---------|
| `company.lehed.nimetamise-kontroll` | Lehtede Nimetamise Kontroll | Sheet naming QA (Estonian) |
| `company.delivery.check` | Delivery Check | Compare delivery folder vs Revit sheets |
| `company.parameter.qa` | Parameter QA | Required parameter completeness |
| `company.coordination.qa` | Coordination QA | Clash detection and reporting |
| `company.project.pre-delivery` | Pre-Delivery Combined Check | All checks in one run |

---

## Running a Skill

```
revit_list_skills                          — list all available skills
revit_get_skill_details skillId=...        — see tasks and settings
revit_preview_skill_run skillId=...        — dry-run: see what would execute
revit_run_skill skillId=... projectId=...  — execute the skill
```

---

## Configuring a Project-Specific Skill

Use `revit_create_project_skill_override` or `revit_update_project_skill_override` to configure project-specific settings. Only the fields you want to change need to be specified — everything else falls back to the master.

**Example**: disable clash detection in the pre-delivery skill for a project:

```json
{
  "skillId": "company.project.pre-delivery",
  "projectId": "1626",
  "taskOverrides": {
    "coordination.run.clash-preset": { "enabled": false }
  }
}
```

---

## Comparing Override to Master

```
revit_compare_skill_override_to_master skillId=... projectId=...
revit_export_skill_override_diff_markdown skillId=... projectId=...
```

These tools are read-only. They help you understand what a project override changes relative to the current company master.

---

## Proposing a Master Skill Update

If a project override works well and you want to propose making it the new company default:

```
revit_propose_master_skill_update skillId=... projectId=... notes="..."
```

This writes a `.skill.proposal.json` file to `%AppData%\RKTools\RevitMCP\SkillProposals\`. A skill administrator can review and apply the proposal manually. **The tool never modifies company master files.**

---

## Skill File Format

```json
{
  "id": "company.delivery.check",
  "name": "Delivery Check",
  "version": "1.0.0",
  "author": "EULE / RK Tools",
  "isCompanyMaster": true,
  "defaultSettings": {
    "stopOnCriticalFailure": true,
    "allowProjectOverride": true
  },
  "tasks": [
    { "id": "delivery.scan.folder", "enabled": true, "settings": { "folderPath": "", "includeExtensions": "pdf,dwg" } },
    { "id": "delivery.compare.revit-sheets", "enabled": true, "settings": {} },
    { "id": "common.export.excel-report", "enabled": true, "settings": { "reportTitle": "Delivery Check Report" } },
    { "id": "common.export.html-dashboard", "enabled": true, "settings": { "reportTitle": "Delivery Check Dashboard" } }
  ]
}
```

Place `.skill.json` files in the company skills folder. The skill loader picks them up automatically on next Revit start or AppLoader hot-reload.
