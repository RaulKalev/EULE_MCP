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
// A table whose body rows carry data-revit-id also gets tools without any markup: a search box,
// filters per column, sorting by a click on a header, grouping by a column, and "select shown".
//   data-revit-tools="none"              on the table (or an ancestor): leave it alone
//   data-revit-tools="rows"              on a table whose rows are not model elements: the same tools,
//                                        without the "select in Revit" buttons
//   data-sort="2024-05-01"               on a cell: the value to sort and filter by instead of its text
//
// and exposes the same to the page's own scripts as window.revit:
//   revit.select(ids, {zoom}), revit.values(ids, parameters), revit.set(changes),
//   revit.refresh(), revit.status(), revit.memory(action, {ids, proposal}) (the memory review page)
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
    search: 'Otsi…', groupBy: 'Grupeeri', noGroup: 'Grupeerimata', filters: 'Filtrid', reset: 'Lähtesta', all: 'Kõik',
    empty: '(tühi)', selectShown: 'Vali nähtavad Revitis', selectGroup: 'Vali Revitis', contains: 'sisaldab…',
    sortHint: 'Klõps sorteerib',
    approve: 'Kinnita muudatus Revitis: RevitMCP aken → Pending',
    saved: function (n) { return n === 1 ? 'Väärtus kirjutatud Revitisse' : n + ' väärtust kirjutatud Revitisse'; },
    notSaved: 'Ei kirjutatud: ', memoryDone: 'Tehtud', rejected: 'Muudatus lükati Revitis tagasi', timeout: 'Kinnitust ei tulnud. Muudatus ootab Revitis edasi.'
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
    search: 'Search…', groupBy: 'Group by', noGroup: 'No grouping', filters: 'Filters', reset: 'Reset', all: 'All',
    empty: '(empty)', selectShown: 'Select shown in Revit', selectGroup: 'Select in Revit', contains: 'contains…',
    sortHint: 'Click to sort',
    approve: 'Approve the change in Revit: RevitMCP window → Pending',
    saved: function (n) { return n === 1 ? 'Value written to Revit' : n + ' values written to Revit'; },
    notSaved: 'Not written: ', memoryDone: 'Done', rejected: 'The change was rejected in Revit', timeout: 'No approval yet. The change is still waiting in Revit.'
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
        tables();
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
    return post('/api/set', { changes: list }).then(awaitApproval).then(function (r) {
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

  // An answer of "approval_required" is followed until the user decides in Revit (or ten minutes pass).
  function awaitApproval(r) {
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
  }

  // Shared-memory review (the memory page): confirm, retire, promote, accept or reject. Same approval path as set.
  function memory(action, args) {
    var body = { action: action, ids: [].concat((args && args.ids) || []).map(String) };
    if (args && args.proposal) body.proposal = String(args.proposal);
    return post('/api/memory', body).then(awaitApproval).then(function (r) {
      if (r.ok) say(r.message || TEXT.memoryDone);
      else if (r.status === 'rejected') say(TEXT.rejected, true);
      else say(TEXT.notSaved + (r.message || r.status), true);
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

  // ── Table tools: search, column filters, sorting, grouping ───────────────
  // Works on the rows that are there; nothing is fetched. Rows that do not match get the hidden
  // attribute, which also keeps them out of "select all".
  function cellValue(cell) {
    return cell ? String(cell.getAttribute('data-sort') || cell.textContent || '').replace(/\s+/g, ' ').trim() : '';
  }
  function toNumber(text) {
    var m = /^-?\d+(?:[.,]\d+)?/.exec(text.replace(/[\s ]/g, ''));
    return m ? parseFloat(m[0].replace(',', '.')) : NaN;
  }
  var collator = (window.Intl && Intl.Collator) ? new Intl.Collator(root.lang || undefined, { numeric: true, sensitivity: 'base' }) : null;
  function compareText(a, b) { return collator ? collator.compare(a, b) : (a < b ? -1 : a > b ? 1 : 0); }

  function setupTable(table) {
    if (table.closest('[data-revit-tools="none"]')) return;
    var elements = !!table.querySelector('tr[data-revit-id]');
    if (!elements && table.getAttribute('data-revit-tools') !== 'rows') return;
    if (table.tBodies.length !== 1 || table.querySelector('td[rowspan], td[colspan], th[rowspan], th[colspan]')) return;
    var body = table.tBodies[0];
    var headRow = table.tHead && table.tHead.rows.length ? table.tHead.rows[table.tHead.rows.length - 1] : null;
    if (!headRow) {
      var first = body.rows[0];
      if (!first || first.hasAttribute('data-revit-id') || !first.querySelector('th')) return;
      table.createTHead().appendChild(first);   // sorting reorders the body; the header must not move with it
      headRow = first;
    }
    var heads = [].slice.call(headRow.cells);
    if (!heads.length) return;

    var state = { query: '', filters: {}, sort: -1, dir: 1, group: -1, collapsed: {}, filtersOpen: false };
    var order = 0;
    function dataRows() {
      return [].filter.call(body.rows, function (row) {
        if (row.classList.contains('revit-group')) return false;
        if (row.__revitOrder == null) row.__revitOrder = order++;
        return true;
      });
    }
    function numeric(column, rows) {
      var seen = false;
      for (var i = 0; i < rows.length; i++) {
        var text = cellValue(rows[i].cells[column]);
        if (!text) continue;
        if (isNaN(toNumber(text))) return false;
        seen = true;
      }
      return seen;
    }

    // Toolbar
    var tools = el('div', 'revit-tools');
    var search = el('input');
    search.type = 'search';
    search.placeholder = TEXT.search;
    search.setAttribute('aria-label', TEXT.search);
    var groupSelect = el('select');
    groupSelect.setAttribute('aria-label', TEXT.groupBy);
    var none = el('option', null, TEXT.noGroup);
    none.value = '-1';
    groupSelect.appendChild(none);
    heads.forEach(function (head, i) {
      var name = cellValue(head);
      if (!name) return;
      var option = el('option', null, TEXT.groupBy + ': ' + name);
      option.value = String(i);
      groupSelect.appendChild(option);
    });
    var filterButton = el('button', 'ghost', TEXT.filters);
    filterButton.type = 'button';
    var resetButton = el('button', 'ghost', TEXT.reset);
    resetButton.type = 'button';
    var count = el('span', 'revit-count');
    var selectButton = el('button', null, TEXT.selectShown);
    selectButton.type = 'button';
    [search, groupSelect, filterButton, resetButton, el('span', 'revit-spacer'), count].concat(elements ? [selectButton] : []).forEach(function (n) { tools.appendChild(n); });
    table.parentNode.insertBefore(tools, table);

    var filterRow = null;
    function buildFilters() {
      if (filterRow) filterRow.parentNode.removeChild(filterRow);
      filterRow = null;
      if (!state.filtersOpen) return;
      var rows = dataRows();
      filterRow = el('tr', 'revit-filters');
      heads.forEach(function (head, i) {
        var cell = el('td'), control;
        var distinct = [];
        rows.forEach(function (row) { var v = cellValue(row.cells[i]); if (distinct.indexOf(v) < 0) distinct.push(v); });
        var current = state.filters[i];
        if (distinct.length >= 2 && distinct.length <= 24 && distinct.length < rows.length) {
          control = el('select');
          var all = el('option', null, TEXT.all);
          all.value = '';
          control.appendChild(all);
          distinct.sort(compareText).forEach(function (v) {
            var option = el('option', null, v === '' ? TEXT.empty : v);
            option.value = '=' + v;
            control.appendChild(option);
          });
          control.value = current && current.exact ? '=' + current.value : '';
          control.addEventListener('change', function () {
            if (control.value === '') delete state.filters[i];
            else state.filters[i] = { exact: true, value: control.value.slice(1) };
            apply();
          });
        } else {
          control = el('input');
          control.type = 'search';
          control.placeholder = TEXT.contains;
          control.value = current && !current.exact ? current.value : '';
          control.addEventListener('input', function () {
            if (control.value.trim() === '') delete state.filters[i];
            else state.filters[i] = { exact: false, value: control.value.trim().toLowerCase() };
            apply();
          });
        }
        control.setAttribute('aria-label', cellValue(head));
        cell.appendChild(control);
        filterRow.appendChild(cell);
      });
      headRow.parentNode.appendChild(filterRow);
    }

    function matches(row) {
      if (state.query && row.textContent.toLowerCase().indexOf(state.query) < 0) return false;
      for (var key in state.filters) {
        var filter = state.filters[key], text = cellValue(row.cells[key]);
        if (filter.exact ? text !== filter.value : text.toLowerCase().indexOf(filter.value) < 0) return false;
      }
      return true;
    }

    function apply() {
      [].slice.call(body.querySelectorAll('tr.revit-group')).forEach(function (row) { body.removeChild(row); });
      var rows = dataRows();
      var sortNumeric = state.sort >= 0 && numeric(state.sort, rows);
      var groupNumeric = state.group >= 0 && numeric(state.group, rows);
      function compareBy(column, isNumber, a, b) {
        var x = cellValue(a.cells[column]), y = cellValue(b.cells[column]);
        if (x === '' || y === '') return x === y ? 0 : (x === '' ? 1 : -1);   // empty values last
        return isNumber ? toNumber(x) - toNumber(y) : compareText(x, y);
      }
      rows.sort(function (a, b) {
        var result = state.group >= 0 ? compareBy(state.group, groupNumeric, a, b) : 0;
        if (!result && state.sort >= 0) result = compareBy(state.sort, sortNumeric, a, b) * state.dir;
        return result || a.__revitOrder - b.__revitOrder;
      });

      var shown = 0, groups = [], current = null;
      rows.forEach(function (row) {
        var match = matches(row);
        row.__revitMatch = match;
        if (match) shown++;
        if (state.group >= 0 && match) {
          var key = cellValue(row.cells[state.group]);
          if (!current || current.key !== key) { current = { key: key, rows: [] }; groups.push(current); }
          current.rows.push(row);
        }
      });

      var fragment = document.createDocumentFragment();
      if (state.group >= 0) {
        groups.forEach(function (group) {
          var collapsed = !!state.collapsed[group.key];
          var header = el('tr', 'revit-group' + (collapsed ? ' collapsed' : ''));
          var cell = el('td');
          cell.colSpan = heads.length;
          cell.appendChild(el('span', 'revit-caret', collapsed ? '▸' : '▾'));
          cell.appendChild(el('b', null, group.key === '' ? TEXT.empty : group.key));
          cell.appendChild(el('span', 'badge', String(group.rows.length)));
          var pick = el('button', 'ghost', TEXT.selectGroup);
          pick.type = 'button';
          pick.addEventListener('click', function (event) {
            event.stopPropagation();
            select(group.rows.map(function (row) { return row.getAttribute('data-revit-id'); }).join(','));
          });
          if (elements) cell.appendChild(pick);
          header.appendChild(cell);
          header.addEventListener('click', function () { state.collapsed[group.key] = !collapsed; apply(); });
          fragment.appendChild(header);
          group.rows.forEach(function (row) { row.hidden = collapsed; fragment.appendChild(row); });
        });
        rows.forEach(function (row) { if (!row.__revitMatch) { row.hidden = true; fragment.appendChild(row); } });
      } else {
        rows.forEach(function (row) { row.hidden = !row.__revitMatch; fragment.appendChild(row); });
      }
      body.appendChild(fragment);

      heads.forEach(function (head, i) {
        if (state.sort === i) head.setAttribute('aria-sort', state.dir > 0 ? 'ascending' : 'descending');
        else head.removeAttribute('aria-sort');
      });
      count.textContent = shown === rows.length ? String(rows.length) : shown + ' / ' + rows.length;
      var active = state.query || state.sort >= 0 || state.group >= 0 || Object.keys(state.filters).length > 0;
      resetButton.hidden = !active;
      filterButton.classList.toggle('on', state.filtersOpen);
    }

    heads.forEach(function (head, i) {
      head.classList.add('revit-sortable');
      head.tabIndex = 0;
      if (!head.title) head.title = TEXT.sortHint;
      function cycle() {
        if (state.sort !== i) { state.sort = i; state.dir = 1; }
        else if (state.dir > 0) state.dir = -1;
        else state.sort = -1;
        apply();
      }
      head.addEventListener('click', cycle);
      head.addEventListener('keydown', function (event) { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); cycle(); } });
    });
    search.addEventListener('input', function () { state.query = search.value.trim().toLowerCase(); apply(); });
    groupSelect.addEventListener('change', function () { state.group = parseInt(groupSelect.value, 10); state.collapsed = {}; apply(); });
    filterButton.addEventListener('click', function () { state.filtersOpen = !state.filtersOpen; buildFilters(); apply(); });
    resetButton.addEventListener('click', function () {
      state.query = ''; state.filters = {}; state.sort = -1; state.group = -1; state.collapsed = {};
      search.value = ''; groupSelect.value = '-1';
      buildFilters();
      apply();
    });
    selectButton.addEventListener('click', function () {
      select(dataRows().filter(function (row) { return row.__revitMatch !== false && row.hasAttribute('data-revit-id'); })
        .map(function (row) { return row.getAttribute('data-revit-id'); }).join(','));
    });

    table.__revitTools = { apply: apply, active: function () { return state.sort >= 0 || state.group >= 0 || state.query || Object.keys(state.filters).length > 0; } };
    apply();
  }

  // New tables get their tools; a table already sorted, filtered or grouped is laid out again,
  // because live values may have changed what a row matches.
  function tables() {
    [].forEach.call(document.querySelectorAll('table'), function (table) {
      try {
        if (!table.__revitTools) setupTable(table);
        else if (table.__revitTools.active()) table.__revitTools.apply();
      } catch (e) { /* a table the tools cannot handle stays as the page made it */ }
    });
  }

  // ── Wiring ───────────────────────────────────────────────────────────────
  var clickTimer = null;
  document.addEventListener('click', function (event) {
    var target = event.target;
    if (!target || !target.closest || target.closest('#revit-bar, .revit-tools, .revit-filters, .revit-group, .revit-editing')) return;

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
    select: select, values: values, set: set, memory: memory, status: status, pageId: cfg.id,
    refresh: function () { return refresh(false); }
  };

  function start() { buildUi(); tables(); refresh(false); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
