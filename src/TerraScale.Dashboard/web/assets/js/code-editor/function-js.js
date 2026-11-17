const documented = (text, example, signature) => `/** ${text}\n * @example ${example}\n */ ${signature}`;
const sqlLimit = 'Shares the invocation budget of 256 SQL calls and 10 s wall time.';
const logLimit = 'Shares the invocation limit of 50 lines, each truncated to 1,024 characters.';
const handlerHelp = 'Handle an HTTP or cron event within 10 s; return JSON or {status, body}. Response limit: 1 MiB.';

export const extraLib = [
  documented('A JSON value returned by a handler.', '{ ok: true }', 'type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };'),
  documented('A SQL value; binary values use base64.', '{ base64: "aGk=" }', 'type SqlValue = null | boolean | number | string | { base64: string };'),
  documented('Positional or named bindings; placeholders accept :name, @name or $name.', '[42]; ({ name: "Ada" })', 'type SqlArgs = SqlValue[] | Record<string, SqlValue> | null;'),
  'interface SqlExecuteResult {',
  documented('Column names in result order.', 'result.columns[0]', 'columns: string[];'),
  documented('Rows as arrays in column order.', 'result.rows[0][0]', 'rows: SqlValue[][];'),
  documented('Number of rows changed.', 'result.rowsAffected', 'rowsAffected: number;'),
  documented('Last inserted row ID, or null.', 'result.lastInsertRowid', 'lastInsertRowid: number | null;'),
  '}',
  'interface SqlStatement {',
  documented('SQL text for one statement.', '({ sql: "SELECT ?", args: [42] })', 'sql: string;'),
  documented('Positional bindings in placeholder order; null means no bindings.', '({ sql: "SELECT ?", args: [42] })', 'args?: SqlValue[] | null;'),
  documented('Named bindings without the placeholder prefix; null means no bindings.', '({ sql: "SELECT :name", named: { name: "Ada" } })', 'named?: Record<string, SqlValue> | null;'),
  '}',
  'interface SqlTransaction {',
  documented(`Run a statement; rows are arrays. Commits on its own outside a transaction. ${sqlLimit}`, 'await sql.execute("INSERT INTO items (name) VALUES (?)", ["Ada"])', 'execute(text: string | SqlStatement, args?: SqlArgs): Promise<SqlExecuteResult>;'),
  documented(`Read rows as objects keyed by column name. ${sqlLimit}`, 'await sql.query("SELECT :name AS name", { name: "Ada" })', 'query(text: string | SqlStatement, args?: SqlArgs): Promise<Record<string, SqlValue>[]>;'),
  documented(`Run 1–256 statements together in one transaction; one batch consumes one SQL call. ${sqlLimit}`, 'await sql.batch(["SELECT 1", { sql: "SELECT ?", args: [42] }])', 'batch(statements: Iterable<string | SqlStatement>): Promise<SqlExecuteResult[]>;'),
  '}',
  'interface Sql extends SqlTransaction {',
  documented(`Commit when the callback returns; roll back when it throws. Begin, commit and rollback each consume a SQL call. ${sqlLimit}`, 'await sql.transaction(async tx => { await tx.execute("INSERT INTO items (name) VALUES (?)", ["Ada"]); })', 'transaction<T>(work: (tx: SqlTransaction) => T | Promise<T>): Promise<T>;'),
  '}',
  'interface SqlError extends Error {',
  documented('Identifies an error from SQL.', 'error.name === "SqlError"', 'name: "SqlError";'),
  documented('Stable SQL error code.', 'error.code === "sql_sandbox"', 'code: string;'),
  documented('Reason the SQL call failed.', 'console.error(error.message)', 'message: string;'),
  documented('Zero-based index of the failed statement.', 'error.statement === 0', 'statement: number;'),
  documented('SQLSTATE supplied by the SQL engine.', 'error.sqlstate === "42501"', 'sqlstate: string;'),
  '}',
  'interface HttpEvent {',
  documented('An invocation through POST …/invoke.', 'event.trigger === "http"', 'trigger: "http";'),
  documented('Parsed JSON, otherwise text; null for an empty body. Request limit: 1 MiB.', 'event.body', 'body: JsonValue;'),
  documented('Query string values; repeated values are comma-joined.', 'event.query.name', 'query: Record<string, string>;'),
  '}',
  'interface CronEvent {',
  documented('An invocation from the UTC schedule.', 'event.trigger === "cron"', 'trigger: "cron";'),
  documented('Planned execution time as an ISO timestamp.', 'console.log(event.scheduledAt)', 'scheduledAt: string;'),
  '}',
  documented('Narrow by trigger to read HTTP or cron fields.', 'if (event.trigger === "http") console.log(event.body)', 'type FunctionEvent = HttpEvent | CronEvent;'),
  'interface FunctionResponse {',
  documented('HTTP status from 200 to 599; other values leave the result as ordinary JSON with status 200.', 'return { status: 201, body: { ok: true } }', 'status: number;'),
  documented('Strings are plain text, null is empty, other values are JSON. Serialized handler result limit: 1,048,576 characters.', 'return { status: 200, body: { ok: true } }', 'body: JsonValue;'),
  '}',
  documented(handlerHelp, 'async function handler(event) { return { ok: true }; }', 'type FunctionHandler = (event: FunctionEvent) => JsonValue | FunctionResponse | void | Promise<JsonValue | FunctionResponse | void>;'),
  documented(`SQL on this function's database. ${sqlLimit}`, 'await sql.query("SELECT 1 AS ok")', 'declare const sql: Sql;'),
  'declare const console: {',
  ...['log', 'info', 'warn', 'error'].map(level => documented(`Write values to the invocation log. ${logLimit}`, `console.${level}("rows", 3)`, `${level}(...values: unknown[]): void;`)),
  '};',
].join('\n');

const typedHandler = '/** @param {FunctionEvent} event */\nasync function handler(event) {\n';
const snippets = [
  ['HTTP handler', 'Respond to POST …/invoke with JSON', typedHandler + '  if (event.trigger !== "http") return;\n  return { status: ${1:200}, body: { ${2:ok}: ${3:true} } };\n}\n$0'],
  ['Cron handler', 'Run on a schedule; event.scheduledAt is the planned time', typedHandler + '  if (event.trigger !== "cron") return;\n  console.log(event.scheduledAt);\n  ${1:await sql.query("SELECT 1 AS ok");}\n}\n$0'],
  ['Query rows', 'Rows as objects keyed by column name', 'const ${1:rows} = await sql.query("SELECT * FROM ${2:items} WHERE ${3:id} = ?", [${4:1}]);$0'],
  ['Query with named args', 'Bind :name, @name or $name from an object', 'const ${1:rows} = await sql.query("SELECT * FROM ${2:items} WHERE name = :name", { name: "${3:Ada}" });$0'],
  ['Batch', 'Run statements in one transaction', 'const ${1:results} = await sql.batch([\n  { sql: "INSERT INTO ${2:items} (name) VALUES (?)", args: ["${3:Ada}"] },\n  { sql: "SELECT * FROM ${2:items} WHERE name = :name", named: { name: "${3:Ada}" } },\n]);$0'],
  ['Transaction', 'Commit when the callback returns, roll back when it throws', 'const ${1:result} = await sql.transaction(async tx => {\n  const rows = await tx.query("SELECT * FROM ${2:items} WHERE id = ?", [${3:1}]);\n  if (rows.length === 0) throw new Error("${4:Item not found}");\n  await tx.execute("UPDATE ${2:items} SET name = ? WHERE id = ?", ["${5:Ada}", ${3:1}]);\n  return rows;\n});$0'],
  ['Handle SQL error', 'Catch SqlError and branch on its code', 'try {\n  ${1:await sql.query("SELECT 1");}\n} catch (error) {\n  if (!(error instanceof Error) || error.name !== "SqlError") throw error;\n  const sqlError = /** @type {SqlError} */ (error);\n  if (sqlError.code !== "${2:sql_sandbox}") throw error;\n  console.error(sqlError.message);\n  $0\n}'],
  ['Custom response', 'Return a status code and JSON body', 'return { status: ${1:201}, body: { ${2:ok}: ${3:true} } };$0'],
  ['Read request', 'Use event.body and event.query', 'if (event.trigger === "http") {\n  const ${1:body} = event.body;\n  const ${2:name} = event.query.${3:name};\n  $0\n}'],
];

const keywords = ['SELECT', 'FROM', 'WHERE', 'AND', 'OR', 'NOT', 'IN', 'IS', 'NULL', 'ORDER BY', 'ASC', 'DESC', 'LIMIT', 'TRUE', 'FALSE', 'INSERT INTO', 'VALUES', 'UPDATE', 'SET', 'DELETE FROM', 'JOIN', 'ON', 'GROUP BY', 'HAVING', 'AS', 'CREATE TABLE', 'RETURNING'];
const eventFields = [...extraLib.matchAll(/\/\*\* ((?:(?!\*\/)[\s\S])*?)\*\/ (trigger|body|query|scheduledAt): ([^;]+);/g)]
  .map(match => ({ name: match[2], type: match[2] === 'trigger' ? '"http" | "cron"' : match[3], doc: match[1].replaceAll(/^ \* ?/gm, '').trim() }))
  .filter((field, index, fields) => fields.findIndex(other => other.name === field.name) === index);
const tokens = /\/\/[^\n]*|\/\*[\s\S]*?(?:\*\/|$)|"(?:\\[\s\S]|[^"\\])*"?|'(?:\\[\s\S]|[^'\\])*'?|`(?:\\[\s\S]|[^`\\])*`?/g;
const codeTokens = new RegExp(`${tokens.source}|[\\w$]+|[^\\s]`, 'g');

function sqlContext(source) {
  const parts = [...source.matchAll(codeTokens)].filter(part => !part[0].startsWith('//') && !part[0].startsWith('/*'));
  const stack = [];
  for (let index = 0; index < parts.length; index++) {
    const token = parts[index][0];
    const parent = stack.at(-1);
    if (token === '(') {
      const receiver = parts[index - 3]?.[0];
      const member = parts[index - 1]?.[0];
      const binding = receiver === 'sql' || stack.some(frame => frame.binding === receiver);
      const method = binding && parts[index - 2]?.[0] === '.' ? member : null;
      const callback = method === 'transaction' && source.slice(parts[index].index + 1).match(/^\s*(?:async\s+)?(?:\(\s*([\w$]+)\s*\)|([\w$]+))\s*=>|^\s*(?:async\s+)?function\s*(?:[\w$]+\s*)?\(\s*([\w$]+)\s*\)/);
      stack.push({ token, method, argument: 0, binding: callback ? callback[1] ?? callback[2] ?? callback[3] : null });
    } else if (token === '[' || token === '{') {
      const firstArgument = parent?.token === '(' && parent.argument === 0;
      const batch = token === '[' && firstArgument && parent.method === 'batch';
      const statement = token === '{' && (parent?.batch || (firstArgument && ['query', 'execute'].includes(parent.method)));
      stack.push({ token, batch, statement, property: null });
    } else if (token === ')' || token === ']' || token === '}') {
      stack.pop();
    } else if (token === ',' && parent) {
      parent.argument++;
      parent.property = null;
    } else if (token === ':' && parent?.statement) {
      parent.property = parts[index - 1]?.[0].replace(/^["']|["']$/g, '');
    }
  }
  const context = stack.at(-1);
  return Boolean(context?.batch || (context?.statement && context.property === 'sql')
    || (context?.token === '(' && context.argument === 0 && ['execute', 'query'].includes(context.method)));
}

const models = new WeakSet();
let languageService;
let registered = false;

export async function attach(api, editor) {
  const model = editor.getModel();
  languageService ??= import('modern-monaco/lsp/typescript/setup').then(module => module.setup(api, 'javascript', {
    compilerOptions: { allowJs: true, checkJs: true, strict: true, noImplicitAny: false, useUnknownInCatchVariables: false, lib: ['es2023'], target: 'es2023', moduleDetection: 'legacy' },
    extraLibs: { 'file:///terrascale-function.d.ts': { content: extraLib, version: 1 } },
  }, { tabSize: 2, insertSpaces: true }));
  await languageService;
  if (model.isDisposed()) return () => {};
  register(api);
  models.add(model);
  editor.updateOptions({
    wordBasedSuggestions: 'off',
    snippetSuggestions: 'top',
    quickSuggestions: { other: true, comments: false, strings: true },
    bracketPairColorization: { enabled: true },
  });
  const save = editor.getOption(api.editor.EditorOption.readOnly) ? null : editor.addAction({
    id: 'terrascale.saveFunction',
    label: 'Deploy function',
    keybindings: [api.KeyMod.CtrlCmd | api.KeyCode.KeyS],
    run: () => editor.getDomNode().closest('ts-code-editor').querySelector('textarea').form?.requestSubmit(),
  });
  return () => {
    models.delete(model);
    save?.dispose();
  };
}

function register(api) {
  if (registered) return;
  registered = true;
  const { CompletionItemKind: Kind, CompletionItemInsertTextRule: Rule } = api.languages;
  api.languages.registerCompletionItemProvider('javascript', {
    triggerCharacters: ['.', ' ', '"', "'"],
    provideCompletionItems(model, position) {
      if (!models.has(model)) return { suggestions: [] };
      const before = model.getValue().slice(0, model.getOffsetAt(position));
      const last = [...before.matchAll(tokens)].at(-1);
      const inLiteral = last && last.index + last[0].length === before.length;
      const sqlString = inLiteral && /^["'`]/.test(last[0]) && (last[0].length === 1 || !last[0].endsWith(last[0][0]))
        && sqlContext(before.slice(0, last.index)) && !last[0].includes('${');
      const word = model.getWordAtPosition(position) ?? model.getWordUntilPosition(position);
      const range = { startLineNumber: position.lineNumber, startColumn: word.startColumn, endLineNumber: position.lineNumber, endColumn: word.endColumn };
      if (sqlString) {
        return { suggestions: keywords.map(keyword => ({ label: keyword, kind: Kind.Keyword, insertText: keyword, range })) };
      }
      if (inLiteral) return { suggestions: [] };
      if (/\bevent\.\w*$/.test(before) && !/\/\*\*[^]*?@param\s*\{[^}]+\}\s*event/.test(before)) {
        return { suggestions: eventFields.map(field => ({ label: field.name, detail: field.type, documentation: field.doc, kind: Kind.Property, insertText: field.name, range })) };
      }
      if (/\.\w*$/.test(before)) return { suggestions: [] };
      return {
        suggestions: snippets.map(([label, detail, insertText]) => ({
          label, detail, documentation: detail, kind: Kind.Snippet, insertText,
          insertTextRules: Rule.InsertAsSnippet, range,
        })),
      };
    },
  });
  api.languages.registerHoverProvider('javascript', {
    provideHover(model, position) {
      if (!models.has(model)) return null;
      const word = model.getWordAtPosition(position);
      if (!word) return null;
      if (word.word === 'handler') return { contents: [{ value: '`handler(event: FunctionEvent): ReturnType<FunctionHandler>`' }, { value: `${handlerHelp}\n\n@example async function handler(event) { return { ok: true }; }` }] };
      if (!/\bevent\.$/.test(model.getLineContent(position.lineNumber).slice(0, word.startColumn - 1))) return null;
      const field = eventFields.find(field => field.name === word.word);
      return field ? { contents: [{ value: `\`\`\`typescript\n${field.name}: ${field.type}\n\`\`\`` }, { value: field.doc }] } : null;
    },
  });
}
