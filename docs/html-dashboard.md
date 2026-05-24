# HTML Issue Dashboard

The HTML dashboard exporter converts an `IssueReportDto` (produced by any QA tool) into a standalone offline HTML file suitable for review, sharing with project managers, or archiving.

---

## Tool

### `revit_export_issues_html_dashboard`

Input:

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `reportJson` | string | ✓ | Full `IssueReportDto` serialised as JSON |
| `fileName` | string | — | Output file name (without path). Auto-generated if omitted. |
| `includeEmbeddedJson` | bool | — | Embed raw JSON in the HTML for re-import. Default `true`. |

Output:

```json
{
  "filePath": "C:\\Users\\...\\Documents\\RKTools\\RevitMCP\\Exports\\QA_Dashboard_1626_20260524.html",
  "totalIssues": 42,
  "criticalCount": 1,
  "errorCount": 5,
  "warningCount": 36
}
```

---

## Dashboard Features

- **Header** — report title, model name, created date, run ID
- **Severity summary cards** — Critical / Error / Warning / Info counts
- **Filter controls** — filter by severity, status, category, discipline, and free-text search
- **Sortable issue table** — click any column header to sort ascending/descending
- **Expandable rows** — click any row to see full details including suggested fix and element ID
- **Embedded JSON** — collapsible raw JSON block at the bottom for re-import or manual inspection

---

## Output Location

Dashboards are saved to:

```
%USERPROFILE%\Documents\RKTools\RevitMCP\Exports\
```

The file name is auto-generated as `{ToolName}_{Date}_{RunId}.html` unless `fileName` is specified.

---

## Offline Support

The HTML file is fully self-contained — no CDN links, no external fonts. It works in Edge, Chrome, and Firefox without an internet connection. You can email it or commit it to a project folder.

---

## Typical Workflow

1. Run any QA skill or issue-report tool (e.g. delivery check, clash detection, parameter QA).
2. The skill automatically calls `common.export.html-dashboard` as a final task and saves the file.
3. Open the HTML file in a browser and share it with stakeholders.

You can also export manually by passing an existing `IssueReportDto` JSON:

```
revit_export_issues_html_dashboard reportJson="..." fileName="QA_1626_EP.html"
```
