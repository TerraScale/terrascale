<script lang="ts">
  import { afterNavigate } from '$app/navigation'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import LanguageSelector from '@/components/LanguageSelector.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { docsLink, getStartedLink, loginUrl, navigation } from '@/lib/site'
  import { dict } from '@/lib/i18n'

  let isMenuOpen = false

  // The logo is the home link, so the bar lists the other pages only.
  const links = navigation.filter((item) => item.href !== '/')

  afterNavigate(() => {
    isMenuOpen = false
  })

</script>

<header class="ts-header">
  <div class="ts-header__inner">
    <RouteLink href="/" class="ts-header__brand" aria-label={$dict.chrome.brandHome}>
      <img src="/icon-blue.svg" alt="" width="28" height="28" decoding="async" fetchpriority="high" />
      <span class="ts-header__word">TerraScale</span>
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

  <div class="ts-topnav-end">
    <div class="site-nav__desktop">
      <LanguageSelector />
    </div>

    <ExternalLink href={getStartedLink.href} class="ts-cta ts-header__mobile-cta">
      {$dict.chrome.getStarted}
      <Icon name={getStartedLink.icon} className="ts-cta__icon" />
    </ExternalLink>

    <button
      type="button"
      class="ts-icon-btn site-nav__menu"
      aria-expanded={isMenuOpen}
      aria-controls="mobile-navigation"
      aria-label={$dict.chrome.toggleNav}
      on:click={() => (isMenuOpen = !isMenuOpen)}
    >
      <Icon name={isMenuOpen ? 'x' : 'menu'} className="site-nav__icon" />
    </button>
  </div>

  {#if isMenuOpen}
    <div id="mobile-navigation" class="ts-glass-strong site-nav__sheet">
      <nav class="site-nav__sheet-links" aria-label="Mobile primary">
        {#each navigation as item (item.href)}
          <RouteLink
            href={item.href}
            match={item.href === '/' ? 'exact' : 'prefix'}
            class="site-nav__sheet-link"
            activeClass="is-active"
            afterClick={() => (isMenuOpen = false)}
          >
            <Icon name={item.icon} className="site-nav__icon" />
            {$dict.chrome.nav[item.key]}
          </RouteLink>
        {/each}
        <ExternalLink href={docsLink.href} target="_blank" class="site-nav__sheet-link">
          <Icon name={docsLink.icon} className="site-nav__icon" />
          {$dict.chrome.docs}
        </ExternalLink>
        <ExternalLink href="https://status.terrascale.tech" target="_blank" class="site-nav__sheet-link">
          <span class="site-nav__icon site-nav__status"><span class="pulse-dot ts-status-link__dot"></span></span>
          {$dict.chrome.status}
        </ExternalLink>
      </nav>
      <div class="site-nav__sheet-actions">
        <ExternalLink href={loginUrl} class="ts-btn ts-btn-secondary ts-btn-lg">{$dict.chrome.login}</ExternalLink>
        <ExternalLink href={getStartedLink.href} class="ts-btn ts-btn-primary ts-btn-lg">{$dict.chrome.getStarted}</ExternalLink>
      </div>
      <LanguageSelector variant="inline" />
    </div>
  {/if}
    </div>
  </div>
</header>

<style>
  .ts-header {
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    z-index: 100;
    border-bottom: 1px solid var(--ts-divider);
    background: rgba(var(--ts-bg-base-raw), 0.9);
    backdrop-filter: blur(20px);
    -webkit-backdrop-filter: blur(20px);
    transition: background 250ms ease;
  }

  /* The frost sits on ::before, not the header, so the header is not a backdrop root and the
     menus inside it (language, mobile sheet) still blur the page behind them. */
  .site-nav.ts-glass {
    background: none;
    backdrop-filter: none;
  }

  .ts-header__inner {
    padding: 0 clamp(16px, 2.5vw, 40px);
    /* Equal side tracks keep the nav on the viewport center, not between brand and actions. */
    display: grid;
    grid-template-columns: minmax(max-content, 1fr) auto minmax(max-content, 1fr);
    align-items: center;
    height: 60px;
    gap: 24px;
  }

  :global(.ts-header__brand) {
    display: flex;
    align-items: center;
    gap: 10px;
    flex-shrink: 0;
    justify-self: start;
    color: var(--ts-text);
    text-decoration: none;
  }

  :global(.ts-header__brand img) {
    height: 28px;
    width: auto;
    display: block;
  }

  .ts-header__word {
    font-family: var(--ts-font-accent);
    font-size: 17px;
    font-weight: 600;
    line-height: 1;
    letter-spacing: -0.02em;
    white-space: nowrap;
  }

  .ts-nav {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 4px;
  }

  :global(.ts-nav__link),
  :global(.ts-header__docs) {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 6px 12px;
    border-radius: 8px;
    font-family: var(--ts-font-accent);
    font-size: 14px;
    font-weight: 500;
    color: var(--ts-text-muted);
    background: transparent;
    border: 0;
    text-decoration: none;
    transition:
      color 150ms ease,
      background 150ms ease;
  }

  :global(.ts-nav__link:hover),
  :global(.ts-header__docs:hover) {
    color: var(--ts-text);
    background: rgba(255, 255, 255, 0.05);
  }

  :global(.ts-nav__icon) {
    width: 16px;
    height: 16px;
    display: inline-block;
    flex-shrink: 0;
  }

  :global(.ts-nav__link.is-active) {
    color: var(--ts-text);
    background: rgba(255, 255, 255, 0.08);
  }

  .ts-header__actions {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 8px;
  }

  :global(.ts-status-link) {
    display: inline-flex;
    align-items: center;
    gap: 9px;
    color: var(--ts-text-muted);
    font-size: 14px;
    font-weight: 500;
    font-family: var(--ts-font-accent);
    text-decoration: none;
    padding: 6px 4px;
    transition: color 150ms ease;
  }

  :global(.ts-status-link:hover) {
    color: var(--ts-text);
  }

  .ts-status-link__dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: #6bca92;
    display: inline-block;
  }

  :global(.ts-cta) {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    height: 32px;
    background: var(--ts-primary);
    color: #fff;
    font-family: var(--ts-font-accent);
    font-weight: 500;
    font-size: 13px;
    padding: 0 14px;
    border-radius: 8px;
    text-decoration: none;
    transition: opacity 150ms ease;
  }

  :global(.ts-cta:hover) {
    opacity: 0.9;
  }

  :global(.ts-cta__icon) {
    width: 13px;
    height: 13px;
    display: inline-block;
  }

  .site-nav__menu {
    display: none;
  }

  :global(.ts-header__mobile-cta) {
    display: none;
  }

  .site-nav__menu:hover {
    color: var(--ts-text);
    background: rgba(255, 255, 255, 0.08);
  }

  :global(.site-nav__icon) {
    width: var(--ts-icon-lg);
    height: var(--ts-icon-lg);
  }

  .site-nav__sheet-links {
    display: grid;
    gap: 2px;
  }

  .site-nav__sheet :global(.site-nav__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .site-nav__status {
    display: inline-grid;
    place-items: center;
  }

  .site-nav__sheet {
    display: none;
    position: absolute;
    top: 60px;
    left: 0;
    right: 0;
    max-height: calc(100dvh - 60px);
    overflow-y: auto;
    border-top: 1px solid var(--ts-divider);
    padding: 16px clamp(16px, 2.5vw, 40px) 20px;
    /* Opaque: the header's own backdrop-filter makes it the backdrop root, so frost on the sheet
       cannot blur the page and the text behind would show through. */
    background: var(--ts-bg-base);
    backdrop-filter: none;
  }

  :global(.site-nav__sheet-link) {
    display: flex;
    align-items: center;
    gap: var(--ts-s3);
    height: var(--ts-control-h-lg);
    padding: 0 var(--ts-s3);
    border-radius: var(--ts-radius);
    color: var(--ts-ink-2);
    font-size: var(--ts-fs-md);
    font-weight: 500;
  }

  :global(.site-nav__sheet-link:hover) {
    background: var(--ts-surface-2);
    color: var(--ts-ink);
    text-decoration: none;
  }

  :global(.site-nav__sheet-link.is-active) {
    background: var(--ts-accent-soft);
    color: var(--ts-ink);
  }

  :global(.site-nav__sheet-link.is-active .site-nav__icon) {
    color: var(--ts-accent);
  }

  .site-nav__sheet-actions {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: var(--ts-s2);
    margin-block: var(--ts-s4);
    padding-top: var(--ts-s4);
    border-top: 1px solid var(--ts-divider);
  }

  @media (max-width: 1240px) {
    .ts-header__inner {
      display: flex;
      gap: 8px;
    }
    :global(.ts-header__mobile-cta) {
      display: inline-flex;
      margin-left: auto;
    }
    .ts-nav,
    .site-nav__desktop,
    .ts-header__actions > :global(a) {
      display: none;
    }
    .ts-header__actions {
      margin-left: auto;
    }
    .site-nav__sheet {
      display: block;
    }
    .site-nav__menu {
      display: inline-grid;
    }
  }
</style>
