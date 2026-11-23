<script lang="ts">
  import PlanRecommendation from '@/components/PlanRecommendation.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict, locale } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'
  import { localeMeta } from '@/lib/i18n/locales'
  import { formatPlanBilling, formatPlanCadences, formatPlanLimits } from '@/lib/pricing'
  import { publicAlphaCatalog } from '@/lib/site'

  $: plans = publicAlphaCatalog
    .filter((plan) => plan.available)
    .map((plan) => ({
      ...plan,
      billing: formatPlanBilling(plan, 'month', $dict.pricing, localeMeta($locale).bcp47),
      cadences: formatPlanCadences(plan, $dict.pricing, localeMeta($locale).bcp47),
      summary: formatPlanLimits({ ...plan.limits, members: null }, $dict.pricing.limits, localeMeta($locale).bcp47).join(' · '),
    }))
</script>

<section class="pricing-teaser" id="pricing" use:reveal>
  <div class="ts-container">
    <div class="pricing-teaser__head">
      <div class="pricing-teaser__intro">
        <h2 class="pricing-teaser__title">{$dict.pricingTeaser.title}</h2>
      </div>
      <RouteLink href="/pricing" class="ts-btn ts-btn-glass">
        {$dict.pricingTeaser.cta}
        <Icon name="arrowRight" className="pricing-teaser__cta-icon" />
      </RouteLink>
    </div>

    <div class="pricing-teaser__grid">
      {#each plans as plan (plan.slug)}
        <RouteLink
          href={`/pricing#${plan.slug}`}
          class="pricing-teaser__plan"
          style={`--plan-accent: ${plan.accent}`}
        >
          <div class="pricing-teaser__plan-head">
            <h3 class="pricing-teaser__name">
              <span class="pricing-teaser__icon"><Icon name={plan.icon} className="pricing-teaser__icon-glyph" /></span>
              {plan.name}
            </h3>
            {#if plan.popular}<PlanRecommendation />{/if}
          </div>
          <p class="pricing-teaser__price">
            <strong>{plan.billing.price}</strong>
            <span>{plan.billing.suffix}</span>
          </p>
          <p class="pricing-teaser__limits">{plan.cadences}</p>
          <p class="pricing-teaser__limits">{plan.summary}</p>
        </RouteLink>
      {/each}
    </div>
  </div>
</section>

<style>
  .pricing-teaser {
    padding: var(--ts-space-20) 0;
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
    max-width: 760px;
  }

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
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: var(--ts-space-4);
  }

  :global(.pricing-teaser__plan) {
    display: flex;
    flex-direction: column;
    min-width: 0;
    overflow-wrap: anywhere;
    gap: var(--ts-space-4);
    box-shadow: inset 0 2px 0 var(--plan-accent);
    padding: var(--ts-space-6);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
    color: var(--ts-text);
    text-decoration: none;
    transition:
      transform 300ms cubic-bezier(0.22, 1, 0.36, 1),
      border-color 300ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  :global(.pricing-teaser__plan:hover) {
    transform: translateY(-3px);
    border-color: color-mix(in srgb, var(--plan-accent) 45%, transparent);
  }

  .pricing-teaser__icon {
    display: inline-grid;
    place-items: center;
    width: 28px;
    height: 28px;
    border-radius: var(--ts-radius-md);
    background: color-mix(in srgb, var(--plan-accent) 12%, transparent);
    color: var(--plan-accent);
  }

  :global(.pricing-teaser__icon-glyph) {
    width: 15px;
    height: 15px;
  }

  :global(.pricing-teaser__plan:focus-visible) {
    outline: 2px solid var(--ts-primary-light);
    outline-offset: 2px;
  }

  .pricing-teaser__plan-head {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: var(--ts-space-2) var(--ts-space-3);
    min-height: 54px;
  }

  .pricing-teaser__name {
    color: var(--plan-accent);
    display: flex;
    align-items: center;
    gap: var(--ts-space-3);
    margin: 0;
    font-size: 18px;
    font-weight: 600;
  }

  .pricing-teaser__price {
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: var(--ts-space-2);
    margin: 0;
  }

  .pricing-teaser__price strong {
    font: 600 44px / 1 var(--ts-font-mono);
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

  @media (prefers-reduced-motion: reduce) {
    :global(.pricing-teaser__plan) {
      transition: none;
    }

    :global(.pricing-teaser__plan:hover) {
      transform: none;
    }
  }

  @media (max-width: 1100px) {
    .pricing-teaser__grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
  }

  @media (max-width: 600px) {
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
