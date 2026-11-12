# Reading the elements of a view

`revit_count_elements`, `revit_get_elements_info` and `revit_find_elements_by_parameter` take a `view` argument
(issue #136). With it they answer for what one view or sheet shows, and each element also says where it is and what it
belongs to. `filters`, `category`, `elementIds`, `useSelection`, paging, `compact`, `summaryOnly` and `document` work as
they do without it. A call without `view` runs the old code and returns the same payload as before.

| Argument | Tools | Meaning |
|---|---|---|
| `view` | all three | `active`, a view id or name, or a sheet id, number or name |
| `scope` | all three | `model`, `annotation` or `all` (default) |
| `includeRoom` | the two list reads | adds `Room` to model elements |

## Choosing the view

A reference is matched in this order, ignoring case:

1. `active`: the active view. A sheet that is active counts as a sheet.
2. A number that is the id of a view or sheet. If it is also another view's name, the id is used and a warning says so.
3. The whole text as a view name, sheet name, sheet number or `number - name`. A partial name is never picked.

| Status | When | `data` |
|---|---|---|
| `view_not_found` | Nothing matches | `candidates`: the closest names |
| `view_ambiguous` | Several match (a floor plan and a ceiling plan may share a name) | `candidates` with ids; pass an id |
| `view_is_template` | The target is a view template | `view` |
| `view_not_collectable` | A schedule, report, browser or rendering view, or a placeholder sheet | `view` |
| `active_document_required` | `active` with `document=` naming a document that is not active | |
| `validation_failed` | `scope` is not one of the three | |

These are failures with `success: false`. A view template with the same name as a real view is ignored in favour of the view.

## What comes back

Revit's view collector (`FilteredElementCollector(doc, viewId)`) is the starting point. Then, in this order, the `excluded`
block of the response counts what is left out and why.

### Not elements of the view

| `excluded` key | Rule |
|---|---|
| `viewElement` | The view itself, and the view's own crop element (`OST_Viewers` with the view's name). Section and callout markers of other views stay. |
| `helper` | `Sketch` and `SketchPlane`; any category with `Sketch` in its name; reference planes, reference lines and reference points; weak dimensions, constraints and lock controls; crop and section boxes, annotation crops, cameras, render regions; internal object styles (`OST_IOS*`) except groups and arrays |
| `noCategory` | Elements with no category |
| `sketchMember` | Lines of a sketch, recognised through `Sketch.GetAllElements()` for sketches the view lists and sketches behind its filled regions. They look like ordinary detail lines otherwise. |
| `internalCategory` | `CategoryType.Internal` |

Reference planes are left out because they are construction helpers: they are in the view, but rarely what the agent
is after. Read them with `elementIds` and without `view` when they matter.

### Not visible

| `excluded` key | Check |
|---|---|
| `hiddenInView` | `Element.IsHidden(view)` |
| `categoryHidden` | `View.GetCategoryHidden` for the category and its parents, then `AreModelCategoriesHidden`, `AreAnnotationCategoriesHidden` and `AreAnalyticalModelCategoriesHidden` |
| `filterHidden` | An enabled view filter set to invisible: a selection filter that holds the element, or a rule filter for its category whose `ElementFilter` passes it |
| `temporarilyHidden` | `IsElementVisibleInTemporaryViewMode(TemporaryHideIsolate, id)` while temporary hide/isolate is on; a warning says so |
| `outsideCrop` | Model elements whose bounding box misses the crop shape (`GetCropShape`, else the crop box) in the plane of the view |

Every check fails open: when Revit will not answer, the element stays. The crop check is conservative for the same
reason, so an element whose box overlaps the crop stays even if its geometry does not. Perspective views and
view-specific elements skip it. The checks are needed because the Revit API documents that the collector may pass
elements outside a crop region and that `IsHidden` and `GetCategoryHidden` each cover only part of the story.

### Scope

`annotation` is everything drawn in one view (`Element.ViewSpecific`, so detail lines, filled regions, detail
components and title blocks too, whose category type is Model) plus annotation categories (tags, text, dimensions,
levels and grids). `model` is the rest. `scope` is applied before the visibility checks and does not count as excluded.

### Linked models

Elements inside links are not returned; use `revit_query_linked_elements`. The link instance is a host element and is listed.

## Fields per element

Beside the usual fields (`ElementId`, `Category`, `Family`, `Type`, `Name`, `Level`, `Parameters`), these are added and left
out when there is nothing to say:

| Field | Content |
|---|---|
| `Family` | Now also for system types (`ElementType.FamilyName`: "Text", "Filled region") |
| `Location` | `Kind` `point`, `text`, `tagHead` or `viewport` with `Point` (mm) and `RotationDeg` where it applies; `Kind` `curve` with `Start`, `End`, `LengthMm`, `CurveType` |
| `BoundingBox` | `get_BoundingBox(view)`, else the model box: `{min, max, sizeMm, centerMm}` in mm |
| `Host` | `{Id, Category, Name}` of a hosted family instance. On a face of a linked element this is the link instance. |
| `TaggedElementIds` | For a tag: the local elements it points at |
| `OwnerView` | `{Id, Name}` for a view-specific element |
| `LineStyle`, `PartOfGroup`, `GroupId` | Lines always have `PartOfGroup`; other elements only when true. `LineStyle` is also kept with `compact=true`. |
| `Tags` | The tags on the element in this view. `includeTags=true` lists its tags in all views and keeps the empty list. |
| `Room` | With `includeRoom=true`: `{Id, Number, Name}` by the view's phase. Not for view-specific elements. |

So a detail line has its start and end points, line style and group flag; a text note, filled region or detail component
has its bounding box and type.

Coordinates are host model mm for elements of a view. For elements on a sheet they are sheet coordinates in mm.

Parameters come on request with `view`: `includeInstanceParameters`, `includeTypeParameters`, or by naming them
(`parameterNames` / `returnParameters`). Filters always see instance parameters.

`compact=true` keeps all of these fields. `summaryOnly=true` returns counts only, with `viewports` for a sheet.

## Sheets

A sheet reads as its own elements (`sheetElements`: title block, viewports, text, schedule instances) plus one entry per
viewport in `viewports`, with `viewportId`, `viewId`, `viewName`, `viewType`, `total`, `returned` and `elements`. There is no
top-level `elements`. A viewport whose view cannot be listed has `problem` and a warning, and the rest still reads.

Paging walks the sheet's elements and then each viewport as one list, so `totalMatched` is the sum and an element shown in
two viewports counts twice. `revit_count_elements` gives `groups` for the whole sheet, and `count` and `groups` in
`sheetElements` and in each viewport.

## Large views

A list read of 200 or more elements with no `category`, `filters`, `elementIds` or `useSelection` does not read them. It
answers `success: true`, `status: view_large_read`, with `categories` (counts), `viewports` for a sheet, and `next`: a call per
largest category and a paged call. Passing `pageSize` or `page` is how to read everything. `summaryOnly=true` also goes through.

`revit_count_elements` is never guarded and never reads parameters. The collector, however, may make Revit rebuild the
view's geometry the first time it is asked about a view, which the API documents as a possible significant slowdown. A
sheet does that for each viewport. A read stops at 100,000 listed elements, and filters stop at 20,000 evaluated; both warn.

A call with `view` is never answered `graph_first` and gets no routing hint: the graph does not know what a view shows.

## To be confirmed in Revit

This was built without a running Revit. The code assumes the following; each item says what to check and what to change
if it does not hold. Test steps are in [testing/11-annotation-and-views.md](testing/11-annotation-and-views.md).

1. **Crop region.** Assumed: the collector may pass elements outside the crop (the API says so), so `outsideCrop` removes those
   whose box misses the crop. Check that a device outside a cropped plan is gone from the read and counted, including for a
   non-rectangular and a split crop. Check `GetCropShape` and `CropBox` agree in the crop's coordinates; a wrong transform
   would remove visible elements.
2. **Annotation crop.** Assumed: the collector leaves out annotation hidden by the annotation crop. It is not post-filtered,
   since the API gives no shape for it. Check a text note outside the annotation crop.
3. **View filters.** Assumed: the collector does not reliably remove elements hidden by a filter, so `filterHidden` does. Check a
   rule filter and a selection filter, and with the filter coming from a view template: the code assumes `View.GetFilters()`,
   `GetFilterVisibility` and `GetIsFilterEnabled` report the template's values.
4. **Category hide.** Assumed: `GetCategoryHidden` reports the effective value when a template controls Visibility/Graphics.
5. **Temporary hide.** Assumed: `IsElementVisibleInTemporaryViewMode(TemporaryHideIsolate, id)` is false for elements hidden by
   temporary hide or outside an isolate. Reveal Hidden Elements mode is not handled: check what the collector returns then.
6. **Phase and design options.** Assumed: the collector respects the view's phase filter and design option. Not post-filtered.
7. **View range and far clip.** Assumed: the collector handles them. Not post-filtered.
8. **Section boxes.** Assumed: the collector handles a 3D view's section box. Not post-filtered.
9. **Sketch helpers.** Assumed: `OfClass(typeof(Sketch))` works with a view-scoped collector, and that `GetDependentElements` finds a
   filled region's sketch. Check a drafting view with filled regions: their boundary lines must not be returned as detail lines.
10. **Own crop element.** Assumed: it has category `OST_Viewers` and the view's name. If a section marker in its own view
    carries the same name it goes too, which is correct.
11. **Category types.** Assumed: levels and grids have `CategoryType.Annotation`, and a title block is `ViewSpecific`. They decide `scope`.
12. **View kinds.** Assumed: legends and drafting views can be listed; schedules return `view_not_collectable` by `ViewType` before
    any collector runs. `FilteredElementCollector.IsViewValidForElementIteration` catches the rest and gives the same status.
13. **Sheet coordinates.** Assumed: `get_BoundingBox(sheet)` and `Location` on a sheet element are in sheet coordinates.
14. **Performance.** Check a large 3D view and a sheet with many viewports stay within the request timeout (30 s by default).
