# Model pages

A model page is HTML an AI agent generated from a model — a check report, an overview table, a list
of findings — published back to the user's browser by the add-in. On the page the user selects and
zooms to elements in Revit, sees current parameter values and edits them. The agent is not involved
in any of that, so going through a list and fixing values costs no tokens.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_publish_page` | Read-only | Stores the HTML (`html`, or `htmlPath` to an `.html` file, max 2 MB), binds it to the active model, and opens `http://127.0.0.1:<port>/p/<pageId>`. `pageId` replaces an earlier page and keeps its link; `open=false` only returns the link |
| `revit_list_pages` | Read-only | Published pages with title, model, whether they belong to the active model, and url. `removePageId` deletes one |
| `revit_page_set_values` | Requires approval | Internal: the write a page sends when the user edits a value. Registered in the add-in only, not advertised by the bridge |

The first two are in the `pages` tool group; the server instructions point agents at
`revit_publish_page`. "Read-only" there means the model: the tool writes the page to
`%LOCALAPPDATA%\RevitMCP\Pages`.

## Markup

The add-in adds a helper script and a default stylesheet to every page it serves. The agent only
marks up the HTML:

| Attribute | On | Effect |
|---|---|---|
| `data-revit-id="123456"` | a row, card or any element. Several ids: `"123,456"` | A click selects those elements in Revit and zooms to them |
| `data-revit-param="Comments"` | a cell inside an element with `data-revit-id` | The text is replaced by the element's current value on load and on **Refresh**. A value that differs from what the agent wrote is highlighted, with the original in the tooltip. Instance parameters first, then type parameters, by exact name. Double click edits it when it can be written (below) |
| `data-revit-readonly` | a cell, row, table or any ancestor | No editing inside it |
| `data-revit-select-all` | a button | Selects every visible element on the page; `data-revit-select-all="#table1"` limits it to that selector. Rows hidden by the page's own filter are left out |

A row whose element no longer exists is struck through; a cell naming a parameter the element does
not have is shown in italics.

The page's own scripts can use the same functions:

```js
revit.select([123, 456], { zoom: true })       // → { ok, selected, missing, zoomed }
revit.values([123, 456], ['Comments', 'Mark'])  // → { ok, values: { "123": { Comments: "…" } }, editable, missing }
revit.set([{ id: 123, parameter: 'Comments', value: 'PM' }])  // → { ok, changes: [{ id, parameter, ok, value, reason }] }
revit.refresh()                                // re-read every data-revit-param cell (call after adding rows)
revit.status()                                 // → { ok, activeDocument } or { ok: false, status, message }
```

Helper texts are Estonian when the page says `<html lang="et">`, English otherwise.

## Editing values

Double click on a `data-revit-param` cell opens it; Enter or leaving the field saves, Esc cancels.
A cell is editable when the parameter is a **writable instance parameter** holding text, a number or
yes/no. Type parameters are shown but never editable: one table cell would change every element of
the type.

The write is not done by the page listener. It is handed to the connector's dispatcher as
`revit_page_set_values`, so it is treated like an agent's write:

- **Approval.** Unless the user has switched Direct Edit on, the change waits on the Pending tab of
  the RevitMCP window. The cell shows a waiting state, the page says where to approve, and it picks
  the result up when the user approves or rejects. With Direct Edit on it is written at once.
- **One transaction**, named "Page edit - …", so it is one Undo step in Revit.
- **Activity log** entry with client "Model page".

Values are read the way the user sees them, unlike `revit_set_parameter`:

| Parameter holds | Typed value |
|---|---|
| Text | Written as typed |
| Number with units (length, area…) | Parsed like Revit's own fields, in the project's units: `1200` or `1200 mm` |
| Whole number | The number |
| Yes/No | `yes`/`no`, `jah`/`ei`, `true`/`false`, `1`/`0` |
| Another element (a type, a level…) | Not editable from a page |

A change carries the value the page showed. When the model holds something else by then, that value
is not written and the page says the value changed in Revit; Refresh shows the current one. A cell on
a row with several ids writes the value to all of them.

`pages.allowEdits=false` in the user configuration keeps pages read-only.

## Look

Pages get a default theme after the EULE team hub: teal accent, cards, a top bar with the page
title, the Revit state, **Refresh** and a light/dark switch. Dark mode is black. The theme follows
the system setting until the user picks one; the choice is remembered in the browser.

The stylesheet is linked first in `<head>` and its element rules have zero specificity, so anything
a page styles itself wins. An agent should write plain semantic HTML and no colours of its own:

| Use | For |
|---|---|
| `<main>`, `h1`, `h2`, `p` | Page structure (content is centred, max 1280 px) |
| `<table>` with `<thead>` | Lists of elements; headers stay in view while scrolling |
| `<button>`, `<button class="ghost">` | Primary and secondary actions |
| `.card`, `.grid` of cards, `.stat` | Summary tiles |
| `.toolbar` | A row of buttons and filters |
| `.badge` with `.ok` / `.warn` / `.bad` | Status labels |
| `.muted`, `td.num` | Secondary text, right-aligned numbers |

Custom CSS should use the variables `--text`, `--muted`, `--accent`, `--card`, `--stroke`, `--bg`,
`--ok`, `--warn`, `--bad` so it follows light and dark. `<html data-revit-theme="none">` switches the
element defaults off; the bar and the edit states stay styled.

Example:

```html
<html lang="et"><body><main>
<h1>KK.48 pistikupesad</h1>
<div class="grid">
  <div class="card"><small>Seadmeid</small><div class="stat">6</div></div>
  <div class="card"><small>Kontrolli</small><div class="stat warn">2</div></div>
</div>
<div class="toolbar"><button data-revit-select-all="#sockets">Vali kõik Revitis</button></div>
<table id="sockets">
  <thead><tr><th>Tüüp</th><th>Ruum</th><th>Comments</th><th>Olek</th></tr></thead>
  <tr data-revit-id="1784421"><td>Pistik 2x</td><td>48.1</td><td data-revit-param="Comments">PM</td><td><span class="badge ok">korras</span></td></tr>
  <tr data-revit-id="1784430"><td>Pistik 1x</td><td>48.2</td><td data-revit-param="Comments">KÜ</td><td><span class="badge warn">kontrolli</span></td></tr>
</table>
</main></body></html>
```

## Limits

- **This computer, while Revit runs.** The link is a loopback address. A colleague cannot open it;
  the page has to be published in their own Revit session.
- **One model per page.** A page is bound to the model that was active when it was published
  (central path, else local path, else title). With another document active, every request is
  refused with `different_model` and the bar says so.
- **Self-contained HTML only.** Inline CSS and scripts; no CDN, web fonts or remote images. The
  page is served with a policy that blocks every other origin. `revit_publish_page` warns when the
  HTML loads something external, and when no element carries `data-revit-id`.
- **Not inside a Claude artifact.** A page hosted elsewhere cannot reach the local listener; link to
  the local page from there instead.
- Per request: 20 000 ids to select, 2 000 ids × 40 parameters to read (the helper splits larger
  reads), 500 values to write. 200 pages are kept; the oldest drop off.
- After a Revit restart an open tab holds an old token: its requests are refused and the bar asks
  for a reload.

## How it is built

| Location | Contents |
|---|---|
| `RevitMCP.Addin/ModelPages/ModelPageStore.cs` | Pages on disk (`<id>.html` + `<id>.json`), id rules, size limit. Pure |
| `RevitMCP.Addin/ModelPages/ModelPageHtml.cs` | Stylesheet and bootstrap injection, page index, publish-time review. Pure |
| `RevitMCP.Addin/ModelPages/ModelPageServer.cs` | The loopback listener and its actions. Pure |
| `RevitMCP.Addin/ModelPages/ModelPageEdits.cs` | Reading requested changes, the approval summary, yes/no and stale-value rules. Pure |
| `RevitMCP.Addin/ModelPages/Viewer/pages.js`, `pages.css` | The helper script and the default theme (embedded resources) |
| `RevitMCP.Addin/ModelPages/Hosting/` | `ModelPageRevitHandler` (its own `ExternalEvent` for select, zoom and reading; the hand-over to the dispatcher for writes) and `ModelPageService` (wiring, start, config switches) |
| `RevitMCP.Addin/Tools/ModelPages/` | `revit_publish_page`, `revit_list_pages`, `revit_page_set_values` |

The listener starts on port 47830 (the next free one of ten when taken) when the first page is
published, or at startup when earlier pages exist. `pages.enabled=false` in the user configuration
switches the feature off.

## Security model

The listener follows the village listener's rules (see `docs/project-village.md`), because a local
web server that can act on Revit must not be reachable by other web pages:

- binds to `127.0.0.1` only; the `Host` header must name a loopback host (blocks DNS rebinding);
- `GET`/`HEAD` for `/`, `/p/{id}`, `/pages.js`, `/pages.css`, `/health`; `POST` for `/api/status`,
  `/api/select`, `/api/values`, `/api/set`, `/api/approval`; everything else is `404`/`405`;
- a `POST` needs the listener's per-start token in `X-Revit-Page-Token`. The token is written only
  into pages the listener serves; no CORS headers are sent, so another origin can neither read a
  page nor send the header. `Sec-Fetch-Site` and `Origin`, when present, must be this listener;
- every response carries `default-src 'none'; connect-src 'self'; frame-ancestors 'none'; …`: a
  generated page can talk to this listener and nothing else, and cannot be framed;
- the actions are fixed. Status, select and values never reach the MCP dispatcher and cannot change
  the model. `/api/set` can only become `revit_page_set_values`, which writes instance parameter
  values and nothing else; `/api/approval` answers only for requests of that tool;
- page ids are a lowercase slug, checked before any file access.

What this does not cover: a page is HTML written by an agent and runs with the page token. Its
script can select elements, read parameter values of its model and *request* a write without the
user clicking anything. The approval step is what stands between such a script and the model, and
the approval line names the page, the parameter and the value. **With Direct Edit on there is no
such step**: a published page can then write instance parameters on its own, the same trust the
agent itself has in that mode. A page cannot send data anywhere; its script could at most navigate
the tab away.

## Tests

`ModelPageTests` covers the store, the injected HTML, the listener over real sockets (token, origin,
host, body validation, the write and approval routes) and the edit rules. The helper script, the
theme and the edit flow were exercised in a browser against a fake backend. The Revit side —
`ModelPageRevitHandler` and `PageSetValuesTool` — has no unit tests; check it in Revit after changes.
