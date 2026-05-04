<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { footerColumns, socialLinks } from '@/lib/site'

  const year = new Date().getFullYear()

  const isExternal = (href: string) => href.startsWith('http') || href.startsWith('mailto:')
</script>

<footer class="ts-footer">
  <div class="ts-footer__inner">
    <div class="ts-footer__grid">
      <div class="ts-footer__brand">
        <RouteLink href="/" class="ts-footer__logo" aria-label="TerraScale home">
          <img
            src="/logo-white.svg"
            alt="TerraScale"
            width="1429"
            height="345"
            decoding="async"
            loading="lazy"
          />
        </RouteLink>
        <p class="ts-footer__tagline">
          Empowering teams with secure, scalable database infrastructure. Scale your tech, effortlessly.
        </p>
        <div class="ts-footer__socials">
          {#each socialLinks as social (social.name)}
            <ExternalLink
              href={social.href}
              target="_blank"
              rel="noopener noreferrer"
              aria-label={social.name}
              class="ts-footer__social"
              style={`color: ${social.color}; background: ${social.bg}; border-color: ${social.border};`}
            >
              <Icon name={social.icon} className="ts-footer__social-icon" />
            </ExternalLink>
          {/each}
        </div>
      </div>

      {#each footerColumns as column (column.title)}
        <div class="ts-footer__column">
          <div class="ts-footer__heading">{column.title}</div>
          <ul class="ts-footer__list">
            {#each column.links as link (link.name)}
              <li>
                {#if isExternal(link.href)}
                  <ExternalLink
                    href={link.href}
                    target={link.href.startsWith('mailto:') ? undefined : '_blank'}
                    rel={link.href.startsWith('mailto:') ? undefined : 'noopener noreferrer'}
                    class="ts-footer__link"
                  >
                    <Icon name="chevronRight" className="ts-footer__chevron" />
                    {link.name}
                  </ExternalLink>
                {:else}
                  <RouteLink href={link.href} class="ts-footer__link">
                    <Icon name="chevronRight" className="ts-footer__chevron" />
                    {link.name}
                  </RouteLink>
                {/if}
              </li>
            {/each}
          </ul>
        </div>
      {/each}
    </div>

    <div class="ts-footer__meta">
      <div class="ts-footer__copy">© {year} TerraScale. All rights reserved.</div>
      <div class="ts-footer__stats">
        <span class="ts-footer__stat">
          <span class="pulse-dot ts-footer__stat-dot"></span>
          sfo · fra · gru
        </span>
        <span class="ts-footer__stat">
          <Icon name="shield" className="ts-footer__stat-icon" />
          OpenTelemetry-ready
        </span>
      </div>
    </div>
  </div>
</footer>

<style>
  .ts-footer {
    background: var(--ts-bg-surface);
    border-top: 1px solid var(--ts-divider);
    padding: 36px 0 20px;
  }

  .ts-footer__inner {
    max-width: 1240px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .ts-footer__grid {
    display: grid;
    grid-template-columns: 1.6fr repeat(4, 1fr);
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
    color: var(--ts-text-muted);
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
    border: 1px solid transparent;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    text-decoration: none;
    transition:
      transform 180ms ease,
      box-shadow 180ms ease,
      background 180ms ease;
  }

  :global(.ts-footer__social:hover) {
    transform: translateY(-2px);
    box-shadow: 0 6px 18px rgba(var(--ts-primary-rgb), 0.25);
  }

  :global(.ts-footer__social-icon) {
    width: 14px;
    height: 14px;
    display: block;
  }

  .ts-footer__heading {
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 700;
    color: var(--ts-text);
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
    color: var(--ts-text-muted);
    text-decoration: none;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    transition: color 150ms ease;
    font-family: var(--ts-font-accent);
  }

  :global(.ts-footer__link:hover) {
    color: var(--ts-text);
  }

  :global(.ts-footer__chevron) {
    width: 10px;
    height: 10px;
    color: var(--ts-text-muted);
    transition: transform 150ms ease;
  }

  :global(.ts-footer__link:hover .ts-footer__chevron) {
    transform: translateX(2px);
    color: var(--ts-primary-light);
  }

  .ts-footer__meta {
    border-top: 1px solid var(--ts-divider);
    padding-top: 16px;
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex-wrap: wrap;
    gap: 12px;
  }

  .ts-footer__copy {
    font-size: 12px;
    color: var(--ts-text-muted);
  }

  .ts-footer__stats {
    display: flex;
    gap: 20px;
    align-items: center;
  }

  .ts-footer__stat {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 11px;
    color: var(--ts-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.12em;
    font-weight: 600;
    font-family: var(--ts-font-accent);
  }

  .ts-footer__stat-dot {
    width: 6px;
    height: 6px;
    border-radius: 3px;
    background: var(--ts-secondary);
    box-shadow: 0 0 6px var(--ts-secondary);
    display: inline-block;
  }

  :global(.ts-footer__stat-icon) {
    width: 11px;
    height: 11px;
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
