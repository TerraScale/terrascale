import { SDK_SAMPLES, SDK_LANGUAGES } from '../../../src/TerraScale.Dashboard/web/assets/js/sdk-examples.js'

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
  ...SDK_SAMPLES,
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
  ...SDK_LANGUAGES as Array<{ id: Lang; label: string; file: string; icon: string }>,
  { id: 'json', label: 'MCP', file: '~/app/.mcp.json', icon: '/language-icons/mcp.svg' },
  { id: 'shell', label: 'CLI', file: '~/app $', icon: '/language-icons/terminal.svg' },
]
