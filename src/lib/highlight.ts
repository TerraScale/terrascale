// Minimal syntax highlighter — token regex per language.
// Returns an array of spans { text, className? } to be rendered safely.

export type Lang = 'typescript' | 'csharp'

export type Token = { text: string; className?: string }

const KEYWORDS: Record<Lang, RegExp> = {
  typescript:
    /\b(import|from|const|let|var|async|await|function|return|if|else|new|class|extends|export|default|interface|type|as|in|of)\b/g,
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
  typescript: `import { createTerraScaleClient, toUlid } from '@terrascale/sdk';

const client = createTerraScaleClient({
  apiBaseUrl: 'https://api.terrascale.tech',
  authBaseUrl: 'https://auth.terrascale.tech',
  projectId: toUlid(process.env.TERRASCALE_PROJECT_ID!),
  accessToken: async () => process.env.TERRASCALE_TOKEN!,
});

const repositoryId = toUlid(process.env.TERRASCALE_REPOSITORY_ID!);

await client.documents.create(
  { repositoryId },
  { email: 'team@example.com', plan: 'pro' },
);

const page = await client.query.execute(
  { repositoryId },
  { query: "from customers where plan = 'pro'", take: 25 },
);`,
  csharp: `using TerraScale.DotNet;

var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        ProjectId = Environment.GetEnvironmentVariable("TERRASCALE_PROJECT_ID"),
        ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY")
    });

var repository = await client.Repositories.CreateAsync(
    name: "customers",
    collection: "customers");

await client.Documents.CreateAsync(
    repository.RepositoryId,
    new { email = "team@example.com", plan = "pro" });

var page = await client.Tql.QueryAsync(
    repository.RepositoryId,
    TerraScaleQuery.From("customers").WhereEqual("plan", "pro").Take(25));`,
}

export const LANG_META: Array<{ id: Lang; label: string; ext: string; icon: string }> = [
  { id: 'csharp', label: 'C#', ext: 'cs', icon: '/language-icons/csharp.svg' },
  { id: 'typescript', label: 'TypeScript', ext: 'ts', icon: '/language-icons/typescript.svg' },
]
