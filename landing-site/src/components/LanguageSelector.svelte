<script lang="ts">
  import { onMount } from 'svelte'
  import Icon from '@/components/Icon.svelte'
  import { locale, dict, setLocale, LOCALES, type LocaleCode } from '@/lib/i18n'

  export let variant: 'dropdown' | 'inline' = 'dropdown'

  let open = false
  let triggerEl: HTMLButtonElement | undefined
  let menuEl: HTMLUListElement | undefined

  $: current = LOCALES.find((l) => l.code === $locale) ?? LOCALES[0]
  $: triggerAria = $dict.chrome.language.currentSuffix.replace('{label}', current.label)

  const toggle = () => (open = !open)
  const choose = (code: LocaleCode) => {
    setLocale(code)
    open = false
    triggerEl?.focus()
  }

  const onWindowClick = (e: MouseEvent) => {
    if (!open) return
    const target = e.target as Node | null
    if (triggerEl?.contains(target as Node)) return
    if (menuEl?.contains(target as Node)) return
    open = false
  }
  const onKeydown = (e: KeyboardEvent) => {
    if (!open) return
    if (e.key === 'Escape') {
      open = false
      triggerEl?.focus()
    } else if (e.key === 'ArrowDown' && menuEl) {
      e.preventDefault()
      const first = menuEl.querySelector<HTMLButtonElement>('button')
      first?.focus()
    }
  }
  const onMenuItemKeydown = (e: KeyboardEvent) => {
    const buttons = Array.from(menuEl?.querySelectorAll<HTMLButtonElement>('button') ?? [])
    const idx = buttons.indexOf(document.activeElement as HTMLButtonElement)
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      buttons[(idx + 1) % buttons.length]?.focus()
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      buttons[(idx - 1 + buttons.length) % buttons.length]?.focus()
    } else if (e.key === 'Tab') {
      open = false
    }
  }

  onMount(() => {
    if (variant !== 'dropdown') return

    window.addEventListener('click', onWindowClick)
    window.addEventListener('keydown', onKeydown)

    return () => {
      window.removeEventListener('click', onWindowClick)
      window.removeEventListener('keydown', onKeydown)
    }
  })
</script>

{#if variant === 'inline'}
  <div class="ts-segmented lang--inline" role="group" aria-label={$dict.chrome.language.menu}>
    {#each LOCALES as l (l.code)}
      <button
        type="button"
        aria-pressed={l.code === $locale}
        aria-current={l.code === $locale ? 'true' : undefined}
        lang={l.bcp47}
        on:click={() => choose(l.code)}
      >
        {l.label}
      </button>
    {/each}
  </div>
{:else}
  <div class="lang">
    <button
      bind:this={triggerEl}
      type="button"
      class="ts-btn ts-btn-ghost ts-btn-sm lang__trigger"
      aria-haspopup="listbox"
      aria-expanded={open}
      aria-label={triggerAria}
      on:click={toggle}
    >
      <Icon name="globe" className="lang__globe" />
      <span class="lang__code">{current.short}</span>
      <Icon name="chevronDown" className="lang__chev" />
    </button>

    {#if open}
      <ul
        bind:this={menuEl}
        class="ts-menu ts-glass lang__menu"
        role="listbox"
        aria-label={$dict.chrome.language.menu}
        on:keydown={onMenuItemKeydown}
      >
        {#each LOCALES as l (l.code)}
          <li role="option" aria-selected={l.code === $locale}>
            <button
              type="button"
              class="ts-menu-item lang__option"
              lang={l.bcp47}
              on:click={() => choose(l.code)}
            >
              <span class="lang__option-label">{l.label}</span>
              {#if l.code === $locale}
                <Icon name="check" className="lang__option-check" />
              {/if}
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  </div>
{/if}

<style>
  .lang {
    position: relative;
  }

  .lang__trigger {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    height: 32px;
    padding: 0 12px;
    border-radius: 8px;
    border: 1px solid var(--ts-divider);
    background: transparent;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-accent);
    font-size: 14px;
    font-weight: 500;
    cursor: pointer;
    transition:
      color 150ms ease,
      border-color 150ms ease,
      background 150ms ease;
  }

  .lang__trigger:hover {
    color: var(--ts-text);
    background: rgba(255, 255, 255, 0.08);
  }

  :global(.lang__globe) {
    width: 15px;
    height: 15px;
  }

  .lang__code {
    line-height: 1;
  }

  :global(.lang__chev) {
    width: 13px;
    height: 13px;
    transition: transform 180ms cubic-bezier(0.2, 0.8, 0.2, 1);
  }

  .lang__trigger[aria-expanded='true'] :global(.lang__chev) {
    transform: rotate(180deg);
  }

  .lang__menu {
    position: absolute;
    top: calc(100% + var(--ts-s2));
    right: 0;
    min-width: 188px;
    border-radius: 8px;
    border: 1px solid var(--ts-glass-border);
    background: rgba(var(--ts-bg-base-raw), 0.96);
    backdrop-filter: blur(18px);
    -webkit-backdrop-filter: blur(18px);
  }

  .lang__option {
    width: 100%;
    border: 0;
    background: transparent;
    font: inherit;
    font-size: var(--ts-fs-sm);
    text-align: left;
    cursor: pointer;
  }

  .lang__option:focus-visible {
    background: rgba(255, 255, 255, 0.08);
    color: var(--ts-text);
    outline: none;
  }

  .lang__option.is-active {
    color: var(--ts-text);
  }

  :global(.lang__option-check) {
    width: 14px;
    height: 14px;
  }

  .lang--inline {
    display: inline-flex;
    padding: 2px;
    gap: 2px;
    border: 1px solid var(--ts-divider);
    border-radius: 8px;
  }

  .lang__inline-btn {
    padding: 6px 12px;
    border-radius: 6px;
    border: 0;
    background: transparent;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-accent);
    font-size: 13px;
    font-weight: 500;
    cursor: pointer;
    transition:
      color 150ms ease,
      background 150ms ease;
  }

  .lang__inline-btn:hover {
    color: var(--ts-text);
  }

  .lang__inline-btn.is-active {
    color: var(--ts-text);
    background: rgba(255, 255, 255, 0.08);
  }
</style>
