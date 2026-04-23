<script lang="ts">
  import { onMount } from 'svelte'
  import { page } from '$app/stores'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { docsLink, navigation } from '@/lib/site'

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
</script>

<header class="ts-header" class:scrolled>
  <div class="ts-header__inner">
    <RouteLink href="/" class="ts-header__brand" aria-label="TerraScale home">
      <img
        src="/logo-principal.svg"
        alt="TerraScale"
        width="1429"
        height="345"
        decoding="async"
        fetchpriority="high"
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
          {item.name}
        </RouteLink>
      {/each}
    </nav>

    <div class="ts-header__actions">
      <ExternalLink
        href={docsLink.href}
        target="_blank"
        rel="noopener noreferrer"
        class="ts-header__docs"
      >
        <Icon name={docsLink.icon} className="ts-nav__icon" />
        {docsLink.name}
      </ExternalLink>

      <ExternalLink
        href="https://status.terrascale.tech"
        target="_blank"
        rel="noopener noreferrer"
        class="ts-status-link"
      >
        <span class="pulse-dot ts-status-link__dot"></span>
        Status
      </ExternalLink>

      <RouteLink href="/contact" class="ts-cta cta-shine">
        Get Started
        <Icon name="arrowRight" className="ts-cta__icon" />
      </RouteLink>
    </div>

    <button
      type="button"
      class="ts-header__menu"
      aria-expanded={isMenuOpen}
      aria-controls="mobile-navigation"
      aria-label="Toggle navigation menu"
      on:click={() => {
        isMenuOpen = !isMenuOpen
      }}
    >
      <Icon name={isMenuOpen ? 'x' : 'menu'} className="ts-header__menu-icon" />
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
            {item.name}
          </RouteLink>
        {/each}
        <ExternalLink
          href={docsLink.href}
          target="_blank"
          rel="noopener noreferrer"
          class="ts-nav__link ts-nav__link--mobile"
        >
          <Icon name={docsLink.icon} className="ts-nav__icon" />
          {docsLink.name}
        </ExternalLink>
        <ExternalLink
          href="https://status.terrascale.tech"
          target="_blank"
          rel="noopener noreferrer"
          class="ts-status-link ts-status-link--mobile"
        >
          <span class="pulse-dot ts-status-link__dot"></span>
          Status
        </ExternalLink>
        <RouteLink
          href="/contact"
          class="ts-cta cta-shine ts-cta--mobile"
          afterClick={() => {
            isMenuOpen = false
          }}
        >
          Get Started
          <Icon name="arrowRight" className="ts-cta__icon" />
        </RouteLink>
      </nav>
    </div>
  {/if}
</header>

<style>
  .ts-header {
    position: sticky;
    top: 0;
    z-index: 100;
    border-bottom: 1px solid var(--ts-divider);
    background: rgba(var(--ts-bg-base-raw), 0.75);
    backdrop-filter: blur(20px);
    -webkit-backdrop-filter: blur(20px);
    transition: background 250ms ease;
  }

  .ts-header.scrolled {
    background: rgba(var(--ts-bg-base-raw), 0.94);
  }

  .ts-header__inner {
    max-width: 1440px;
    margin: 0 auto;
    padding: 0 40px;
    display: grid;
    grid-template-columns: 1fr auto 1fr;
    align-items: center;
    height: 72px;
    gap: 16px;
  }

  :global(.ts-header__brand) {
    display: flex;
    align-items: center;
    flex-shrink: 0;
    text-decoration: none;
  }

  :global(.ts-header__brand img) {
    height: 38px;
    width: auto;
    display: block;
  }

  .ts-nav {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 2px;
  }

  :global(.ts-nav__link),
  :global(.ts-header__docs) {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 8px 14px;
    border-radius: 4px;
    font-family: var(--ts-font-accent);
    font-size: 15px;
    font-weight: 600;
    color: var(--ts-text-muted);
    background: transparent;
    border: 1px solid transparent;
    text-decoration: none;
    transition:
      color 150ms ease,
      background 150ms ease,
      border-color 150ms ease;
  }

  :global(.ts-nav__link:hover),
  :global(.ts-header__docs:hover) {
    color: var(--ts-text);
  }

  :global(.ts-nav__icon) {
    width: 16px;
    height: 16px;
    display: inline-block;
    flex-shrink: 0;
  }

  :global(.ts-nav__link.is-active) {
    color: var(--ts-primary-light);
    background: rgba(var(--ts-primary-rgb), 0.1);
    border-color: rgba(var(--ts-primary-rgb), 0.25);
  }

  .ts-header__actions {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 16px;
  }

  :global(.ts-status-link) {
    display: inline-flex;
    align-items: center;
    gap: 9px;
    color: var(--ts-text);
    font-size: 15px;
    font-weight: 700;
    font-family: var(--ts-font-accent);
    text-transform: uppercase;
    letter-spacing: 0.08em;
    text-decoration: none;
    padding: 8px 4px;
    transition: color 150ms ease;
  }

  :global(.ts-status-link:hover) {
    color: var(--ts-secondary);
  }

  .ts-status-link__dot {
    width: 9px;
    height: 9px;
    border-radius: 5px;
    background: var(--ts-secondary);
    box-shadow: 0 0 10px var(--ts-secondary);
    display: inline-block;
  }

  :global(.ts-cta) {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    background: var(--ts-gradient-primary);
    color: #fff;
    font-family: var(--ts-font-accent);
    font-weight: 700;
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    padding: 9px 20px;
    border-radius: 5px;
    text-decoration: none;
    box-shadow: 0 4px 14px rgba(var(--ts-primary-rgb), 0.35);
    transition:
      transform 180ms cubic-bezier(0.2, 0.8, 0.2, 1),
      box-shadow 180ms ease;
  }

  :global(.ts-cta:hover) {
    transform: translateY(-1px);
    box-shadow: 0 8px 22px rgba(var(--ts-primary-rgb), 0.5);
  }

  :global(.ts-cta__icon) {
    width: 13px;
    height: 13px;
    display: inline-block;
  }

  .ts-header__menu {
    display: none;
    background: transparent;
    border: 1px solid var(--ts-divider);
    color: var(--ts-text-muted);
    border-radius: 6px;
    padding: 8px;
    cursor: pointer;
    transition:
      color 150ms,
      border-color 150ms,
      background 150ms;
  }

  .ts-header__menu:hover {
    color: var(--ts-text);
    border-color: rgba(var(--ts-primary-rgb), 0.35);
    background: rgba(var(--ts-primary-rgb), 0.08);
  }

  :global(.ts-header__menu-icon) {
    width: 20px;
    height: 20px;
    display: block;
  }

  .ts-header__mobile {
    border-top: 1px solid var(--ts-divider);
    padding: 16px 40px 20px;
    background: rgba(var(--ts-bg-base-raw), 0.96);
  }

  .ts-mobile-nav {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  :global(.ts-nav__link--mobile) {
    padding: 10px 14px;
  }

  :global(.ts-status-link--mobile) {
    padding: 10px 14px;
    font-size: 13px;
  }

  :global(.ts-cta--mobile) {
    justify-content: center;
    margin-top: 6px;
  }

  @media (max-width: 960px) {
    .ts-header__inner {
      padding: 0 20px;
      grid-template-columns: 1fr auto;
    }
    .ts-nav {
      display: none;
    }
    .ts-header__actions {
      display: none;
    }
    .ts-header__menu {
      display: inline-flex;
    }
  }
</style>
