// SQL support for editors inside an element with data-sql-catalog: completion
// of collections, fields and TerraBase keywords, hovers, inline problems from
// the server's SQL check, query formatting, and a per-database run history.
// The page embeds the catalog and the first diagnostics as JSON scripts, so
// opening the console makes no extra request.

const checkDelay = 400;
const lineBreaks = new Set(['SELECT', 'FROM', 'WHERE', 'ORDER', 'GROUP', 'HAVING', 'LIMIT', 'UNION']);

// Quoted values and names stay whole, and so do dotted field paths,
// hyphenated collection names, compound SQL operators and $1.
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
  const catalog = readJSON(host.dataset.sqlCatalog) ?? { keywords: [], collections: [] };
  const context = {
    catalog,
    form: textarea.form,
    collections: new Map(catalog.collections.map(collection => [collection.name, collection])),
    keywords: new Set(catalog.keywords.flatMap(keyword => keyword.split(' '))),
  };
  contexts.set(model, context);
  register(api);
  editor.updateOptions({ wordBasedSuggestions: 'off', quickSuggestions: { other: true, comments: false, strings: false } });

  const disposables = [];
  let timer;
  let request;
  const mark = server => {
    api.editor.setModelMarkers(model, 'terrascale-sql', [...server, ...localDiagnostics(context, model.getValue())].map(diagnostic => {
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
      // A collection name may contain hyphens; a field path, dots.
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
          suggestions: context.catalog.collections.map(collection => ({
            label: collection.name,
            kind: Kind.Module,
            detail: documentCount(collection),
            documentation: collectionSummary(collection),
            insertText: sqlName(collection.name),
            filterText: collection.name,
            range,
          })),
        };
      }
      const collection = statementCollection(context, model.getValue());
      const fields = collection?.fields ?? [];
      if (dot >= 0) {
        // a.b. completes the members of a.b.
        const prefix = word.slice(0, dot + 1);
        return {
          suggestions: fields.filter(field => field.path.startsWith(prefix) && !field.path.slice(prefix.length).includes('.')).map(field => fieldItem(Kind, collection, field, field.path.slice(prefix.length), range)),
        };
      }
      const suggestions = [
        ...fields.map(field => fieldItem(Kind, collection, field, field.path, range)),
        ...context.catalog.keywords.map(keyword => ({ label: keyword, kind: Kind.Keyword, insertText: keyword, sortText: `2${keyword}`, range })),
      ];
      if (tokens.length === 0) {
        // The default collection is snippet text, where $, } and \ are syntax.
        const name = sqlName(collection?.name ?? 'collection').replace(/[\\$}]/g, '\\$&');
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
      const collection = context.collections.get(token.text);
      if (collection) {
        return { range, contents: [{ value: `**${collection.name}** collection` }, { value: collectionSummary(collection) }] };
      }
      // Hovering a in a.b.c names a; hovering c names the whole path.
      const segment = token.text.slice(offset - token.start).match(/^[A-Za-z0-9_]*/)[0];
      const path = token.text.slice(0, offset - token.start) + segment;
      const owner = statementCollection(context, model.getValue());
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

function fieldItem(Kind, collection, field, label, range) {
  return {
    label,
    kind: Kind.Field,
    detail: field.type,
    documentation: `${field.path}: ${field.type}\n${fieldSource(collection)}`,
    insertText: label,
    sortText: `1${field.path.startsWith('_') ? '~' : ''}${field.path}`,
    range,
  };
}

function snippets(Kind, Rule, collection, range) {
  return [
    ['SELECT … FROM … LIMIT', `SELECT \${1:*} FROM \${2:${collection}} LIMIT \${3:20}`],
    ['SELECT … FROM … WHERE … LIMIT', `SELECT \${1:*} FROM \${2:${collection}}\nWHERE \${3:field} = \${4:'value'}\nLIMIT \${5:20}`],
    ['SELECT … FROM … ORDER BY … LIMIT', `SELECT \${1:*} FROM \${2:${collection}}\nORDER BY \${3:field} \${4|DESC,ASC|}\nLIMIT \${5:20}`],
  ].map(([label, insertText]) => ({ label, kind: Kind.Snippet, insertText, insertTextRules: Rule.InsertAsSnippet, sortText: `0${label}`, range }));
}

// sqlName writes a collection name as SQL: as it is when it reads as one
// name (single hyphens between parts included, as the dashboard runs them),
// double-quoted otherwise, as for spaces or a -- that would start a comment.
function sqlName(name) {
  return /^[A-Za-z_][A-Za-z0-9_]*(?:-[A-Za-z0-9_]+)*$/.test(name) ? name : `"${name.replaceAll('"', '""')}"`;
}

// Words after which a field stands, so a FROM there is a field named from.
const fieldStarts = new Set(['SELECT', ',', '(', 'WHERE', 'AND', 'OR', 'NOT', 'BY']);

// clauseFrom reports whether tokens[i], spelled FROM, starts the FROM clause:
// it does not stand where a field does, nor where FROM names its collection.
function clauseFrom(tokens, i) {
  const before = tokens[i - 1]?.text.toUpperCase();
  return !fieldStarts.has(before) && !(before === 'FROM' && clauseFrom(tokens, i - 1));
}

// fromTargets are the collection names after the FROM clause keyword, quoted
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

// The collection after FROM, or else the one the console has open.
function statementCollection(context, text) {
  return context.collections.get(fromTargets(text)[0]?.name ?? context.form.elements.collection?.value);
}

function documentCount(collection) {
  if (collection.approximate && collection.documents === 0) {
    return 'collection';
  }
  const count = `${collection.documents.toLocaleString()} document${collection.documents === 1 ? '' : 's'}`;
  return collection.approximate ? `at least ${count}` : count;
}

function collectionSummary(collection) {
  const fields = collection.fields.filter(field => !field.path.startsWith('_')).length;
  return `${documentCount(collection)} · ${fields} field${fields === 1 ? '' : 's'} ${collection.source === 'schema' ? 'in its schema' : 'in sampled documents'}`;
}

function fieldSource(collection) {
  return collection.source === 'schema' ? `Declared in the ${collection.name} schema.` : `Inferred from recent ${collection.name} documents.`;
}

// Problems the catalog shows without asking the server: unknown collections.
function localDiagnostics(context, text) {
  if (context.catalog.collections.length === 0) {
    return [];
  }
  return fromTargets(text).flatMap(({ name, from, to }) => (context.collections.has(name) ? [] : [{ severity: 'warning', message: `This database has no collection named ${name}.`, from, to }]));
}

// formatSQL puts each clause on its own line and each AND or OR on an
// indented one. It changes only whitespace: names are case-sensitive, and a
// field may be named like a keyword (desc, limit).
function formatSQL(text, keywords) {
  let out = '';
  let previous = null;
  let previousEnd = -1;
  for (const { text: token, start, end } of tokenize(text)) {
    // A collection keeps its line even when it reads as a keyword.
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
