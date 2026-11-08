<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import LanguageSelector from '@/components/LanguageSelector.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { footerColumns, registrationUrl, socialLinks } from '@/lib/site'
  import { dict } from '@/lib/i18n'

  const year = new Date().getFullYear()

  const isExternal = (href: string) => href.startsWith('http') || href.startsWith('mailto:')

  $: columns = footerColumns.map((column) => ({
    column,
    title: $dict.footer.columns[column.key].title,
    links: column.links.map((link) => ({
      link,
      name: ($dict.footer.columns[column.key].links as Record<string, string>)[link.key] ?? link.key,
    })),
  }))
  $: socials = socialLinks.map((social) => ({
    social,
    name: ($dict.footer.social as Record<string, string>)[social.key] ?? social.key,
  }))
</script>

<footer class="site-footer">
  <div class="ts-container site-footer__grid">
    <div class="site-footer__brand">
      <RouteLink href="/" class="site-footer__logo" aria-label={$dict.chrome.brandHome}>
        <img src="/icon-blue.svg" alt="" width="835" height="942" decoding="async" loading="lazy" />
        <span class="site-footer__word">TerraScale</span>
      </RouteLink>
      <p class="site-footer__tagline">{$dict.footer.tagline}</p>
      <ul class="site-footer__socials">
        {#each socials as { social, name } (social.key)}
          <li>
            <ExternalLink
              href={social.href}
              target={social.href.startsWith('mailto:') ? undefined : '_blank'}
              aria-label={name}
              class="site-footer__social"
            >
              <Icon name={social.icon} className="site-footer__social-icon" />
            </ExternalLink>
          </li>
        {/each}
      </ul>
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary site-footer__cta">
        {$dict.chrome.getStarted}
      </ExternalLink>
    </div>

    {#each columns as { column, title, links } (column.key)}
      <nav class="site-footer__column" aria-label={title}>
        <div class="ts-label">{title}</div>
        <ul class="site-footer__list">
          {#each links as { link, name } (link.key)}
            <li>
              {#if isExternal(link.href)}
                <ExternalLink href={link.href} target={link.href.startsWith('mailto:') ? undefined : '_blank'} class="site-footer__link">{name}</ExternalLink>
              {:else}
                <RouteLink href={link.href} class="site-footer__link">{name}</RouteLink>
              {/if}
            </li>
          {/each}
        </ul>
      </nav>
    {/each}
  </div>

  <div class="ts-container">
    <div class="site-footer__meta">
      <span>© {year} TerraScale. {$dict.footer.rightsReserved}</span>
      <div class="site-footer__meta-end">
        <ExternalLink href="https://status.terrascale.tech" target="_blank" class="ts-status-link">
          <span class="ts-dot ts-dot-live site-footer__dot" aria-hidden="true"></span>
          {$dict.chrome.status}
        </ExternalLink>
        <div class="site-footer__lang">
          <LanguageSelector variant="inline" />
        </div>
      </div>
    </div>
  </div>
</footer>

<style>
  .site-footer {
    background: var(--ts-bg-surface);
    border-top: 1px solid var(--ts-divider);
    color: var(--ts-ink-3);
    font-size: var(--ts-fs-sm);
  }

  .site-footer__grid {
    display: grid;
    grid-template-columns: 1.6fr repeat(4, minmax(0, 1fr));
    gap: var(--ts-s10) var(--ts-s8);
    padding-block: var(--ts-s10) var(--ts-s8);
  }

  .site-footer__brand {
    display: grid;
    gap: var(--ts-s4);
    align-content: start;
    justify-items: center;
    text-align: center;
  }

  .site-footer :global(.site-footer__logo) {
    display: inline-flex;
    align-items: center;
    gap: 14px;
    color: var(--ts-text);
    text-decoration: none;
  }

  .site-footer :global(.site-footer__logo img) {
    width: auto;
    height: 42px;
  }

  .site-footer__word {
    font-size: 27px;
    font-weight: 600;
    line-height: 1;
    letter-spacing: -0.02em;
    white-space: nowrap;
  }

  .site-footer__tagline {
    margin: 0;
    color: var(--ts-ink-3);
    font-size: var(--ts-fs-sm);
    line-height: var(--ts-lh);
  }

  .site-footer__socials {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: var(--ts-s2);
  }

  .site-footer :global(.site-footer__social) {
    display: inline-grid;
    place-items: center;
    width: 36px;
    height: 36px;
    border: 1px solid var(--ts-line-strong);
    border-radius: var(--ts-radius);
    color: var(--ts-ink-2);
    transition:
      color var(--ts-fast) var(--ts-ease),
      border-color var(--ts-fast) var(--ts-ease),
      background-color var(--ts-fast) var(--ts-ease);
  }

  .site-footer :global(.site-footer__social:hover) {
    color: var(--ts-ink);
    border-color: var(--ts-control-line);
    background: var(--ts-surface-2);
  }

  .site-footer :global(.site-footer__social-icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .site-footer :global(.site-footer__cta) {
    justify-self: center;
  }

  .site-footer__column {
    display: grid;
    gap: var(--ts-s4);
    align-content: start;
  }

  .site-footer__list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: var(--ts-s3);
  }

  .site-footer :global(.site-footer__link) {
    color: var(--ts-ink-2);
    line-height: var(--ts-lh);
    overflow-wrap: anywhere;
    transition: color var(--ts-fast) var(--ts-ease);
  }

  .site-footer :global(.site-footer__link:hover) {
    color: var(--ts-ink);
  }

  .site-footer__meta {
    display: flex;
    align-items: center;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: var(--ts-s3) var(--ts-s5);
    padding-block: var(--ts-s5) var(--ts-s6);
    border-top: 1px solid var(--ts-line);
  }

  .site-footer__meta-end {
    display: flex;
    align-items: center;
    gap: var(--ts-s5);
    margin-left: auto;
  }

  .site-footer__dot {
    --state: var(--ts-success);
  }

  @media (max-width: 960px) {
    .site-footer__grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: var(--ts-s8) var(--ts-s6);
    }

    .site-footer__brand {
      grid-column: 1 / -1;
    }
  }

  @media (max-width: 640px) {
    .site-footer__grid {
      padding-block: var(--ts-s8) var(--ts-s6);
    }

    .site-footer__meta-end {
      margin-left: 0;
      width: 100%;
      justify-content: space-between;
    }
  }
</style>
