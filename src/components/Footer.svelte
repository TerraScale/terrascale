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

<footer class="ts-footer">
  <div class="ts-footer__inner">
    <div class="ts-footer__grid">
      <div class="ts-footer__brand">
        <RouteLink href="/" class="ts-footer__logo" aria-label="TerraScale home">
          <img
            class="brand-logo brand-logo--dark"
            src="/logo-white.svg"
            alt="TerraScale"
            width="1429"
            height="345"
            decoding="async"
            loading="lazy"
          />
          <img
            class="brand-logo brand-logo--light"
            src="/logo-black.svg"
            alt="TerraScale"
            width="1429"
            height="345"
            decoding="async"
            loading="lazy"
          />
        </RouteLink>
        <p class="ts-footer__tagline">
          {$dict.footer.tagline}
        </p>
        <div class="ts-footer__socials">
          {#each socials as { social, name } (social.key)}
            <ExternalLink
              href={social.href}
              target="_blank"
              aria-label={name}
              class="ts-footer__social"
            >
              <Icon name={social.icon} className="ts-footer__social-icon" />
            </ExternalLink>
          {/each}
        </div>
      </div>

      {#each columns as { column, title, links } (column.key)}
        <div class="ts-footer__column">
          <div class="ts-footer__heading">{title}</div>
          <ul class="ts-footer__list">
            {#each links as { link, name } (link.key)}
              <li>
                {#if isExternal(link.href)}
                  <ExternalLink
                    href={link.href}
                    target={link.href.startsWith('mailto:') ? undefined : '_blank'}
                    class="ts-footer__link"
                  >
                    <Icon name="chevronRight" className="ts-footer__chevron" />
                    {name}
                  </ExternalLink>
                {:else}
                  <RouteLink href={link.href} class="ts-footer__link">
                    <Icon name="chevronRight" className="ts-footer__chevron" />
                    {name}
                  </RouteLink>
                {/if}
              </li>
            {/each}
          </ul>
        </div>
      {/each}
    </div>

    <div class="ts-footer__meta">
      <div class="ts-footer__copy">© {year} TerraScale. {$dict.footer.rightsReserved}</div>
      <LanguageSelector variant="inline" />
    </div>
  </div>
</footer>

<style>
  .ts-footer {
    background: var(--ts-surface);
    border-top: 1px solid var(--ts-line);
    padding: 36px 0 20px;
  }

  .ts-footer__inner {
    max-width: 1240px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .ts-footer__grid {
    display: grid;
    grid-template-columns: 1.6fr repeat(3, 1fr);
    gap: 32px;
    margin-bottom: 28px;
  }

  :global(.ts-footer__logo) {
    display: inline-flex;
    margin-bottom: 12px;
  }

  :global(.ts-footer__logo img) {
    height: 26px;
    width: auto;
    display: block;
  }

  .ts-footer__tagline {
    font-size: 13px;
    color: var(--ts-ink-2);
    line-height: 1.5;
    max-width: 320px;
    margin: 0 0 14px;
  }

  .ts-footer__socials {
    display: flex;
    gap: 8px;
  }

  :global(.ts-footer__social) {
    width: 32px;
    height: 32px;
    border-radius: 7px;
    border: 1px solid var(--ts-line);
    color: var(--ts-ink-2);
    display: inline-flex;
    align-items: center;
    justify-content: center;
    text-decoration: none;
    transition:
      color var(--ts-fast) ease,
      background var(--ts-fast) ease;
  }

  :global(.ts-footer__social:hover) {
    color: var(--ts-ink);
    background: var(--ts-surface-2);
  }

  .brand-logo--light,
  :global([data-theme='light']) .brand-logo--dark {
    display: none;
  }

  :global([data-theme='light']) .brand-logo--light {
    display: block;
  }

  :global(.ts-footer__social-icon) {
    width: 14px;
    height: 14px;
    display: block;
  }

  .ts-footer__heading {
    font-family: var(--ts-font-sans);
    font-size: 11px;
    font-weight: 700;
    color: var(--ts-ink);
    text-transform: uppercase;
    letter-spacing: 0.14em;
    margin-bottom: 10px;
  }

  .ts-footer__list {
    list-style: none;
    padding: 0;
    margin: 0;
  }

  .ts-footer__list li {
    margin-bottom: 6px;
  }

  :global(.ts-footer__link) {
    font-size: 12.5px;
    color: var(--ts-ink-2);
    text-decoration: none;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    transition: color 150ms ease;
    font-family: var(--ts-font-sans);
  }

  :global(.ts-footer__link:hover) {
    color: var(--ts-ink);
  }

  :global(.ts-footer__chevron) {
    width: 10px;
    height: 10px;
    color: var(--ts-ink-2);
    transition: transform 150ms ease;
  }

  :global(.ts-footer__link:hover .ts-footer__chevron) {
    transform: translateX(2px);
    color: var(--ts-accent);
  }

  .ts-footer__meta {
    border-top: 1px solid var(--ts-line);
    padding-top: 16px;
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex-wrap: wrap;
    gap: 12px;
  }

  .ts-footer__copy {
    font-size: 12px;
    color: var(--ts-ink-2);
  }

  @media (max-width: 960px) {
    .ts-footer__inner {
      padding: 0 20px;
    }
    .ts-footer__grid {
      grid-template-columns: 1fr 1fr;
    }
    .ts-footer__brand {
      grid-column: 1 / -1;
    }
  }

  @media (max-width: 560px) {
    .ts-footer__grid {
      grid-template-columns: 1fr;
    }
    .ts-footer__meta {
      flex-direction: column;
      align-items: flex-start;
    }
  }
</style>
