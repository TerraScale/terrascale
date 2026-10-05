<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'

  type ChannelHrefKind = 'external' | 'mailto' | 'internal'
  const channelMeta: Array<{ icon: AppIconName; label: string; href: string; kind: ChannelHrefKind }> = [
    { icon: 'mail', label: 'Email', href: 'mailto:support@terrascale.tech', kind: 'mailto' },
    { icon: 'brandDiscord', label: 'Discord', href: 'https://discord.gg/8Zr2Nw9g', kind: 'external' },
    { icon: 'brandGithub', label: 'GitHub', href: 'https://git.terrascale.tech/TerraScale', kind: 'external' },
  ]
  $: channels = channelMeta.map((meta, i) => ({
    ...meta,
    value: $dict.contact.channels[i].value,
  }))

</script>

<section class="contact-hero">
  <div class="contact-hero__inner">
    <div class="contact-hero__eyebrow">
      <Icon name="messageSquare" className="contact-hero__eyebrow-icon" />
      {$dict.contact.eyebrow}
    </div>
    <h1 class="contact-hero__title">
      {$dict.contact.titleLead}<br />
      <span class="ts-text-gradient">{$dict.contact.titleAccent}</span>
    </h1>
    <p class="contact-hero__subtitle">
      {$dict.contact.subtitle}
    </p>
  </div>
</section>

<section class="contact-body">
  <div class="contact-body__inner">
    <div class="contact-body__grid">
      <div class="contact-email ts-glass">
        <h2 class="contact-email__title">{$dict.contact.formTitle}</h2>
        <ExternalLink href={channelMeta[0].href} class="ts-btn ts-btn-primary">
          <Icon name="mail" />
          {$dict.contact.channels[0].value}
        </ExternalLink>
      </div>

      <div class="contact-channels">
        <h2 class="contact-channels__title">{$dict.contact.channelsTitle}</h2>
        <div class="contact-channels__list">
          {#each channels as c (c.label)}
            {#if c.kind === 'external'}
              <ExternalLink
                href={c.href}
                target="_blank"
                class="contact-channel ts-glass"
              >
                <div class="contact-channel__icon">
                  <Icon name={c.icon} className="contact-channel__icon-svg" />
                </div>
                <div class="contact-channel__body">
                  <div class="contact-channel__label">{c.label}</div>
                  <div class="contact-channel__value">{c.value}</div>
                </div>
                <Icon name="chevronRight" className="contact-channel__chev" />
              </ExternalLink>
            {:else}
              <ExternalLink href={c.href} class="contact-channel ts-glass">
                <div class="contact-channel__icon">
                  <Icon name={c.icon} className="contact-channel__icon-svg" />
                </div>
                <div class="contact-channel__body">
                  <div class="contact-channel__label">{c.label}</div>
                  <div class="contact-channel__value">{c.value}</div>
                </div>
                <Icon name="chevronRight" className="contact-channel__chev" />
              </ExternalLink>
            {/if}
          {/each}
        </div>

      </div>
    </div>
  </div>
</section>

<style>
  .contact-hero {
    position: relative;
    padding: 120px 0 60px;
    overflow: hidden;
  }

  .contact-hero__inner {
    position: relative;
    max-width: 840px;
    margin: 0 auto;
    padding: 0 40px;
    text-align: center;
  }

  .contact-hero__eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 5px 12px;
    border-radius: 4px;
    margin-bottom: 24px;
    background: rgba(var(--ts-primary-rgb), 0.1);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.25);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.14em;
  }

  :global(.contact-hero__eyebrow-icon) {
    width: 11px;
    height: 11px;
  }

  .contact-hero__title {
    font-size: clamp(2.2rem, 4vw + 1rem, 4rem);
    font-weight: 700;
    color: var(--ts-text);
    letter-spacing: -0.025em;
    line-height: 1.1;
    margin: 0 0 20px;
  }

  .contact-hero__subtitle {
    font-family: var(--ts-font-accent);
    font-size: 18px;
    color: var(--ts-text-muted);
    line-height: 1.65;
    max-width: 600px;
    margin: 0 auto;
  }

  .contact-body {
    padding: 40px 0 100px;
  }

  .contact-body__inner {
    max-width: 1120px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .contact-body__grid {
    display: grid;
    grid-template-columns: 1.3fr 1fr;
    gap: 40px;
  }

  .contact-email {
    padding: 36px;
    border-radius: 12px;
  }

  .contact-email__title {
    font-size: 22px;
    font-weight: 700;
    color: var(--ts-text);
    margin: 0 0 24px;
  }

  .contact-channels__title {
    font-size: 22px;
    font-weight: 700;
    color: var(--ts-text);
    margin: 0 0 20px;
  }

  .contact-channels__list {
    display: grid;
    gap: 10px;
  }

  :global(.contact-channel) {
    display: flex;
    align-items: center;
    gap: 14px;
    padding: 18px;
    border-radius: 8px;
    text-decoration: none;
    transition:
      transform 200ms ease,
      border-color 200ms ease,
      box-shadow 200ms ease;
  }

  :global(.contact-channel:hover) {
    transform: translateX(3px);
    border-color: rgba(var(--ts-primary-rgb), 0.4);
    box-shadow: 0 6px 20px rgba(var(--ts-primary-rgb), 0.15);
  }

  .contact-channel__icon {
    width: 40px;
    height: 40px;
    border-radius: 6px;
    flex-shrink: 0;
    background: rgba(var(--ts-primary-rgb), 0.14);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.3);
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--ts-primary-light);
  }

  :global(.contact-channel__icon-svg) {
    width: 18px;
    height: 18px;
    color: var(--ts-primary-light);
  }

  .contact-channel__body {
    flex: 1;
  }

  .contact-channel__label {
    font-family: var(--ts-font-accent);
    font-size: 10px;
    color: var(--ts-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.14em;
    font-weight: 700;
    margin-bottom: 3px;
  }

  .contact-channel__value {
    font-size: 14px;
    color: var(--ts-text);
    font-weight: 600;
  }

  :global(.contact-channel__chev) {
    width: 14px;
    height: 14px;
    color: var(--ts-text-muted);
    transition: transform 200ms ease;
  }

  :global(.contact-channel:hover .contact-channel__chev) {
    transform: translateX(2px);
    color: var(--ts-primary-light);
  }

  @media (max-width: 960px) {
    .contact-hero {
      padding: 80px 0 40px;
    }
    .contact-hero__inner,
    .contact-body__inner {
      padding: 0 20px;
    }
    .contact-body__grid {
      grid-template-columns: 1fr;
      gap: 32px;
    }
    .contact-email {
      padding: 24px;
    }
  }
</style>
