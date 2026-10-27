<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'

  type ChannelHrefKind = 'external' | 'mailto' | 'internal'
  const channelMeta: Array<{ icon: AppIconName; label: string; href: string; kind: ChannelHrefKind }> = [
    { icon: 'mail', label: 'Email', href: 'mailto:support@terrascale.tech', kind: 'mailto' },
    { icon: 'brandDiscord', label: 'Discord', href: 'https://discord.gg/8Zr2Nw9g', kind: 'external' },
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

<section class="ts-section contact-body">
  <div class="ts-container contact-body__inner">
    <h2 class="contact-body__title">{$dict.contact.channelsTitle}</h2>
    <ul class="contact-channels">
      {#each channels as c (c.label)}
        <li>
          <ExternalLink href={c.href} target={c.kind === 'external' ? '_blank' : undefined} class="contact-channel">
            <span class="ts-icon-tile"><Icon name={c.icon} /></span>
            <span class="contact-channel__body">
              <span class="contact-channel__label">{c.label}</span>
              <span class="contact-channel__value">{c.value}</span>
            </span>
            <Icon name="chevronRight" className="contact-channel__chev" />
          </ExternalLink>
        </li>
      {/each}
    </ul>
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
    display: grid;
    grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
    gap: var(--ts-s12);
    align-items: start;
  }

  .contact-body__title {
    font-size: var(--ts-fs-3xl);
  }

  .contact-channels {
    list-style: none;
    margin: 0;
    padding: 0;
    border-top: 1px solid var(--ts-line);
  }

  .contact-channels li {
    border-bottom: 1px solid var(--ts-line);
  }

  .contact-channels :global(.contact-channel) {
    display: flex;
    align-items: center;
    gap: var(--ts-s4);
    min-height: 72px;
    padding: var(--ts-s3) var(--ts-s3);
    color: var(--ts-ink);
    transition: background-color var(--ts-fast) var(--ts-ease);
  }

  .contact-channels :global(.contact-channel:hover) {
    background: var(--ts-surface);
    text-decoration: none;
  }

  /* Label and value side by side on wide screens, stacked when the row gets narrow. */
  .contact-channel__body {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: var(--ts-s1) var(--ts-s4);
    min-width: 0;
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
    transition: transform var(--ts-fast) var(--ts-ease);
  }

  .contact-channels :global(.contact-channel:hover .contact-channel__chev) {
    transform: translateX(2px);
    color: var(--ts-ink);
  }

  @media (max-width: 860px) {
    .contact-hero__title {
      font-size: var(--ts-fs-4xl);
    }
    .contact-body__inner {
      grid-template-columns: minmax(0, 1fr);
      gap: var(--ts-s6);
    }
  }

  @media (max-width: 640px) {
    .contact-channel__body {
      flex-direction: column;
      align-items: flex-start;
    }
  }
</style>
