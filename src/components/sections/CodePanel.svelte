<script lang="ts">
  import Icon from '@/components/Icon.svelte'
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

<section class="ts-section code">
  <div class="ts-container code__grid">
    <div class="code__copy">
      <span class="ts-eyebrow"><span class="ts-dot" aria-hidden="true"></span>{$dict.code.eyebrow}</span>
      <h2 class="code__headline">
        {$dict.code.headlineLead}
        <span class="ts-h-muted">{$dict.code.headlineAccent}</span>
      </h2>
      <p class="ts-lede code__text">{$dict.code.text}</p>
      <ul class="code__bullets">
        {#each bullets as bullet (bullet)}
          <li>
            <Icon name="check" className="code__bullet-icon" />
            <span>{bullet}</span>
          </li>
        {/each}
      </ul>
    </div>

    <div class="ts-code code__panel">
      <div class="ts-code-head">
        <div class="code__tabs" role="tablist" aria-label={$dict.code.tablistLabel}>
          {#each LANG_META as l (l.id)}
            <button
              type="button"
              class="ts-tab code__tab"
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
          class="ts-icon-btn"
          on:click={handleCopy}
          aria-label={copied ? $dict.code.copied : $dict.code.copyAria}
        >
          <Icon name={copied ? 'check' : 'copy'} className="code__copy-icon" />
        </button>
      </div>
      <pre class="code__pre"><code>{#each tokens as t, i (i)}{#if t.className}<span class={t.className}>{t.text}</span>{:else}{t.text}{/if}{/each}</code></pre>
    </div>
  </div>
</section>

<style>
  .code__grid {
    display: grid;
    grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
    gap: var(--ts-s12);
    align-items: center;
  }

  .code__copy {
    display: grid;
    gap: var(--ts-s4);
    justify-items: start;
  }

  .code__headline {
    font-size: var(--ts-fs-4xl);
  }

  .code__text {
    margin-top: 0;
  }

  .code__bullets {
    list-style: none;
    margin: var(--ts-s2) 0 0;
    padding: 0;
    display: grid;
    gap: var(--ts-s3);
    font-size: var(--ts-fs-md);
    color: var(--ts-ink);
  }

  .code__bullets li {
    display: flex;
    align-items: center;
    gap: var(--ts-s3);
  }

  .code__bullets :global(.code__bullet-icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
    color: var(--ts-success);
  }

  .code__panel {
    min-width: 0;
  }

  .code__tabs {
    display: flex;
    gap: var(--ts-s4);
    height: 100%;
  }

  .code__tab {
    height: 39px;
    padding: 0;
    border-top: 0;
    border-inline: 0;
    background: transparent;
    font: inherit;
    font-size: var(--ts-fs-sm);
    cursor: pointer;
  }

  .code__tab-icon {
    width: var(--ts-icon-sm);
    height: var(--ts-icon-sm);
    filter: grayscale(1);
    opacity: 0.7;
  }

  .code__tab[aria-selected='true'] .code__tab-icon {
    filter: none;
    opacity: 1;
  }

  .code__panel :global(.code__copy-icon) {
    width: var(--ts-icon-sm);
    height: var(--ts-icon-sm);
  }

  .code__pre {
    max-height: 480px;
    overflow: auto;
    white-space: pre;
  }

  .code__pre code {
    font: inherit;
  }

  @media (max-width: 1100px) {
    .code__grid {
      grid-template-columns: minmax(0, 1fr);
      gap: var(--ts-s8);
    }
  }

  @media (max-width: 640px) {
    .code__headline {
      font-size: var(--ts-fs-3xl);
    }
  }
</style>
