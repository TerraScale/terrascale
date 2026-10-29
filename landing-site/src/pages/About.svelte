<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { launchRegions } from '@/lib/site'

  type Pillar = {
    icon: AppIconName
    title: string
    text: string
  }

  const pillarIcons: AppIconName[] = ['database', 'shield', 'dollarSign']
  $: pillars = $dict.about.pillars.map((pillar, i) => ({
    icon: pillarIcons[i] ?? 'database',
    ...pillar,
  }))
  const regions = launchRegions.map((region) => ({ id: region.id, name: region.name }))
</script>

<section class="ts-section about-hero">
  <div class="ts-container about-hero__inner">
    <span class="ts-eyebrow"><span class="ts-dot" aria-hidden="true"></span>{$dict.about.eyebrow}</span>
    <h1 class="about-hero__title">
      {$dict.about.titleLead}
      <span class="ts-h-muted">{$dict.about.titleAccent}</span>
    </h1>
    <p class="ts-lede about-hero__lede">{$dict.about.subtitle}</p>
    <div class="ts-actions">
      <RouteLink href="/pricing" class="ts-btn ts-btn-primary ts-btn-lg">
        {$dict.about.ctaPricing}
        <Icon name="arrowRight" className="about__icon" />
      </RouteLink>
      <RouteLink href="/contact" class="ts-btn ts-btn-secondary ts-btn-lg">
        <Icon name="messageSquare" className="about__icon" />
        {$dict.about.ctaContact}
      </RouteLink>
    </div>
  </div>
</section>

<section class="ts-section about-section">
  <div class="ts-container about-section__inner">
    <div class="about-section__intro">
      <span class="ts-eyebrow"><span class="ts-dot" aria-hidden="true"></span>{$dict.about.missionEyebrow}</span>
      <h2 class="about-section__title">{$dict.about.missionTitle}</h2>
      <p class="about-section__text">{$dict.about.missionText}</p>
    </div>

    <div class="about-pillars">
      {#each pillars as pillar (pillar.title)}
        <article class="about-pillar">
          <span class="ts-icon-tile"><Icon name={pillar.icon} /></span>
          <div>
            <h3 class="about-pillar__title">{pillar.title}</h3>
            <p class="about-pillar__text">{pillar.text}</p>
          </div>
        </article>
      {/each}
    </div>
  </div>
</section>

<section class="ts-section about-band">
  <div class="ts-container about-section__inner">
    <div class="about-section__intro">
      <span class="ts-eyebrow"><span class="ts-dot" aria-hidden="true"></span>{$dict.about.pricingPrincipleEyebrow}</span>
      <h2 class="about-section__title">{$dict.about.bandTitle}</h2>
      <p class="about-section__text">{$dict.about.bandText}</p>
    </div>
    <ul class="about-regions" aria-label={$dict.about.eyebrow}>
      {#each regions as region (region.id)}
        <li class="about-region">
          <span class="ts-badge ts-badge-outline about-region__code">{region.id}</span>
          {region.name}
        </li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .about-hero__inner {
    display: grid;
    gap: var(--ts-s5);
    justify-items: start;
  }

  .about-hero__title {
    max-width: 820px;
    font-size: var(--ts-fs-5xl);
    font-weight: var(--ts-display-weight);
    letter-spacing: var(--ts-display-tracking);
    line-height: 1.04;
  }

  .about-hero__lede {
    margin-top: 0;
  }

  .about-hero :global(.about__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .about-section__inner {
    display: grid;
    grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
    gap: var(--ts-s12);
    align-items: start;
  }

  .about-section__intro {
    display: grid;
    gap: var(--ts-s4);
    justify-items: start;
  }

  .about-section__title {
    font-size: var(--ts-fs-3xl);
  }

  .about-section__text {
    color: var(--ts-ink-2);
    line-height: var(--ts-lh-prose);
  }

  .about-pillar {
    display: flex;
    gap: var(--ts-s4);
    padding: var(--ts-s5) 0;
    border-top: 1px solid var(--ts-line);
  }

  .about-pillar:last-child {
    border-bottom: 1px solid var(--ts-line);
  }

  /* Title centred on the 32px icon tile; the text hangs under it. */
  .about-pillar__title {
    display: flex;
    align-items: center;
    min-height: 32px;
    font-size: var(--ts-fs-lg);
  }

  .about-pillar__text {
    margin-top: var(--ts-s1);
    color: var(--ts-ink-2);
  }

  .about-regions {
    list-style: none;
    margin: 0;
    padding: 0;
    border-top: 1px solid var(--ts-line);
  }

  .about-region {
    display: flex;
    align-items: center;
    gap: var(--ts-s3);
    height: 48px;
    border-bottom: 1px solid var(--ts-line);
    color: var(--ts-ink);
  }

  .about-region__code {
    font-family: var(--ts-font-mono);
  }

  @media (max-width: 860px) {
    .about-hero__title {
      font-size: var(--ts-fs-4xl);
    }
    .about-section__inner {
      grid-template-columns: minmax(0, 1fr);
      gap: var(--ts-s8);
    }
  }
</style>
