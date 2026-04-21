// Minimal syntax highlighter — token regex per language.
// Returns an array of spans { text, className? } to be rendered safely.

export type Lang = 'typescript' | 'python' | 'go' | 'rust' | 'csharp'

export type Token = { text: string; className?: string }

const KEYWORDS: Record<Lang, RegExp> = {
  typescript:
    /\b(import|from|const|let|var|async|await|function|return|if|else|new|class|extends|export|default|interface|type|as|in|of)\b/g,
  python: /\b(import|from|def|class|return|if|else|elif|for|in|while|async|await|with|as|lambda|True|False|None|print|len)\b/g,
  go: /\b(package|import|func|return|if|else|for|range|var|const|type|struct|interface|go|chan|select|case|default|map)\b/g,
  rust: /\b(use|fn|let|mut|pub|struct|enum|impl|trait|async|await|return|if|else|for|in|while|match|Result|Ok|Err)\b/g,
  csharp:
    /\b(using|namespace|class|struct|interface|enum|public|private|protected|internal|static|readonly|const|var|new|return|if|else|for|foreach|in|while|switch|case|default|break|continue|async|await|null|true|false|this|base|void)\b/g,
}

const STRINGS = /(["'`])(?:(?=(\\?))\2.)*?\1/g
const COMMENTS = /(\/\/[^\n]*|#[^\n]*|\/\*[\s\S]*?\*\/)/g
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

  mark(COMMENTS, 'tok-comment')
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
  typescript: `import { TerraScale } from '@terrascale/sdk';

const db = new TerraScale({
  apiKey: process.env.TS_API_KEY,
  region: 'us-east-1',
});

// Create and query a table in one flow
const users = db.table<User>('users');

const active = await users
  .where('status', '==', 'active')
  .where('plan', 'in', ['pro', 'enterprise'])
  .orderBy('createdAt', 'desc')
  .limit(50)
  .cache({ ttl: 60 })
  .get();

console.log(\`found \${active.length} users\`);`,
  python: `from terrascale import TerraScale

db = TerraScale(
    api_key=os.environ['TS_API_KEY'],
    region='us-east-1',
)

# Create and query a table in one flow
users = db.table('users')

active = (users
    .where('status', '==', 'active')
    .where('plan', 'in', ['pro', 'enterprise'])
    .order_by('created_at', desc=True)
    .limit(50)
    .cache(ttl=60)
    .get())

print(f"found {len(active)} users")`,
  go: `package main

import (
    "context"
    "fmt"
    "github.com/terrascale/sdk-go"
)

func main() {
    db := terrascale.New(terrascale.Config{
        APIKey: os.Getenv("TS_API_KEY"),
        Region: "us-east-1",
    })

    users := db.Table[User]("users")

    active, err := users.
        Where("status", "==", "active").
        Where("plan", terrascale.In, []string{"pro", "enterprise"}).
        OrderBy("createdAt", terrascale.Desc).
        Limit(50).
        Cache(60 * time.Second).
        Get(context.Background())

    fmt.Printf("found %d users\\n", len(active))
}`,
  rust: `use terrascale::{Client, Config, Order};

#[tokio::main]
async fn main() -> Result<()> {
    let db = Client::new(Config {
        api_key: env::var("TS_API_KEY")?,
        region: "us-east-1".into(),
    });

    let users = db.table::<User>("users");

    let active = users
        .filter(field("status").eq("active"))
        .filter(field("plan").in_(&["pro", "enterprise"]))
        .order_by("created_at", Order::Desc)
        .limit(50)
        .cache(Duration::from_secs(60))
        .fetch()
        .await?;

    println!("found {} users", active.len());
    Ok(())
}`,
  csharp: `using TerraScale;
using TerraScale.Query;

var db = new TerraScaleClient(new Config
{
    ApiKey = Environment.GetEnvironmentVariable("TS_API_KEY")!,
    Region = "us-east-1",
});

var users = db.Table<User>("users");

var active = await users
    .Where(u => u.Status == "active")
    .Where(u => new[] { "pro", "enterprise" }.Contains(u.Plan))
    .OrderByDescending(u => u.CreatedAt)
    .Limit(50)
    .Cache(TimeSpan.FromSeconds(60))
    .FetchAsync();

Console.WriteLine($"found {active.Count} users");`,
}

export const LANG_META: Array<{ id: Lang; label: string; ext: string; icon: string }> = [
  { id: 'csharp', label: 'C#', ext: 'cs', icon: '/language-icons/csharp.svg' },
  { id: 'typescript', label: 'TypeScript', ext: 'ts', icon: '/language-icons/typescript.svg' },
  { id: 'python', label: 'Python', ext: 'py', icon: '/language-icons/python.svg' },
  { id: 'go', label: 'Go', ext: 'go', icon: '/language-icons/go.svg' },
  { id: 'rust', label: 'Rust', ext: 'rs', icon: '/language-icons/rust.svg' },
]
