<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'

  type ChannelHrefKind = 'external' | 'mailto' | 'internal'
  const channelMeta: Array<{ icon: AppIconName; label: string; href: string; kind: ChannelHrefKind }> = [
    { icon: 'mail', label: 'Email', href: 'mailto:support@terrascale.tech', kind: 'mailto' },
    { icon: 'brandGithub', label: 'GitHub', href: 'https://github.com/TerraScale', kind: 'external' },
  ]
  $: channels = channelMeta.map((meta, i) => ({
    ...meta,
    value: $dict.contact.channels[i].value,
  }))

</script>

<section class="ts-section contact-hero">
  <div class="ts-container contact-hero__inner">
    <span class="ts-eyebrow"><span class="ts-dot" aria-hidden="true"></span>{$dict.contact.eyebrow}</span>
    <h1 class="contact-hero__title">
      {$dict.contact.titleLead}
      <span class="ts-h-muted">{$dict.contact.titleAccent}</span>
    </h1>
    <p class="ts-lede contact-hero__lede">{$dict.contact.subtitle}</p>
    <ExternalLink href={channelMeta[0].href} class="ts-btn ts-btn-primary ts-btn-lg">
      <Icon name="mail" className="contact__icon" />
      {$dict.contact.channels[0].value}
    </ExternalLink>
  </div>
</section>

<section class="contact-body">
  <div class="contact-body__inner">
    <div class="contact-body__grid">
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
  .contact-hero__inner {
    display: grid;
    gap: var(--ts-s5);
    justify-items: start;
  }

  .contact-hero__title {
    max-width: 820px;
    font-size: var(--ts-fs-5xl);
    font-weight: var(--ts-display-weight);
    letter-spacing: var(--ts-display-tracking);
    line-height: 1.04;
  }

  .contact-hero__lede {
    margin-top: 0;
  }

  .contact-hero :global(.contact__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .contact-body__inner {
    max-width: 1120px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .contact-body__grid {
    max-width: 560px;
    margin: 0 auto;
  }

  .contact-channels li {
    border-bottom: 1px solid var(--ts-line);
  }

  .contact-channels :global(.contact-channel) {
    display: flex;
    align-items: center;
    gap: var(--ts-s3);
    min-height: 56px;
    padding: 0 var(--ts-s2);
    color: var(--ts-ink);
    transition: background-color var(--ts-fast) var(--ts-ease);
  }

  .contact-channels :global(.contact-channel:hover) {
    background: var(--ts-surface);
    text-decoration: none;
  }

  .contact-channel__label {
    font-weight: 500;
    min-width: 88px;
  }

  .contact-channel__value {
    color: var(--ts-ink-2);
    font-size: var(--ts-fs-md);
    overflow-wrap: anywhere;
  }

  .contact-channels :global(.contact-channel__chev) {
    width: var(--ts-icon);
    height: var(--ts-icon);
    margin-left: auto;
    color: var(--ts-ink-3);
  }

  @media (max-width: 860px) {
    .contact-hero__title {
      font-size: var(--ts-fs-4xl);
    }
    .contact-body__inner {
      padding: 0 20px;
    }
  }
</style>
