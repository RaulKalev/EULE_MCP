# Tagging elements of linked models

`revit_place_tags` and `revit_preview_place_tags` tag elements of a linked Revit or IFC model as well as host
elements (#151). Linked targets go through the same pipeline as host elements: tag type by category, direction and
anchor, leaders, rotation, skipping elements already tagged, and collision avoidance. The tag is placed in the host
view and points at the element through its link instance, as Revit's own Tag by Category does.

## Choosing linked targets

| Arguments | Targets |
|---|---|
| `linkInstanceId` + `linkedElementIds` | Those elements of that link. The ids are in the **linked** document, as `revit_query_linked_elements` returns them |
| `linkedElements: ["link:<instance>:<id>", …]` | Linked nodes of the model graph (`revit_graph_query` returns linked ids in this form), from one or several links |
| `linkedElements: [{linkInstanceId, elementId}, …]` | The same as objects. `linkInstanceId` falls back to the top-level one |
| `useSelection=true` | Host elements selected in Revit, plus linked elements picked with Tab inside a link |
| `tagAllInView=true` + `categoryId` + `linkInstanceId` | Every element of the category in that link that the view shows (Revit's own visibility of linked elements in the host view) |

Host targets (`elementIds`, `useSelection`, `tagAllInView` without `linkInstanceId`) can be combined with linked
ones in one call. `categoryId` also filters explicit linked ids. Up to 2000 linked elements per call.

`linkInstanceId` is the `RevitLinkInstance` id in the host. `revit_list_open_documents` lists a document's links, and
`revit_list_clashable_links` lists them with their instances. An IFC link counts as a Revit link. A DWG link has no
taggable elements, and an unloaded link is reported.

## What is different from host elements

- **Geometry.** The anchor, the minimum offset and the element's rotation are computed from the element's box and
  location, moved into host coordinates by the link's transform, so a moved or rotated link tags in the right place.
- **Already tagged.** `skipAlreadyTagged` (default true) skips an element that a tag of the same tag category in the
  view already points at through the same link instance.
- **Tag type.** Automatic tag type resolution uses the linked element's category, so the host must have a tag family
  of that category loaded. `tagTypeId` / `tagFamilyName` work as usual.
- **No SmartTags marker.** Tags through a link carry no managed-tag marker, because the marker stores a host element
  id. `revit_find_managed_tags` and `revit_retag` therefore do not list or move them. Move them with the alignment
  tools, or delete and tag again.
- **Result items.** Each item has `elementId` (the id in the linked document), `linkInstanceId` and `linkName`. A
  host item has `linkInstanceId: null`.

## Example

Tag every electrical fixture of the architect's IFC link that the current plan shows:

```json
{ "tool": "revit_preview_place_tags", "arguments": {
    "tagAllInView": true,
    "categoryId": -2001060,
    "linkInstanceId": 123456,
    "direction": "Right",
    "addLeader": true
} }
```

Then call `revit_place_tags` with the same arguments (approval, one undo step).

## Revit test checklist

1. Tag three elements of a loaded RVT link with `linkInstanceId` + `linkedElementIds`. The tags show the linked
   elements' values, and selecting a tag highlights the linked element.
2. The same with `linkedElements` as graph ids, and with linked elements Tab-selected and `useSelection=true`.
3. Use `tagAllInView` with `linkInstanceId` and `categoryId`. Only elements the view shows are tagged, and running it
   again skips them all (already tagged).
4. With the link moved and rotated, the tags sit next to their elements.
5. Tag an IFC link's elements the same way.
6. An unloaded link, a DWG link id and a wrong element id give clear messages, and nothing is changed.
7. Run the same calls in Revit 2024.
