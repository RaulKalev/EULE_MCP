# Model families

Authoring and editing loadable 3D families in the Family Editor (#168). It extends the Detail Item workflow of
[family-creation.md](family-creation.md): the same previews, approvals, overwrite handling and save/load conventions.

The tools are in the `familyeditor` group (`--tool-groups familyeditor`, or find them with `revit_tools_search`).
Every name contains `model_family`.

| Feature | Doc | Issue |
|---|---|---|
| Create, inspect, settings, save and load | [model-family-documents.md](model-family-documents.md) | #169 |
| Reference planes, dimensions, constraints | [model-family-references.md](model-family-references.md) | #170 |
| Solid and void forms | [model-family-forms.md](model-family-forms.md) | #171 |
| Parameters, types, formulas, associations | [model-family-parameters.md](model-family-parameters.md) | #172 |
| Materials | [model-family-materials.md](model-family-materials.md) | #173 |
| MEP connectors | [model-family-connectors.md](model-family-connectors.md) | #174 |
| Nested components, symbolic graphics, visibility | [model-family-components.md](model-family-components.md) | #175 |
| Flex tests and placed-instance checks | [model-family-flex.md](model-family-flex.md) | #176 |
| Forms from CAD profiles or family curves | [model-family-profiles.md](model-family-profiles.md) | #177 |

[model-family-acceptance.md](model-family-acceptance.md) is the acceptance runbook of the tracker: one device built
end to end, to be run in Revit 2024 and 2026. Until it has been run, treat every behaviour listed under "needs live
verification" in the feature docs as unconfirmed.

## The `target` argument

Every tool takes one `target` object that says which family document it works on and what happens to the result.

| Target | Meaning | After a successful edit |
|---|---|---|
| `{"family": "<name or id>"}` | A family loaded in the project (`document`, default the active one). Edited as an independent copy (`Document.EditFamily`). | Loaded back into the project as one undo step. The project's parameter values are kept unless `overwriteParameterValues`. `savePath` also saves a copy. |
| `{"path": "C:\\…\\Device.rfa"}` | A family file that is not open in Revit. Opened in the background. | Saved back with `overwrite: true`, or to `savePath`. `load: true` then loads it into the project. |
| `{"familyDocument": "<title>"}` or `"active"` | A family document open in Revit (the user's Family Editor window). Edited in place as one undo group. | Left open and unsaved, unless `save: true` (its own file) or `savePath`. `load: true` loads it into the project. |
| `{"savePath": "…", "familyName"?, "templatePath"?}` | Create only: a new family from a template (default `Metric Generic Model.rft`). | Saved to `savePath`; `load: true` loads it. |

Rules that hold for every tool:

- A **preview** (`revit_preview_…`) runs the same edit inside a transaction group and rolls it back, so it reports
  what Revit would really do (including a constraint Revit refuses) and changes nothing. It returns `ready` and
  `problems`.
- A **failed edit changes nothing**: every step is one transaction inside one group; when Revit refuses a step the
  whole group is rolled back, the file is not written and the loaded family is not reloaded. The result names the
  failed step and `revitFailures` (message, severity, element ids).
- A file is saved to a temporary file and swapped into place, so a failure never leaves a half-written family.
- A family document the user has open is never closed, and never saved unless the target asks for it. A loaded family
  that is open in the Family Editor is refused with the title to pass as `familyDocument`.
- Paths pass the file access policy (`FileAccessPolicy.json`).
- Identifiers are element ids of the family document. They stay valid across save and reopen, so ids from
  `revit_get_model_family` can be used in later calls. Reference planes, parameters, views and subcategories are also
  accepted by name.
- Lengths are millimetres, angles degrees, points `[x, y, z]` from the family origin.

## Result envelope

```json
{
  "family": "ELV_Device", "target": "path", "path": "C:\\fam\\ELV_Device.rfa", "document": "Projekt.rvt",
  "ready": true, "problems": [],
  "result": { },
  "revitFailures": [ { "step": "Dimensions", "severity": "warning", "message": "…", "elementIds": [1234] } ],
  "savedTo": "C:\\fam\\ELV_Device.rfa", "replacedFile": true,
  "load": { "status": "reloaded", "familyId": 5678, "types": [ { "id": 5680, "name": "Type 1" } ] }
}
```

`ready`, `problems`, `outcome`, `savePath` and `willReplaceFile` appear in a preview; `savedTo`, `replacedFile` and
`load` after an edit. `result` is the tool's own data.

## For developers: adding a feature

The shared layer is `RevitMCP.Addin/Tools/ModelFamilies` (Revit) and `RevitMCP.Addin/ModelFamilies` (pure, compiled
into the tests by a wildcard, so **nothing in that folder may use the Revit API**).

- `ModelFamilySession.Edit(uiapp, request, apply, what, body, cancellationToken)` opens the target, runs `body` in a
  transaction group, and saves/loads/closes. `ModelFamilySession.Read(...)` only reads; `ModelFamilySession.Trial(...)`
  lets the body change the family and always rolls everything back (flex tests). A tool is a thin class:

  ```csharp
  public sealed class EditModelFamilyXTool : IRevitMcpTool, IApprovalSummaryTool
  {
      public string Name => "revit_edit_model_family_x";
      public ToolPermission Permission => ToolPermission.RequiresApproval;   // the preview is ReadOnly
      public ToolCategory Category => ToolCategory.FamilyCreation;
      public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken ct)
          => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: true, "edit x", XExecutor.Run, ct));
      public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
          => $"Edit x of {ModelFamilySession.DescribeTarget(request)}.";
  }
  ```

- The body gets a `ModelFamilyContext`: `FamilyDoc`, `Project` (may be null), `Args`, `Apply`, `Problems`, `Warnings`,
  `Failures`, `Summary`. It validates into `Problems` (never throws for a bad argument), changes the document only
  through `context.Transact(step, action)` (throws `ModelFamilyStepException`, which rolls the whole edit back) or
  `context.TryTransact` (a refused item is reported and the rest goes on), and returns the tool's `result`. The body
  runs for the preview too: do the real work in both, the session rolls a preview back.
- `ModelFamilyJson` reads arguments (millimetres, `CheckKeys` for unknown keys, `Suggest` for close names).
  `ModelFamilyLookup` resolves reference planes, parameters, views, work planes and sketch planes.
  `ModelFamilyAssociations` associates an element parameter with a family parameter and reads it back.
- `ModelFamilyInspect.AddSection(name, summary, read)` adds a section to `revit_get_model_family`; a feature
  registers the sections it owns from its `Register` method.
- Each feature owns one registration class (`Tools/ModelFamilies/ModelFamily<Feature>Tools.cs`), one bridge partial
  (`RevitMCP.Bridge/RevitMcpTools.ModelFamily<Feature>.cs`, using `ModelFamilyArgs(target, document)` and the shared
  description constants), one doc and its tests. Bridge names contain `model_family`, which puts them in the
  `familyeditor` group; `revit_edit_model_family…` is classified as creating for plans.
