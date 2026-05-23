# Clash Preset Hang Fix Task

## Goal

Investigate and fix the Revit hang triggered by `revit_run_clash_preset` in the live `EULE_MCP` connector.

This is not a generic cleanup task. Focus on the concrete hang path observed in Revit 2026 with the live model:

- Model: `2312_Käo_EN_2026_WIP`
- Preset: `Electrical vs HVAC`
- Failure mode:
  - MCP client times out after ~30s
  - Revit continues running the preset work
  - Revit UI becomes unresponsive / appears frozen
  - Pipe later reports `Pipe is broken`

## Context

The issue was reproduced from a live Codex session.

Observed sequence:

1. `revit_detect_hard_clashes` succeeded.
2. `revit_get_next_clash` succeeded.
3. `revit_run_clash_preset` timed out.
4. Subsequent calls timed out.
5. Revit had to be restarted manually.

The key log file is:

- `C:\Users\mibil\AppData\Roaming\RKTools\RevitMCP\Logs\2026-05-23.jsonl`

The last-run cache written before the freeze was:

- `C:\Users\mibil\AppData\Roaming\RKTools\RevitMCP\LastClashRun.json`

## Strong Findings Already Confirmed

### 1. Preset execution ignores cancellation

`RevitMCP.Addin\Tools\RunClashPresetTool.cs`

- The tool loops through all preset rules synchronously.
- It does not check `CancellationToken` inside the rule loop.
- It calls detectors that also do not support cancellation.
- If the bridge/client times out, Revit may still be executing detector work.

Relevant file:

- `RevitMCP.Addin\Tools\RunClashPresetTool.cs`

Important area:

- rule loop
- candidate collection
- detector calls

### 2. Imported geometry is being injected into every rule candidate set

`RevitMCP.Addin\Coordination\Clash\Services\ClashCandidateCollector.cs`

The collector currently adds all `ImportInstance` elements whenever `includeImportedGeometry=true`, regardless of the requested categories.

That means a rule like:

- `Cable Trays vs Ducts`

can end up using:

- source = imported DWG/IFC geometry
- target = ducts + imported DWG/IFC geometry

instead of actual cable trays.

This was confirmed live after restart:

- `Cable Trays vs Ducts` candidate query returned:
  - source = `24 ImportedGeometry`
  - target = `24 ImportedGeometry + 2031 Ducts`
- `Conduits vs Ducts` returned the same source problem
- `Conduits vs Pipes` also used imported geometry as source

So the `Electrical vs HVAC` preset is currently not behaving like its name suggests in this model.

### 3. Hard clash fallback is expensive and too permissive for imports

`RevitMCP.Addin\Coordination\Clash\Services\HardClashDetector.cs`

- Bounding boxes are checked first, which is fine.
- Then solids are extracted per pair.
- If a usable solid cannot be extracted, the code falls back to reporting a clash from bounding-box overlap.
- Imported geometry frequently has no usable solid, so the detector can generate a large amount of conservative work and false positives.

This is especially risky when imported geometry has already been added broadly to source/target sets by the collector.

## Files To Inspect First

- `RevitMCP.Addin\Tools\RunClashPresetTool.cs`
- `RevitMCP.Addin\Coordination\Clash\Services\ClashCandidateCollector.cs`
- `RevitMCP.Addin\Coordination\Clash\Services\HardClashDetector.cs`
- `RevitMCP.Addin\Coordination\Clash\Services\ClearanceClashDetector.cs`
- `RevitMCP.Addin\Coordination\Clash\Services\ClashPresetService.cs`

Optional supporting files:

- `RevitMCP.Addin\Tools\GetClashCandidatesTool.cs`
- `RevitMCP.Addin\Tools\DetectHardClashesTool.cs`
- `RevitMCP.Addin\Tools\DetectClearanceClashesTool.cs`

## Constraints

Follow repository rules from `AGENTS.md`, especially:

- Preserve Revit API safety
- No speculative architecture changes
- Keep compatibility in mind for Revit 2021+
- No blocking UI behavior
- Keep changes production-grade and maintainable

Do not “fix” this by only increasing timeouts. The root issue is inside the preset execution path.

## Required Outcomes

Implement a fix that addresses the hang risk and the bad candidate expansion.

### A. Cancellation / early exit

Make preset execution interruptible.

Minimum expectation:

- `RunClashPresetTool` checks cancellation between rules
- detectors can stop early when cancellation is requested
- the tool should return a clean failure or partial-result outcome instead of leaving Revit grinding indefinitely after client timeout

If partial results are returned, that behavior must be explicit and consistent.

### B. Imported geometry scoping

Fix candidate collection so imported geometry is not blindly injected into every rule.

At minimum, imported geometry should not replace or pollute category-specific source sets such as:

- `Cable Trays`
- `Conduits`
- `Ducts`
- `Pipes`

The agent should decide one of these approaches and justify it in code/comments if needed:

1. Only include imported geometry when the requested category explicitly maps to imported geometry
2. Add a separate explicit pseudo-category for imported geometry
3. Restrict imported geometry participation to rules that intentionally opt into it

The current behavior is too broad and is considered incorrect for this task.

### C. Better preset robustness

If a rule resolves to:

- zero real source candidates, or
- zero real target candidates

the preset runner should skip it cleanly and record a warning rather than entering expensive detector work.

### D. Better observability

Add lightweight logging or warnings so future failures are diagnosable.

Useful examples:

- per-rule start/end
- source/target candidate counts
- whether imported geometry was included
- whether a rule was skipped
- whether cancellation occurred

Do not add noisy or user-hostile logging. Keep it targeted.

## Non-Goals

- Do not redesign the whole clash subsystem
- Do not change write-tool approval behavior
- Do not add unrelated UI features
- Do not rewrite the pipe layer unless strictly required

## Suggested Acceptance Criteria

The fix is acceptable if all of the following are true:

1. `revit_run_clash_preset` no longer causes Revit to hang when the client disconnects or times out.
2. `Electrical vs HVAC` no longer uses imported geometry as the only source for `Cable Trays` / `Conduits` rules in this model.
3. Rules with no meaningful candidates are skipped quickly.
4. The code path is testable or at least produces clear runtime diagnostics.
5. Existing ad-hoc clash tools still behave correctly.

## Recommended Verification

### Runtime checks

After implementing:

1. Run `revit_get_connection_status`
2. Run candidate checks for:
   - `Cable Trays` vs `Ducts`
   - `Conduits` vs `Ducts`
   - `Conduits` vs `Pipes`
3. Confirm source candidates are not just `ImportedGeometry`
4. Run `revit_run_clash_preset` with a conservative limit if possible
5. Confirm Revit remains responsive

### Code quality checks

- Build the affected projects
- Run any existing tests that cover clash behavior
- Add tests if the repo has a suitable place for them and the logic can be unit-tested without Revit

## Deliverable

Return:

1. A short summary of root cause
2. Files changed
3. Verification performed
4. Any residual risk or follow-up work

If you cannot fully fix the hang safely, prefer a smaller defensive fix that:

- prevents the freeze path
- narrows imported-geometry participation
- leaves clear notes for the next iteration

