# View filters

The connector creates, edits and deletes view filters, adds them to views and view templates in Visibility/Graphics, and
copies them between views, templates and open projects (#134).

The main workflow: **an agent creates the filters and adds them to views or templates, and the user sets colours or
hiding later.** Setting graphics through the tools is available but never required.

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_view_filters` | read-only | The filters of the document: kind, categories, rules or members, and which views and templates use them |
| `revit_list_filterable_parameters` | read-only | The parameters a filter on the given categories can use, with the operators each takes. Without categories: the filterable categories |
| `revit_get_view_filters` | read-only | For a view or template: the filters in effect in order, enabled, visible, graphics, and the controlling template |
| `revit_preview_create_view_filters` / `revit_create_view_filters` | read-only / approval | Create rule or selection filters |
| `revit_preview_edit_view_filters` / `revit_edit_view_filters` | read-only / approval | Rename, change categories, rules or selection members |
| `revit_preview_delete_view_filters` / `revit_delete_view_filters` | read-only / **manual approval** | Delete filters; the preview lists every view and template using them |
| `revit_preview_apply_view_filters` / `revit_apply_view_filters` | read-only / approval | Add or remove filters on views and templates, set their order, optionally set their graphics |
| `revit_preview_copy_view_filters` / `revit_copy_view_filters` | read-only / approval | Copy filters and their settings between views, templates and open projects |

Every tool takes the standard `document` argument. A filter, view, template or category is referred to by **name** (exact,
case-insensitive) or **element id**; a category also by its `OST_` name. A name that matches two elements is reported as
ambiguous, never guessed.

## Workflow

1. `revit_list_filterable_parameters categories=[...]` to see what a filter on those categories can test.
2. `revit_preview_create_view_filters` with the specs; every problem comes back per filter.
3. `revit_create_view_filters` (approval). The result gives each `filterId`.
4. `revit_preview_apply_view_filters` to see where the change lands: a template and the views it affects, or the view.
5. `revit_apply_view_filters` (approval). With no graphics the filter is added with no overrides, ready for the user.

Each write is one transaction and one Undo, with a sub-transaction per item. `atomic=true` rejects the request when any item
is invalid and rolls everything back when Revit refuses any item. `dryRun` (previews, default `true`) also tries the writes
in a transaction that is rolled back, so the preview reports what Revit itself refuses.

## Filter specs

```json
{
  "filters": [
    {
      "name": "EN kaablid 1. korrus",
      "categories": ["Cable Trays", "OST_Conduit"],
      "rules": {
        "and": [
          { "parameter": "Comments", "operator": "contains", "value": "EN" },
          { "or": [
            { "parameter": "Reference Level", "operator": "equals", "value": "1. korrus" },
            { "parameter": "Height", "operator": "greaterOrEqual", "value": 2400 }
          ] }
        ]
      }
    },
    { "name": "Parandatavad", "kind": "selection", "elementIds": [987654, 987655] }
  ],
  "ifNameExists": "fail"
}
```

- `rules` — any nesting of `{"and": [...]}` and `{"or": [...]}`, as in the dialog, with rules as leaves. A bare array is an
  AND group. A group with one child means the child. No rules: the filter selects everything in its categories.
- `kind` — `rule` (default) or `selection` (a fixed set of `elementIds`; assumed when `elementIds` is given).
- `ifNameExists` — `fail` (default; code `filter_name_exists`) or `skip`.

### Rules

| Field | Meaning |
|---|---|
| `parameter` | Name, `BuiltInParameter` name, id or shared parameter GUID. Must be filterable for all the filter's categories |
| `operator` | See below; symbols (`=`, `!=`, `>`, `>=`, `<`, `<=`) and the dialog's words (`does not contain`) also work |
| `value` | Text, number, whole number, true/false, or for an element reference an id or a name (levels, phases, materials, types of the filter's categories) |
| `units` | Numbers: `project` (default; the project's display units, as in the dialog) or `internal` (Revit's internal units) |
| `tolerance` | Numbers: in `units`. Default `1e-6` internal units |
| `onlyCategory` | The rule applies to one of the filter's categories only (Revit accepts this directly under an OR group) |

| Parameter holds | Operators |
|---|---|
| Text | `equals`, `notEquals`, `greater`, `greaterOrEqual`, `less`, `lessOrEqual`, `contains`, `notContains`, `beginsWith`, `notBeginsWith`, `endsWith`, `notEndsWith`, `hasValue`, `hasNoValue` |
| Number | `equals`, `notEquals`, `greater`, `greaterOrEqual`, `less`, `lessOrEqual`, `hasValue`, `hasNoValue`, `associatedWithGlobalParameter`, `notAssociatedWithGlobalParameter` |
| Whole number, element reference | `equals`, `notEquals`, `greater`, `greaterOrEqual`, `less`, `lessOrEqual`, `hasValue`, `hasNoValue` |
| Yes/No | `equals`, `notEquals`, `hasValue`, `hasNoValue` |
| Any (by name) | `sharedParameterApplicable`: the element has a shared parameter of this name; no value |

Text rules ignore case in both Revit 2024 and 2026 (the API's case-sensitive overloads are gone in 2026, and are not used).

Revit has no data type lookup for a built-in parameter, so the tools read it from one instance and one type per category.
When a category has no elements and the parameter is a built-in number, its units are unknown: the rule gets code
`rule_units_unknown`; give the value with `"units": "internal"`.

What `revit_list_view_filters` and `revit_copy_view_filters` read back is the same shape, so it can be passed to a create or
edit call. Revit may return a rule set in a structurally different but equivalent tree after the Filters dialog has touched
it (its own documentation says so); it matches the same elements.

## Editing

```json
{ "edits": [
  { "filter": "EN kaablid 1. korrus", "name": "EN kaablid", "categories": ["Cable Trays"] },
  { "filter": 123456, "rules": { "parameter": "Mark", "operator": "hasValue" } },
  { "filter": "Vana", "clearRules": true },
  { "filter": "Parandatavad", "addElementIds": [1], "removeElementIds": [2] }
] }
```

Changing categories alone keeps the rules, but `ParameterFilterElement.SetCategories` silently drops rules whose parameter
no longer applies, so the preview reports `rule_parameter_not_applicable` first; pass new `rules` or `clearRules: true`
with the categories. Every view and template using a filter changes with it; the preview lists them.

## Applying to views and templates

```json
{
  "views": ["EN 1. korrus", "EN_Plaan"],
  "add": [
    "EN kaablid",
    { "filter": "Parandatavad", "visible": true, "enabled": true, "halftone": false, "transparency": 40,
      "projectionLines": { "color": "#FF8000", "weight": 3, "pattern": "solid" },
      "cutLines": { "color": [255, 128, 0] },
      "surfaceForeground": { "color": "#FF8000", "pattern": "<Solid fill>", "visible": true },
      "surfaceBackground": { "pattern": "none" },
      "cutForeground": { "pattern": "Diagonal up" },
      "cutBackground": { "visible": false } }
  ],
  "remove": ["Vana"],
  "order": ["Parandatavad", "EN kaablid"]
}
```

- `add` — a name or id is added with no overrides. An object sets what it names and leaves the rest. On a filter that is
  already applied, the settings update it (`clearGraphics: true` resets its graphics first); without settings it is skipped
  with code `filter_already_applied`.
- `pattern` — a name, an id, `solid` or `none` (back to no override). Fill patterns are looked up among drafting patterns
  first, then model patterns.
- `order` — these filters first, in this order; the others follow as they are. The order is the list in the dialog's Filters
  tab, top first, and it decides which override wins. Revit's API has no reorder call in 2024 or 2026, and `AddFilter` appends, so the tool reads every filter's
  settings, removes them all and adds them back in the new order. Same behaviour in both versions.

### Templates

- A template can be named directly.
- **A view whose filters are controlled by its template sends the change to the template.** The preview's `targets` names
  the template (`routedToTemplate: true` on the view) and lists every view it affects in `affectedViews`.
- A view whose template does not control filters takes the change itself.

"Controls" means the template's `GetTemplateParameterIds` has `VIS_GRAPHICS_FILTERS` and its
`GetNonControlledTemplateParameterIds` does not. `revit_get_view_filters` reads the same rule, and says `source: template`.

## Deleting

`revit_preview_delete_view_filters` lists for each filter `usage.views`, `usage.templates` and `usage.viewsThroughTemplates`.
`revit_delete_view_filters` always waits for manual approval in Revit, also in Direct Edit mode; the approval text names each
filter and how many views and templates use it. Undo restores them.

## Copying

```json
{
  "sourceDocument": "Projekt A",
  "sourceView": "EN_Plaan",
  "document": "Projekt B",
  "targetViews": ["EN_Plaan"],
  "ifExists": "reuse"
}
```

| Argument | Meaning |
|---|---|
| `document` | The target project (empty = active) |
| `sourceDocument` | The open project they come from (empty = the target). Never changed |
| `sourceView` | A view or template in the source: its filters in order, with enabled, visible and graphics |
| `filters` | Names or ids in the source; with `sourceView`, only these |
| `targetViews` | Views or templates in the target to add them to, routed as above. Empty: the filters only arrive in the project |
| `ifExists` | A filter of that name in the target: `reuse` (default; its own definition stays, `definitionDiffers` says when they differ), `overwrite`, `skip` (left alone and not added), `fail` |
| `copySettings` | Copy enabled, visible and graphics from `sourceView` (default `true`) |

Filters are matched by name. Categories and parameters are resolved in the target: a category by its id (built-in categories
have the same id everywhere), a built-in parameter by its id, a shared parameter by its GUID, a project parameter by its
name. An element reference in a rule (a level, a type) is matched by the element's name.

The preview's `unresolved` lists everything the target does not have, as `{filter, kind, name, code, reason, notCreated}`:

| `kind` | What happens |
|---|---|
| `category`, `parameter`, `value`, `rule` | The filter is **not created**, and is not added to any view (`filter_not_copied`) |
| `pattern` | The filter is created; that pattern is left out of its graphics |
| `selectionMembers` | A selection filter's elements cannot transfer between projects: the filter arrives empty, and its members are reported as not copied |

In one document `revit_copy_view_filters` copies a view's filters and settings to other views and templates.

## Status codes

Per item (`code`), and as the result's `status` when nothing can be done:

| Code | Meaning |
|---|---|
| `filter_name_exists`, `duplicate_name_in_request`, `invalid_filter_name` | The name is taken, given twice, or has a character Revit refuses (`\ : { } [ ] \| ; < > ? ` ~`) |
| `no_categories`, `category_not_found`, `category_not_filterable`, `category_ambiguous` | A rule filter's categories |
| `parameter_not_filterable`, `parameter_ambiguous`, `parameter_type_unknown` | The parameter is unknown, not common to the categories, matches two, or has no known type |
| `operator_unknown`, `operator_not_valid_for_type` | The reason lists what the parameter takes |
| `rule_value_missing`, `rule_value_invalid`, `rule_value_not_found`, `rule_value_ambiguous`, `rule_units_unknown` | The rule's value |
| `rule_not_acceptable`, `rule_parameter_not_applicable` | Revit's own checks (`ElementFilterIsAcceptableForParameterFilterElement`, `AllRuleParametersApplicable`) refuse the tree |
| `rule_not_supported` | A stored rule cannot be expressed as a spec (for example an inverted rule set), so it cannot be copied |
| `filter_not_found`, `filter_ambiguous`, `filter_kind_mismatch`, `filter_not_applied`, `filter_already_applied`, `filter_not_copied` | The filter |
| `view_not_found`, `view_ambiguous`, `view_cannot_have_filters`, `order_filter_not_applied` | The view or template |
| `element_not_found` | A selection member does not exist |
| `pattern_not_found`, `graphics_invalid` | A line or fill pattern is missing; a colour, weight or transparency is out of range |
| `source_document_not_found`, `nothing_to_do`, `invalid_spec` | The request itself |
| `revit_refused` | Revit refused the item in the (trial) run; `reason` holds its message |

## Out of scope

Category overrides in Visibility/Graphics, which get their own issue.
