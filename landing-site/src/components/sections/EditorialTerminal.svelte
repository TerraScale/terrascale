<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import { tokenize, CODE_SAMPLES, LANG_META, type Lang } from '@/lib/highlight'
  import { dict } from '@/lib/i18n'

  let lang: Lang = 'typescript'
  let copied = false
  let copyTimer: ReturnType<typeof setTimeout> | null = null

  $: code = CODE_SAMPLES[lang]
  $: tokens = tokenize(code, lang)

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

<div class="ed-term">
  <div class="ed-term__head sdk-tabs-header">
    <div class="sdk-tabs" role="tablist" aria-label={$dict.code.tablistLabel}>
      {#each LANG_META as l (l.id)}
        <button
          type="button"
          role="tab"
          id={`editorial-sdk-tab-${l.id}`}
          aria-controls="editorial-sdk-panel"
          tabindex={lang === l.id ? 0 : -1}
          aria-selected={lang === l.id}
          on:click={() => { lang = l.id; copied = false }}
        >
          <img src={l.icon} alt="" width="16" height="16" aria-hidden="true" />
          {l.label}
        </button>
      {/each}
    </div>
    <button
      type="button"
      class="ed-term__copy"
      class:is-copied={copied}
      on:click={handleCopy}
      aria-label={$dict.code.copyAria}
    >
      <Icon name={copied ? 'check' : 'code2'} className="ed-term__copy-icon" />
      {copied ? $dict.code.copied : $dict.code.copy}
    </button>
  </div>

  <pre id="editorial-sdk-panel" class="ed-term__pre" tabindex="0" role="tabpanel" aria-labelledby={`editorial-sdk-tab-${lang}`}><code>{#each tokens as t, i (i)}{#if t.className}<span class={t.className}>{t.text}</span>{:else}{t.text}{/if}{/each}</code></pre>

  <div class="ed-term__foot">
    <span class="ed-term__file">{LANG_META.find((l) => l.id === lang)?.file}</span>
    <span class="ed-term__dot" aria-hidden="true"></span>
    <span class="ed-term__region">gru · sjc · fra</span>
  </div>
</div>

<style>
  .ed-term {
    min-width: 0;
    background: var(--ts-bg-surface);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    overflow: hidden;
  }

  .ed-term__head {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 48px;
    padding: 0 8px 0 16px;
    border-bottom: 1px solid var(--ts-divider);
    color: var(--ts-text-muted);
    font-family: var(--ts-font-mono);
    font-size: 12px;
  }

  .ed-term__copy {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    flex-shrink: 0;
    height: 32px;
    padding: 0 12px;
    border: 1px solid transparent;
    border-radius: var(--ts-radius-md);
    background: transparent;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-accent);
    font-size: 13px;
    font-weight: 500;
    line-height: 1;
    cursor: pointer;
    transition:
      background-color var(--ts-transition-fast),
      color var(--ts-transition-fast);
  }

  .ed-term__copy:hover {
    background: var(--ts-bg-card);
    color: var(--ts-text);
  }

  .ed-term__copy.is-copied {
    color: var(--ts-success);
  }

  :global(.ed-term__copy-icon) {
    width: 14px;
    height: 14px;
  }

  .ed-term__pre {
    margin: 0;
    padding: 20px;
    height: 380px;
    overflow: auto;
    font-family: var(--ts-font-mono);
    font-size: 13px;
    line-height: 1.7;
    color: var(--ts-text);
    tab-size: 2;
    white-space: pre;
  }

  .ed-term__pre code {
    font: inherit;
  }

  .ed-term__foot {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 10px 16px;
    border-top: 1px solid var(--ts-divider);
    font-family: var(--ts-font-mono);
    font-size: 12px;
    color: var(--ts-text-muted);
  }

  .ed-term__file {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .ed-term__dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--ts-primary-light);
    box-shadow: 0 0 0 3px rgb(var(--ts-primary-rgb) / 0.18);
    flex: none;
  }

  .ed-term__region {
    margin-left: auto;
    white-space: nowrap;
  }

  @media (max-width: 640px) {
    .ed-term__head {
      padding-inline: 8px;
    }

    .ed-term__pre {
      height: 300px;
      font-size: 12px;
      padding: 16px;
    }
  }
</style>
