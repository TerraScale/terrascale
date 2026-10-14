<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict, locale } from '@/lib/i18n'
  import { localeMeta } from '@/lib/i18n/locales'
  import { formatPlanLimits } from '@/lib/pricing'
  import { publicAlphaCatalog } from '@/lib/site'

  $: plans = publicAlphaCatalog
    .filter((plan) => plan.available)
    .map((plan) => ({
      ...plan,
      summary: formatPlanLimits({ ...plan.limits, members: null }, $dict.pricing.limits, localeMeta($locale).bcp47).join(' · '),
    }))
</script>

<section class="pricing-teaser" id="pricing">
  <div class="pricing-teaser__head">
    <div class="pricing-teaser__intro">
      <span class="ts-eyebrow pricing-teaser__eyebrow">
        <Icon name="dollarSign" className="pricing-teaser__eyebrow-icon" />
        {$dict.pricingTeaser.eyebrow}
      </span>
      <h2 class="pricing-teaser__title">{$dict.pricingTeaser.title}</h2>
    </div>
    <RouteLink href="/pricing" class="ts-btn ts-btn-glass">
      {$dict.pricingTeaser.cta}
      <Icon name="arrowRight" className="pricing-teaser__cta-icon" />
    </RouteLink>
  </div>

  <div class="pricing-teaser__grid">
    {#each plans as plan (plan.slug)}
      <RouteLink href={`/pricing#${plan.slug}`} class={`pricing-teaser__plan${plan.popular ? ' is-popular' : ''}`}>
        <h3 class="pricing-teaser__name">
          {plan.name}
          {#if plan.popular}
            <span class="pricing-teaser__badge">{$dict.pricing.badge}</span>
          {/if}
        </h3>
        <p class="pricing-teaser__price">
          <strong>${plan.price}</strong>
          <span>{$dict.pricing.suffixMonthly}</span>
        </p>
        <p class="pricing-teaser__limits">{plan.summary}</p>
      </RouteLink>
    {/each}
  </div>
</section>

<style>
  .pricing-teaser {
    padding: var(--ts-space-20) clamp(16px, 2.5vw, 40px);
    border-top: 1px solid var(--ts-divider);
  }

  .pricing-teaser__head {
    display: flex;
    align-items: flex-end;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: var(--ts-space-6);
    margin-bottom: var(--ts-space-10);
  }

  .pricing-teaser__intro {
    display: grid;
    gap: var(--ts-space-3);
  }

  .pricing-teaser__eyebrow {
    display: inline-flex;
    align-items: center;
    gap: var(--ts-space-2);
  }

  :global(.pricing-teaser__eyebrow-icon),
  :global(.pricing-teaser__cta-icon) {
    width: 14px;
    height: 14px;
  }

  .pricing-teaser__title {
    margin: 0;
    font-size: 40px;
    font-weight: 600;
    line-height: 1.1;
    letter-spacing: -0.035em;
    color: var(--ts-text);
  }

  .pricing-teaser__grid {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: var(--ts-space-4);
  }

  :global(.pricing-teaser__plan) {
    display: flex;
    flex-direction: column;
    gap: var(--ts-space-4);
    padding: var(--ts-space-6);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
    color: var(--ts-text);
    text-decoration: none;
    transition:
      background-color var(--ts-transition-fast),
      border-color var(--ts-transition-fast);
  }

  :global(.pricing-teaser__plan:hover) {
    background: #222222;
    border-color: var(--ts-glass-border);
  }

  :global(.pricing-teaser__plan:focus-visible) {
    outline: 2px solid var(--ts-primary-light);
    outline-offset: 2px;
  }

  :global(.pricing-teaser__plan.is-popular) {
    border-color: color-mix(in oklab, var(--ts-primary) 70%, transparent);
  }

  .pricing-teaser__name {
    display: flex;
    align-items: center;
    gap: var(--ts-space-3);
    margin: 0;
    font-size: 18px;
    font-weight: 500;
  }

  .pricing-teaser__badge {
    display: inline-flex;
    align-items: center;
    height: 20px;
    padding: 0 8px;
    border-radius: var(--ts-radius-sm);
    background: rgba(var(--ts-primary-light-rgb), 0.14);
    color: var(--ts-primary-light);
    font-size: 11px;
    font-weight: 500;
  }

  .pricing-teaser__price {
    display: flex;
    align-items: baseline;
    gap: var(--ts-space-2);
    margin: 0;
  }

  .pricing-teaser__price strong {
    font: 500 40px / 1 var(--ts-font-mono);
    letter-spacing: -0.04em;
    font-variant-numeric: tabular-nums;
  }

  .pricing-teaser__price span,
  .pricing-teaser__limits {
    font-size: 14px;
    color: var(--ts-text-muted);
  }

  .pricing-teaser__limits {
    margin: 0;
    line-height: 1.7;
  }

  @media (max-width: 900px) {
    .pricing-teaser__grid {
      grid-template-columns: minmax(0, 1fr);
    }
  }

  @media (max-width: 640px) {
    .pricing-teaser {
      padding-block: var(--ts-space-12);
    }

    .pricing-teaser__head {
      margin-bottom: var(--ts-space-8);
    }

    .pricing-teaser__title {
      font-size: 24px;
    }
  }
</style>
