// Helper for model pages. The server adds it to every page it serves, together with
// window.__revitPage = { id, token }. It makes the page's markup work against Revit:
//
//   data-revit-id="123"  or  "123,456"   click selects and zooms to those elements
//   data-revit-param="Comments"          text is replaced by the element's current value
//                                        (the element is the nearest data-revit-id)
//   data-revit-select-all                click selects every element on the page;
//   data-revit-select-all="#table1"      ...or every element inside that CSS selector
//
// and exposes the same to the page's own scripts as window.revit:
//   revit.select(ids, {zoom}), revit.values(ids, parameters), revit.refresh(), revit.status()
// A page that changes its own markup later calls revit.refresh() to bind and fill the new rows.
(function () {
  'use strict';
  var cfg = window.__revitPage || {};
  var et = /^et\b/i.test(document.documentElement.lang || navigator.language || '');
  var TEXT = et ? {
    ready: 'Revit: ', offline: 'Revit ei vasta', other: 'Revitis on aktiivne teine mudel', refresh: 'Värskenda',
    selected: function (n) { return n === 1 ? 'Valitud 1 element' : 'Valitud ' + n + ' elementi'; },
    missing: function (n) { return n + ' ei ole enam mudelis'; },
    stale: 'Leht avati enne Reviti taaskäivitust. Laadi leht uuesti.',
    unreachable: 'Revit ei ole kättesaadav. Kas Revit töötab ja ühendaja on käivitatud?',
    was: 'Lehe koostamisel: ', gone: 'Elementi ei ole enam mudelis', noParam: 'Elemendil ei ole seda parameetrit',
    updated: function (n) { return n ? 'Uuendatud väärtusi: ' + n : 'Väärtused on ajakohased'; }
  } : {
    ready: 'Revit: ', offline: 'Revit is not responding', other: 'Another model is active in Revit', refresh: 'Refresh',
    selected: function (n) { return n === 1 ? 'Selected 1 element' : 'Selected ' + n + ' elements'; },
    missing: function (n) { return n + ' no longer in the model'; },
    stale: 'This page was opened before Revit restarted. Reload it.',
    unreachable: 'Revit is not reachable. Is Revit running with the connector started?',
    was: 'When the page was made: ', gone: 'This element is no longer in the model', noParam: 'The element has no such parameter',
    updated: function (n) { return n ? 'Values updated: ' + n : 'Values are up to date'; }
  };

  function post(path, body) {
    var payload = { page: cfg.id };
    for (var k in body || {}) payload[k] = body[k];
    return fetch(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Revit-Page-Token': cfg.token || '' },
      body: JSON.stringify(payload)
    }).then(function (r) {
      return r.text().then(function (t) {
        var j = null;
        try { j = JSON.parse(t); } catch (e) { /* not JSON */ }
        if (r.status === 403) return { ok: false, status: 'stale', message: TEXT.stale };
        return j || { ok: false, status: 'http_' + r.status, message: 'HTTP ' + r.status };
      });
    }).catch(function () {
      return { ok: false, status: 'offline', message: TEXT.unreachable };
    });
  }

  function parseIds(value) {
    var out = [];
    String(value == null ? '' : value).split(/[\s,;]+/).forEach(function (part) {
      if (/^\d+$/.test(part) && out.indexOf(part) < 0) out.push(part);
    });
    return out;
  }

  function toIds(ids) {
    return Array.isArray(ids) ? parseIds(ids.join(',')) : parseIds(ids);
  }

  // ── UI: styles, toast, status pill ───────────────────────────────────────
  var style = document.createElement('style');
  style.textContent =
    '[data-revit-id],[data-revit-select-all]{cursor:pointer}' +
    '[data-revit-id]:hover{outline:2px solid #2f7bf6;outline-offset:-2px}' +
    '.revit-active{outline:2px solid #2f7bf6;outline-offset:-2px;background-color:rgba(47,123,246,.12)}' +
    '.revit-changed{background-color:rgba(245,166,35,.25)}' +
    '.revit-gone{opacity:.45;text-decoration:line-through}' +
    '.revit-missing{opacity:.6;font-style:italic}' +
    '#revit-pill,#revit-toast{position:fixed;z-index:2147483000;font:13px/1.4 system-ui,Segoe UI,sans-serif;' +
    'background:#1c2330;color:#fff;border-radius:999px;box-shadow:0 4px 16px rgba(0,0,0,.25)}' +
    '#revit-pill{right:16px;bottom:16px;padding:6px 8px 6px 12px;display:flex;align-items:center;gap:8px}' +
    '#revit-pill i{width:9px;height:9px;border-radius:50%;background:#8a94a6;flex:none}' +
    '#revit-pill.ok i{background:#3ecf6e}#revit-pill.warn i{background:#f5a623}#revit-pill.bad i{background:#f0524f}' +
    '#revit-pill button{font:inherit;color:#fff;background:#39445a;border:0;border-radius:999px;padding:3px 10px;cursor:pointer}' +
    '#revit-pill button:hover{background:#4a5873}' +
    '#revit-toast{left:50%;bottom:64px;transform:translateX(-50%);padding:8px 16px;max-width:80vw;border-radius:8px;' +
    'opacity:0;transition:opacity .15s;pointer-events:none}#revit-toast.show{opacity:1}#revit-toast.bad{background:#a5282a}' +
    '@media print{#revit-pill,#revit-toast{display:none}}';
  document.head.appendChild(style);

  var pill, pillText, toast, toastTimer;
  function buildUi() {
    pill = document.createElement('div');
    pill.id = 'revit-pill';
    pill.appendChild(document.createElement('i'));
    pillText = document.createElement('span');
    pill.appendChild(pillText);
    var button = document.createElement('button');
    button.type = 'button';
    button.textContent = TEXT.refresh;
    button.addEventListener('click', function () { refresh(true); });
    pill.appendChild(button);
    toast = document.createElement('div');
    toast.id = 'revit-toast';
    toast.setAttribute('role', 'status');
    document.body.appendChild(pill);
    document.body.appendChild(toast);
  }

  function say(message, bad) {
    if (!toast || !message) return;
    toast.textContent = message;
    toast.className = 'show' + (bad ? ' bad' : '');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toast.className = ''; }, bad ? 6000 : 2500);
  }

  function setPill(result) {
    if (!pill) return;
    if (result && result.ok) { pill.className = 'ok'; pillText.textContent = TEXT.ready + (result.activeDocument || ''); }
    else if (result && result.status === 'different_model') { pill.className = 'warn'; pillText.textContent = TEXT.other; }
    else { pill.className = 'bad'; pillText.textContent = (result && result.status === 'stale') ? TEXT.stale : TEXT.offline; }
    pill.title = (result && result.message) || '';
  }

  // ── Actions ──────────────────────────────────────────────────────────────
  function select(ids, options) {
    var list = toIds(ids);
    if (!list.length) return Promise.resolve({ ok: false, status: 'bad_request', message: '' });
    return post('/api/select', { ids: list, zoom: !(options && options.zoom === false) }).then(function (r) {
      if (r.ok) {
        var gone = (r.missing || []).length;
        say(TEXT.selected(r.selected) + (gone ? ' · ' + TEXT.missing(gone) : ''));
      } else {
        say(r.message, true);
        if (r.status !== 'not_found') setPill(r);
      }
      return r;
    });
  }

  function values(ids, parameters) {
    var list = toIds(ids), names = [].concat(parameters || []), merged = { ok: true, values: {}, missing: [] };
    var chunks = [];
    for (var i = 0; i < list.length; i += 500) chunks.push(list.slice(i, i + 500));
    return chunks.reduce(function (chain, chunk) {
      return chain.then(function (acc) {
        if (!acc.ok) return acc;
        return post('/api/values', { ids: chunk, parameters: names }).then(function (r) {
          if (!r.ok) return r;
          for (var id in r.values) acc.values[id] = r.values[id];
          acc.missing = acc.missing.concat(r.missing || []);
          return acc;
        });
      });
    }, Promise.resolve(merged));
  }

  function ownerId(node) {
    var host = node.closest('[data-revit-id]');
    return host ? parseIds(host.getAttribute('data-revit-id'))[0] : null;
  }

  // Fills every data-revit-param cell with the element's current value and marks what changed.
  function fill() {
    var cells = [].slice.call(document.querySelectorAll('[data-revit-param]'));
    var ids = [], names = [];
    cells.forEach(function (cell) {
      var id = ownerId(cell), name = cell.getAttribute('data-revit-param');
      if (!id || !name) return;
      if (ids.indexOf(id) < 0) ids.push(id);
      if (names.indexOf(name) < 0) names.push(name);
      if (!cell.hasAttribute('data-revit-original')) cell.setAttribute('data-revit-original', cell.textContent);
    });
    if (!ids.length) return Promise.resolve({ ok: true, changed: 0 });
    return values(ids, names).then(function (r) {
      if (!r.ok) return r;
      var changed = 0;
      cells.forEach(function (cell) {
        var id = ownerId(cell), name = cell.getAttribute('data-revit-param');
        if (!id || !name) return;
        var row = r.values[id], host = cell.closest('[data-revit-id]');
        cell.classList.remove('revit-changed', 'revit-missing');
        if (!row) { if (host) { host.classList.add('revit-gone'); host.title = TEXT.gone; } return; }
        if (host) host.classList.remove('revit-gone');
        if (!(name in row)) { cell.classList.add('revit-missing'); cell.title = TEXT.noParam; return; }
        var now = row[name] == null ? '' : String(row[name]);
        var original = cell.getAttribute('data-revit-original');
        if (cell.textContent !== now) cell.textContent = now;
        if (now.trim() !== original.trim()) { cell.classList.add('revit-changed'); cell.title = TEXT.was + original; changed++; }
        else cell.removeAttribute('title');
      });
      r.changed = changed;
      return r;
    });
  }

  function status() {
    return post('/api/status', {}).then(function (r) { setPill(r); return r; });
  }

  function refresh(announce) {
    return status().then(function (s) {
      if (!s.ok) { if (announce) say(s.message, true); return s; }
      return fill().then(function (r) {
        if (!r.ok) { setPill(r); if (announce) say(r.message, true); }
        else if (announce) say(TEXT.updated(r.changed || 0));
        return r;
      });
    });
  }

  // ── Wiring ───────────────────────────────────────────────────────────────
  document.addEventListener('click', function (event) {
    var target = event.target;
    if (!target || !target.closest || target.closest('#revit-pill')) return;

    var all = target.closest('[data-revit-select-all]');
    if (all) {
      var scopeSelector = all.getAttribute('data-revit-select-all');
      var scope = scopeSelector ? document.querySelector(scopeSelector) : document;
      var collected = [];
      [].forEach.call((scope || document).querySelectorAll('[data-revit-id]'), function (node) {
        if (node.offsetParent === null && node.tagName !== 'BODY') return;   // filtered-out rows stay out
        parseIds(node.getAttribute('data-revit-id')).forEach(function (id) { if (collected.indexOf(id) < 0) collected.push(id); });
      });
      event.preventDefault();
      select(collected);
      return;
    }

    // Links, form controls and text being selected keep their own behaviour.
    if (target.closest('a[href],button,input,select,textarea,label,[contenteditable="true"]')) return;
    if (String(window.getSelection && window.getSelection()).length > 0) return;
    var host = target.closest('[data-revit-id]');
    if (!host) return;
    [].forEach.call(document.querySelectorAll('.revit-active'), function (n) { n.classList.remove('revit-active'); });
    host.classList.add('revit-active');
    select(host.getAttribute('data-revit-id'));
  });

  window.revit = { select: select, values: values, refresh: function () { return refresh(false); }, status: status, pageId: cfg.id };

  function start() { buildUi(); refresh(false); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
