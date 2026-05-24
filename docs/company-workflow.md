# Company Workflow Guide

This guide explains how to use the EULE MCP connector for company-standard QA workflows: delivery checks, parameter validation, clash coordination, and skill management.

---

## Before You Start

Make sure:
- Revit is open with your project model loaded.
- The RevitMCP bridge is running (started automatically by your AI client).
- You have the project ID ready (e.g. `1626`, `2401`).

---

## 1. Sheet Naming Check (Lehtede Nimetamise Kontroll)

Run the built-in Estonian sheet naming check against the active model:

```
revit_run_skill skillId=company.lehed.nimetamise-kontroll projectId=1626
```

This checks:
- Sheet number format matches `{ProjectNumber}_{Stage}_{Discipline}-{Group}-{Sequence}` pattern
- Required designer parameters are filled and consistent
- Allowed disciplines: `EL`, `EN`, `EA`
- Allowed stages: `EP`, `PP`, `TP`, `TJ`

To see a full task breakdown before running:

```
revit_preview_skill_run skillId=company.lehed.nimetamise-kontroll projectId=1626
```

---

## 2. Delivery Check

Compare exported drawing files on disk against the sheets in the Revit model:

```
revit_run_skill skillId=company.delivery.check projectId=1626
```

The skill requires the `folderPath` setting to be configured. Set it via a project override:

```
revit_create_project_skill_override skillId=company.delivery.check projectId=1626
  taskOverrides={
    "delivery.scan.folder": {
      "settings": { "folderPath": "G:\\Projects\\1626\\Delivery\\EP", "includeExtensions": "pdf,dwg" }
    }
  }
```

The skill will:
1. Scan the delivery folder for PDF and DWG files
2. Compare against Revit sheets by sheet number
3. Report missing files, orphaned files, and duplicates
4. Export an Excel report and HTML dashboard

---

## 3. Parameter QA

Run a parameter completeness check using a named rule set:

```
revit_run_skill skillId=company.parameter.qa projectId=1626
```

Configure the rule set via a project override:

```
revit_update_project_skill_override skillId=company.parameter.qa projectId=1626
  taskOverrides={
    "parameterqa.run.rule-set": {
      "settings": { "ruleSetName": "EULE_Default" }
    }
  }
```

---

## 4. Coordination QA (Clash Detection)

Run a clash detection preset and export the results:

```
revit_run_skill skillId=company.coordination.qa projectId=1626
```

Configure the preset:

```
revit_update_project_skill_override skillId=company.coordination.qa projectId=1626
  taskOverrides={
    "coordination.run.clash-preset": {
      "settings": { "presetName": "EL_EN_Hard" }
    }
  }
```

---

## 5. Pre-Delivery Combined Check

Run all QA checks in sequence and get a merged report:

```
revit_run_skill skillId=company.project.pre-delivery projectId=1626
```

By default, the parameter QA and coordination QA tasks are **disabled** in this skill. Enable them via a project override and configure the required settings (folder path, rule set name, clash preset name).

---

## 6. Indexing and Searching Company Standards

Index your company standards documents:

```
standards_index_sources
```

Search for rules:

```
standards_search query="lehtede nimetamise reeglid" discipline=electrical
```

Get the full text of a specific chunk:

```
standards_get_document_chunk chunkId=... contextBefore=1 contextAfter=2
```

See [standards-lookup.md](standards-lookup.md) for setup instructions.

---

## 7. Exporting an HTML Dashboard

Any skill that produces issues will automatically export an HTML dashboard as its last task. You can also export manually from an existing issue report JSON:

```
revit_export_issues_html_dashboard reportJson="..." fileName="QA_1626_EP.html"
```

See [html-dashboard.md](html-dashboard.md) for details.

---

## 8. Proposing Master Skill Changes

If your project override works well and you want to propose it as the new company standard:

1. Compare the override to the master:

   ```
   revit_compare_skill_override_to_master skillId=... projectId=...
   revit_export_skill_override_diff_markdown skillId=... projectId=...
   ```

2. Create a proposal file:

   ```
   revit_propose_master_skill_update skillId=... projectId=... notes="Wider delivery folder extension list"
   ```

3. Send the `.skill.proposal.json` file from `%AppData%\RKTools\RevitMCP\SkillProposals\` to the skill administrator.

4. The administrator reviews the proposal and manually updates the master skill file in `%ProgramData%\RKTools\MCP\Skills\`.

**The `revit_propose_master_skill_update` tool never modifies company master files.**

---

## File Locations Reference

| Item | Path |
|------|------|
| Company master skills | `%ProgramData%\RKTools\MCP\Skills\` |
| Project skill overrides | `%AppData%\RKTools\RevitMCP\ProjectSkillOverrides\{projectId}\` |
| Skill proposals | `%AppData%\RKTools\RevitMCP\SkillProposals\` |
| Standards config | `%ProgramData%\RKTools\MCP\Config\StandardsSources.json` |
| Standards index | `%AppData%\RKTools\RevitMCP\StandardsIndex\` |
| Export output | `%USERPROFILE%\Documents\RKTools\RevitMCP\Exports\` |
| Startup log | `%LOCALAPPDATA%\RevitMCP_startup.log` |
