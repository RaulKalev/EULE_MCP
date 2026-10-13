# Model pages

A model page is HTML an AI agent generated from a model — a check report, an overview table, a list
of findings — published back to the user's browser by the add-in. On the page the user selects and
zooms to elements in Revit and sees current parameter values by clicking. The agent is not involved
in those clicks, so going through a list costs no tokens.

Stage 1 (this document) is read-only: select, zoom, read values. Changing parameter values from a
page is a later stage and will go through the same approval path as an agent's write.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_publish_page` | Read-only | Stores the HTML (`html`, or `htmlPath` to an `.html` file, max 2 MB), binds it to the active model, and opens `http://127.0.0.1:<port>/p/<pageId>`. `pageId` replaces an earlier page and keeps its link; `open=false` only returns the link |
| `revit_list_pages` | Read-only | Published pages with title, model, whether they belong to the active model, and url. `removePageId` deletes one |

Both are in the `pages` tool group; the server instructions point agents at `revit_publish_page`.
"Read-only" here means the model: the tool writes the page to `%LOCALAPPDATA%\RevitMCP\Pages`.

## Markup

The add-in adds a helper script to every page it serves. The agent only marks up the HTML:

| Attribute | On | Effect |
|---|---|---|
| `data-revit-id="123456"` | a row, card or any element. Several ids: `"123,456"` | A click selects those elements in Revit and zooms to them |
| `data-revit-param="Comments"` | a cell inside an element with `data-revit-id` | The text is replaced by the element's current value on load and on **Refresh**. A value that differs from what the agent wrote is highlighted, with the original in the tooltip. Instance parameters first, then type parameters, by exact name |
| `data-revit-select-all` | a button | Selects every visible element on the page; `data-revit-select-all="#table1"` limits it to that selector. Rows hidden by the page's own filter are left out |

A row whose element no longer exists is struck through; a cell naming a parameter the element does
not have is shown in italics.

The page's own scripts can use the same functions:

```js
revit.select([123, 456], { zoom: true })      // → { ok, selected, missing, zoomed }
revit.values([123, 456], ['Comments', 'Mark']) // → { ok, values: { "123": { Comments: "…" } }, missing }
revit.refresh()                               // re-read every data-revit-param cell (call after adding rows)
revit.status()                                // → { ok, activeDocument } or { ok: false, status, message }
```

A status pill in the corner shows whether Revit is on the page's model. Helper texts are Estonian
when the page says `<html lang="et">`, English otherwise.

Example:

```html
<html lang="et"><body>
<button data-revit-select-all="#sockets">Vali kõik</button>
<table id="sockets">
  <tr data-revit-id="1784421"><td>Pistik 2x</td><td data-revit-param="Comments">PM</td></tr>
  <tr data-revit-id="1784430"><td>Pistik 1x</td><td data-revit-param="Comments">KÜ</td></tr>
</table>
</body></html>
```

## Limits

- **This computer, while Revit runs.** The link is a loopback address. A colleague cannot open it;
  the page has to be published in their own Revit session.
- **One model per page.** A page is bound to the model that was active when it was published
  (central path, else local path, else title). With another document active, every request is
  refused with `different_model` and the pill says so.
- **Self-contained HTML only.** Inline CSS and scripts; no CDN, web fonts or remote images. The
  page is served with a policy that blocks every other origin. `revit_publish_page` warns when the
  HTML loads something external, and when no element carries `data-revit-id`.
- **Not inside a Claude artifact.** A page hosted elsewhere cannot reach the local listener; link to
  the local page from there instead.
- Per request: 20 000 ids to select, 2 000 ids × 40 parameters to read (the helper splits larger
  reads). 200 pages are kept; the oldest drop off.
- After a Revit restart an open tab holds an old token: its requests are refused and the pill asks
  for a reload.

## How it is built

| Location | Contents |
|---|---|
| `RevitMCP.Addin/ModelPages/ModelPageStore.cs` | Pages on disk (`<id>.html` + `<id>.json`), id rules, size limit. Pure |
| `RevitMCP.Addin/ModelPages/ModelPageHtml.cs` | Bootstrap injection, page index, publish-time review. Pure |
| `RevitMCP.Addin/ModelPages/ModelPageServer.cs` | The loopback listener and its three actions. Pure |
| `RevitMCP.Addin/ModelPages/Viewer/pages.js` | The helper script (embedded resource) |
| `RevitMCP.Addin/ModelPages/Hosting/` | `ModelPageRevitHandler` (its own `ExternalEvent`: select, zoom, read values) and `ModelPageService` (wiring, start, config switch) |
| `RevitMCP.Addin/Tools/ModelPages/` | The two MCP tools |

The listener starts on port 47830 (the next free one of ten when taken) when the first page is
published, or at startup when earlier pages exist. `pages.enabled=false` in the user configuration
switches the feature off.

## Security model

The listener follows the village listener's rules (see `docs/project-village.md`), because a local
web server that can act on Revit must not be reachable by other web pages:

- binds to `127.0.0.1` only; the `Host` header must name a loopback host (blocks DNS rebinding);
- `GET`/`HEAD` for `/`, `/p/{id}`, `/pages.js`, `/health`; `POST` for `/api/status`, `/api/select`,
  `/api/values`; everything else is `404`/`405`;
- a `POST` needs the listener's per-start token in `X-Revit-Page-Token`. The token is written only
  into pages the listener serves; no CORS headers are sent, so another origin can neither read a
  page nor send the header. `Sec-Fetch-Site` and `Origin`, when present, must be this listener;
- every response carries `default-src 'none'; connect-src 'self'; frame-ancestors 'none'; …`: a
  generated page can talk to this listener and nothing else, and cannot be framed;
- the three actions are fixed. A request never reaches the MCP dispatcher, the tool registry, the
  approval queue or the activity log, and none of the actions changes the model;
- page ids are a lowercase slug, checked before any file access.

What this does not cover: a page is HTML written by an agent and runs with the page token, so it can
select elements and read parameter values of its model. It cannot send them anywhere, and a script
in it could at most navigate the tab away.

## Tests

`ModelPageTests` covers the store, the injected HTML and the listener over real sockets (token,
origin, host, body validation). The helper script was exercised in a browser against a fake backend.
The Revit handler has no unit tests; check it in Revit after changes.
