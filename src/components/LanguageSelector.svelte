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
    gap: 6px;
  }

  .lang__trigger :global(.lang__globe) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .lang__trigger :global(.lang__chev) {
    width: var(--ts-icon-sm);
    height: var(--ts-icon-sm);
    transition: transform var(--ts-fast) var(--ts-ease);
  }

  .lang__trigger[aria-expanded='true'] :global(.lang__chev) {
    transform: rotate(180deg);
  }

  .lang__menu {
    position: absolute;
    top: calc(100% + var(--ts-s2));
    right: 0;
    min-width: 188px;
    margin: 0;
    list-style: none;
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
    background: var(--ts-line);
    outline-offset: -2px;
  }

  .lang__option :global(.lang__option-check) {
    margin-left: auto;
    width: var(--ts-icon-sm);
    height: var(--ts-icon-sm);
    color: var(--ts-accent);
  }

  .lang--inline button {
    height: 100%;
  }
</style>
