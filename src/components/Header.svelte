<script lang="ts">
  import { onMount } from 'svelte'
  import { page } from '$app/stores'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import LanguageSelector from '@/components/LanguageSelector.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { docsLink, getStartedLink, launchPhase, loginUrl, navigation } from '@/lib/site'
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
</script>

<header class="ts-header" class:scrolled>
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
        {launchPhase === 'public-alpha' ? $dict.chrome.getStarted : $dict.chrome.prelaunch}
        <Icon name={getStartedLink.icon} className="ts-cta__icon" />
      </ExternalLink>

      <LanguageSelector />
    </div>

    <ExternalLink href={getStartedLink.href} class="ts-cta ts-header__mobile-cta">
      {launchPhase === 'public-alpha' ? $dict.chrome.getStarted : $dict.chrome.prelaunch}
      <Icon name={getStartedLink.icon} className="ts-cta__icon" />
    </ExternalLink>

    <button
      type="button"
      class="ts-header__menu"
      aria-expanded={isMenuOpen}
      aria-controls="mobile-navigation"
      aria-label={$dict.chrome.toggleNav}
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
          {launchPhase === 'public-alpha' ? $dict.chrome.getStarted : $dict.chrome.prelaunch}
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

  .ts-header.scrolled {
    background: rgba(var(--ts-bg-base-raw), 0.94);
  }

  .ts-header__inner {
    padding: 0 clamp(16px, 2.5vw, 40px);
    display: flex;
    align-items: center;
    height: 60px;
    gap: 24px;
  }

  :global(.ts-header__brand) {
    display: flex;
    align-items: center;
    gap: 10px;
    flex-shrink: 0;
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
    margin: 0 auto;
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

  :global(.ts-header__mobile-cta) {
    display: none;
  }

  .ts-header__menu:hover {
    color: var(--ts-text);
    background: rgba(255, 255, 255, 0.08);
  }

  :global(.ts-header__menu-icon) {
    width: 20px;
    height: 20px;
    display: block;
  }

  .ts-header__mobile {
    max-height: calc(100dvh - 60px);
    overflow-y: auto;
    border-top: 1px solid var(--ts-divider);
    padding: 16px clamp(16px, 2.5vw, 40px) 20px;
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

  .ts-header__mobile-lang {
    margin-top: 8px;
  }

  @media (max-width: 1240px) {
    .ts-header__inner {
      gap: 8px;
    }
    :global(.ts-header__mobile-cta) {
      display: inline-flex;
      margin-left: auto;
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
