// SQL support for editors inside an element with data-sql-catalog: completion
// of SQL tables, columns and TerraBase keywords, hovers, inline problems from
// the server's SQL check, query formatting, and a per-database run history.
// The page embeds the catalog and the first diagnostics as JSON scripts, so
// opening the console makes no extra request.

const checkDelay = 400;
const lineBreaks = new Set(['SELECT', 'FROM', 'WHERE', 'ORDER', 'GROUP', 'HAVING', 'LIMIT', 'UNION']);

// Quoted values and names stay whole, and so do dotted field paths,
// hyphenated table names, compound SQL operators and $1.
const tokenPattern = /--[^\n]*|\/\*[\s\S]*?(?:\*\/|$)|'(?:[^']|'')*'?|"(?:[^"]|"")*"?|`(?:[^`]|``)*`?|\[[^\]]*\]?|[A-Za-z_][A-Za-z0-9_]*(?:[.-][A-Za-z0-9_]+)*\.?|\$\d+|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|->>|->|\|\||<<|>>|==|<=|>=|!=|<>|\S/g;
const tokenize = text => [...text.matchAll(tokenPattern)].map(match => ({ text: match[0], start: match.index, end: match.index + match[0].length }));
const isName = token => /^[A-Za-z_]/.test(token?.text ?? '');
const isComment = token => token.text.startsWith('--') || token.text.startsWith('/*');
// tokenizeCode leaves out comments, which name nothing.
const tokenizeCode = text => tokenize(text).filter(token => !isComment(token));

// Per model: the catalog, the form, and a refresh of its markers.
const contexts = new WeakMap();
let registered = false;

export function attach(api, editor, textarea, host) {
  const model = editor.getModel();
  if (!model || !textarea.form) {
    return () => {};
  }
  const catalog = readJSON(host.dataset.sqlCatalog) ?? { keywords: [], tables: [] };
  const context = {
    catalog,
    form: textarea.form,
    tables: new Map(catalog.tables.map(table => [table.name, table])),
    keywords: new Set(catalog.keywords.flatMap(keyword => keyword.split(' '))),
  };
  contexts.set(model, context);
  register(api);
  editor.updateOptions({ wordBasedSuggestions: 'off', snippetSuggestions: 'top', quickSuggestions: { other: true, comments: false, strings: false } });

  const disposables = [];
  let timer;
  let request;
  const mark = server => {
    api.editor.setModelMarkers(model, 'terrascale-sql', server.map(diagnostic => {
      const start = model.getPositionAt(diagnostic.from);
      const end = model.getPositionAt(Math.max(diagnostic.to, diagnostic.from + 1));
      return {
        severity: diagnostic.severity === 'error' ? api.MarkerSeverity.Error : api.MarkerSeverity.Warning,
        message: diagnostic.message,
        startLineNumber: start.lineNumber,
        startColumn: start.column,
        endLineNumber: end.lineNumber,
        endColumn: end.column,
      };
    }));
  };
  const check = async () => {
    request?.abort();
    const current = (request = new AbortController());
    const version = model.getVersionId();
    const fresh = () => request === current && !model.isDisposed() && model.getVersionId() === version;
    // URL-encoded, as the CSRF check reads the token from a parsed form.
    const body = new URLSearchParams(new FormData(context.form));
    body.set('sql', model.getValue());
    // A check that could not run says so instead of looking clean.
    const failed = reason => [{ severity: 'warning', message: `The input check did not run: ${reason}`, from: 0, to: model.getValueLength() }];
    let response;
    try {
      response = await fetch(host.dataset.sqlCheck, { method: 'POST', body, headers: { Accept: 'application/json' }, signal: current.signal });
    } catch (error) {
      if (error.name !== 'AbortError' && fresh()) {
        mark(failed('the dashboard could not be reached.'));
      }
      return;
    }
    let diagnostics;
    try {
      diagnostics = response.ok ? await response.json() : failed((await response.text()).trim() || `HTTP ${response.status}`);
    } catch {
      diagnostics = failed('the dashboard answered with an unreadable result.');
    }
    if (fresh()) {
      mark(diagnostics);
    }
  };
  // The page carries the server's check of the SQL it rendered; text edited
  // before this module loaded is checked now, later edits once typing pauses.
  const initial = readJSON(host.dataset.sqlDiagnostics);
  if (initial?.sql === model.getValue()) {
    mark(initial.diagnostics);
  } else {
    check();
  }
  disposables.push(model.onDidChangeContent(() => {
    clearTimeout(timer);
    timer = setTimeout(check, checkDelay);
  }));

  const format = context.form.querySelector('[data-sql-format]');
  if (format) {
    const run = () => editor.getAction('editor.action.formatDocument')?.run();
    format.addEventListener('click', run);
    format.hidden = false;
    disposables.push({
      dispose: () => {
        format.removeEventListener('click', run);
        format.hidden = true;
      },
    });
  }
  const history = context.form.querySelector('select[data-sql-history]');
  if (history) {
    disposables.push(runHistory(editor, history));
  }

  return () => {
    clearTimeout(timer);
    request?.abort();
    disposables.forEach(disposable => disposable.dispose());
    contexts.delete(model);
  };
}

function readJSON(id) {
  try {
    return JSON.parse(document.getElementById(id)?.textContent ?? 'null');
  } catch {
    return null;
  }
}

// Providers are per language, so they are registered once and serve every
// model that has a context.
function register(api) {
  if (registered) {
    return;
  }
  registered = true;
  const { CompletionItemKind: Kind, CompletionItemInsertTextRule: Rule, CompletionTriggerKind: Trigger } = api.languages;

  api.languages.registerCompletionItemProvider('sql', {
    triggerCharacters: ['.', ' '],
    provideCompletionItems(model, position, trigger) {
      const context = contexts.get(model);
      if (!context) {
        return { suggestions: [] };
      }
      const offset = model.getOffsetAt(position);
      const before = model.getValue().slice(0, offset);
      // Nothing completes inside a comment.
      const lastToken = tokenize(before).at(-1);
      if (lastToken && isComment(lastToken) && lastToken.end === before.length && (lastToken.text.startsWith('--') || !lastToken.text.slice(2).endsWith('*/'))) {
        return { suggestions: [] };
      }
      // A table name may contain hyphens; a field path, dots.
      const name = before.match(/[A-Za-z0-9_-]*$/)[0];
      const nameTokens = tokenizeCode(before.slice(0, offset - name.length));
      const beforeName = nameTokens.at(-1)?.text.toUpperCase();
      const inFrom = (beforeName === 'FROM' && clauseFrom(nameTokens, nameTokens.length - 1)) || beforeName === 'JOIN';
      const word = inFrom ? name : before.match(/[A-Za-z0-9_.]*$/)[0];
      const tokens = tokenizeCode(before.slice(0, offset - word.length));
      const last = tokens.at(-1);
      const previous = last?.text.toUpperCase();
      // Nothing completes inside a quoted value or name.
      const closer = { "'": "'", '"': '"', '`': '`', '[': ']' }[last?.text[0]];
      if (closer && (last.text.length === 1 || !last.text.endsWith(closer))) {
        return { suggestions: [] };
      }
      if (previous === 'LIMIT' || (trigger.triggerKind === Trigger.TriggerCharacter && trigger.triggerCharacter === ' ' && !inFrom)) {
        return { suggestions: [] };
      }
      const dot = word.lastIndexOf('.');
      const start = model.getPositionAt(offset - word.length + dot + 1);
      // Completing inside a name replaces all of it: tot|al becomes total.
      const rest = model.getValue().slice(offset).match(inFrom ? /^[A-Za-z0-9_-]*/ : /^[A-Za-z0-9_]*/)[0];
      const range = { startLineNumber: start.lineNumber, startColumn: start.column, endLineNumber: position.lineNumber, endColumn: position.column + rest.length };

      if (inFrom) {
        return {
          suggestions: context.catalog.tables.map(table => ({
            label: table.name,
            kind: Kind.Module,
            detail: 'SQL table',
            documentation: tableSummary(table),
            insertText: sqlName(table.name),
            filterText: table.name,
            range,
          })),
        };
      }
      const table = statementTable(context, model.getValue());
      const fields = table?.fields ?? [];
      if (dot >= 0) {
        // a.b. completes the members of a.b.
        const prefix = word.slice(0, dot + 1);
        return {
          suggestions: fields.filter(field => field.path.startsWith(prefix) && !field.path.slice(prefix.length).includes('.')).map(field => fieldItem(Kind, table, field, field.path.slice(prefix.length), range)),
        };
      }
      const suggestions = [
        ...fields.map(field => fieldItem(Kind, table, field, field.path, range)),
        ...context.catalog.keywords.map(keyword => ({ label: keyword, kind: Kind.Keyword, insertText: keyword, sortText: `2${keyword}`, range })),
      ];
      if (tokens.length === 0 || tokens.at(-1).text === ';') {
        // The default table is snippet text, where $, } and \ are syntax.
        const name = sqlName(table?.name ?? 'table').replace(/[\\$}]/g, '\\$&');
        suggestions.push(...snippets(Kind, Rule, name, range));
      }
      return { suggestions };
    },
  });

  api.languages.registerHoverProvider('sql', {
    provideHover(model, position) {
      const context = contexts.get(model);
      const offset = model.getOffsetAt(position);
      const token = context && tokenize(model.getValue()).find(token => isName(token) && token.start <= offset && offset <= token.end);
      if (!token) {
        return null;
      }
      const range = { startLineNumber: position.lineNumber, startColumn: model.getPositionAt(token.start).column, endLineNumber: position.lineNumber, endColumn: model.getPositionAt(token.end).column };
      const table = context.tables.get(token.text);
      if (table) {
        return { range, contents: [{ value: `**${table.name}** SQL table` }, { value: tableSummary(table) }] };
      }
      // Hovering a in a.b.c names a; hovering c names the whole path.
      const segment = token.text.slice(offset - token.start).match(/^[A-Za-z0-9_]*/)[0];
      const path = token.text.slice(0, offset - token.start) + segment;
      const owner = statementTable(context, model.getValue());
      const field = owner?.fields.find(field => field.path === path);
      if (!field) {
        return null;
      }
      return { range, contents: [{ value: `**${field.path}**: \`${field.type}\`` }, { value: fieldSource(owner) }] };
    },
  });

  api.languages.registerDocumentFormattingEditProvider('sql', {
    provideDocumentFormattingEdits(model) {
      const context = contexts.get(model);
      return context ? [{ range: model.getFullModelRange(), text: formatSQL(model.getValue(), context.keywords) }] : [];
    },
  });
}

function fieldItem(Kind, table, field, label, range) {
  return {
    label,
    kind: Kind.Field,
    detail: field.type,
    documentation: `${field.path}: ${field.type}\n${fieldSource(table)}`,
    insertText: sqlName(label),
    sortText: `1${field.path.startsWith('_') ? '~' : ''}${field.path}`,
    range,
  };
}

function snippets(Kind, Rule, table, range) {
  return [
    ['sel', 'SELECT … FROM … LIMIT', 'Read rows from the current table', `SELECT \${1:*} FROM \${2:${table}} LIMIT \${3:20}`],
    ['sel', 'SELECT … FROM … WHERE … LIMIT', 'Read rows that match', `SELECT \${1:*} FROM \${2:${table}}\nWHERE \${3:field} = \${4:'value'}\nLIMIT \${5:20}`],
    ['sel', 'SELECT … FROM … ORDER BY … LIMIT', 'Read rows in order', `SELECT \${1:*} FROM \${2:${table}}\nORDER BY \${3:field} \${4|DESC,ASC|}\nLIMIT \${5:20}`],
    ['count', 'SELECT COUNT(*)', 'Count rows', `SELECT COUNT(*) FROM \${1:${table}}`],
    ['group', 'GROUP BY with COUNT(*)', 'Count rows per group', `SELECT \${1:name}, COUNT(*) AS count FROM \${2:${table}}\nGROUP BY \${1:name}\nORDER BY count DESC\nLIMIT \${3:20}`],
    ['group', 'GROUP BY … HAVING', 'Keep groups that match', `SELECT \${1:name}, COUNT(*) AS count FROM \${2:${table}}\nGROUP BY \${1:name}\nHAVING COUNT(*) > \${3:1}`],
    ['sel', 'SELECT DISTINCT', 'Read distinct values', `SELECT DISTINCT \${1:name} FROM \${2:${table}}`],
    ['sel', 'LIKE', 'Read rows that match a pattern', `SELECT * FROM \${1:${table}}\nWHERE \${2:name} LIKE \${3:'%value%'}`],
    ['sel', 'BETWEEN', 'Read rows in a range', `SELECT * FROM \${1:${table}}\nWHERE \${2:created_at} BETWEEN \${3:'2020-01-01'} AND \${4:'2030-01-01'}`],
    ['sel', 'LIMIT … OFFSET', 'Read a page of rows', `SELECT * FROM \${1:${table}} LIMIT \${2:20} OFFSET \${3:0}`],
    ['sel', 'CASE WHEN … THEN … ELSE … END', 'Map values with CASE', `SELECT CASE WHEN \${1:score} > \${2:0} THEN \${3:'high'} ELSE \${4:'low'} END FROM \${5:${table}}`],
    ['dup', 'Find duplicates', 'Find repeated values', `SELECT \${1:name}, COUNT(*) AS count FROM \${2:${table}}\nGROUP BY \${1:name}\nHAVING COUNT(*) > 1`],
    ['sel', 'json_extract', 'Read a JSON field', `SELECT json_extract(\${1:field}, \${2:'\$.path'}) FROM \${3:${table}}`],
    ['join', 'INNER JOIN … ON', 'Join another table on a key', `SELECT * FROM \${1:${table}} INNER JOIN \${2:other} ON \${1:${table}}.\${3:id} = \${2:other}.\${4:id}`],
    ['lj', 'LEFT JOIN … ON', 'Keep rows without a match', `SELECT * FROM \${1:${table}} LEFT JOIN \${2:other} ON \${1:${table}}.\${3:id} = \${2:other}.\${4:id}`],
    ['cte', 'WITH cte AS (…) SELECT', 'Read through a named result', `WITH \${1:cte} AS (\n  SELECT * FROM \${2:${table}}\n)\nSELECT * FROM \${1:cte}`],
    ['explain', 'EXPLAIN QUERY PLAN', 'Show how the query runs', `EXPLAIN QUERY PLAN SELECT * FROM \${1:${table}}`],
    ['schema', 'sqlite_schema', 'List tables, views and indexes', 'SELECT name, type FROM sqlite_schema'],
    ['pragma', 'PRAGMA table_info', 'List columns of the current table', `PRAGMA table_info(\${1:${table}})`],
    ['ins', 'INSERT one row', 'Insert one row', `INSERT INTO \${1:${table}} (\${2:id}, \${3:name}) VALUES (\${4:1}, \${5:'value'})`],
    ['ins', 'INSERT several rows', 'Insert several rows', `INSERT INTO \${1:${table}} (\${2:id}, \${3:name}) VALUES (\${4:1}, \${5:'value'}), (\${6:2}, \${7:'value'})`],
    ['ins', 'INSERT … ON CONFLICT DO UPDATE', 'Insert or update the matching row', `INSERT INTO \${1:${table}} (\${2:id}, \${3:name}) VALUES (\${4:1}, \${5:'value'})\nON CONFLICT(\${2:id}) DO UPDATE SET \${3:name} = excluded.\${3:name}`],
    ['ins', 'INSERT … RETURNING *', 'Insert a row and return it', `INSERT INTO \${1:${table}} (\${2:id}, \${3:name}) VALUES (\${4:1}, \${5:'value'}) RETURNING *`],
    ['upd', 'UPDATE … SET … WHERE', 'Update rows that match', `UPDATE \${1:${table}} SET \${2:name} = \${3:'value'} WHERE \${4:id} = \${5:1}`],
    ['del', 'DELETE FROM … WHERE', 'Delete rows that match', `DELETE FROM \${1:${table}} WHERE \${2:id} = \${3:1}`],
    ['ct', 'CREATE TABLE', 'Create a table', `CREATE TABLE \${1:${table}} (\n  id INTEGER PRIMARY KEY,\n  \${2:name} \${3|INTEGER,TEXT,REAL,BLOB|} \${4:NOT NULL}\n)`],
    ['ct', 'CREATE TABLE IF NOT EXISTS', 'Create a table if it is missing', `CREATE TABLE IF NOT EXISTS \${1:${table}} (\n  id INTEGER PRIMARY KEY,\n  created_at TEXT DEFAULT CURRENT_TIMESTAMP\n)`],
    ['ct', 'CREATE TABLE … AS SELECT', 'Create a table from a query', `CREATE TABLE \${1:copy} AS SELECT * FROM \${2:${table}}`],
    ['ci', 'CREATE INDEX', 'Index a column', `CREATE INDEX \${1:idx} ON \${2:${table}} (\${3:id})`],
    ['ci', 'CREATE UNIQUE INDEX', 'Index unique values', `CREATE UNIQUE INDEX \${1:uidx} ON \${2:${table}} (\${3:name})`],
    ['cv', 'CREATE VIEW … AS SELECT', 'Save a query as a view', `CREATE VIEW \${1:view_name} AS SELECT * FROM \${2:${table}}`],
    ['drop', 'DROP TABLE IF EXISTS', 'Drop a table if it exists', `DROP TABLE IF EXISTS \${1:${table}}`],
    ['drop', 'DROP INDEX IF EXISTS', 'Drop an index if it exists', `DROP INDEX IF EXISTS \${1:idx}`],
    ['drop', 'DROP VIEW IF EXISTS', 'Drop a view if it exists', `DROP VIEW IF EXISTS \${1:view_name}`],
  ].map(([prefix, label, detail, insertText]) => ({
    label,
    kind: Kind.Snippet,
    detail,
    documentation: detail,
    insertText,
    insertTextRules: Rule.InsertAsSnippet,
    filterText: `${prefix} ${label}`,
    sortText: `0${label}`,
    range,
  }));
}

// Quote SQL identifiers, including reserved words, spaces and embedded quotes.
function sqlName(name) {
  return `"${name.replaceAll('"', '""')}"`;
}

// Words after which a field stands, so a FROM there is a field named from.
const fieldStarts = new Set(['SELECT', ',', '(', 'WHERE', 'AND', 'OR', 'NOT', 'BY']);

// clauseFrom reports whether tokens[i], spelled FROM, starts the FROM clause:
// it does not stand where a field does, nor where FROM names its table.
function clauseFrom(tokens, i) {
  const before = tokens[i - 1]?.text.toUpperCase();
  return !fieldStarts.has(before) && !(before === 'FROM' && clauseFrom(tokens, i - 1));
}

// fromTargets are the table names after the FROM clause keyword, quoted
// or not, located in the text.
function fromTargets(text) {
  const tokens = tokenizeCode(text);
  return tokens.flatMap((token, i) => {
    const next = tokens[i + 1];
    if (token.text.toUpperCase() !== 'FROM' || !next || !clauseFrom(tokens, i)) {
      return [];
    }
    if (isName(next)) {
      return [{ name: next.text, from: next.start, to: next.end }];
    }
    // A quoted name doubles its closing quote to contain it; one still
    // being typed names nothing yet.
    const close = { '"': '"', '`': '`', '[': ']' }[next.text[0]];
    if (!close || next.text.length < 2 || !next.text.endsWith(close)) {
      return [];
    }
    const name = next.text.slice(1, -1);
    return [{ name: close === ']' ? name : name.replaceAll(close + close, close), from: next.start + 1, to: next.end - 1 }];
  });
}

// The table after FROM.
function statementTable(context, text) {
  return context.tables.get(fromTargets(text)[0]?.name);
}

function tableSummary(table) {
  return `${table.fields.length} declared columns`;
}

function fieldSource(table) {
  return `Declared in SQL table ${table.name}.`;
}

// formatSQL puts each clause on its own line and each AND or OR on an
// indented one. It changes only whitespace: names are case-sensitive, and a
// field may be named like a keyword (desc, limit).
function formatSQL(text, keywords) {
  let out = '';
  let previous = null;
  let previousEnd = -1;
  for (const { text: token, start, end } of tokenize(text)) {
    // A table keeps its line even when it reads as a keyword.
    const upper = previous?.toUpperCase() === 'FROM' ? '' : token.toUpperCase();
    if (previous === null) {
      out = token;
    } else if (start === previousEnd && !previous.startsWith('--')) {
      // Keep adjacent lexical fragments together (blob literals, .5, :name, etc.).
      out += token;
    } else if (previous.startsWith('--') || lineBreaks.has(upper)) {
      // A line comment ends at its line; comments are kept as written.
      out += `\n${token}`;
    } else if (upper === 'AND' || upper === 'OR') {
      out += `\n  ${token}`;
    } else if (token === ',' || token === ')' || token === ';' || previous === '(' || (token === '(' && !keywords.has(previous.toUpperCase()))) {
      out += token;
    } else {
      out += ` ${token}`;
    }
    previous = token;
    previousEnd = end;
  }
  return out;
}

// runHistory loads a statement from the console's run history, which the
// server renders and the form posts back, into the editor.
function runHistory(editor, select) {
  const onChange = () => {
    const model = editor.getModel();
    if (select.value && model) {
      editor.executeEdits('history', [{ range: model.getFullModelRange(), text: select.value }]);
      editor.focus();
    }
    select.value = '';
  };
  select.addEventListener('change', onChange);
  select.hidden = false;
  return {
    dispose: () => {
      select.removeEventListener('change', onChange);
      select.hidden = true;
    },
  };
}
