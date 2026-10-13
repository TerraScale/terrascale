<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import LanguageSelector from '@/components/LanguageSelector.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { footerColumns, socialLinks } from '@/lib/site'
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
        <img src="/logo-principal.svg" alt="TerraScale" width="1429" height="345" decoding="async" loading="lazy" />
      </RouteLink>
      <p class="site-footer__tagline">{$dict.footer.tagline}</p>
      <div class="site-footer__socials">
        {#each socials as { social, name } (social.key)}
          <ExternalLink href={social.href} target="_blank" aria-label={name} class="ts-icon-btn">
            <Icon name={social.icon} className="site-footer__social-icon" />
          </ExternalLink>
        {/each}
      </div>
    </div>

    {#each columns as { column, title, links } (column.key)}
      <div class="site-footer__column">
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
      </div>
    {/each}
  </div>

  <div class="ts-container site-footer__meta">
    <span>© {year} TerraScale. {$dict.footer.rightsReserved}</span>
    <ExternalLink href="https://status.terrascale.tech" target="_blank" class="ts-status-link">
      <span class="ts-dot ts-dot-live site-footer__dot" aria-hidden="true"></span>
      {$dict.chrome.status}
    </ExternalLink>
    <div class="site-footer__lang">
      <LanguageSelector variant="inline" />
    </div>
  </div>
</footer>

<style>
  .site-footer {
    border-top: 1px solid var(--ts-line);
    color: var(--ts-ink-3);
    font-size: var(--ts-fs-sm);
  }

  /* Same half-gap as .ts-section, so the footer sits on the section rhythm. */
  .site-footer__grid {
    display: grid;
    grid-template-columns: minmax(0, 2fr) repeat(3, minmax(0, 1fr));
    gap: var(--ts-s8);
    padding-block: var(--ts-s12);
  }

  .site-footer__brand {
    display: grid;
    gap: var(--ts-s4);
    align-content: start;
    justify-items: start;
  }

  .site-footer :global(.site-footer__logo img) {
    height: 18px;
    width: auto;
  }

  .site-footer__tagline {
    max-width: 340px;
    color: var(--ts-ink-2);
  }

  .site-footer__socials {
    display: flex;
    gap: var(--ts-s1);
    margin-left: -6px;
  }

  .site-footer :global(.site-footer__social-icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .site-footer__column {
    display: grid;
    gap: var(--ts-s3);
    align-content: start;
  }

  .site-footer__list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: var(--ts-s2);
  }

  .site-footer :global(.site-footer__link) {
    color: var(--ts-ink-2);
  }

  .site-footer :global(.site-footer__link:hover) {
    color: var(--ts-ink);
  }

  /* Same width as the text above, so the hairline does not run into the gutters. */
  .site-footer__meta {
    width: calc(100% - 2 * var(--ts-gutter));
    max-width: var(--ts-content-max);
    padding-inline: 0;
    display: flex;
    align-items: center;
    gap: var(--ts-s5);
    flex-wrap: wrap;
    padding-block: var(--ts-s5);
    border-top: 1px solid var(--ts-line);
  }

  .site-footer__dot {
    --state: var(--ts-accent);
  }

  .site-footer__lang {
    margin-left: auto;
  }

  @media (max-width: 860px) {
    .site-footer__grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
      padding-block: var(--ts-s8);
    }
    .site-footer__brand {
      grid-column: 1 / -1;
    }
    .site-footer__lang {
      margin-left: 0;
      width: 100%;
    }
  }
</style>
