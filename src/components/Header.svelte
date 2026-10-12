<script lang="ts">
  import { page } from '$app/stores'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import LanguageSelector from '@/components/LanguageSelector.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { docsLink, getStartedLink, loginUrl, navigation } from '@/lib/site'
  import { dict } from '@/lib/i18n'

  let isMenuOpen = false

  // The logo is the home link, so the bar lists the other pages only.
  const links = navigation.filter((item) => item.href !== '/')

  $: if ($page.url.pathname) {
    isMenuOpen = false
  }

</script>

<header class="ts-topnav ts-topnav-floating ts-glass site-nav">
  <RouteLink href="/" class="ts-topnav-brand" aria-label={$dict.chrome.brandHome}>
    <img src="/logo-principal.svg" alt="TerraScale" width="1429" height="345" decoding="async" fetchpriority="high" />
  </RouteLink>

  <nav class="ts-topnav-links site-nav__links" aria-label="Primary">
    {#each links as item (item.href)}
      <RouteLink href={item.href} match="prefix" class="ts-topnav-link" activeClass="is-active">
        {$dict.chrome.nav[item.key]}
      </RouteLink>
    {/each}
    <ExternalLink href={docsLink.href} target="_blank" class="ts-topnav-link">{$dict.chrome.docs}</ExternalLink>
  </nav>

  <div class="ts-topnav-end">
    <div class="site-nav__desktop">
      <LanguageSelector />
    </div>
    <ExternalLink href={loginUrl} class="ts-btn ts-btn-ghost ts-btn-sm site-nav__desktop">{$dict.chrome.login}</ExternalLink>
    <ExternalLink href={getStartedLink.href} class="ts-btn ts-btn-primary ts-btn-sm">{$dict.chrome.getStarted}</ExternalLink>
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
      </nav>
      <div class="site-nav__sheet-actions">
        <ExternalLink href={loginUrl} class="ts-btn ts-btn-secondary ts-btn-lg">{$dict.chrome.login}</ExternalLink>
        <ExternalLink href={getStartedLink.href} class="ts-btn ts-btn-primary ts-btn-lg">{$dict.chrome.getStarted}</ExternalLink>
      </div>
      <LanguageSelector variant="inline" />
    </div>
  {/if}
</header>

<style>
  /* Floating frosted nav (.ts-topnav-floating). It sits over the top of each page; pages pad for it. */
  .site-nav {
    z-index: 100;
    max-width: var(--ts-page-max);
  }

  /* The frost sits on ::before, not the header, so the header is not a backdrop root and the
     menus inside it (language, mobile sheet) still blur the page behind them. */
  .site-nav.ts-glass {
    background: none;
    backdrop-filter: none;
  }

  .site-nav::before {
    content: '';
    position: absolute;
    inset: 0;
    z-index: -1;
    border-radius: inherit;
    background: var(--ts-glass);
    backdrop-filter: blur(var(--ts-blur)) saturate(var(--ts-saturate));
  }

  @supports not ((backdrop-filter: blur(1px)) or (-webkit-backdrop-filter: blur(1px))) {
    .site-nav::before {
      background: var(--ts-surface);
    }
  }

  @media (prefers-reduced-transparency: reduce) {
    .site-nav::before {
      background: var(--ts-surface);
      backdrop-filter: none;
    }
  }

  @media (min-width: 1464px) {
    .site-nav {
      margin-inline: auto;
    }
  }

  :global(.ts-topnav-link.is-active) {
    color: var(--ts-ink);
  }

  .site-nav :global(.site-nav__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .site-nav__menu {
    display: none;
  }

  .site-nav__sheet {
    position: absolute;
    top: calc(100% + var(--ts-s2));
    left: -1px;
    right: -1px;
    max-height: calc(100dvh - var(--ts-header-h) - var(--ts-s8));
    overflow-y: auto;
    display: grid;
    gap: var(--ts-s4);
    padding: var(--ts-s3);
    border-radius: var(--ts-radius-xl);
  }

  .site-nav__sheet-links {
    display: grid;
    gap: 2px;
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
  }

  @media (max-width: 860px) {
    .site-nav__links,
    .site-nav :global(.site-nav__desktop) {
      display: none;
    }
    .site-nav__menu {
      display: inline-grid;
    }
  }
</style>
