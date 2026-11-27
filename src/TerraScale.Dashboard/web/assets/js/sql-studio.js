const host = document.querySelector('.ts-studio-sql');
const textarea = document.getElementById('sql');
const strip = document.getElementById('sql-tabs');
const databaseId = document.querySelector('main[data-database-id]')?.dataset.databaseId ?? '';
const storageKey = `ts-sql-worksheets:${databaseId}`;
const tabIcon = strip?.querySelector('.ts-studio-tab-icon')?.cloneNode(true) ?? null;
const addButton = strip?.querySelector('[data-new-sheet]')?.cloneNode(true) ?? null;

const load = () => {
  try {
    const raw = sessionStorage.getItem(storageKey);
    return raw === null ? null : JSON.parse(raw);
  } catch {
    return null;
  }
};

const valid = value => value !== null && typeof value === 'object' && Array.isArray(value.sheets)
  && value.sheets.every(sheet => typeof sheet?.id === 'string' && typeof sheet.title === 'string' && typeof sheet.sql === 'string'
    && (sheet.table === undefined || typeof sheet.table === 'string')
    && (sheet.savedQueryId === undefined || typeof sheet.savedQueryId === 'string'))
  && typeof value.active === 'string' && Number.isInteger(value.next);

const quoteIdent = name => `"${name.replaceAll('"', '""')}"`;

const stored = load();
const state = valid(stored) ? stored : { sheets: [], active: '', next: 1 };

const persist = () => {
  try {
    sessionStorage.setItem(storageKey, JSON.stringify(state));
  } catch {
  }
};

const create = (title, sql, table, savedQueryId) => {
  const number = state.next++;
  const sheet = { id: `sheet-${number}`, title: title ?? `Query ${number}`, sql };
  if (table) sheet.table = table;
  if (savedQueryId) sheet.savedQueryId = savedQueryId;
  state.sheets.push(sheet);
  return sheet;
};

const active = () => state.sheets.find(sheet => sheet.id === state.active);

const syncSaved = () => {
  const sql = active()?.sql;
  for (const link of document.querySelectorAll('#saved-queries [data-saved-query]')) {
    if (sql && link.title === sql) {
      link.setAttribute('aria-current', 'true');
    } else {
      link.removeAttribute('aria-current');
    }
  }
};

const render = () => {
  const items = state.sheets.map(sheet => {
    const item = document.createElement('span');
    item.className = 'ts-studio-tab-item';
    const tab = document.createElement('button');
    tab.type = 'button';
    tab.className = 'ts-studio-tab';
    tab.dataset.sheet = sheet.id;
    if (sheet.id === state.active) tab.setAttribute('aria-current', 'true');
    const label = document.createElement('span');
    label.className = 'ts-truncate';
    label.textContent = sheet.title;
    if (tabIcon) tab.append(tabIcon.cloneNode(true));
    tab.append(label);
    item.append(tab);
    if (state.sheets.length > 1) {
      const close = document.createElement('button');
      close.type = 'button';
      close.className = 'ts-studio-tab-close';
      close.dataset.closeSheet = sheet.id;
      close.setAttribute('aria-label', `Close ${sheet.title}`);
      close.textContent = '×';
      item.append(close);
    }
    return item;
  });
  strip.replaceChildren(...items, ...(addButton ? [addButton.cloneNode(true)] : []));
  syncSaved();
};

const editor = () => document.querySelector('#sql-editor ts-code-editor')?.editor ?? null;

const focusEditor = () => {
  const monaco = editor();
  if (monaco) {
    monaco.focus();
  } else {
    textarea.focus();
  }
};

const show = sheet => {
  state.active = sheet.id;
  persist();
  render();
  if (textarea.value !== sheet.sql) {
    textarea.value = sheet.sql;
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
  }
  focusEditor();
};

const insertKeywords = new Set([
  'SELECT', 'DISTINCT', 'FROM', 'WHERE', 'AND', 'OR', 'NOT', 'IN', 'IS',
  'BY', 'ON', 'AS', 'SET', 'HAVING', 'JOIN', 'LEFT', 'RIGHT', 'INNER',
  'OUTER', 'CROSS', 'WHEN', 'THEN', 'ELSE', 'CASE', 'BETWEEN', 'LIKE',
  'RETURNING', 'INTO', 'VALUES', 'UPDATE', 'DELETE', 'USING',
]);

const formatInsert = (value, start, end, ident) => {
  const before = value.slice(0, start);
  const after = value.slice(end);
  const previous = before.slice(-1);
  const next = after[0] ?? '';
  const lastWord = before.match(/([A-Za-z_][A-Za-z0-9_]*)$/)?.[1]?.toUpperCase() ?? '';
  let prefix = '';
  if (previous === ',') {
    prefix = ' ';
  } else if (lastWord && insertKeywords.has(lastWord)) {
    prefix = ' ';
  } else if (/[A-Za-z0-9_"`\]]/.test(previous)) {
    prefix = ', ';
  } else if (previous && !/[\s(.[]/.test(previous)) {
    prefix = ' ';
  }
  const suffix = next && !/[\s,;.()[\]]/.test(next) ? ' ' : '';
  return `${prefix}${ident}${suffix}`;
};

const insert = ident => {
  const monaco = editor();
  const model = monaco?.getModel();
  const selection = monaco?.getSelection();
  if (monaco && model && selection) {
    const start = model.getOffsetAt(selection.getStartPosition());
    const end = model.getOffsetAt(selection.getEndPosition());
    const text = formatInsert(model.getValue(), start, end, ident);
    monaco.pushUndoStop();
    monaco.executeEdits('sql-studio', [{ range: selection, text, forceMoveMarkers: true }]);
    monaco.setPosition(model.getPositionAt(start + text.length));
    monaco.pushUndoStop();
    monaco.focus();
    return;
  }
  const start = textarea.selectionStart;
  const end = textarea.selectionEnd;
  textarea.setRangeText(formatInsert(textarea.value, start, end, ident), start, end, 'end');
  textarea.dispatchEvent(new Event('input', { bubbles: true }));
  textarea.focus();
};

const savedTitle = id => {
  if (!id) return null;
  const link = [...document.querySelectorAll('#saved-queries [data-saved-query]')].find(item => item.dataset.savedQuery === id);
  return link?.querySelector('.ts-truncate')?.textContent.trim() || null;
};

const mount = () => {
  const params = new URL(location.href).searchParams;
  const hasResult = params.has('result');
  const savedId = !hasResult ? params.get('savedQueryId') : null;
  const queryTable = !hasResult && !savedId ? params.get('queryTable') : null;
  const queryColumn = queryTable ? params.get('column') : null;
  const current = textarea.value;
  let sheet = null;
  if (savedId) {
    const title = savedTitle(savedId);
    sheet = state.sheets.find(item => item.savedQueryId === savedId)
      ?? state.sheets.find(item => item.sql === current)
      ?? create(title, current, null, savedId);
    sheet.savedQueryId = savedId;
    if (title) sheet.title = title;
  } else if (queryTable) {
    const title = queryColumn ? `${queryTable}.${queryColumn}` : queryTable;
    sheet = queryColumn
      ? state.sheets.find(item => item.title === title) ?? create(title, current, null, null)
      : state.sheets.find(item => item.table === queryTable) ?? create(title, current, queryTable, null);
  } else {
    const matching = state.sheets.find(item => item.id === state.active && item.sql === current)
      ?? state.sheets.find(item => item.sql === current);
    const empty = active()?.sql === '' ? active() : null;
    sheet = matching ?? empty ?? create(null, current, null, null);
  }
  sheet.sql = current;
  state.active = sheet.id;
  persist();
  render();
};

if (host && textarea && strip) {
  mount();

  textarea.addEventListener('input', () => {
    const sheet = active();
    if (!sheet || sheet.sql === textarea.value) return;
    sheet.sql = textarea.value;
    persist();
    syncSaved();
  });

  strip.addEventListener('click', event => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;
    if (target.closest('[data-new-sheet]')) {
      show(create(null, '', null, null));
      return;
    }
    const close = target.closest('[data-close-sheet]');
    if (close) {
      const index = state.sheets.findIndex(sheet => sheet.id === close.dataset.closeSheet);
      if (index < 0 || state.sheets.length < 2) return;
      const [removed] = state.sheets.splice(index, 1);
      const neighbour = state.sheets[Math.min(index, state.sheets.length - 1)];
      if (removed.id === state.active) {
        show(neighbour);
      } else {
        persist();
        render();
        strip.querySelector(`[data-sheet="${CSS.escape(neighbour.id)}"]`)?.focus();
      }
      return;
    }
    const tab = target.closest('[data-sheet]');
    const sheet = tab && state.sheets.find(item => item.id === tab.dataset.sheet);
    if (sheet && sheet.id !== state.active) show(sheet);
  });

  host.addEventListener('click', event => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) return;
    const column = target.closest('[data-sql-insert]');
    if (column) {
      insert(quoteIdent(column.dataset.sqlInsert));
      return;
    }
    const table = target.closest('[data-sql-table]');
    if (table) {
      const name = table.dataset.sqlTable;
      show(state.sheets.find(sheet => sheet.table === name) ?? create(name, `SELECT *\nFROM ${quoteIdent(name)}\nLIMIT 100;`, name, null));
    }
  });

  document.addEventListener('datastar-fetch', event => {
    if (event.detail?.type !== 'datastar-patch-elements') return;
    queueMicrotask(syncSaved);
  });
}
