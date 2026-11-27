const page = document.getElementById('database-detail');
const databaseId = page?.dataset.databaseId ?? '';
const tabsKey = `ts-table-tabs:${databaseId}`;
const viewKey = `ts-table-view:${databaseId}`;
const endpoint = '/api/v1/dashboard/databases/tables/rows';
const maxEdits = 50;
const edits = new Map();
let editing = null;
let pending = null;
let deleting = null;
let saving = false;
let suppressLiveUntil = 0;
let tabIcon = null;
let tabAdd = null;

const load = (key, fallback) => {
  try {
    const raw = sessionStorage.getItem(key);
    return raw === null ? fallback : JSON.parse(raw);
  } catch {
    return fallback;
  }
};

const store = (key, value) => {
  try {
    sessionStorage.setItem(key, JSON.stringify(value));
  } catch {
  }
};

const parsed = (text, fallback) => {
  try {
    return JSON.parse(text);
  } catch {
    return fallback;
  }
};

const panel = () => document.getElementById('table-rows');
const quoteIdent = name => `"${name.replaceAll('"', '""')}"`;
const quoteText = value => `'${value.replaceAll("'", "''")}'`;
const plural = (count, word) => `${count.toLocaleString('en-US')} ${count === 1 ? word : `${word}s`}`;
const numeric = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/;
const numericType = type => /INT|REAL|FLOA|DOUB|NUMERIC|DECIMAL|BOOL/i.test(type ?? '');
const isNumeric = (type, origKind) => numericType(type) || (!type && origKind === 'number');
const busy = () => edits.size > 0 || editing !== null || saving;

const compactType = type => {
  const text = (type ?? '').trim().toUpperCase();
  if (text === '' || text === 'NO DECLARED TYPE') return '—';
  const name = text.split('(')[0].trim();
  if (['INTEGER', 'INT', 'BIGINT', 'SMALLINT', 'TINYINT', 'MEDIUMINT', 'INT2', 'INT8', 'UNSIGNED BIG INT'].includes(name)) return 'INT';
  if (['BOOLEAN', 'BOOL'].includes(name)) return 'BOOL';
  if (['CHARACTER VARYING', 'VARCHAR', 'VARYING CHARACTER', 'NVARCHAR', 'NCHAR', 'NATIVE CHARACTER', 'CHARACTER', 'CHAR', 'CLOB', 'TEXT'].includes(name)) return 'TEXT';
  if (['DOUBLE PRECISION', 'DOUBLE', 'FLOAT', 'REAL'].includes(name)) return 'REAL';
  if (['DECIMAL', 'NUMERIC'].includes(name)) return 'NUM';
  if (['DATETIME', 'TIMESTAMP'].includes(name)) return 'TIME';
  return name;
};

const compactCount = value => {
  const suffixes = ['', 'k', 'M', 'B', 'T'];
  let scaled = value;
  let tier = 0;
  while (Math.abs(scaled) >= 1000 && tier < suffixes.length - 1) {
    scaled /= 1000;
    tier++;
  }
  if (tier === 0) return Math.trunc(scaled).toLocaleString('en-US');
  let rounded = Math.round(scaled * 10) / 10;
  if (Math.abs(rounded) >= 1000 && tier < suffixes.length - 1) {
    rounded = Math.round(rounded / 100) / 10;
    tier++;
  }
  return `${rounded.toFixed(1)}${suffixes[tier]}`;
};

const primaryKey = rows => {
  const names = parsed(rows.dataset.primaryKey ?? '[]', []);
  return Array.isArray(names) ? names.filter(name => typeof name === 'string') : [];
};

const schema = rows => {
  const list = parsed(rows?.dataset.columns ?? '[]', []);
  return Array.isArray(list) ? list.filter(column => column !== null && typeof column === 'object' && typeof column.name === 'string') : [];
};

const columns = rows => [...rows.querySelectorAll('thead th[data-column]')].map(th => th.dataset.column);

const columnMeta = (rows, name) => schema(rows).find(column => column.name === name) ?? null;

const columnType = (rows, name) => rows.querySelector(`thead th[data-column="${CSS.escape(name)}"]`)?.dataset.type
  ?? columnMeta(rows, name)?.decltype ?? '';

const editable = rows => {
  if (rows?.dataset.editable !== '1') return false;
  const keys = primaryKey(rows);
  const names = columns(rows);
  return keys.length > 0 && keys.every(key => names.includes(key));
};

const originalText = cell => cell.dataset.original ?? cell.textContent;
const kindOf = cell => cell.dataset.originalKind ?? cell.dataset.kind;

const literal = cell => {
  const text = originalText(cell);
  if (kindOf(cell) === 'null') return 'null';
  if (kindOf(cell) === 'number' && numeric.test(text)) return text;
  return JSON.stringify(text);
};

const sqlLiteral = cell => {
  const text = originalText(cell);
  if (kindOf(cell) === 'null') return 'NULL';
  if (kindOf(cell) === 'number' && numeric.test(text)) return text;
  return quoteText(text);
};

const typedJson = (value, type, origKind = '') => {
  if (value === null) return 'null';
  return isNumeric(type, origKind) && numeric.test(value.trim()) ? value.trim() : JSON.stringify(value);
};

const typedSql = (value, type, origKind = '') => {
  if (value === null) return 'NULL';
  return isNumeric(type, origKind) && numeric.test(value.trim()) ? value.trim() : quoteText(value);
};

const keysOf = (rows, tr) => {
  const names = columns(rows);
  return primaryKey(rows).map(key => [key, tr.cells[names.indexOf(key)]]);
};

const rowEditable = (rows, tr) => {
  if (!editable(rows) || !tr || tr.dataset.rowEditable === '0') return false;
  const keys = keysOf(rows, tr);
  return keys.length > 0 && keys.every(([, cell]) => cell && (kindOf(cell) === 'string' || kindOf(cell) === 'number'));
};

const where = keys => keys.map(([key, cell]) => `${quoteIdent(key)} = ${sqlLiteral(cell)}`).join(' AND ');

const statement = (rows, edit) => `UPDATE ${quoteIdent(rows.dataset.tableName)} SET ${quoteIdent(edit.column)} = ${typedSql(edit.value, edit.type, edit.origKind)} WHERE ${where(edit.keys)};`;

const keyJson = keys => `{${keys.map(([key, cell]) => `${JSON.stringify(key)}:${literal(cell)}`).join(',')}}`;

const payload = () => `{"edits":[${[...edits.values()].map(edit => `{"keys":${keyJson(edit.keys)},"column":${JSON.stringify(edit.column)},"value":${typedJson(edit.value, edit.type, edit.origKind)}}`).join(',')}]}`;

const setStatus = text => {
  const status = document.getElementById('table-commit-status');
  if (status) status.textContent = text;
};

const renderCommit = () => {
  const bar = document.getElementById('table-commit');
  const rows = panel();
  if (!bar || !rows) return;
  bar.toggleAttribute('data-dirty', edits.size > 0);
  const first = edits.values().next().value;
  const sql = bar.querySelector('[data-commit-sql]');
  if (sql) sql.textContent = first ? statement(rows, first) : '';
  const count = bar.querySelector('[data-commit-count]');
  if (count) count.textContent = plural(edits.size, 'change');
  const apply = bar.querySelector('[data-commit-apply]');
  if (apply) apply.disabled = saving;
  const cancel = bar.querySelector('[data-commit-discard]');
  if (cancel) cancel.disabled = saving;
  const add = document.querySelector('.ts-studio-bar [commandfor="insert-row"]');
  if (add) add.disabled = edits.size > 0 || saving;
  document.querySelector('#table-tabs [aria-current="page"]')?.toggleAttribute('data-dirty', edits.size > 0);
};

const flush = () => {
  if (busy() || !pending) return;
  const detail = pending;
  pending = null;
  document.dispatchEvent(new CustomEvent('datastar-fetch', { detail }));
};

const visibleCells = rows => [...rows.querySelectorAll('tbody tr:not([hidden]) td[tabindex]')];

const focusCell = cell => {
  const rows = panel();
  if (!rows || !cell || !cell.isConnected) return;
  for (const other of rows.querySelectorAll('tbody td[tabindex="0"]')) {
    if (other !== cell) other.tabIndex = -1;
  }
  cell.tabIndex = 0;
  cell.focus();
};

const stepCell = (cell, direction) => {
  const rows = panel();
  if (!rows) return null;
  const cells = visibleCells(rows);
  return cells[cells.indexOf(cell) + direction] ?? null;
};

const moveCell = (cell, dx, dy) => {
  const tr = cell.parentElement;
  if (dx !== 0) {
    const next = tr.cells[cell.cellIndex + dx];
    return next?.hasAttribute('tabindex') ? next : null;
  }
  let row = tr;
  do {
    row = dy > 0 ? row.nextElementSibling : row.previousElementSibling;
  } while (row && (row.hidden || !row.cells[cell.cellIndex]?.hasAttribute('tabindex')));
  const next = row?.cells[cell.cellIndex];
  return next?.hasAttribute('tabindex') ? next : null;
};

const finishEdit = (apply, shouldFlush = true, forceNull = false) => {
  if (!editing) return;
  const { cell, input, key, column, type, nullable, origKind, tr, shown, original } = editing;
  editing = null;
  const value = input.value;
  input.remove();
  const nextValue = forceNull && nullable
    ? null
    : value === '' && nullable && (isNumeric(type, origKind) || origKind === 'null')
      ? null
      : value;
  const unchanged = origKind === 'null' ? nextValue === null : nextValue !== null && nextValue === original;
  if (!apply) {
    cell.textContent = shown;
  } else if (unchanged) {
    edits.delete(key);
    cell.textContent = original;
    if (cell.dataset.originalKind !== undefined) cell.dataset.kind = cell.dataset.originalKind;
    delete cell.dataset.originalKind;
    delete cell.dataset.original;
    delete cell.dataset.dirty;
  } else if (!edits.has(key) && edits.size >= maxEdits) {
    cell.textContent = shown;
    setStatus(`Commit or discard the ${maxEdits} staged changes before editing more cells.`);
  } else {
    const rows = panel();
    cell.dataset.original = original;
    cell.dataset.originalKind ??= cell.dataset.kind;
    if (nextValue === null) {
      cell.dataset.kind = 'null';
      cell.textContent = 'NULL';
    } else {
      cell.dataset.kind = isNumeric(type, origKind) && numeric.test(nextValue.trim()) ? 'number' : 'string';
      cell.textContent = nextValue;
    }
    cell.dataset.dirty = '';
    edits.set(key, { cell, column, type, origKind, value: nextValue, keys: keysOf(rows, tr) });
  }
  delete cell.dataset.editing;
  renderCommit();
  if (shouldFlush) flush();
};

const beginEdit = cell => {
  const rows = panel();
  const tr = cell.parentElement;
  if (saving || !rows || !rowEditable(rows, tr) || cell.dataset.editing !== undefined || cell.classList.contains('ts-studio-row-actions')) return;
  finishEdit(true, false);
  const column = columns(rows)[cell.cellIndex];
  if (column === undefined) return;
  const key = `${tr.sectionRowIndex}:${column}`;
  const meta = columnMeta(rows, column);
  const type = columnType(rows, column);
  const nullable = meta?.notNull !== true;
  const origKind = kindOf(cell) ?? 'string';
  const shown = cell.textContent;
  const original = originalText(cell);
  const input = document.createElement('input');
  input.className = 'ts-input ts-input-sm ts-mono ts-studio-cell-input';
  const staged = edits.get(key);
  input.value = staged ? (staged.value ?? '') : (origKind === 'null' ? '' : shown);
  input.setAttribute('aria-label', `${column} value`);
  if (nullable) input.placeholder = 'NULL';
  if (isNumeric(type, origKind)) input.inputMode = 'decimal';
  input.spellcheck = false;
  input.autocomplete = 'off';
  cell.dataset.editing = '';
  cell.replaceChildren(input);
  setStatus('');
  editing = { cell, input, key, column, type, nullable, origKind, tr, shown, original };
  input.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      finishEdit(true, false);
      focusCell(cell);
      flush();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      finishEdit(false, false);
      focusCell(cell);
      flush();
    } else if (event.key === 'Backspace' && event.altKey && nullable) {
      event.preventDefault();
      finishEdit(true, false, true);
      focusCell(cell);
      flush();
    } else if (event.key === 'Tab') {
      event.preventDefault();
      const next = stepCell(cell, event.shiftKey ? -1 : 1);
      finishEdit(true, false);
      if (next) {
        focusCell(next);
        beginEdit(next);
      } else {
        focusCell(cell);
        flush();
      }
    }
  });
  input.addEventListener('blur', () => {
    if (editing?.input === input) finishEdit(true, true);
  });
  input.focus();
  input.select();
};

const discard = () => {
  if (saving) return;
  finishEdit(false, false);
  for (const edit of edits.values()) {
    edit.cell.textContent = originalText(edit.cell);
    if (edit.cell.dataset.originalKind !== undefined) edit.cell.dataset.kind = edit.cell.dataset.originalKind;
    delete edit.cell.dataset.originalKind;
    delete edit.cell.dataset.original;
    delete edit.cell.dataset.dirty;
  }
  edits.clear();
  renderCommit();
  setStatus('Changes discarded.');
  flush();
};

const parseResponse = text => JSON.parse(text, (_, value, context) => typeof value === 'number' ? { number: context?.source ?? String(value) } : value);

const isNumber = value => typeof value === 'object' && value !== null && typeof value.number === 'string';
const numberValue = (value, fallback) => isNumber(value) ? Number(value.number) : typeof value === 'number' ? value : fallback;

const send = async body => {
  try {
    const response = await fetch(endpoint, { method: 'POST', body, headers: { accept: 'application/json' } });
    const text = await response.text();
    const result = (() => {
      try {
        return parseResponse(text);
      } catch {
        return null;
      }
    })();
    if (!response.ok || result?.ok !== true) {
      const detail = typeof result?.error === 'string' ? result.error : typeof result?.detail === 'string' ? result.detail : null;
      return { error: detail ?? `The change failed with HTTP ${response.status}.` };
    }
    return result;
  } catch {
    return { error: 'The change could not be sent. Check your connection and try again.' };
  }
};

const filterPanel = (container, query, emptyText) => {
  if (!container) return;
  const body = container.querySelector('tbody');
  let matches = 0;
  for (const tr of body?.rows ?? []) {
    const match = query === '' || [...tr.cells].some(cell => !cell.classList.contains('ts-studio-row-actions') && cell.textContent.toLowerCase().includes(query));
    tr.hidden = !match;
    if (match) matches++;
  }
  let empty = container.querySelector('[data-filter-empty]');
  if (body && query !== '' && matches === 0) {
    if (!empty) {
      empty = document.createElement('p');
      empty.className = 'ts-studio-filter-empty';
      empty.dataset.filterEmpty = '';
      empty.setAttribute('role', 'status');
      container.append(empty);
    }
    empty.textContent = emptyText;
  } else {
    empty?.remove();
  }
};

const applyFilter = () => {
  const input = document.getElementById('table-filter');
  const rows = panel();
  if (!input || !rows) return;
  const query = input.value.trim().toLowerCase();
  filterPanel(rows, query, 'No rows match the filter.');
  filterPanel(document.getElementById('table-columns'), query, 'No columns match the filter.');
  const stop = rows.querySelector('tbody td[tabindex="0"]');
  if (stop?.parentElement.hidden) stop.tabIndex = -1;
  if (!stop || stop.parentElement.hidden) {
    const first = visibleCells(rows)[0];
    if (first) first.tabIndex = 0;
  }
};

const updateCount = (rows, count, shown) => {
  const counter = document.querySelector('[data-table-count]');
  if (counter) counter.textContent = count > shown ? `First ${shown.toLocaleString('en-US')} of ${count.toLocaleString('en-US')} rows` : plural(count, 'row');
  const rail = document.querySelector(`#tables-browser [data-table="${CSS.escape(rows.dataset.tableName ?? '')}"] .ts-studio-rail-count`);
  if (!rail) return;
  rail.title = count.toLocaleString('en-US');
  const unit = document.createElement('span');
  unit.className = 'ts-sr-only';
  unit.textContent = count === 1 ? ' row' : ' rows';
  rail.replaceChildren(document.createTextNode(compactCount(count)), unit);
};

const cellFor = (value, number, focusable) => {
  const td = document.createElement('td');
  if (number) td.className = 'ts-num';
  if (focusable) td.tabIndex = -1;
  if (value === null) {
    td.textContent = 'NULL';
    td.dataset.kind = 'null';
  } else if (isNumber(value)) {
    td.textContent = value.number;
    td.dataset.kind = 'number';
  } else if (typeof value === 'string') {
    td.textContent = value;
    td.dataset.kind = 'string';
  } else if (Array.isArray(value)) {
    td.textContent = JSON.stringify(value);
    td.dataset.kind = 'array';
  } else if (typeof value === 'object') {
    td.textContent = JSON.stringify(value);
    td.dataset.kind = 'object';
  } else {
    td.textContent = JSON.stringify(value);
    td.dataset.kind = 'string';
  }
  return td;
};

const actionCell = (tag, template) => {
  const cell = document.createElement(tag);
  cell.className = 'ts-studio-row-actions';
  if (tag === 'th') {
    cell.scope = 'col';
    const label = document.createElement('span');
    label.className = 'ts-sr-only';
    label.textContent = 'Actions';
    cell.append(label);
  } else if (template) {
    cell.append(template.cloneNode(true));
  }
  return cell;
};

const renderRows = result => {
  const rows = panel();
  if (!rows) return;
  if (!Array.isArray(result.rows)) {
    location.reload();
    return;
  }
  const count = numberValue(result.rowCount, result.rows.length);
  updateCount(rows, count, result.rows.length);
  if (result.rows.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'ts-empty';
    empty.setAttribute('role', 'status');
    const title = document.createElement('p');
    title.className = 'ts-empty-title';
    title.textContent = 'No rows yet.';
    empty.append(title);
    rows.replaceChildren(empty);
    return;
  }
  const deleteButton = document.getElementById('table-row-delete-template')?.content.firstElementChild?.cloneNode(true) ?? rows.querySelector('[data-delete-row]')?.cloneNode(true) ?? null;
  const resultColumns = (result.columns ?? []).map(column => typeof column === 'string' ? { name: column, decltype: '' } : { name: column?.name ?? '', decltype: column?.decltype ?? '' });
  const types = resultColumns.map(column => column.decltype || (schema(rows).find(item => item.name === column.name)?.decltype ?? ''));
  const numbers = resultColumns.map((_, index) => numericType(types[index])
    || result.rows.every(row => row[index] === null || isNumber(row[index])) && result.rows.some(row => isNumber(row[index])));
  const keys = primaryKey(rows);
  const keyIndexes = keys.map(key => resultColumns.findIndex(column => column.name === key));
  const canEdit = rows.dataset.editable === '1' && keys.length > 0 && keyIndexes.every(index => index >= 0);
  const wrap = document.createElement('div');
  wrap.className = 'ts-table-wrap ts-data-result-wrap';
  wrap.setAttribute('role', 'region');
  wrap.tabIndex = 0;
  wrap.setAttribute('aria-labelledby', 'table-columns-title');
  const table = document.createElement('table');
  table.className = 'ts-table ts-data-result ts-studio-grid';
  const head = table.createTHead().insertRow();
  resultColumns.forEach((column, index) => {
    const th = document.createElement('th');
    th.scope = 'col';
    if (numbers[index]) th.className = 'ts-num';
    th.dataset.column = column.name;
    if (types[index]) th.dataset.type = types[index];
    const type = document.createElement('span');
    type.className = 'ts-studio-col-type';
    type.textContent = compactType(types[index]);
    th.append(document.createTextNode(column.name), type);
    head.append(th);
  });
  if (canEdit) head.append(actionCell('th'));
  const body = table.createTBody();
  for (const row of result.rows) {
    const tr = body.insertRow();
    const canEditRow = canEdit && keyIndexes.every(index => typeof row[index] === 'string' || isNumber(row[index]));
    tr.dataset.rowEditable = canEditRow ? '1' : '0';
    row.forEach((value, index) => tr.append(cellFor(value, numbers[index], canEditRow)));
    if (canEdit) tr.append(actionCell('td', canEditRow ? deleteButton : null));
  }
  const first = body.querySelector('td[tabindex]');
  if (first) first.tabIndex = 0;
  wrap.append(table);
  rows.replaceChildren(wrap);
  applyFilter();
};

const formBody = (rows, operation, data) => new URLSearchParams({
  _csrf: rows.dataset.csrf ?? '',
  databaseId: rows.dataset.databaseId ?? '',
  table: rows.dataset.tableName ?? '',
  operation,
  payload: data,
});

const commit = async () => {
  if (saving) return;
  finishEdit(true, false);
  const rows = panel();
  if (!rows || edits.size === 0) return;
  const total = edits.size;
  saving = true;
  renderCommit();
  setStatus(`Committing ${plural(total, 'change')}…`);
  const result = await send(formBody(rows, 'update', payload()));
  saving = false;
  if (result.error) {
    renderCommit();
    setStatus(result.error);
    return;
  }
  const affected = numberValue(result.rowsAffected, 0);
  const expected = numberValue(result.expectedRows, 0);
  const matched = numberValue(result.matchedRows, affected);
  edits.clear();
  pending = null;
  suppressLiveUntil = Date.now() + 800;
  renderCommit();
  renderRows(result);
  if (affected === 0) {
    setStatus('No rows matched — the row may have changed or been deleted. Reloaded table.');
  } else if (expected > 0 && matched < expected) {
    setStatus(`Updated ${plural(matched, 'row')} of ${expected} — some rows may have changed or been deleted. Reloaded table.`);
  } else {
    setStatus(`Committed ${plural(total, 'change')}.`);
  }
};

const applyView = view => {
  const mode = view === 'schema' ? 'schema' : 'data';
  if (page) page.dataset.studioView = mode;
  for (const button of document.querySelectorAll('button[data-table-view]')) {
    button.setAttribute('aria-pressed', String(button.dataset.tableView === mode));
  }
};

const openInsert = () => {
  const rows = panel();
  const dialog = document.getElementById('insert-row');
  const form = document.querySelector('[data-insert-row]');
  const fields = form?.querySelector('[data-insert-fields]');
  if (!rows || !form || !fields) return;
  if (busy()) {
    dialog?.close();
    setStatus('Commit or discard the staged changes before adding a row.');
    return;
  }
  form.querySelector('[data-insert-error]').hidden = true;
  fields.replaceChildren(...schema(rows).map((column, index) => {
    const id = `insert-row-${index}`;
    const field = document.createElement('div');
    field.className = 'ts-field';
    const label = document.createElement('label');
    label.className = 'ts-label ts-mono';
    label.htmlFor = id;
    label.textContent = column.name;
    const input = document.createElement('input');
    input.className = 'ts-input ts-mono';
    input.id = id;
    input.dataset.insertColumn = column.name;
    input.dataset.insertType = typeof column.decltype === 'string' ? column.decltype : '';
    input.placeholder = typeof column.defaultValue === 'string' ? column.defaultValue : input.dataset.insertType;
    input.required = column.notNull === true && column.defaultValue == null && !(column.primaryKey > 0);
    if (numericType(input.dataset.insertType)) input.inputMode = 'decimal';
    input.autocomplete = 'off';
    input.spellcheck = false;
    field.append(label, input);
    return field;
  }));
};

const insert = async form => {
  const rows = panel();
  const alert = form.querySelector('[data-insert-error]');
  const submit = form.querySelector('[type="submit"]');
  if (!rows || busy()) {
    setStatus('Commit or discard the staged changes before adding a row.');
    return;
  }
  const values = [...form.querySelectorAll('[data-insert-column]')]
    .filter(input => input.value !== '')
    .map(input => `${JSON.stringify(input.dataset.insertColumn)}:${typedJson(input.value, input.dataset.insertType)}`);
  saving = true;
  submit.disabled = true;
  renderCommit();
  const result = await send(formBody(rows, 'insert', `{"values":{${values.join(',')}}}`));
  saving = false;
  submit.disabled = false;
  renderCommit();
  if (result.error) {
    alert.querySelector('p').textContent = result.error;
    alert.hidden = false;
    return;
  }
  alert.hidden = true;
  form.reset();
  form.closest('dialog')?.close();
  applyView('data');
  store(viewKey, 'data');
  pending = null;
  suppressLiveUntil = Date.now() + 800;
  renderRows(result);
  const affected = numberValue(result.rowsAffected, 1);
  setStatus(affected === 0 ? 'No row was added. Reloaded table.' : 'Added 1 row.');
};

const askDelete = button => {
  const rows = panel();
  const dialog = document.getElementById('delete-row');
  const tr = button.closest('tr');
  if (!rows || !dialog || !tr || !rowEditable(rows, tr)) return;
  if (busy()) {
    setStatus('Commit or discard the staged changes before deleting a row.');
    return;
  }
  const keys = keysOf(rows, tr);
  if (keys.length === 0 || keys.some(([, cell]) => !cell)) return;
  deleting = { json: keyJson(keys), index: tr.sectionRowIndex };
  dialog.querySelector('[data-delete-sql]').textContent = `DELETE FROM ${quoteIdent(rows.dataset.tableName ?? '')} WHERE ${where(keys)};`;
  dialog.querySelector('[data-delete-error]').hidden = true;
  dialog.showModal();
};

const remove = async form => {
  const rows = panel();
  const target = deleting;
  const alert = form.querySelector('[data-delete-error]');
  const submit = form.querySelector('[type="submit"]');
  if (!rows || !target || busy()) return;
  saving = true;
  submit.disabled = true;
  renderCommit();
  const result = await send(formBody(rows, 'delete', `{"keys":${target.json}}`));
  saving = false;
  submit.disabled = false;
  renderCommit();
  if (result.error) {
    alert.querySelector('p').textContent = result.error;
    alert.hidden = false;
    return;
  }
  deleting = null;
  form.closest('dialog')?.close();
  pending = null;
  suppressLiveUntil = Date.now() + 800;
  renderRows(result);
  const affected = numberValue(result.rowsAffected, 0);
  setStatus(affected === 0 ? 'Row was already deleted or changed. Reloaded table.' : 'Deleted 1 row.');
  const remaining = panel()?.querySelectorAll('tbody tr') ?? [];
  focusCell(remaining[Math.min(target.index, remaining.length - 1)]?.querySelector('td[tabindex]') ?? null);
};

const tabLink = (strip, name, current) => {
  const link = document.createElement('a');
  link.className = 'ts-studio-tab';
  link.href = `${strip.dataset.tablesPath}?table=${encodeURIComponent(name)}`;
  link.dataset.openTable = name;
  if (current) link.setAttribute('aria-current', 'page');
  const label = document.createElement('span');
  label.className = 'ts-truncate ts-mono';
  label.textContent = name;
  if (tabIcon) link.append(tabIcon.cloneNode(true));
  link.append(label);
  return link;
};

const renderTabs = () => {
  const strip = document.getElementById('table-tabs');
  const current = panel()?.dataset.tableName;
  if (!strip || !current) return;
  tabIcon ??= strip.querySelector('.ts-studio-rail-icon')?.cloneNode(true) ?? null;
  tabAdd ??= strip.querySelector('.ts-studio-tab-add')?.cloneNode(true) ?? null;
  const known = new Set([...document.querySelectorAll('#tables-browser [data-table]')].map(item => item.dataset.table));
  const saved = load(tabsKey, []);
  const open = (Array.isArray(saved) ? saved : []).filter(name => typeof name === 'string' && known.has(name));
  if (!open.includes(current)) open.push(current);
  store(tabsKey, open);
  const items = open.map(name => {
    const item = document.createElement('span');
    item.className = 'ts-studio-tab-item';
    item.append(tabLink(strip, name, name === current));
    if (open.length > 1) {
      const close = document.createElement('button');
      close.type = 'button';
      close.className = 'ts-studio-tab-close';
      close.dataset.closeTable = name;
      close.setAttribute('aria-label', `Close ${name}`);
      close.textContent = '×';
      item.append(close);
    }
    return item;
  });
  strip.replaceChildren(...items, ...(tabAdd ? [tabAdd.cloneNode(true)] : []));
  renderCommit();
};

const closeTab = name => {
  const strip = document.getElementById('table-tabs');
  const current = panel()?.dataset.tableName;
  const saved = load(tabsKey, []);
  const open = Array.isArray(saved) ? saved.filter(item => typeof item === 'string') : [];
  const index = open.indexOf(name);
  const remaining = open.filter(item => item !== name);
  store(tabsKey, remaining);
  if (name !== current || !strip) {
    renderTabs();
    strip?.querySelector('[aria-current="page"]')?.focus();
    return;
  }
  const next = remaining[Math.min(index, remaining.length - 1)];
  const link = next ? strip.querySelector(`[data-open-table="${CSS.escape(next)}"]`) : null;
  location.assign(link?.href ?? strip.dataset.tablesPath);
};

window.addEventListener('datastar-fetch', event => {
  const { type, argsRaw } = event.detail ?? {};
  if (type !== 'datastar-patch-elements') return;
  if (!String(argsRaw?.elements ?? '').includes('id="tables-browser"')) return;
  if (busy()) {
    event.stopImmediatePropagation();
    pending = event.detail;
  } else if (Date.now() < suppressLiveUntil) {
    event.stopImmediatePropagation();
  }
}, true);

document.addEventListener('datastar-fetch', event => {
  const { type, argsRaw } = event.detail ?? {};
  if (type !== 'datastar-patch-elements' || !String(argsRaw?.elements ?? '').includes('id="tables-browser"')) return;
  queueMicrotask(() => {
    renderTabs();
    applyFilter();
  });
});

document.addEventListener('click', event => {
  const target = event.target;
  if (!(target instanceof Element)) return;
  const view = target.closest('button[data-table-view]');
  if (view) {
    applyView(view.dataset.tableView);
    store(viewKey, view.dataset.tableView);
    return;
  }
  const close = target.closest('button[data-close-table]');
  if (close) {
    closeTab(close.dataset.closeTable);
    return;
  }
  if (target.closest('[commandfor="insert-row"][command="show-modal"]')) {
    openInsert();
    return;
  }
  const removal = target.closest('[data-delete-row]');
  if (removal) {
    askDelete(removal);
    return;
  }
  if (target.closest('[data-commit-discard]')) {
    discard();
    return;
  }
  const apply = target.closest('[data-commit-apply]');
  if (apply) {
    commit();
    return;
  }
  const cell = target.closest('#table-rows tbody td');
  if (cell && !target.closest('input')) beginEdit(cell);
});

const arrows = { ArrowRight: [1, 0], ArrowLeft: [-1, 0], ArrowDown: [0, 1], ArrowUp: [0, -1] };

document.addEventListener('keydown', event => {
  const cell = event.target instanceof HTMLTableCellElement && event.target.matches('#table-rows tbody td[tabindex]') ? event.target : null;
  if (!cell || event.altKey || event.ctrlKey || event.metaKey) return;
  if (event.key === 'Enter' || event.key === 'F2') {
    event.preventDefault();
    beginEdit(cell);
    return;
  }
  const delta = arrows[event.key];
  if (!delta) return;
  event.preventDefault();
  const next = moveCell(cell, delta[0], delta[1]);
  if (next) focusCell(next);
});

document.addEventListener('focusin', event => {
  const cell = event.target instanceof HTMLTableCellElement && event.target.matches('#table-rows tbody td[tabindex]') ? event.target : null;
  if (cell && cell.tabIndex !== 0) focusCell(cell);
});

document.addEventListener('input', event => {
  if (event.target instanceof HTMLInputElement && event.target.id === 'table-filter') applyFilter();
});

document.addEventListener('submit', event => {
  const form = event.target instanceof HTMLFormElement ? event.target : null;
  if (form?.matches('[data-insert-row]')) {
    event.preventDefault();
    insert(form);
  } else if (form?.matches('[data-delete-row-form]')) {
    event.preventDefault();
    remove(form);
  }
});

document.getElementById('delete-row')?.addEventListener('close', () => {
  deleting = null;
});

addEventListener('beforeunload', event => {
  if (edits.size > 0) event.preventDefault();
});

applyView(load(viewKey, 'data'));
renderTabs();
applyFilter();
