<script lang="ts">
  import { onMount } from 'svelte'
  import { page } from '$app/stores'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import LanguageSelector from '@/components/LanguageSelector.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { docsLink, getStartedLink, loginUrl, navigation } from '@/lib/site'
  import { dict } from '@/lib/i18n'

  let scrolled = false
  let isMenuOpen = false

  $: if ($page.url.pathname) {
    isMenuOpen = false
  }

  onMount(() => {
    const onScroll = () => {
      scrolled = window.scrollY > 24
    }
    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  })

  // app.html applies the saved theme before paint; this only flips and saves it.
  const toggleTheme = () => {
    const root = document.documentElement
    const next = root.dataset.theme === 'light' ? 'dark' : 'light'
    root.dataset.theme = next
    try {
      localStorage.setItem('ts-theme', next)
    } catch {
      // Private mode: the choice lasts until reload.
    }
  }
</script>

<header class="ts-header" class:scrolled>
  <div class="ts-header__inner">
    <RouteLink href="/" class="ts-header__brand" aria-label={$dict.chrome.brandHome}>
      <img
        class="brand-logo brand-logo--dark"
        src="/logo-principal.svg"
        alt="TerraScale"
        width="1429"
        height="345"
        decoding="async"
        fetchpriority="high"
      />
      <img
        class="brand-logo brand-logo--light"
        src="/logo-blue.svg"
        alt="TerraScale"
        width="1429"
        height="345"
        decoding="async"
      />
    </RouteLink>

    <nav class="ts-nav" aria-label="Primary">
      {#each navigation as item (item.href)}
        <RouteLink
          href={item.href}
          match={item.href === '/' ? 'exact' : 'prefix'}
          class="ts-nav__link"
          activeClass="is-active"
          inactiveClass=""
        >
          <Icon name={item.icon} className="ts-nav__icon" />
          {$dict.chrome.nav[item.key]}
        </RouteLink>
      {/each}
    </nav>

    <div class="ts-header__actions">
      <ExternalLink
        href={docsLink.href}
        target="_blank"
        class="ts-header__docs"
      >
        <Icon name={docsLink.icon} className="ts-nav__icon" />
        {$dict.chrome.docs}
      </ExternalLink>

      <ExternalLink
        href="https://status.terrascale.tech"
        target="_blank"
        class="ts-status-link"
      >
        <span class="pulse-dot ts-status-link__dot"></span>
        {$dict.chrome.status}
      </ExternalLink>

      <ExternalLink href={loginUrl} class="ts-status-link">
        {$dict.chrome.login}
      </ExternalLink>

      <ExternalLink href={getStartedLink.href} class="ts-cta">
        {$dict.chrome.getStarted}
        <Icon name={getStartedLink.icon} className="ts-cta__icon" />
      </ExternalLink>

      <LanguageSelector />
    </div>

    <button
      type="button"
      class="ts-header__icon-btn ts-header__theme"
      aria-label={$dict.chrome.toggleTheme}
      on:click={toggleTheme}
    >
      <Icon name="sun" className="ts-header__btn-icon theme-icon--sun" />
      <Icon name="moon" className="ts-header__btn-icon theme-icon--moon" />
    </button>

    <button
      type="button"
      class="ts-header__icon-btn ts-header__menu"
      aria-expanded={isMenuOpen}
      aria-controls="mobile-navigation"
      aria-label={$dict.chrome.toggleNav}
      on:click={() => {
        isMenuOpen = !isMenuOpen
      }}
    >
      <Icon name={isMenuOpen ? 'x' : 'menu'} className="ts-header__btn-icon" />
    </button>
  </div>

  {#if isMenuOpen}
    <div id="mobile-navigation" class="ts-header__mobile">
      <nav class="ts-mobile-nav" aria-label="Mobile primary">
        {#each navigation as item (item.href)}
          <RouteLink
            href={item.href}
            match={item.href === '/' ? 'exact' : 'prefix'}
            class="ts-nav__link ts-nav__link--mobile"
            activeClass="is-active"
            inactiveClass=""
            afterClick={() => {
              isMenuOpen = false
            }}
          >
            <Icon name={item.icon} className="ts-nav__icon" />
            {$dict.chrome.nav[item.key]}
          </RouteLink>
        {/each}
        <ExternalLink
          href={docsLink.href}
          target="_blank"
          class="ts-nav__link ts-nav__link--mobile"
        >
          <Icon name={docsLink.icon} className="ts-nav__icon" />
          {$dict.chrome.docs}
        </ExternalLink>
        <ExternalLink
          href="https://status.terrascale.tech"
          target="_blank"
          class="ts-status-link ts-status-link--mobile"
        >
          <span class="pulse-dot ts-status-link__dot"></span>
          {$dict.chrome.status}
        </ExternalLink>
        <ExternalLink href={loginUrl} class="ts-status-link ts-status-link--mobile">
          {$dict.chrome.login}
        </ExternalLink>
        <ExternalLink href={getStartedLink.href} class="ts-cta ts-cta--mobile">
          {$dict.chrome.getStarted}
          <Icon name={getStartedLink.icon} className="ts-cta__icon" />
        </ExternalLink>
        <div class="ts-header__mobile-lang">
          <LanguageSelector variant="inline" />
        </div>
      </nav>
    </div>
  {/if}
</header>

<style>
  /* Frost lives on ::before so the header is not a backdrop root: the mobile sheet and
     language menu inside it can then blur the page too. Write only backdrop-filter;
     Lightning CSS adds -webkit- itself (writing both makes it drop the standard one). */
  .ts-header {
    position: sticky;
    top: 0;
    z-index: 100;
    border-bottom: 1px solid var(--ts-glass-line);
  }

  .ts-header::before {
    content: '';
    position: absolute;
    inset: 0;
    z-index: -1;
    background: var(--ts-glass);
    backdrop-filter: blur(var(--ts-blur)) saturate(var(--ts-saturate));
    transition: background var(--ts-base) ease;
  }

  .ts-header.scrolled::before {
    background: var(--ts-glass-strong);
  }

  .ts-header__inner {
    max-width: var(--ts-page-max);
    margin: 0 auto;
    padding: 0 40px;
    display: flex;
    align-items: center;
    gap: 8px;
    height: var(--ts-header-h);
  }

  :global(.ts-header__brand) {
    display: flex;
    align-items: center;
    flex-shrink: 0;
    text-decoration: none;
  }

  .brand-logo {
    height: 34px;
    width: auto;
    display: block;
  }

  .brand-logo--light,
  :global([data-theme='light']) .brand-logo--dark {
    display: none;
  }

  :global([data-theme='light']) .brand-logo--light {
    display: block;
  }

  .ts-nav {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 2px;
    margin: 0 auto;
  }

  :global(.ts-nav__link),
  :global(.ts-header__docs) {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 8px 10px;
    border-radius: var(--ts-radius);
    font-family: var(--ts-font-sans);
    font-size: 15px;
    font-weight: 500;
    color: var(--ts-ink-2);
    background: transparent;
    border: 1px solid transparent;
    text-decoration: none;
    transition:
      color var(--ts-fast) ease,
      background var(--ts-fast) ease;
  }

  :global(.ts-nav__link:hover),
  :global(.ts-header__docs:hover) {
    color: var(--ts-ink);
  }

  :global(.ts-nav__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
    display: inline-block;
    flex-shrink: 0;
  }

  :global(.ts-nav__link.is-active) {
    color: var(--ts-accent);
    background: var(--ts-accent-soft);
  }

  .ts-header__actions {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 14px;
  }

  :global(.ts-status-link) {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    color: var(--ts-ink);
    font-size: 15px;
    font-weight: 500;
    font-family: var(--ts-font-sans);
    text-decoration: none;
    padding: 8px 4px;
    transition: color var(--ts-fast) ease;
  }

  :global(.ts-status-link:hover) {
    color: var(--ts-accent);
  }

  .ts-status-link__dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--ts-success);
    display: inline-block;
  }

  :global(.ts-cta) {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    height: var(--ts-control-h);
    padding: 0 16px;
    border-radius: var(--ts-radius);
    background: var(--ts-primary);
    color: var(--ts-on-primary);
    font-family: var(--ts-font-sans);
    font-weight: 600;
    font-size: 14px;
    text-decoration: none;
    transition: background var(--ts-fast) ease;
  }

  :global(.ts-cta:hover) {
    background: var(--ts-primary-hover);
  }

  :global(.ts-cta__icon) {
    width: 14px;
    height: 14px;
    display: inline-block;
  }

  .ts-header__icon-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: var(--ts-control-h);
    height: var(--ts-control-h);
    padding: 0;
    flex-shrink: 0;
    background: transparent;
    border: 1px solid var(--ts-line);
    color: var(--ts-ink-2);
    border-radius: var(--ts-radius);
    cursor: pointer;
    transition:
      color var(--ts-fast) ease,
      background var(--ts-fast) ease;
  }

  .ts-header__icon-btn:hover {
    color: var(--ts-ink);
    background: var(--ts-surface-2);
  }

  .ts-header__theme {
    margin-left: 6px;
  }

  .ts-header__icon-btn :global(.ts-header__btn-icon) {
    width: 18px;
    height: 18px;
    display: block;
  }

  /* Show the icon of the theme you would switch to. */
  .ts-header__theme :global(.theme-icon--moon),
  :global([data-theme='light']) .ts-header__theme :global(.theme-icon--sun) {
    display: none;
  }

  :global([data-theme='light']) .ts-header__theme :global(.theme-icon--moon) {
    display: block;
  }

  .ts-header__menu {
    display: none;
  }

  .ts-header__mobile {
    position: absolute;
    top: 100%;
    left: 0;
    right: 0;
    max-height: calc(100dvh - var(--ts-header-h));
    overflow-y: auto;
    border-bottom: 1px solid var(--ts-glass-line);
    padding: 16px 20px 20px;
    background: var(--ts-glass-strong);
    backdrop-filter: blur(var(--ts-blur-lg)) saturate(var(--ts-saturate));
  }

  @supports not ((backdrop-filter: blur(1px)) or (-webkit-backdrop-filter: blur(1px))) {
    .ts-header::before,
    .ts-header__mobile {
      background: var(--ts-canvas);
    }
  }

  .ts-mobile-nav {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  :global(.ts-nav__link--mobile) {
    padding: 10px 14px;
  }

  :global(.ts-status-link--mobile) {
    padding: 10px 14px;
  }

  :global(.ts-cta--mobile) {
    justify-content: center;
    margin-top: 8px;
    height: var(--ts-control-h-lg);
  }

  .ts-header__mobile-lang {
    margin-top: 12px;
  }

  @media (max-width: 1240px) {
    .ts-header__inner {
      padding: 0 20px;
    }
    .ts-nav,
    .ts-header__actions {
      display: none;
    }
    /* Push the theme + menu buttons to the right edge once the nav is hidden. */
    .ts-header__theme {
      margin-left: auto;
    }
    .ts-header__menu {
      display: inline-flex;
    }
  }
</style>
