export type Lang = 'typescript' | 'csharp' | 'json' | 'shell' | 'python' | 'go' | 'rust'

export type Token = { text: string; className?: string }

const KEYWORDS: Record<Lang, RegExp> = {
  typescript:
    /\b(import|from|const|let|var|async|await|function|return|if|else|new|class|extends|export|default|interface|type|as|in|of)\b/g,
  csharp:
    /\b(using|namespace|class|struct|interface|enum|public|private|protected|internal|static|readonly|const|var|new|return|if|else|for|foreach|in|while|switch|case|default|break|continue|async|await|null|true|false|this|base|void)\b/g,
  python: /\b(from|import|with|as|def|return|class|if|else|for|in|None|True|False)\b/g,
  go: /\b(package|import|func|var|if|return|defer|nil|panic)\b/g,
  rust: /\b(use|let|mut|fn|async|await|pub|impl|return)\b/g,
  json: /\b(true|false|null)\b/g,
  shell: /^(claude|curl)\b/gm,
}

const STRINGS = /(["'`])(?:(?=(\\?))\2.)*?\1/g
const CODE_COMMENTS = /(\/\/[^\n]*|#[^\n]*|\/\*[\s\S]*?\*\/)/g
// JSON has no comments, and URLs in it and in shell lines contain `//`.
const COMMENTS: Record<Lang, RegExp> = {
  typescript: CODE_COMMENTS,
  csharp: CODE_COMMENTS,
  python: /#[^\n]*/g,
  go: CODE_COMMENTS,
  rust: CODE_COMMENTS,
  json: /(?!)/g,
  shell: /^#[^\n]*/gm,
}
const NUMBERS = /\b(\d+(?:\.\d+)?)\b/g
const FNS = /\b([a-zA-Z_][\w]*)(?=\()/g

export function tokenize(code: string, lang: Lang): Token[] {
  const tokens: Array<{ start: number; end: number; text: string; className: string }> = []
  const claimed = new Array<boolean>(code.length).fill(false)

  const mark = (regex: RegExp, className: string) => {
    regex.lastIndex = 0
    let m: RegExpExecArray | null
    while ((m = regex.exec(code)) !== null) {
      const s = m.index
      const e = s + m[0].length
      let clear = true
      for (let i = s; i < e; i++) {
        if (claimed[i]) {
          clear = false
          break
        }
      }
      if (clear) {
        tokens.push({ start: s, end: e, text: m[0], className })
        for (let i = s; i < e; i++) claimed[i] = true
      }
    }
  }

  mark(COMMENTS[lang], 'tok-comment')
  mark(STRINGS, 'tok-string')
  mark(KEYWORDS[lang] || KEYWORDS.typescript, 'tok-keyword')
  mark(FNS, 'tok-fn')
  mark(NUMBERS, 'tok-num')

  tokens.sort((a, b) => a.start - b.start)

  const parts: Token[] = []
  let i = 0
  for (const t of tokens) {
    if (t.start > i) {
      parts.push({ text: code.slice(i, t.start) })
    }
    parts.push({ text: t.text, className: t.className })
    i = t.end
  }
  if (i < code.length) {
    parts.push({ text: code.slice(i) })
  }
  return parts
}

export const CODE_SAMPLES: Record<Lang, string> = {
  typescript: `import { createTerraBaseV1Client } from "@terrascale/sdk";

// Server-side: TERRASCALE_ACCESS_TOKEN is a 60-second token exchanged from
// your dashboard API key. Keep both out of browser bundles.
const client = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT,
  token: process.env.TERRASCALE_ACCESS_TOKEN,
});
const db = client.database("app");

// One transaction: both statements commit or neither does.
await db.batch([
  "CREATE TABLE IF NOT EXISTS customers (id INTEGER PRIMARY KEY, email TEXT UNIQUE NOT NULL, seats INTEGER NOT NULL)",
  { sql: "INSERT INTO customers (email, seats) VALUES (?, ?)", args: ["team@example.com", 12] },
]);

const customers = await db.query(
  "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25",
  [10],
);`,
  csharp: `using TerraScale.DotNet.TerraBase;

// Server-side: the client exchanges your dashboard API key for
// short-lived access tokens. Keep the key out of client apps.
using var http = new HttpClient(TerraBaseHttp.CreateHandler());
var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { ApiKey = apiKey });
TerraBaseSqlDatabase db = sql.Database(projectId, "app");

// One transaction: both statements commit or neither does.
await db.BatchAsync([
    new("CREATE TABLE IF NOT EXISTS customers (id INTEGER PRIMARY KEY, email TEXT UNIQUE NOT NULL, seats INTEGER NOT NULL)"),
    new("INSERT INTO customers (email, seats) VALUES (?, ?)", ["team@example.com", 12])
], cancellationToken);

var customers = await db.QueryAsync(
    "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25",
    [10], cancellationToken);
foreach (var row in customers)
    Console.WriteLine($"{row.GetString("email")}: {row.GetInt64("seats")}");`,
  python: `from terrascale import TerraScale

# Server-side: reads TERRASCALE_API_KEY and TERRASCALE_PROJECT_ID.
with TerraScale() as client:
    db = client.database("app")
    customers = db.query(
        "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25",
        [10],
    )
    for row in customers:
        print(row.get_string("email"), row.get_int64("seats"))`,
  go: `import (
    "context"
    "os"

    tb "terrascale.tech/sdk/v1"
    "terrascale.tech/sdk/tsauth"
)

// Server-side: the token source exchanges your API key.
project := os.Getenv("TERRASCALE_PROJECT_ID")
tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
    Key: os.Getenv("TERRASCALE_API_KEY"), Project: project,
})
if err != nil { panic(err) }
client, err := tb.New(tb.Config{TokenSource: tokens})
if err != nil { panic(err) }
db := client.Database(project, "app")
customers, err := db.Query(context.Background(),
    "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25", 10)
if err != nil { panic(err) }`,
  rust: `use terrascale::Client;

// Server-side: the client exchanges and refreshes project tokens.
let project = std::env::var("TERRASCALE_PROJECT_ID")?;
let api_key = std::env::var("TERRASCALE_API_KEY")?;
let client = Client::for_project_api_key(&project, &api_key).build()?;
let db = client.database("app").sql();
let customers = db.query(
    "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25",
    &[10.into()],
).await?;
for row in customers {
    println!("{}: {}", row.get_str("email")?, row.get_i64("seats")?);
}`,
  // Server config from ts-dashboard docs/mcp.md; tokens come from Profile → Access tokens.
  json: `{
  "mcpServers": {
    "terrascale": {
      "type": "http",
      "url": "https://mcp.terrascale.tech/",
      "headers": { "Authorization": "Bearer tspat_..." }
    }
  }
}`,
  shell: `# Run SQL from the CLI
ts auth login
ts exec app "SELECT email, seats FROM customers WHERE seats >= 10 LIMIT 25"

# Or connect psql with an API key as the password
read -rs PGPASSWORD && export PGPASSWORD
psql "postgresql://app@pg.terrascale.tech:5432/app?sslmode=verify-full&sslrootcert=system"

# Add the TerraScale MCP server to Claude Code
claude mcp add --transport http terrascale https://mcp.terrascale.tech/ \\
  --header "Authorization: Bearer $TERRASCALE_PAT"
claude -p "Which customers in the app database have 10 or more seats?"`,
}

export const LANG_META: Array<{ id: Lang; label: string; file: string; icon?: string }> = [
  { id: 'python', label: 'Python', file: '~/app/queries.py', icon: '/language-icons/python.svg' },
  { id: 'typescript', label: 'JavaScript', file: '~/app/queries.js', icon: '/language-icons/javascript.svg' },
  { id: 'go', label: 'Go', file: '~/app/queries.go', icon: '/language-icons/go.svg' },
  { id: 'csharp', label: '.NET', file: '~/app/queries.cs', icon: '/language-icons/dotnet.svg' },
  { id: 'rust', label: 'Rust', file: '~/app/queries.rs', icon: '/language-icons/rust.svg' },
  { id: 'json', label: 'MCP', file: '~/app/.mcp.json' },
  { id: 'shell', label: 'CLI', file: '~/app $' },
]
