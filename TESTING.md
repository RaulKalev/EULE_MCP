# Revit MCP Connector — Smoke Test Checklist

Run through this checklist after any code change to verify nothing is broken. All MCP responses should be valid JSON with `"success": true`.

> **Note:** Several tool families were consolidated into discriminator-based tools
> (e.g. `revit_delete_views`/`revit_delete_sheets` → `revit_delete` with
> `target`). Some tool names referenced below are the pre-consolidation names.
> See [docs/tool-consolidation.md](docs/tool-consolidation.md) for the full
> old → new mapping.

The checklist is split by area under [`docs/testing/`](docs/testing/). Section numbers are unchanged
(a few were used twice over time: 40–42 appear in both the coordination and the skills file).

| File | Sections |
| --- | --- |
| [Setup, connection and core tools](docs/testing/01-setup-and-core.md) | 1. Revit Startup<br>2. Claude Code Connection<br>3. Codex Connection<br>4. Read Tools<br>5. Query Tools<br>6. Excel Export<br>7. Error Handling<br>8. Parameter Discovery<br>9. Query Presets<br>10. Parameter Completeness Check<br>11. View/Sheet/Schedule Exports<br>12. Selection Tools<br>13. Write Tool (Set Parameter) |
| [Electrical circuits](docs/testing/02-electrical.md) | 14. Electrical Circuit Discovery Tools<br>15. Electrical Circuit Modification Tools<br>16. Approval / Reject / Undo Tests<br>17. Direct Edit Safety Tests<br>18. Electrical QA and Inspection Tools (Phase 3 + 5)<br>19. Circuit Parameter Completeness, Selection, and Export Tools<br>20. Load Summary, Panel Utilization, Numbering, and Bulk Tools<br>21. Electrical Dashboard Tools<br>22. Circuit Route and Length Estimation Tools<br>23. Voltage-Drop Input Export and Precheck<br>24. Fire Alarm / ATS Preset Tools<br>25. Cable Resistance Profile Tools |
| [Views and sheets](docs/testing/03-views-and-sheets.md) | 26. View/Sheet Discovery Tools<br>27. View/Sheet Preview Tools<br>28. View/Sheet Write Tools (Requires Approval)<br>29. View/Sheet/Element Destructive Delete Tools |
| [Tool coverage matrix, result template and test model](docs/testing/04-coverage-and-test-setup.md) | 30. Full MCP Tool Coverage Matrix<br>Phase 1 New Tool Smoke Tests<br>31. Agent Test Run Result Template<br>32. Recommended Test Model Setup<br>33. Special Notes / Deferred Features |
| [Coordination and clash detection](docs/testing/05-coordination-clash.md) | 34. Coordination / Clash Detection Tools — Overview<br>35. Coordination Discovery Tools<br>36. Coordination Detection Tools<br>37. Coordination Preset Tools<br>38. Coordination Reporting Tools<br>39. Coordination Review Tools<br>40. Coordination Unit Tests<br>41. Coordination Tool Safety Notes and Known Limitations<br>42. Hard Clash False Positive Regression Tests |
| [Parameter QA and IFC Space to Room](docs/testing/06-parameter-qa-and-ifc.md) | 43. Parameter QA Rule Sets<br>44. IFC Space to Revit Room — Full Pipeline (Phases 1–4) |
| [Skill builder, dimensions, linked selection, storage, family types](docs/testing/07-skills-dimensions-links.md) | 40. Skill Builder — Guided Skill Creation (issue #20)<br>41. Dimension Placement Tools (issue #21)<br>42. Linked Model Selection Tools (issue #22)<br>45. Extensible Storage Queries (issue #29)<br>46. Family Types — Duplicate, Rename, Edit (issue #31) |
| [Alignment, placing from DWG, moving elements](docs/testing/08-placement-and-alignment.md) | 47. Element Alignment Against Surfaces (issue #33)<br>48. Placing Items From a DWG (issue #32)<br>49. Lining Elements Up In a View (issue #38)<br>50. Moving Elements To Exact Coordinates |
| [Model graph and Project Village](docs/testing/09-graph-and-village.md) | 51. Model Graph (routing layer)<br>52. Project Village (read-only activity visualizer) |
| [Cable types, room devices and fire alarm rules](docs/testing/10-room-devices.md) | 53. Cable Types (issue #51)<br>54. Room Geometry and Device Placement (issue #52)<br>55. Adjusting and Auditing Devices (issue #52)<br>56. Fire Alarm Rules |
| [Text notes, annotation checks, view reads, filled regions, view filters](docs/testing/11-annotation-and-views.md) | 57. Editing Text Note Content (issue #85)<br>58. Instance Pin, Delete Approvals, Annotation and Sheet Checks (issues #137-#143)<br>59. View Element Reads (issue #136)<br>60. Filled Regions (issue #135)<br>61. View Filters (issue #134) |
