export type Lang = 'typescript' | 'csharp' | 'json' | 'shell'

export type Token = { text: string; className?: string }

const KEYWORDS: Record<Lang, RegExp> = {
  typescript:
    /\b(import|from|const|let|var|async|await|function|return|if|else|new|class|extends|export|default|interface|type|as|in|of)\b/g,
  csharp:
    /\b(using|namespace|class|struct|interface|enum|public|private|protected|internal|static|readonly|const|var|new|return|if|else|for|foreach|in|while|switch|case|default|break|continue|async|await|null|true|false|this|base|void)\b/g,
  json: /\b(true|false|null)\b/g,
  shell: /^(claude|curl)\b/gm,
}

const STRINGS = /(["'`])(?:(?=(\\?))\2.)*?\1/g
const CODE_COMMENTS = /(\/\/[^\n]*|#[^\n]*|\/\*[\s\S]*?\*\/)/g
// JSON has no comments, and URLs in it and in shell lines contain `//`.
const COMMENTS: Record<Lang, RegExp> = {
  typescript: CODE_COMMENTS,
  csharp: CODE_COMMENTS,
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
  typescript: `// Server-side: TERRASCALE_ACCESS_TOKEN is a 60-second token exchanged from
// your dashboard API key. Keep both out of browser bundles.
// Supply a resolver and trusted origins from server configuration.
// The override skips discovery.
async function createAndQueryCustomers(
  resolveRegion: (signal: AbortSignal) => Promise<string>, trustedOrigins: readonly string[],
  project: string, database: string, collection: string,
  staticOrigin?: string, callerSignal?: AbortSignal,
) {
  const signal = AbortSignal.any([AbortSignal.timeout(30_000), ...(callerSignal ? [callerSignal] : [])]);
  signal.throwIfAborted();
  let abort!: () => void;
  const cancelled = new Promise<never>((_, reject) => {
    abort = () => reject(signal.reason);
    signal.addEventListener('abort', abort, { once: true });
  });
  let resolved: string;
  try { resolved = staticOrigin ?? await Promise.race([resolveRegion(signal), cancelled]); }
  finally { signal.removeEventListener('abort', abort); }
  const origin = new URL(resolved);
  if (origin.protocol !== 'https:' || origin.username || origin.password ||
      origin.pathname !== '/' || origin.search || origin.hash ||
      !trustedOrigins.includes(origin.origin)) {
    throw new Error('Expected a trusted HTTPS origin');
  }
  if ([project, database, collection].some(name => !name || name === '.' || name === '..')) {
    throw new Error('Resource names are required');
  }
  const accessToken = process.env.TERRASCALE_ACCESS_TOKEN;
  if (!accessToken?.trim()) throw new Error('TERRASCALE_ACCESS_TOKEN is required');

  const path = '/v1/projects/' + encodeURIComponent(project) +
    '/databases/' + encodeURIComponent(database) +
    '/collections/' + encodeURIComponent(collection);
  const headers = {
    Authorization: 'Bearer ' + accessToken,
    'Content-Type': 'application/json',
  };
  const created = await fetch(new URL(path + '/documents', origin), {
    method: 'POST', redirect: 'error', headers, signal,
    body: JSON.stringify({ email: 'team@example.com', plan: 'pro' }),
  });
  await created.body?.cancel();
  if (!created.ok) throw new Error('Create failed: ' + created.status);
  const response = await fetch(new URL(path + '/query', origin), {
    method: 'POST',
    redirect: 'error',
    headers, signal,
    body: JSON.stringify({
      filter: { eq: ['/plan', 'pro'] },
      limit: 25,
    }),
  });
  if (!response.ok) {
    await response.body?.cancel();
    throw new Error('Query failed: ' + response.status);
  }
  return response.json();
}`,
  csharp: `using System.Net.Http.Headers;
using System.Net.Http.Json;

// Server-side: TERRASCALE_ACCESS_TOKEN is a 60-second token exchanged from
// your dashboard API key; the key itself as Bearer answers 401.
// Supply a resolver and trusted origins from server configuration.
// The override skips discovery.
static class DocumentsExample
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    public static async Task<string> CreateAndQueryCustomers(
        Func<CancellationToken, Task<Uri>> resolveRegion, IReadOnlyCollection<Uri> trustedOrigins,
        string project, string database, string collection, Uri? staticOrigin = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var origin = staticOrigin ?? await resolveRegion(token).WaitAsync(token);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            !trustedOrigins.Contains(origin))
            throw new ArgumentException("Expected a trusted HTTPS origin");
        if (new[] { project, database, collection }.Any(name =>
            string.IsNullOrEmpty(name) || name == "." || name == ".."))
            throw new ArgumentException("Resource names are required");
        var accessToken = Environment.GetEnvironmentVariable("TERRASCALE_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("TERRASCALE_ACCESS_TOKEN is required");

        var path = "/v1/projects/" + Uri.EscapeDataString(project) +
            "/databases/" + Uri.EscapeDataString(database) +
            "/collections/" + Uri.EscapeDataString(collection);
        using var create = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, path + "/documents"));
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        create.Content = JsonContent.Create(new { email = "team@example.com", plan = "pro" });
        using (var created = await Http.SendAsync(create, HttpCompletionOption.ResponseHeadersRead, token))
            created.EnsureSuccessStatusCode();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, path + "/query"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new {
            filter = new { eq = new[] { "/plan", "pro" } },
            limit = 25
        });
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }
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
  shell: `# Add the TerraScale MCP server to Claude Code
claude mcp add --transport http terrascale https://mcp.terrascale.tech/ \\
  --header "Authorization: Bearer $TERRASCALE_PAT"

# Then ask in plain language
claude -p "Which of my TerraScale databases replicate to fra?"

# Or let the agent call the HTTP API from its shell.
# Exchange the dashboard API key (<key ID>_<secret>) for an access token first.
TOKEN=$(curl -sS -u "\${TERRASCALE_API_KEY%%_*}:$TERRASCALE_API_KEY" \\
  -d grant_type=client_credentials https://auth.terrascale.tech/terrascale/oauth/token | jq -r .access_token)
curl https://api.terrascale.tech/v1/projects/$PROJECT/databases/app/sql \\
  -H "Authorization: Bearer $TOKEN" \\
  --json '{"sql": "SELECT email, seats FROM customers WHERE seats >= 10 LIMIT 25"}'`,
}

export const LANG_META: Array<{ id: Lang; label: string; file: string; icon?: string }> = [
  { id: 'csharp', label: 'C#', file: '~/app/queries.cs', icon: '/language-icons/csharp.svg' },
  { id: 'typescript', label: 'TypeScript', file: '~/app/queries.ts', icon: '/language-icons/typescript.svg' },
  { id: 'json', label: 'MCP', file: '~/app/.mcp.json' },
  { id: 'shell', label: 'CLI', file: '~/app $' },
]
