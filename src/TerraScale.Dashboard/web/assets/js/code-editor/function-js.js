const documented = (text, signature) => `/** ${text} */ ${signature}`;

const extraLib = [
  'interface SqlExecuteResult { columns: string[]; rows: unknown[][]; rowsAffected: number; lastInsertRowid: number | null; }',
  'interface SqlStatement { sql: string; args?: unknown[] | Record<string, unknown>; named?: boolean; }',
  'interface SqlTransaction {',
  'execute(text: string, args?: unknown[] | Record<string, unknown>): Promise<SqlExecuteResult>;',
  'query(text: string, args?: unknown[] | Record<string, unknown>): Promise<Record<string, unknown>[]>;',
  'batch(statements: readonly SqlStatement[]): Promise<SqlExecuteResult[]>;',
  '}',
  'interface Sql {',
  documented('Runs one statement. rows are arrays. Commits on its own outside a transaction.', 'execute(text: string, args?: unknown[] | Record<string, unknown>): Promise<SqlExecuteResult>;'),
  documented('Runs a query. Rows are objects keyed by column name.', 'query(text: string, args?: unknown[] | Record<string, unknown>): Promise<Record<string, unknown>[]>;'),
  documented('Runs every statement in one transaction and returns one result per statement.', 'batch(statements: readonly SqlStatement[]): Promise<SqlExecuteResult[]>;'),
  documented('Runs tx.execute, tx.query and tx.batch in one transaction. Commits when the callback returns and rolls back when it throws.', 'transaction(run: (tx: SqlTransaction) => Promise<unknown>): Promise<unknown>;'),
  '}',
  'interface FunctionEvent { trigger: "http" | "cron"; body: unknown; query: Record<string, string>; scheduledAt?: string; }',
  'declare const sql: Sql;',
  'declare const event: FunctionEvent;',
].join('\n');

const members = [...extraLib.matchAll(/\/\*\*\s*([\s\S]*?)\s*\*\/\s*(\w+)\(([\s\S]*?)\)\s*:/g)].map(match => ({
  name: match[2],
  signature: `${match[2]}(${match[3]})`,
  doc: match[1].replaceAll(/^\s*\*\s?/gm, '').replaceAll(/\s+/g, ' ').trim(),
}));

const snippets = [
  ['handler', 'async function handler(event) {\n  const rows = await sql.query("SELECT 1 AS ok");\n  return { rows };\n}'],
  ['query rows', 'const rows = await sql.query("SELECT 1 AS ok");'],
  ['insert with args', 'await sql.execute("INSERT INTO items (name) VALUES (?)", ["demo"]);'],
  ['batch', 'await sql.batch([{ sql: "INSERT INTO items (name) VALUES (?)", args: ["demo"] }]);'],
  ['transaction', 'await sql.transaction(async (tx) => {\n  await tx.execute("INSERT INTO items (name) VALUES (?)", ["demo"]);\n});'],
  ['return JSON response', 'return { status: 200, body: { ok: true } };'],
];

let registered = false;

export function attach(api, editor) {
  register(api);
  editor.updateOptions({ wordBasedSuggestions: 'off', quickSuggestions: { other: true, comments: false, strings: false } });
  return () => {};
}

function register(api) {
  if (registered) {
    return;
  }
  registered = true;
  const defaults = api.languages.typescript?.javascriptDefaults;
  const languageService = Boolean(defaults?.addExtraLib);
  if (languageService) {
    defaults.addExtraLib(extraLib, 'file:///terrascale-function.d.ts');
  }
  const { CompletionItemKind: Kind } = api.languages;
  api.languages.registerCompletionItemProvider('javascript', {
    triggerCharacters: ['.'],
    provideCompletionItems(model, position) {
      const before = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
      const call = before.match(/(?:^|[^\w$])sql\.(\w*)$/);
      if (call) {
        if (languageService) {
          return { suggestions: [] };
        }
        const range = rangeEnding(position, call[1].length);
        return {
          suggestions: members.map(member => ({
            label: member.name,
            kind: Kind.Method,
            detail: member.signature,
            documentation: member.doc,
            insertText: member.name,
            range,
          })),
        };
      }
      if (before.endsWith('.')) {
        return { suggestions: [] };
      }
      const word = before.match(/[A-Za-z]*$/)[0];
      const range = rangeEnding(position, word.length);
      return {
        suggestions: snippets.map(([label, insertText]) => ({
          label,
          kind: Kind.Snippet,
          insertText,
          range,
        })),
      };
    },
  });
}

function rangeEnding(position, length) {
  return {
    startLineNumber: position.lineNumber,
    startColumn: position.column - length,
    endLineNumber: position.lineNumber,
    endColumn: position.column,
  };
}
