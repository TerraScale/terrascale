<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { docsLink } from '@/lib/site'

  const email = 'support@terrascale.tech'
  const topicIcons: AppIconName[] = ['messageCircle', 'dollarSign', 'users']
  const resources: { key: 'documentation' | 'status' | 'api'; icon: AppIconName; href: string }[] = [
    { key: 'documentation', icon: 'bookOpen', href: docsLink.href },
    { key: 'status', icon: 'activity', href: 'https://status.terrascale.tech' },
    { key: 'api', icon: 'code2', href: 'https://api.terrascale.tech/scalar/' },
  ]
</script>

<div class="ts-container contact-page">
  <section class="contact-overview" aria-labelledby="contact-title">
    <div class="contact-intro">
      <p class="contact-eyebrow">{$dict.contact.eyebrow}</p>
      <h1 id="contact-title">
        {$dict.contact.titleLead}
        <span>{$dict.contact.titleAccent}</span>
      </h1>
      <p class="contact-description">{$dict.contact.subtitle}</p>
      <ExternalLink href={`mailto:${email}`} class="ts-btn ts-btn-primary contact-email-button">
        {$dict.contact.emailCta}
        <Icon name="externalLink" className="contact-email-icon" />
      </ExternalLink>
      <ExternalLink href={`mailto:${email}`} class="contact-email-address">{email}</ExternalLink>
      <p class="contact-description">Report a security issue to <ExternalLink href="mailto:security@terrascale.tech">security@terrascale.tech</ExternalLink>.</p>
    </div>

    <div class="contact-details">
      <section class="contact-topics" aria-labelledby="contact-topics-title">
        <h2 id="contact-topics-title">{$dict.contact.topicsTitle}</h2>
        <ul class="contact-topic-list">
          {#each $dict.contact.topics as topic, index (topic.title)}
            <li class="contact-topic">
              <Icon name={topicIcons[index]} className="contact-topic-icon" />
              <div>
                <h3>{topic.title}</h3>
                <p>{topic.text}</p>
              </div>
            </li>
          {/each}
        </ul>
      </section>
      <ExternalLink href="https://github.com/TerraScale" target="_blank" class="contact-github">
        <div>
          <h2 class="contact-github-title">{$dict.contact.github.title}</h2>
          <p class="contact-github-description">{$dict.contact.github.text}</p>
        </div>
        <Icon name="externalLink" className="contact-link-arrow" />
      </ExternalLink>
    </div>
  </section>

  <section class="contact-resources" aria-labelledby="contact-resources-title">
    <h2 id="contact-resources-title">{$dict.contact.resourcesTitle}</h2>
    <p class="contact-resources-description">{$dict.contact.resourcesSubtitle}</p>
    <ul class="contact-resource-list">
      {#each resources as resource (resource.key)}
        <li>
          <ExternalLink href={resource.href} target="_blank" class="contact-resource">
            <span class="contact-resource-icons">
              <Icon name={resource.icon} className="contact-resource-icon" />
              <Icon name="externalLink" className="contact-link-arrow" />
            </span>
            <h3>{$dict.contact.resources[resource.key].title}</h3>
            <p class="contact-resource-description">{$dict.contact.resources[resource.key].text}</p>
          </ExternalLink>
        </li>
      {/each}
    </ul>
    <p class="contact-note">{$dict.contact.note}</p>
  </section>
</div>

<style>
  .contact-page {
    max-width: calc(var(--ts-content-max) + var(--ts-s20));
    padding-block: var(--ts-s16) var(--ts-s12);
  }

  .contact-overview {
    display: grid;
    grid-template-columns: minmax(0, 1.08fr) minmax(0, 1fr);
    gap: var(--ts-s16);
    align-items: start;
    padding-bottom: var(--ts-s12);
  }

  .contact-intro,
  .contact-details {
    min-width: 0;
  }

  .contact-eyebrow {
    font-family: var(--ts-font-mono);
    font-size: var(--ts-fs-xs);
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--ts-ink-3);
  }

  h1 {
    margin-top: var(--ts-s6);
    font-size: calc(var(--ts-fs-5xl) + var(--ts-s2));
    font-weight: var(--ts-display-weight);
    letter-spacing: var(--ts-display-tracking);
    line-height: 1.09;
  }

  h1 span {
    display: block;
    color: var(--ts-heading-muted);
  }

  .contact-description {
    margin-top: var(--ts-s6);
    max-width: 48ch;
    font-size: var(--ts-fs-lg);
    line-height: var(--ts-lh-prose);
    color: var(--ts-ink-2);
  }

  .contact-intro :global(.contact-email-button) {
    margin-top: var(--ts-s8);
    min-height: calc(var(--ts-control-h-lg) + var(--ts-s4));
    height: auto;
    min-width: calc(var(--ts-s20) * 3);
    max-width: 100%;
    padding: var(--ts-s4) var(--ts-s6);
    justify-content: space-between;
    gap: var(--ts-s10);
    font-size: var(--ts-fs-lg);
    font-weight: 600;
  }

  .contact-intro :global(.contact-email-icon) {
    width: var(--ts-icon-lg);
    height: var(--ts-icon-lg);
  }

  .contact-intro :global(.contact-email-address) {
    display: block;
    width: fit-content;
    max-width: 100%;
    margin-top: var(--ts-s4);
    font-size: var(--ts-fs-xl);
    font-weight: 500;
    color: var(--ts-ink);
    overflow-wrap: anywhere;
  }

  .contact-topics {
    padding: var(--ts-s8);
    border: 1px solid var(--ts-line);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-surface);
  }

  h2 {
    font-size: var(--ts-fs-xl);
  }

  .contact-topic-list {
    list-style: none;
    margin: var(--ts-s6) 0 0;
    padding: 0;
  }

  .contact-topic {
    display: grid;
    grid-template-columns: var(--ts-icon-lg) minmax(0, 1fr);
    gap: var(--ts-s4);
    padding-block: var(--ts-s5);
    border-top: 1px solid var(--ts-line);
  }

  .contact-topic:last-child {
    padding-bottom: 0;
  }

  .contact-topic :global(.contact-topic-icon) {
    width: var(--ts-icon-lg);
    height: var(--ts-icon-lg);
    color: var(--ts-ink-2);
  }

  h3 {
    font-size: var(--ts-fs-lg);
  }

  .contact-topic p {
    margin-top: var(--ts-s2);
    font-size: var(--ts-fs-md);
    line-height: var(--ts-lh-prose);
    color: var(--ts-ink-2);
  }

  .contact-details :global(.contact-github) {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--ts-s5);
    margin-top: var(--ts-s4);
    padding: var(--ts-s6) var(--ts-s8);
    border: 1px solid var(--ts-line);
    border-radius: var(--ts-radius-lg);
  }

  .contact-github-title {
    font-size: var(--ts-fs-lg);
  }

  .contact-github-description {
    margin-top: var(--ts-s1);
    font-size: var(--ts-fs-sm);
    line-height: var(--ts-lh-prose);
    color: var(--ts-ink-2);
  }

  .contact-page :global(.contact-link-arrow) {
    width: var(--ts-icon);
    height: var(--ts-icon);
    color: var(--ts-ink-3);
  }

  .contact-resources {
    padding-top: var(--ts-s8);
    border-top: 1px solid var(--ts-line);
  }

  .contact-resources-description {
    margin-top: var(--ts-s2);
    font-size: var(--ts-fs-md);
    color: var(--ts-ink-2);
  }

  .contact-resource-list {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: var(--ts-s5);
    list-style: none;
    margin: var(--ts-s6) 0 0;
    padding: 0;
  }

  .contact-resource-list :global(.contact-resource) {
    display: block;
    height: 100%;
    padding: var(--ts-s6);
    border: 1px solid var(--ts-line);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-surface);
  }

  .contact-resource-icons {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--ts-s4);
    margin-bottom: var(--ts-s6);
  }

  .contact-resource-icons :global(.contact-resource-icon) {
    width: var(--ts-icon-lg);
    height: var(--ts-icon-lg);
    color: var(--ts-ink-2);
  }

  .contact-resource-description {
    max-width: 36ch;
    margin-top: var(--ts-s2);
    font-size: var(--ts-fs-md);
    line-height: var(--ts-lh-prose);
    color: var(--ts-ink-2);
  }

  .contact-page :global(.contact-github),
  .contact-page :global(.contact-resource) {
    transition: border-color var(--ts-fast) var(--ts-ease);
  }

  .contact-page :global(.contact-github:hover),
  .contact-page :global(.contact-resource:hover) {
    border-color: var(--ts-line-strong);
    text-decoration: none;
  }

  .contact-note {
    margin-top: var(--ts-s6);
    font-size: var(--ts-fs-xs);
    line-height: var(--ts-lh-prose);
    color: var(--ts-ink-3);
  }

  @media (max-width: 1100px) {
    .contact-overview {
      gap: var(--ts-s10);
    }

    h1 {
      font-size: var(--ts-fs-5xl);
    }

    .contact-topics,
    .contact-details :global(.contact-github) {
      padding: var(--ts-s6);
    }
  }

  @media (max-width: 760px) {
    .contact-page {
      padding-block: var(--ts-s12);
    }

    .contact-overview {
      grid-template-columns: minmax(0, 1fr);
      gap: var(--ts-s10);
      padding-bottom: var(--ts-s10);
    }

    h1 {
      max-width: 16ch;
      font-size: var(--ts-fs-4xl);
    }

    .contact-resource-list {
      grid-template-columns: minmax(0, 1fr);
      gap: var(--ts-s4);
    }

    .contact-resource-description {
      max-width: none;
    }
  }
</style>
