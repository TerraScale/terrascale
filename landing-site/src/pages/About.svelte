<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import { dict } from '@/lib/i18n'
  import { docsLink } from '@/lib/site'
</script>

<article class="ts-container about-page" aria-labelledby="about-title">
  <h1 id="about-title">{$dict.about.title}</h1>

  <div class="about-content">
    <p class="about-introduction">{$dict.about.introduction}</p>

    <section aria-labelledby="about-database-title">
      <h2 id="about-database-title">{$dict.about.databaseTitle}</h2>
      <p>{$dict.about.databaseText}</p>
      <p>{$dict.about.integrationText}</p>
    </section>

    <section aria-labelledby="about-alpha-title">
      <h2 id="about-alpha-title">{$dict.about.alphaTitle}</h2>
      <p>{$dict.about.alphaText}</p>
    </section>

    <ExternalLink href={docsLink.href} class="ts-btn ts-btn-primary ts-btn-lg about-action">
      {$dict.about.ctaDocs}
      <Icon name="arrowRight" className="about-action-icon" />
    </ExternalLink>
  </div>
</article>

<style>
  .about-page {
    display: grid;
    grid-template-columns: minmax(0, 0.9fr) minmax(0, 1.4fr);
    gap: var(--ts-s16);
    max-width: calc(var(--ts-content-max) + 2 * var(--ts-gutter));
    padding-block: var(--ts-s16);
  }

  h1 {
    align-self: start;
    max-width: 10ch;
    font-size: var(--ts-fs-5xl);
    font-weight: var(--ts-display-weight);
    letter-spacing: var(--ts-display-tracking);
    line-height: var(--ts-lh-tight);
    text-wrap: balance;
  }

  .about-content {
    display: grid;
    gap: var(--ts-s8);
    min-width: 0;
    max-width: 62ch;
  }

  p {
    color: var(--ts-ink-2);
    line-height: var(--ts-lh-prose);
  }

  .about-introduction {
    font-size: var(--ts-fs-xl);
    color: var(--ts-ink);
  }

  section {
    display: grid;
    gap: var(--ts-s4);
    padding-top: var(--ts-s6);
    border-top: 1px solid var(--ts-line);
  }

  h2 {
    font-size: var(--ts-fs-xl);
  }

  .about-content :global(.about-action) {
    justify-self: start;
    max-width: 100%;
    min-height: 44px;
    height: auto;
    padding-block: var(--ts-s3);
    white-space: normal;
    line-height: var(--ts-lh);
  }

  .about-content :global(.about-action-icon) {
    flex: none;
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  @media (max-width: 760px) {
    .about-page {
      grid-template-columns: minmax(0, 1fr);
      gap: var(--ts-s8);
      padding-block: var(--ts-s10);
    }

    h1 {
      max-width: none;
      font-size: var(--ts-fs-4xl);
    }
  }
</style>
