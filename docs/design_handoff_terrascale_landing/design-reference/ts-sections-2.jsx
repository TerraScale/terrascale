// ts-sections-2.jsx — Features, Code Panel, Pricing, CTA, Footer

// ── Features ──────────────────────────────────────────────────────────────────
function FeaturesSection() {
  const features = [
    {
      icon: 'zap',
      title: 'Lightning Performance',
      text: 'Sub-20ms query latency with intelligent caching, query optimization, and globally distributed edge replicas that stay close to your users.',
      bullets: ['In-memory caching', 'Query optimization', 'Edge replicas'],
    },
    {
      icon: 'shield',
      title: 'Enterprise Security',
      text: 'SOC 2 Type II, ISO 27001, and HIPAA compliant. End-to-end encryption, private networking, and fine-grained role-based access control by default.',
      bullets: ['SOC 2 Type II', 'E2E encryption', 'RBAC'],
    },
    {
      icon: 'globe',
      title: 'Global Scale',
      text: 'Deploy in 20+ regions with automatic failover, multi-region replication, and a geographic DNS router that serves queries from the nearest node.',
      bullets: ['20+ regions', 'Auto failover', 'Geo routing'],
    },
    {
      icon: 'trendingUp',
      title: 'Auto-scaling',
      text: 'Horizontal and vertical scaling on demand. Zero-downtime resizing, predictive autoscale, and usage-based billing that follows your traffic.',
      bullets: ['Horizontal scale', 'Zero-downtime', 'Usage-based'],
    },
  ];

  return (
    <section style={{ padding: '120px 0', position: 'relative' }}>
      <div style={{
        position: 'absolute', inset: 0, opacity: 0.45,
        backgroundImage: `linear-gradient(var(--ts-grid-color) 1px, transparent 1px), linear-gradient(90deg, var(--ts-grid-color) 1px, transparent 1px)`,
        backgroundSize: '60px 60px', pointerEvents: 'none',
      }} />
      <div style={{ position: 'relative', maxWidth: 1240, margin: '0 auto', padding: '0 40px' }}>
        <SectionHeader
          eyebrow="Platform"
          title="Built for scale, designed for developers"
          subtitle="Every feature engineered for production. No footguns, no surprises, no 3 AM pages."
        />
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, 1fr)', gap: 20, marginTop: 64 }}>
          {features.map((f, i) => <FeatureCard key={f.title} feature={f} index={i} />)}
        </div>
      </div>
    </section>
  );
}

function SectionHeader({ eyebrow, title, subtitle }) {
  return (
    <div style={{ textAlign: 'center', maxWidth: 720, margin: '0 auto' }}>
      <div style={{
        display: 'inline-flex', alignItems: 'center', gap: 8,
        padding: '5px 12px', borderRadius: 4, marginBottom: 20,
        background: 'rgba(var(--ts-primary-rgb), 0.1)',
        border: '1px solid rgba(var(--ts-primary-rgb), 0.25)',
        color: 'var(--ts-primary-light)',
        fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.14em',
      }}>
        <span style={{ width: 5, height: 5, borderRadius: 3, background: 'var(--ts-primary)' }} />
        {eyebrow}
      </div>
      <h2 style={{
        fontSize: 'clamp(1.8rem, 2.5vw + 0.5rem, 3rem)', fontWeight: 700,
        color: 'var(--ts-text)', letterSpacing: '-0.02em', lineHeight: 1.12, marginBottom: 16,
      }}>{title}</h2>
      <p style={{ fontSize: 18, color: 'var(--ts-text-muted)', lineHeight: 1.65 }}>{subtitle}</p>
    </div>
  );
}

function FeatureCard({ feature, index }) {
  const [hov, setHov] = useState(false);
  return (
    <div
      onMouseEnter={() => setHov(true)}
      onMouseLeave={() => setHov(false)}
      style={{
        position: 'relative', overflow: 'hidden',
        background: 'var(--ts-glass-bg)', backdropFilter: 'blur(14px)',
        border: '1px solid var(--ts-glass-border)',
        borderRadius: 10, padding: 36,
        transition: 'all 250ms',
        transform: hov ? 'translateY(-2px)' : '',
        boxShadow: hov
          ? '0 14px 40px rgba(var(--ts-primary-rgb), 0.2), inset 0 1px 0 rgba(255,255,255,0.06)'
          : '0 1px 0 rgba(255,255,255,0.04) inset',
      }}>
      {/* Corner glow */}
      <div style={{
        position: 'absolute', top: -60, right: -60, width: 180, height: 180,
        background: hov
          ? 'radial-gradient(circle, rgba(var(--ts-primary-rgb), 0.25) 0%, transparent 70%)'
          : 'radial-gradient(circle, rgba(var(--ts-primary-rgb), 0.08) 0%, transparent 70%)',
        transition: 'all 250ms', pointerEvents: 'none',
      }} />

      <div style={{
        width: 48, height: 48, borderRadius: 6,
        background: hov
          ? 'rgba(var(--ts-primary-rgb), 0.22)'
          : 'rgba(var(--ts-primary-rgb), 0.14)',
        border: hov
          ? '1px solid rgba(var(--ts-primary-rgb), 0.55)'
          : '1px solid rgba(var(--ts-primary-rgb), 0.3)',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        marginBottom: 24,
        transition: 'background 260ms, border-color 260ms, box-shadow 260ms',
        boxShadow: hov
          ? '0 0 0 4px rgba(var(--ts-primary-rgb), 0.12), 0 8px 22px rgba(var(--ts-primary-rgb), 0.28)'
          : '0 0 0 0 rgba(var(--ts-primary-rgb), 0)',
      }}>
        <Icon name={feature.icon} size={22} color="var(--ts-primary-light)" />
      </div>

      <h3 style={{ fontSize: 21, fontWeight: 700, color: 'var(--ts-text)', marginBottom: 12, letterSpacing: '-0.01em' }}>
        {feature.title}
      </h3>
      <p style={{ fontSize: 15, color: 'var(--ts-text-muted)', lineHeight: 1.7, marginBottom: 20 }}>
        {feature.text}
      </p>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
        {feature.bullets.map(b => (
          <span key={b} style={{
            display: 'inline-flex', alignItems: 'center', gap: 6,
            padding: '4px 10px', borderRadius: 4,
            background: 'rgba(var(--ts-primary-rgb), 0.08)',
            border: '1px solid rgba(var(--ts-primary-rgb), 0.18)',
            color: 'var(--ts-primary-light)',
            fontSize: 11, fontWeight: 600, fontFamily: 'var(--ts-font-mono)',
          }}>
            <span style={{ width: 4, height: 4, borderRadius: 2, background: 'var(--ts-primary)' }} />
            {b}
          </span>
        ))}
      </div>
    </div>
  );
}

// ── Code Panel ────────────────────────────────────────────────────────────────
const CODE_SAMPLES = {
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
};

function CodePanelSection() {
  const [lang, setLang] = useState('csharp');
  const [copied, setCopied] = useState(false);
  const langs = [
    { id: 'csharp', label: 'C#', icon: 'assets/lang-csharp.svg' },
    { id: 'typescript', label: 'TypeScript', icon: 'assets/lang-typescript.svg' },
    { id: 'python', label: 'Python', icon: 'assets/lang-python.svg' },
    { id: 'go', label: 'Go', icon: 'assets/lang-go.svg' },
    { id: 'rust', label: 'Rust', icon: 'assets/lang-rust.svg' },
  ];

  const handleCopy = () => {
    navigator.clipboard?.writeText(CODE_SAMPLES[lang]).catch(() => {});
    setCopied(true);
    setTimeout(() => setCopied(false), 1600);
  };

  return (
    <section style={{ padding: '120px 0', position: 'relative', background: 'var(--ts-bg-surface)' }}>
      <div style={{ maxWidth: 1240, margin: '0 auto', padding: '0 40px' }}>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1.2fr', gap: 72, alignItems: 'center' }}>

          {/* Left: copy */}
          <div>
            <div style={{
              display: 'inline-flex', alignItems: 'center', gap: 8,
              padding: '5px 12px', borderRadius: 4, marginBottom: 20,
              background: 'rgba(var(--ts-primary-rgb), 0.1)',
              border: '1px solid rgba(var(--ts-primary-rgb), 0.25)',
              color: 'var(--ts-primary-light)',
              fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.14em',
            }}>
              <Icon name="code2" size={12} />
              Developer SDK
            </div>
            <h2 style={{
              fontSize: 'clamp(1.8rem, 2.5vw + 0.5rem, 2.8rem)', fontWeight: 700,
              color: 'var(--ts-text)', lineHeight: 1.14, letterSpacing: '-0.02em', marginBottom: 20,
            }}>
              Query your data<br />
              <span style={{
                background: 'var(--ts-gradient-primary)',
                WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent', backgroundClip: 'text',
              }}>in the language you love</span>
            </h2>
            <p style={{ fontSize: 17, color: 'var(--ts-text-muted)', lineHeight: 1.7, marginBottom: 32 }}>
              Type-safe SDKs for C#, TypeScript, Python, Go, Rust, and Java. A consistent query
              builder across runtimes — no ORM lock-in, no N+1 surprises.
            </p>

            <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
              {[
                { icon: 'check', text: 'Type-safe from query → result' },
                { icon: 'check', text: 'Built-in caching & query batching' },
                { icon: 'check', text: 'Real-time subscriptions built in' },
                { icon: 'check', text: 'Zero-dependency clients' },
              ].map(item => (
                <div key={item.text} style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                  <div style={{
                    width: 20, height: 20, borderRadius: 4,
                    background: 'var(--ts-secondary)',
                    display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
                  }}>
                    <Icon name="check" size={12} color="var(--ts-bg-base)" />
                  </div>
                  <span style={{ color: 'var(--ts-text)', fontSize: 15 }}>{item.text}</span>
                </div>
              ))}
            </div>
          </div>

          {/* Right: terminal */}
          <div style={{
            position: 'relative',
            background: 'var(--ts-code-bg)',
            border: '1px solid var(--ts-glass-border)',
            borderRadius: 10, overflow: 'hidden',
            boxShadow: '0 20px 60px rgba(0,0,0,0.5), 0 0 80px rgba(var(--ts-primary-rgb), 0.08)',
          }}>
            {/* Terminal chrome */}
            <div style={{
              display: 'flex', alignItems: 'center', justifyContent: 'space-between',
              padding: '12px 16px', background: 'rgba(0,0,0,0.3)',
              borderBottom: '1px solid var(--ts-glass-border)',
            }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <div style={{ width: 11, height: 11, borderRadius: 6, background: '#FF5F57' }} />
                <div style={{ width: 11, height: 11, borderRadius: 6, background: '#FEBC2E' }} />
                <div style={{ width: 11, height: 11, borderRadius: 6, background: '#28C840' }} />
                <div style={{
                  marginLeft: 12, fontFamily: 'var(--ts-font-mono)', fontSize: 11,
                  color: 'var(--ts-text-muted)',
                }}>
                  ~/app/queries.{lang === 'typescript' ? 'ts' : lang === 'python' ? 'py' : lang === 'go' ? 'go' : lang === 'csharp' ? 'cs' : 'rs'}
                </div>
              </div>
              <button
                onClick={handleCopy}
                style={{
                  display: 'inline-flex', alignItems: 'center', gap: 6,
                  padding: '4px 10px', borderRadius: 4,
                  background: copied ? 'rgba(16,185,129,0.15)' : 'rgba(255,255,255,0.05)',
                  border: copied ? '1px solid rgba(16,185,129,0.35)' : '1px solid rgba(255,255,255,0.1)',
                  color: copied ? '#34D399' : 'var(--ts-text-muted)',
                  fontSize: 11, fontWeight: 600, cursor: 'pointer',
                  fontFamily: 'var(--ts-font-mono)', transition: 'all 150ms',
                }}>
                <Icon name={copied ? 'check' : 'code2'} size={11} />
                {copied ? 'Copied' : 'Copy'}
              </button>
            </div>

            {/* Language tabs */}
            <div style={{
              display: 'flex', gap: 2, padding: '10px 12px 0',
              background: 'rgba(0,0,0,0.15)', borderBottom: '1px solid var(--ts-glass-border)',
            }}>
              {langs.map(l => (
                <button key={l.id} onClick={() => setLang(l.id)}
                  style={{
                    display: 'inline-flex', alignItems: 'center', gap: 7,
                    padding: '8px 14px',
                    background: lang === l.id ? 'var(--ts-code-bg)' : 'transparent',
                    border: 'none',
                    borderBottom: lang === l.id ? '2px solid var(--ts-primary)' : '2px solid transparent',
                    borderTopLeftRadius: 5, borderTopRightRadius: 5,
                    color: lang === l.id ? 'var(--ts-text)' : 'var(--ts-text-muted)',
                    fontSize: 12, fontWeight: 600, cursor: 'pointer', marginBottom: -1,
                    fontFamily: 'var(--ts-font-mono)',
                  }}>
                  <img src={l.icon} alt="" style={{ width: 13, height: 13, filter: lang === l.id ? 'none' : 'grayscale(1) opacity(0.7)' }} />
                  {l.label}
                </button>
              ))}
            </div>

            {/* Code body */}
            <pre style={{
              margin: 0, padding: 24,
              fontFamily: 'var(--ts-font-mono)', fontSize: 13.5, lineHeight: 1.75,
              color: '#CBD5E1', overflow: 'auto', maxHeight: 480,
              whiteSpace: 'pre',
            }}>
              <HighlightedCode code={CODE_SAMPLES[lang]} lang={lang} />
            </pre>
          </div>
        </div>
      </div>
    </section>
  );
}

// Minimal syntax highlighter — token regex per language
function HighlightedCode({ code, lang }) {
  const keywords = {
    typescript: /\b(import|from|const|let|var|async|await|function|return|if|else|new|class|extends|export|default|interface|type|as|in|of)\b/g,
    python: /\b(import|from|def|class|return|if|else|elif|for|in|while|async|await|with|as|lambda|True|False|None|print|len)\b/g,
    go: /\b(package|import|func|return|if|else|for|range|var|const|type|struct|interface|go|chan|select|case|default|map)\b/g,
    rust: /\b(use|fn|let|mut|pub|struct|enum|impl|trait|async|await|return|if|else|for|in|while|match|Result|Ok|Err)\b/g,
    csharp: /\b(using|namespace|class|struct|interface|enum|public|private|protected|internal|static|readonly|const|var|new|return|if|else|for|foreach|in|while|switch|case|default|break|continue|async|await|null|true|false|this|base|void)\b/g,
  };
  const strings = /(["'`])(?:(?=(\\?))\2.)*?\1/g;
  const comments = /(\/\/[^\n]*|#[^\n]*|\/\*[\s\S]*?\*\/)/g;
  const numbers = /\b(\d+(?:\.\d+)?)\b/g;
  const fns = /\b([a-zA-Z_][\w]*)(?=\()/g;

  // naive but readable tokenizer: mark segments
  const tokens = [];
  const claimed = new Array(code.length).fill(false);

  const mark = (regex, className) => {
    let m;
    while ((m = regex.exec(code)) !== null) {
      const s = m.index, e = s + m[0].length;
      let clear = true;
      for (let i = s; i < e; i++) if (claimed[i]) { clear = false; break; }
      if (clear) {
        tokens.push({ start: s, end: e, text: m[0], className });
        for (let i = s; i < e; i++) claimed[i] = true;
      }
    }
  };

  mark(comments, 'comment');
  mark(strings, 'string');
  mark(keywords[lang] || keywords.typescript, 'keyword');
  mark(fns, 'fn');
  mark(numbers, 'number');

  tokens.sort((a, b) => a.start - b.start);

  const parts = [];
  let i = 0, k = 0;
  for (const t of tokens) {
    if (t.start > i) parts.push(<span key={k++}>{code.slice(i, t.start)}</span>);
    const color = {
      comment: '#64748B',
      string: '#86EFAC',
      keyword: 'var(--ts-primary-light)',
      fn: '#FCD34D',
      number: '#F0ABFC',
    }[t.className];
    const style = t.className === 'comment' ? { color, fontStyle: 'italic' } : { color };
    parts.push(<span key={k++} style={style}>{t.text}</span>);
    i = t.end;
  }
  if (i < code.length) parts.push(<span key={k++}>{code.slice(i)}</span>);
  return <>{parts}</>;
}

Object.assign(window, { FeaturesSection, CodePanelSection, SectionHeader, FeatureCard });
