<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import { tokenize, CODE_SAMPLES, LANG_META, type Lang } from '@/lib/highlight'

  let lang: Lang = 'csharp'
  let copied = false
  let copyTimer: ReturnType<typeof setTimeout> | null = null

  $: code = CODE_SAMPLES[lang]
  $: tokens = tokenize(code, lang)
  $: ext = LANG_META.find((l) => l.id === lang)?.ext ?? 'txt'

  const bullets = [
    'Type-safe from query → result',
    'Built-in caching & query batching',
    'Real-time subscriptions built in',
    'Zero-dependency clients',
  ]

  async function handleCopy() {
    try {
      await navigator.clipboard?.writeText(code)
      copied = true
      if (copyTimer) clearTimeout(copyTimer)
      copyTimer = setTimeout(() => {
        copied = false
      }, 1600)
    } catch {
      /* noop */
    }
  }
</script>

<section class="code">
  <div class="code__inner">
    <div class="code__grid">
      <div class="code__copy">
        <div class="code__eyebrow">
          <Icon name="code2" className="code__eyebrow-icon" />
          Developer SDK
        </div>
        <h2 class="code__headline">
          Query your data<br />
          <span class="ts-text-gradient">in the language you love</span>
        </h2>
        <p class="code__text">
          Type-safe SDKs for C#, TypeScript, Python, Go, Rust, and Java. A consistent query builder
          across runtimes — no ORM lock-in, no N+1 surprises.
        </p>
        <ul class="code__bullets">
          {#each bullets as bullet (bullet)}
            <li class="code__bullet">
              <span class="code__bullet-check">
                <Icon name="check" className="code__bullet-icon" />
              </span>
              <span>{bullet}</span>
            </li>
          {/each}
        </ul>
      </div>

      <div class="code__terminal">
        <div class="code__chrome">
          <div class="code__chrome-left">
            <span class="code__dot code__dot--red"></span>
            <span class="code__dot code__dot--yellow"></span>
            <span class="code__dot code__dot--green"></span>
            <span class="code__filename">~/app/queries.{ext}</span>
          </div>
          <button
            type="button"
            class="code__copy-btn"
            class:is-copied={copied}
            on:click={handleCopy}
            aria-label="Copy code"
          >
            <Icon name={copied ? 'check' : 'code2'} className="code__copy-icon" />
            {copied ? 'Copied' : 'Copy'}
          </button>
        </div>

        <div class="code__tabs" role="tablist" aria-label="Language examples">
          {#each LANG_META as l (l.id)}
            <button
              type="button"
              class="code__tab"
              class:is-active={lang === l.id}
              role="tab"
              aria-selected={lang === l.id}
              on:click={() => (lang = l.id)}
            >
              <img src={l.icon} alt="" width="13" height="13" class="code__tab-icon" />
              {l.label}
            </button>
          {/each}
        </div>

        <pre class="code__pre"><code>{#each tokens as t, i (i)}{#if t.className}<span class={t.className}>{t.text}</span>{:else}{t.text}{/if}{/each}</code></pre>
      </div>
    </div>
  </div>
</section>

<style>
  .code {
    padding: 80px 0;
    position: relative;
  }

  .code__inner {
    max-width: 1240px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .code__grid {
    display: grid;
    grid-template-columns: 1fr 1.2fr;
    gap: 72px;
    align-items: center;
  }

  .code__eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 5px 12px;
    border-radius: 4px;
    margin-bottom: 20px;
    background: rgba(var(--ts-primary-rgb), 0.1);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.25);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.14em;
    line-height: 1;
  }

  :global(.code__eyebrow-icon) {
    width: 12px;
    height: 12px;
  }

  .code__headline {
    font-size: clamp(1.8rem, 2.5vw + 0.5rem, 2.8rem);
    font-weight: 700;
    color: var(--ts-text);
    line-height: 1.14;
    letter-spacing: -0.02em;
    margin: 0 0 20px;
  }

  .code__text {
    font-family: var(--ts-font-accent);
    font-size: 17px;
    color: var(--ts-text-muted);
    line-height: 1.7;
    margin: 0 0 32px;
  }

  .code__bullets {
    list-style: none;
    padding: 0;
    margin: 0;
    display: flex;
    flex-direction: column;
    gap: 14px;
  }

  .code__bullet {
    display: flex;
    align-items: center;
    gap: 12px;
    color: var(--ts-text);
    font-size: 15px;
  }

  .code__bullet-check {
    width: 20px;
    height: 20px;
    border-radius: 4px;
    background: var(--ts-secondary);
    color: var(--ts-bg-base);
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  :global(.code__bullet-icon) {
    width: 12px;
    height: 12px;
  }

  .code__terminal {
    position: relative;
    background: var(--ts-code-bg);
    border: 1px solid var(--ts-glass-border);
    border-radius: 10px;
    overflow: hidden;
    box-shadow:
      0 20px 60px rgba(0, 0, 0, 0.5),
      0 0 80px rgba(var(--ts-primary-rgb), 0.08);
  }

  .code__chrome {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 16px;
    background: rgba(0, 0, 0, 0.3);
    border-bottom: 1px solid var(--ts-glass-border);
  }

  .code__chrome-left {
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .code__dot {
    width: 11px;
    height: 11px;
    border-radius: 6px;
    display: inline-block;
  }
  .code__dot--red {
    background: #ff5f57;
  }
  .code__dot--yellow {
    background: #febc2e;
  }
  .code__dot--green {
    background: #28c840;
  }

  .code__filename {
    margin-left: 12px;
    font-family: var(--ts-font-mono);
    font-size: 11px;
    color: var(--ts-text-muted);
  }

  .code__copy-btn {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 4px 10px;
    border-radius: 4px;
    background: rgba(255, 255, 255, 0.05);
    border: 1px solid rgba(255, 255, 255, 0.1);
    color: var(--ts-text-muted);
    font-size: 11px;
    font-weight: 600;
    cursor: pointer;
    font-family: var(--ts-font-mono);
    transition: all 150ms ease;
  }

  .code__copy-btn:hover {
    background: rgba(255, 255, 255, 0.08);
    color: var(--ts-text);
  }

  .code__copy-btn.is-copied {
    background: rgba(16, 185, 129, 0.15);
    border-color: rgba(16, 185, 129, 0.35);
    color: #34d399;
  }

  :global(.code__copy-icon) {
    width: 11px;
    height: 11px;
  }

  .code__tabs {
    display: flex;
    gap: 2px;
    padding: 10px 12px 0;
    background: rgba(0, 0, 0, 0.15);
    border-bottom: 1px solid var(--ts-glass-border);
    overflow-x: auto;
  }

  .code__tab {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    padding: 8px 14px;
    background: transparent;
    border: none;
    border-bottom: 2px solid transparent;
    border-top-left-radius: 5px;
    border-top-right-radius: 5px;
    color: var(--ts-text-muted);
    font-size: 12px;
    font-weight: 600;
    cursor: pointer;
    margin-bottom: -1px;
    font-family: var(--ts-font-mono);
    white-space: nowrap;
    transition:
      color 150ms,
      background 150ms,
      border-color 150ms;
  }

  .code__tab:hover {
    color: var(--ts-text);
  }

  .code__tab.is-active {
    background: var(--ts-code-bg);
    border-bottom-color: var(--ts-primary);
    color: var(--ts-text);
  }

  .code__tab-icon {
    width: 13px;
    height: 13px;
    filter: grayscale(1) opacity(0.7);
    transition: filter 150ms ease;
  }

  .code__tab.is-active .code__tab-icon {
    filter: none;
  }

  .code__pre {
    margin: 0;
    padding: 24px;
    font-family: var(--ts-font-mono);
    font-size: 13.5px;
    line-height: 1.75;
    color: #cbd5e1;
    overflow: auto;
    max-height: 480px;
    white-space: pre;
  }

  .code__pre code {
    font-family: inherit;
  }

  @media (max-width: 960px) {
    .code {
      padding: 60px 0;
    }
    .code__inner {
      padding: 0 20px;
    }
    .code__grid {
      grid-template-columns: 1fr;
      gap: 40px;
    }
  }
</style>
