<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import { tokenize, CODE_SAMPLES, LANG_META, type Lang } from '@/lib/highlight'
  import { dict } from '@/lib/i18n'

  let lang: Lang = 'csharp'
  let copied = false
  let copyTimer: ReturnType<typeof setTimeout> | null = null

  $: code = CODE_SAMPLES[lang]
  $: tokens = tokenize(code, lang)
  $: ext = LANG_META.find((l) => l.id === lang)?.ext ?? 'txt'
  $: bullets = $dict.code.bullets

  async function handleCopy() {
    try {
      await navigator.clipboard?.writeText(code)
      copied = true
      if (copyTimer) clearTimeout(copyTimer)
      copyTimer = setTimeout(() => {
        copied = false
      }, 1600)
    } catch {
      copied = false
    }
  }
</script>

<section class="code">
  <div class="code__inner">
    <div class="code__grid">
      <div class="code__copy">
        <span class="ts-eyebrow code__eyebrow">
          <Icon name="code2" className="code__eyebrow-icon" />
          {$dict.code.eyebrow}
        </span>
        <h2 class="code__headline">
          {$dict.code.headlineLead}<br />
          <span class="code__accent">{$dict.code.headlineAccent}</span>
        </h2>
        <p class="code__text">
          {$dict.code.text}
        </p>
        <ul class="code__bullets">
          {#each bullets as bullet (bullet)}
            <li class="code__bullet">
              <span class="code__bullet-check">
                <Icon name="check" className="code__bullet-icon" />
              </span>
              {#if bullet.endsWith('gru, sjc, fra')}
                <span>{bullet.slice(0, -13)}</span><RegionFlag code="gru" /><RegionFlag code="sjc" /><RegionFlag code="fra" />
              {:else}
                <span>{bullet}</span>
              {/if}
            </li>
          {/each}
        </ul>
      </div>

      <div class="code__terminal">
        <div class="code__head">
          <span class="code__filename">~/app/queries.{ext}</span>
          <div class="code__tabs" role="tablist" aria-label={$dict.code.tablistLabel}>
            {#each LANG_META as l (l.id)}
              <button
                type="button"
                class="code__tab"
                class:is-active={lang === l.id}
                role="tab"
                aria-selected={lang === l.id}
                on:click={() => (lang = l.id)}
              >
                <img src={l.icon} alt="" width="14" height="14" class="code__tab-icon" />
                {l.label}
              </button>
            {/each}
          </div>
          <button
            type="button"
            class="code__copy-btn"
            class:is-copied={copied}
            on:click={handleCopy}
            aria-label={$dict.code.copyAria}
          >
            <Icon name={copied ? 'check' : 'code2'} className="code__copy-icon" />
            {copied ? $dict.code.copied : $dict.code.copy}
          </button>
        </div>

        <pre class="code__pre"><code>{#each tokens as t, i (i)}{#if t.className}<span class={t.className}>{t.text}</span>{:else}{t.text}{/if}{/each}</code></pre>
      </div>
    </div>
  </div>
</section>

<style>
  .code {
    padding: 80px 0;
    border-top: 1px solid var(--ts-divider);
  }

  .code__inner {
    padding: 0 clamp(16px, 2.5vw, 40px);
  }

  .code__grid {
    display: grid;
    grid-template-columns: minmax(0, 6fr) minmax(0, 7fr);
    gap: 64px;
    align-items: center;
  }

  .code__copy {
    display: grid;
    gap: 16px;
    align-content: start;
  }

  :global(.code__eyebrow-icon) {
    width: 14px;
    height: 14px;
  }

  .code__headline {
    font-size: clamp(30px, 3.2vw, 48px);
    font-weight: 600;
    color: var(--ts-text);
    line-height: 1.08;
    letter-spacing: -0.035em;
    margin: 0;
  }

  .code__accent {
    color: var(--ts-primary);
  }

  .code__text {
    font-size: 16px;
    color: var(--ts-text-muted);
    line-height: 1.7;
    max-width: 56ch;
    margin: 0;
  }

  .code__bullets {
    list-style: none;
    padding: 0;
    margin: 16px 0 0;
    display: grid;
    gap: 12px;
  }

  .code__bullet {
    display: flex;
    align-items: center;
    gap: 12px;
    color: var(--ts-text);
    font-size: 14px;
  }

  .code__bullet-check {
    width: 22px;
    height: 22px;
    border-radius: 4px;
    background: color-mix(in oklab, #6bca92 18%, transparent);
    color: #6bca92;
    display: inline-grid;
    place-items: center;
    flex-shrink: 0;
  }

  :global(.code__bullet-icon) {
    width: 14px;
    height: 14px;
  }

  .code__terminal {
    min-width: 0;
    background: var(--ts-bg-surface);
    border: 1px solid var(--ts-divider);
    border-radius: 10px;
    overflow: hidden;
  }

  .code__head {
    display: flex;
    align-items: center;
    gap: 16px;
    height: 40px;
    padding: 0 8px 0 16px;
    border-bottom: 1px solid var(--ts-divider);
    color: var(--ts-text-muted);
    font-family: var(--ts-font-mono);
    font-size: 12px;
  }

  .code__filename {
    white-space: nowrap;
  }

  .code__tabs {
    display: flex;
    gap: 4px;
    margin-left: auto;
    overflow-x: auto;
    scrollbar-width: none;
  }

  .code__tab {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    height: 39px;
    margin-bottom: -1px;
    padding: 0;
    background: none;
    border: 0;
    border-bottom: 2px solid transparent;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-accent);
    font-size: 13px;
    font-weight: 500;
    line-height: 1;
    white-space: nowrap;
    cursor: pointer;
    transition:
      color 150ms ease,
      border-color 150ms ease;
  }

  .code__tab:hover {
    color: var(--ts-text);
  }

  .code__tab.is-active {
    color: var(--ts-text);
    border-bottom-color: var(--ts-primary);
  }

  .code__tab:focus-visible {
    outline-offset: -2px;
  }

  .code__tab-icon {
    width: 14px;
    height: 14px;
    filter: grayscale(1) opacity(0.7);
    transition: filter 150ms ease;
  }

  .code__tab.is-active .code__tab-icon {
    filter: none;
  }

  .code__copy-btn {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    flex-shrink: 0;
    height: 32px;
    padding: 0 12px;
    border: 1px solid transparent;
    border-radius: 6px;
    background: transparent;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-accent);
    font-size: 13px;
    font-weight: 500;
    line-height: 1;
    cursor: pointer;
    transition:
      background-color 150ms ease,
      color 150ms ease;
  }

  .code__copy-btn:hover {
    background: var(--ts-bg-surface);
    color: var(--ts-text);
  }

  .code__copy-btn.is-copied {
    color: #6bca92;
  }

  :global(.code__copy-icon) {
    width: 14px;
    height: 14px;
  }

  .code__pre {
    margin: 0;
    padding: 16px;
    height: 520px;
    overflow: auto;
    font-family: var(--ts-font-mono);
    font-size: 13px;
    line-height: 1.7;
    color: var(--ts-text);
    tab-size: 2;
    white-space: pre;
  }

  .code__pre code {
    font-family: inherit;
  }

  @media (max-width: 1280px) {
    .code__grid {
      grid-template-columns: minmax(0, 1fr);
      gap: 40px;
    }
  }

  @media (max-width: 640px) {
    .code {
      padding: 64px 0;
    }
    .code__inner {
      padding: 0 clamp(16px, 2.5vw, 40px);
    }
    .code__head {
      overflow-x: auto;
    }
    .code__filename {
      display: none;
    }
    .code__tabs {
      margin-left: 0;
    }
    .code__pre {
      height: 380px;
      font-size: 12px;
    }
  }
</style>
