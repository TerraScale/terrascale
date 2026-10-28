<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict, locale } from '@/lib/i18n'
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

<section class="pricing-teaser" id="pricing">
  <div class="pricing-teaser__head">
    <div class="pricing-teaser__intro">
      <span class="ts-eyebrow pricing-teaser__eyebrow">
        <Icon name="dollarSign" className="pricing-teaser__eyebrow-icon" />
        {$dict.pricingTeaser.eyebrow}
      </span>
      <h2 class="pricing-teaser__title">{$dict.pricingTeaser.title}</h2>
      <p class="pricing-teaser__status">{$dict.pricing.status}</p>
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
        class={`pricing-teaser__plan${plan.popular ? ' is-popular' : ''}`}
        style={`--plan-accent: ${plan.accent}; --plan-accent-rgb: ${plan.accentRgb}`}
      >
        <h3 class="pricing-teaser__name">
          <span class="pricing-teaser__icon"><Icon name={plan.icon} className="pricing-teaser__icon-glyph" /></span>
          {plan.name}
          {#if plan.popular}
            <span class="pricing-teaser__badge">{$dict.pricing.badge}</span>
          {/if}
        </h3>
        <p class="pricing-teaser__price">
          <strong>{plan.billing.price}</strong>
          <span>{plan.billing.suffix}</span>
        </p>
        <p class="pricing-teaser__limits">{plan.cadences}</p>
        <p class="pricing-teaser__limits">{plan.summary}</p>
      </RouteLink>
    {/each}
  </div>
</section>

<style>
  .pricing-teaser {
    padding: var(--ts-space-20) clamp(16px, 2.5vw, 40px);
    border-top: 1px solid var(--ts-divider);
    background:
      radial-gradient(50% 60% at 50% 80%, rgba(var(--ts-primary-rgb), 0.12), transparent 70%),
      radial-gradient(35% 50% at 10% 90%, rgba(var(--ts-secondary-rgb), 0.07), transparent 70%);
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

  .pricing-teaser__status {
    margin: 0;
    font-size: 16px;
    line-height: 1.7;
    color: var(--ts-text-muted);
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
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: var(--ts-space-4);
  }

  :global(.pricing-teaser__plan) {
    display: flex;
    flex-direction: column;
    gap: var(--ts-space-4);
    padding: var(--ts-space-6);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background:
      radial-gradient(120% 70% at 0% 0%, rgba(var(--plan-accent-rgb), 0.14), transparent 60%),
      var(--ts-bg-surface);
    box-shadow: inset 0 2px 0 rgba(var(--plan-accent-rgb), 0.85);
    color: var(--ts-text);
    text-decoration: none;
    transition:
      transform var(--ts-transition-normal),
      border-color var(--ts-transition-normal);
  }

  :global(.pricing-teaser__plan:hover) {
    transform: translateY(-3px);
    border-color: rgba(var(--plan-accent-rgb), 0.45);
  }

  .pricing-teaser__icon {
    display: inline-grid;
    place-items: center;
    width: 28px;
    height: 28px;
    border-radius: var(--ts-radius-md);
    background: rgba(var(--plan-accent-rgb), 0.18);
    /* Lifted toward white so the glyph keeps >=3:1 on the surface. */
    color: color-mix(in oklab, var(--plan-accent) 65%, white);
  }

  :global(.pricing-teaser__icon-glyph) {
    width: 15px;
    height: 15px;
  }

  :global(.pricing-teaser__plan:focus-visible) {
    outline: 2px solid var(--ts-primary-light);
    outline-offset: 2px;
  }

  :global(.pricing-teaser__plan.is-popular) {
    border-color: transparent;
    background:
      radial-gradient(120% 70% at 50% 0%, rgba(var(--ts-primary-rgb), 0.28), transparent 65%) padding-box,
      linear-gradient(var(--ts-bg-surface), var(--ts-bg-surface)) padding-box,
      linear-gradient(140deg, var(--ts-primary-light), var(--ts-secondary) 45%, var(--ts-tertiary)) border-box;
    box-shadow:
      0 0 0 1px rgba(var(--ts-primary-rgb), 0.25),
      0 18px 50px -12px rgba(var(--ts-primary-rgb), 0.55);
  }

  .pricing-teaser__name {
    display: flex;
    align-items: center;
    gap: var(--ts-space-3);
    margin: 0;
    font-size: 18px;
    font-weight: 600;
  }

  .pricing-teaser__badge {
    display: inline-flex;
    align-items: center;
    height: 20px;
    padding: 0 8px;
    border-radius: var(--ts-radius-sm);
    background: linear-gradient(90deg, var(--ts-primary), color-mix(in oklab, var(--ts-primary) 55%, var(--ts-secondary)));
    color: #fff;
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
    font: 600 44px / 1 var(--ts-font-mono);
    letter-spacing: -0.04em;
    font-variant-numeric: tabular-nums;
  }

  :global(.pricing-teaser__plan.is-popular) .pricing-teaser__price strong {
    background: linear-gradient(120deg, #fff 20%, var(--ts-primary-light));
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
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

    .pricing-teaser__status {
      font-size: 15px;
    }
  }
</style>
