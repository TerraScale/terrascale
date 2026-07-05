<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import type { AppIconName } from '@/lib/icons'

  type Pillar = {
    icon: AppIconName
    title: string
    text: string
  }

  const pillars: Pillar[] = [
    {
      icon: 'database',
      title: 'TerraBase foundation',
      text: 'TerraScale is built on TerraBase so teams can run durable document, query, and operational data workflows behind one public platform surface.',
    },
    {
      icon: 'shield',
      title: 'Auth included',
      text: 'Database access, users, sessions, invitations, and machine credentials are planned as one product boundary instead of separate glue code.',
    },
    {
      icon: 'dollarSign',
      title: 'Usage-aware billing',
      text: 'Plans stay predictable while metered usage lets teams grow by workload, not by negotiating region-by-region pricing.',
    },
  ]

  const regions = ['San Francisco', 'Frankfurt', 'Guarulhos']
</script>

<section class="about-hero">
  <div class="about-hero__grid" aria-hidden="true"></div>
  <div class="about-hero__inner">
    <div class="about-hero__eyebrow">
      <Icon name="globe" className="about-hero__eyebrow-icon" />
      About TerraScale
    </div>
    <h1 class="about-hero__title">
      Globally distributed<br />
      <span class="ts-text-gradient">database infrastructure.</span>
    </h1>
    <p class="about-hero__subtitle">
      TerraScale exists to make globally distributed DBaaS feel like one coherent product:
      database, auth, and payments built together on TerraBase with usage-based billing and flat
      pricing across regions.
    </p>
    <div class="about-hero__actions">
      <RouteLink href="/pricing" class="ts-btn ts-btn-primary cta-shine">
        View Pricing
        <Icon name="arrowRight" className="about-hero__action-icon" />
      </RouteLink>
      <RouteLink href="/contact" class="ts-btn ts-btn-glass">
        Talk to Us
        <Icon name="messageSquare" className="about-hero__action-icon" />
      </RouteLink>
    </div>
  </div>
</section>

<section class="about-section">
  <div class="about-section__inner">
    <div class="about-section__intro">
      <div class="about-section__eyebrow">Mission</div>
      <h2 class="about-section__title">One platform surface for teams that ship across regions.</h2>
      <p class="about-section__text">
        Teams should not have to stitch together a database, identity layer, billing system, and
        regional deployment plan before they can serve customers. TerraScale brings those concerns
        into a single developer-facing platform so product teams can start small, pick the regions
        that matter, and scale without changing their application model.
      </p>
    </div>

    <div class="about-pillars">
      {#each pillars as pillar (pillar.title)}
        <article class="about-pillar ts-glass">
          <div class="about-pillar__icon">
            <Icon name={pillar.icon} className="about-pillar__icon-svg" />
          </div>
          <h3 class="about-pillar__title">{pillar.title}</h3>
          <p class="about-pillar__text">{pillar.text}</p>
        </article>
      {/each}
    </div>
  </div>
</section>

<section class="about-band">
  <div class="about-band__inner ts-glass">
    <div>
      <div class="about-section__eyebrow">Pricing principle</div>
      <h2 class="about-band__title">Flat regional pricing, metered by real use.</h2>
      <p class="about-band__text">
        Region choice should be an architecture decision, not a surprise line item. TerraScale's
        public pricing is designed around the same plan prices across launch regions, with storage,
        operations, and transfer growing with workload.
      </p>
    </div>
    <div class="about-regions" aria-label="Launch regions">
      {#each regions as region (region)}
        <span class="about-region">
          <span class="about-region__dot"></span>
          {region}
        </span>
      {/each}
    </div>
  </div>
</section>

<style>
  .about-hero {
    position: relative;
    overflow: hidden;
    padding: 112px 0 88px;
  }

  .about-hero__grid {
    position: absolute;
    inset: 0;
    pointer-events: none;
    opacity: 0.55;
    background-image:
      linear-gradient(var(--ts-grid-color) 1px, transparent 1px),
      linear-gradient(90deg, var(--ts-grid-color) 1px, transparent 1px);
    background-size: 32px 32px;
  }

  .about-hero__inner,
  .about-section__inner,
  .about-band__inner {
    position: relative;
    max-width: 1120px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .about-hero__inner {
    text-align: center;
  }

  .about-hero__eyebrow,
  .about-section__eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 5px 12px;
    border-radius: 4px;
    background: rgba(var(--ts-primary-rgb), 0.1);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.25);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.14em;
  }

  :global(.about-hero__eyebrow-icon) {
    width: 12px;
    height: 12px;
  }

  .about-hero__title {
    font-size: clamp(2.8rem, 6vw + 0.5rem, 5.6rem);
    font-weight: 700;
    color: var(--ts-text);
    line-height: 0.98;
    letter-spacing: -0.035em;
    margin: 24px 0 24px;
  }

  .about-hero__subtitle,
  .about-section__text,
  .about-band__text {
    font-family: var(--ts-font-accent);
    color: var(--ts-text-muted);
    line-height: 1.7;
  }

  .about-hero__subtitle {
    max-width: 820px;
    margin: 0 auto 34px;
    font-size: 18px;
  }

  .about-hero__actions {
    display: flex;
    justify-content: center;
    gap: 12px;
    flex-wrap: wrap;
  }

  :global(.about-hero__action-icon) {
    width: 14px;
    height: 14px;
  }

  .about-section {
    padding: 72px 0;
  }

  .about-section__inner {
    display: grid;
    grid-template-columns: 0.82fr 1.18fr;
    gap: 42px;
    align-items: start;
  }

  .about-section__title,
  .about-band__title {
    color: var(--ts-text);
    font-weight: 700;
    letter-spacing: -0.02em;
    line-height: 1.12;
    margin: 18px 0 16px;
  }

  .about-section__title {
    font-size: clamp(1.9rem, 2.2vw + 0.5rem, 3rem);
  }

  .about-section__text,
  .about-band__text {
    font-size: 16px;
    margin: 0;
  }

  .about-pillars {
    display: grid;
    gap: 16px;
  }

  .about-pillar {
    border-radius: 12px;
    padding: 24px;
    display: grid;
    grid-template-columns: auto 1fr;
    column-gap: 16px;
    align-items: start;
  }

  .about-pillar__icon {
    width: 42px;
    height: 42px;
    border-radius: 8px;
    background: rgba(var(--ts-primary-rgb), 0.14);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.3);
    color: var(--ts-primary-light);
    display: flex;
    align-items: center;
    justify-content: center;
  }

  :global(.about-pillar__icon-svg) {
    width: 20px;
    height: 20px;
  }

  .about-pillar__title {
    margin: 0 0 8px;
    color: var(--ts-text);
    font-size: 19px;
    font-weight: 700;
  }

  .about-pillar__text {
    grid-column: 2;
    margin: 0;
    color: var(--ts-text-muted);
    font-size: 14.5px;
    line-height: 1.65;
  }

  .about-band {
    padding: 16px 0 96px;
  }

  .about-band__inner {
    border-radius: 16px;
    padding: 36px 40px;
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 36px;
    align-items: center;
    background:
      radial-gradient(ellipse 70% 120% at 0% 0%, rgba(var(--ts-primary-rgb), 0.14), transparent 65%),
      var(--ts-glass-bg);
  }

  .about-band__title {
    font-size: clamp(1.7rem, 2vw + 0.5rem, 2.5rem);
  }

  .about-regions {
    display: grid;
    gap: 10px;
    min-width: 210px;
  }

  .about-region {
    display: inline-flex;
    align-items: center;
    gap: 9px;
    padding: 9px 12px;
    border-radius: 6px;
    border: 1px solid rgba(var(--ts-primary-rgb), 0.22);
    background: rgba(var(--ts-primary-rgb), 0.08);
    color: var(--ts-text);
    font-family: var(--ts-font-accent);
    font-size: 13px;
    font-weight: 700;
  }

  .about-region__dot {
    width: 7px;
    height: 7px;
    border-radius: 4px;
    background: var(--ts-secondary);
    box-shadow: 0 0 10px rgba(var(--ts-secondary-rgb), 0.7);
  }

  @media (max-width: 860px) {
    .about-section__inner,
    .about-band__inner {
      grid-template-columns: 1fr;
    }
  }

  @media (max-width: 640px) {
    .about-hero {
      padding: 72px 0 56px;
    }
    .about-hero__inner,
    .about-section__inner,
    .about-band__inner {
      padding-left: 20px;
      padding-right: 20px;
    }
    .about-section {
      padding: 54px 0;
    }
    .about-pillar {
      grid-template-columns: 1fr;
    }
    .about-pillar__text {
      grid-column: 1;
    }
  }
</style>
