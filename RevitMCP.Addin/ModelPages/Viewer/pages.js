// Helper for model pages. The server adds it to every page it serves, together with
// window.__revitPage = { id, token, title }. It makes the page's markup work against Revit:
//
//   data-revit-id="123"  or  "123,456"   click selects and zooms to those elements
//   data-revit-param="Comments"          text is replaced by the element's current value
//                                        (the element is the nearest data-revit-id);
//                                        double click edits it when the parameter is writable
//   data-revit-readonly                  on a cell or any ancestor: no editing there
//   data-revit-select-all                click selects every element on the page;
//   data-revit-select-all="#table1"      ...or every element inside that CSS selector
//
// and exposes the same to the page's own scripts as window.revit:
//   revit.select(ids, {zoom}), revit.values(ids, parameters), revit.set(changes),
//   revit.refresh(), revit.status()
// A page that changes its own markup later calls revit.refresh() to bind and fill the new rows.
//
// A write goes through the connector's approval queue: unless the user switched Direct Edit on,
// they approve it in the RevitMCP window and the helper waits for that.
(function () {
  'use strict';
  var cfg = window.__revitPage || {};
  var root = document.documentElement;
  var et = /^et\b/i.test(root.lang || navigator.language || '');
  var TEXT = et ? {
    ready: 'Revit: ', offline: 'Revit ei vasta', other: 'Revitis on teine mudel', refresh: 'Värskenda', pages: 'Kõik lehed',
    theme: 'Hele/tume',
    selected: function (n) { return n === 1 ? 'Valitud 1 element' : 'Valitud ' + n + ' elementi'; },
    missing: function (n) { return n + ' ei ole enam mudelis'; },
    stale: 'Leht avati enne Reviti taaskäivitust. Laadi leht uuesti.',
    unreachable: 'Revit ei ole kättesaadav. Kas Revit töötab ja ühendaja on käivitatud?',
    was: 'Lehe koostamisel: ', gone: 'Elementi ei ole enam mudelis', noParam: 'Elemendil ei ole seda parameetrit',
    updated: function (n) { return n ? 'Uuendatud väärtusi: ' + n : 'Väärtused on ajakohased'; },
    editHint: 'Topeltklõps muutmiseks',
    approve: 'Kinnita muudatus Revitis: RevitMCP aken → Pending',
    saved: function (n) { return n === 1 ? 'Väärtus kirjutatud Revitisse' : n + ' väärtust kirjutatud Revitisse'; },
    notSaved: 'Ei kirjutatud: ', rejected: 'Muudatus lükati Revitis tagasi', timeout: 'Kinnitust ei tulnud. Muudatus ootab Revitis edasi.'
  } : {
    ready: 'Revit: ', offline: 'Revit is not responding', other: 'Another model is active in Revit', refresh: 'Refresh', pages: 'All pages',
    theme: 'Light/dark',
    selected: function (n) { return n === 1 ? 'Selected 1 element' : 'Selected ' + n + ' elements'; },
    missing: function (n) { return n + ' no longer in the model'; },
    stale: 'This page was opened before Revit restarted. Reload it.',
    unreachable: 'Revit is not reachable. Is Revit running with the connector started?',
    was: 'When the page was made: ', gone: 'This element is no longer in the model', noParam: 'The element has no such parameter',
    updated: function (n) { return n ? 'Values updated: ' + n : 'Values are up to date'; },
    editHint: 'Double click to edit',
    approve: 'Approve the change in Revit: RevitMCP window → Pending',
    saved: function (n) { return n === 1 ? 'Value written to Revit' : n + ' values written to Revit'; },
    notSaved: 'Not written: ', rejected: 'The change was rejected in Revit', timeout: 'No approval yet. The change is still waiting in Revit.'
  };

  // ── Theme: follows the system until the user picks one ───────────────────
  var THEME_KEY = 'revit-page-theme';
  function storedTheme() { try { return localStorage.getItem(THEME_KEY); } catch (e) { return null; } }
  function applyTheme(theme) {
    root.classList.remove('light', 'dark');
    if (theme === 'light' || theme === 'dark') root.classList.add(theme);
  }
  applyTheme(storedTheme());
  function toggleTheme() {
    var dark = root.classList.contains('dark') ||
      (!root.classList.contains('light') && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    var next = dark ? 'light' : 'dark';
    applyTheme(next);
    try { localStorage.setItem(THEME_KEY, next); } catch (e) { /* private mode */ }
  }

  // ── Transport ────────────────────────────────────────────────────────────
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
  function toIds(ids) { return Array.isArray(ids) ? parseIds(ids.join(',')) : parseIds(ids); }
  function wait(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }

  // ── UI: bar and toast ────────────────────────────────────────────────────
  var bar, stateText, toast, toastTimer;
  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
  }

  function buildUi() {
    bar = el('div');
    bar.id = 'revit-bar';
    var mark = el('a', 'revit-mark', 'EULE');
    mark.href = '/';
    mark.title = TEXT.pages;
    bar.appendChild(mark);
    bar.appendChild(el('span', 'revit-title', cfg.title || document.title || ''));
    bar.appendChild(el('span', 'revit-spacer'));
    var state = el('span', 'revit-state');
    state.appendChild(el('i'));
    stateText = el('span', null, '…');
    state.appendChild(stateText);
    bar.appendChild(state);
    var refreshButton = el('button', null, TEXT.refresh);
    refreshButton.type = 'button';
    refreshButton.addEventListener('click', function () { refresh(true); });
    bar.appendChild(refreshButton);
    var themeButton = el('button', null, '◐');
    themeButton.type = 'button';
    themeButton.title = TEXT.theme;
    themeButton.setAttribute('aria-label', TEXT.theme);
    themeButton.addEventListener('click', toggleTheme);
    bar.appendChild(themeButton);
    document.body.insertBefore(bar, document.body.firstChild);

    toast = el('div');
    toast.id = 'revit-toast';
    toast.setAttribute('role', 'status');
    document.body.appendChild(toast);
  }

  function say(message, bad, sticky) {
    if (!toast || !message) return;
    toast.textContent = message;
    toast.className = 'show' + (bad ? ' bad' : '');
    clearTimeout(toastTimer);
    if (!sticky) toastTimer = setTimeout(function () { toast.className = ''; }, bad ? 6000 : 2500);
  }

  function setState(result) {
    if (!bar) return;
    if (result && result.ok) { bar.className = 'ok'; stateText.textContent = TEXT.ready + (result.activeDocument || ''); }
    else if (result && result.status === 'different_model') { bar.className = 'warn'; stateText.textContent = TEXT.other; }
    else { bar.className = 'bad'; stateText.textContent = (result && result.status === 'stale') ? TEXT.stale : TEXT.offline; }
    stateText.parentNode.title = (result && result.message) || '';
  }

  // ── Select / read ────────────────────────────────────────────────────────
  function select(ids, options) {
    var list = toIds(ids);
    if (!list.length) return Promise.resolve({ ok: false, status: 'bad_request', message: '' });
    return post('/api/select', { ids: list, zoom: !(options && options.zoom === false) }).then(function (r) {
      if (r.ok) {
        var gone = (r.missing || []).length;
        say(TEXT.selected(r.selected) + (gone ? ' · ' + TEXT.missing(gone) : ''));
      } else {
        say(r.message, true);
        if (r.status !== 'not_found') setState(r);
      }
      return r;
    });
  }

  function values(ids, parameters) {
    var list = toIds(ids), names = [].concat(parameters || []), merged = { ok: true, values: {}, editable: {}, missing: [] };
    var chunks = [];
    for (var i = 0; i < list.length; i += 500) chunks.push(list.slice(i, i + 500));
    return chunks.reduce(function (chain, chunk) {
      return chain.then(function (acc) {
        if (!acc.ok) return acc;
        return post('/api/values', { ids: chunk, parameters: names }).then(function (r) {
          if (!r.ok) return r;
          for (var id in r.values) acc.values[id] = r.values[id];
          for (var key in r.editable || {}) acc.editable[key] = r.editable[key];
          acc.missing = acc.missing.concat(r.missing || []);
          return acc;
        });
      });
    }, Promise.resolve(merged));
  }

  function hostOf(node) { return node.closest('[data-revit-id]'); }
  function ownerIds(node) { var host = hostOf(node); return host ? parseIds(host.getAttribute('data-revit-id')) : []; }

  // Fills every data-revit-param cell with the element's current value, marks what changed since the
  // page was made, and opens the cells whose parameter can be written.
  function fill() {
    var cells = [].slice.call(document.querySelectorAll('[data-revit-param]'));
    var ids = [], names = [];
    cells.forEach(function (cell) {
      var id = ownerIds(cell)[0], name = cell.getAttribute('data-revit-param');
      if (!id || !name || cell.classList.contains('revit-editing')) return;
      if (ids.indexOf(id) < 0) ids.push(id);
      if (names.indexOf(name) < 0) names.push(name);
      if (!cell.hasAttribute('data-revit-original')) cell.setAttribute('data-revit-original', cell.textContent);
    });
    if (!ids.length) return Promise.resolve({ ok: true, changed: 0 });
    return values(ids, names).then(function (r) {
      if (!r.ok) return r;
      var changed = 0;
      cells.forEach(function (cell) {
        var id = ownerIds(cell)[0], name = cell.getAttribute('data-revit-param');
        if (!id || !name || cell.classList.contains('revit-editing') || cell.classList.contains('revit-pending')) return;
        var row = r.values[id], host = hostOf(cell);
        cell.classList.remove('revit-changed', 'revit-missing', 'revit-editable');
        if (!row) { if (host) { host.classList.add('revit-gone'); host.title = TEXT.gone; } return; }
        if (host) host.classList.remove('revit-gone');
        if (!(name in row)) { cell.classList.add('revit-missing'); cell.title = TEXT.noParam; return; }
        var now = row[name] == null ? '' : String(row[name]);
        var original = cell.getAttribute('data-revit-original');
        if (cell.textContent !== now) cell.textContent = now;
        var open = (r.editable[id] || []).indexOf(name) >= 0 && !cell.closest('[data-revit-readonly]');
        if (open) cell.classList.add('revit-editable');
        if (now.trim() !== original.trim()) { cell.classList.add('revit-changed'); cell.title = TEXT.was + original; changed++; }
        else if (open) cell.title = TEXT.editHint;
        else cell.removeAttribute('title');
      });
      r.changed = changed;
      return r;
    });
  }

  function status() {
    return post('/api/status', {}).then(function (r) { setState(r); return r; });
  }

  function refresh(announce) {
    return status().then(function (s) {
      if (!s.ok) { if (announce) say(s.message, true); return s; }
      return fill().then(function (r) {
        if (!r.ok) { setState(r); if (announce) say(r.message, true); }
        else if (announce) say(TEXT.updated(r.changed || 0));
        return r;
      });
    });
  }

  // ── Write ────────────────────────────────────────────────────────────────
  // Resolves when the write is done, refused or rejected. While it waits for approval the toast says where.
  function set(changes) {
    var list = [].concat(changes || []).map(function (c) {
      var change = { id: String(c.id), parameter: c.parameter, value: c.value == null ? '' : String(c.value) };
      if (c.expected != null) change.expected = String(c.expected);
      return change;
    });
    if (!list.length) return Promise.resolve({ ok: false, status: 'bad_request', message: '' });
    return post('/api/set', { changes: list }).then(function (r) {
      if (r.status !== 'approval_required') return r;
      say(TEXT.approve, false, true);
      var started = Date.now();
      function poll() {
        return wait(1200).then(function () { return post('/api/approval', { requestId: r.requestId }); }).then(function (p) {
          if (p.status !== 'pending') return p;
          if (Date.now() - started > 10 * 60 * 1000) return { ok: false, status: 'timeout', message: TEXT.timeout };
          return poll();
        });
      }
      return poll();
    }).then(function (r) {
      var rows = r.changes || [];
      var done = rows.filter(function (c) { return c.ok; }).length;
      var failed = rows.filter(function (c) { return !c.ok; })[0];
      if (r.ok && !failed) say(TEXT.saved(done));
      else if (r.status === 'rejected') say(TEXT.rejected, true);
      else say(TEXT.notSaved + ((failed && failed.reason) || r.message || r.status), true);
      if (!r.ok && !rows.length && r.status !== 'rejected' && r.status !== 'timeout') setState(r);
      return r;
    });
  }

  function beginEdit(cell) {
    if (cell.classList.contains('revit-editing') || cell.classList.contains('revit-pending')) return;
    var ids = ownerIds(cell), name = cell.getAttribute('data-revit-param'), before = cell.textContent;
    if (!ids.length || !name) return;
    var input = el('input');
    input.type = 'text';
    input.value = before;
    input.setAttribute('aria-label', name);
    cell.textContent = '';
    cell.classList.add('revit-editing');
    cell.appendChild(input);
    input.focus();
    input.select();

    var finished = false;
    function finish(save) {
      if (finished) return;
      finished = true;
      var value = input.value;
      cell.classList.remove('revit-editing');
      cell.textContent = before;
      if (!save || value === before) return;

      cell.textContent = value;
      cell.classList.add('revit-pending');
      set(ids.map(function (id, i) {
        // The page showed one value for the row; it is what the first element is checked against.
        return i === 0 ? { id: id, parameter: name, value: value, expected: before } : { id: id, parameter: name, value: value };
      })).then(function (r) {
        cell.classList.remove('revit-pending');
        var row = (r.changes || []).filter(function (c) { return String(c.id) === ids[0]; })[0];
        if (row && row.ok) {
          cell.textContent = row.value == null ? '' : String(row.value);
          cell.setAttribute('data-revit-original', cell.textContent);   // the user's own edit is not a surprise
          cell.classList.remove('revit-changed');
          cell.title = TEXT.editHint;
          cell.classList.remove('revit-saved');
          void cell.offsetWidth;
          cell.classList.add('revit-saved');
        } else {
          cell.textContent = row && row.value != null ? String(row.value) : before;
        }
      });
    }
    input.addEventListener('keydown', function (event) {
      if (event.key === 'Enter') { event.preventDefault(); finish(true); }
      else if (event.key === 'Escape') { event.preventDefault(); finish(false); }
      event.stopPropagation();
    });
    input.addEventListener('blur', function () { finish(true); });
  }

  // ── Wiring ───────────────────────────────────────────────────────────────
  var clickTimer = null;
  document.addEventListener('click', function (event) {
    var target = event.target;
    if (!target || !target.closest || target.closest('#revit-bar') || target.closest('.revit-editing')) return;

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
    // On an editable cell a second click may follow: wait for it, so editing does not also move the view.
    clearTimeout(clickTimer);
    if (target.closest('.revit-editable')) clickTimer = setTimeout(function () { select(host.getAttribute('data-revit-id')); }, 260);
    else select(host.getAttribute('data-revit-id'));
  });

  document.addEventListener('dblclick', function (event) {
    var cell = event.target && event.target.closest && event.target.closest('.revit-editable');
    if (!cell) return;
    clearTimeout(clickTimer);
    event.preventDefault();
    if (window.getSelection) window.getSelection().removeAllRanges();
    beginEdit(cell);
  });

  window.revit = {
    select: select, values: values, set: set, status: status, pageId: cfg.id,
    refresh: function () { return refresh(false); }
  };

  function start() { buildUi(); refresh(false); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
