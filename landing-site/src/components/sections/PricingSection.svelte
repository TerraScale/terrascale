<script lang="ts">
  import PlanRecommendation from '@/components/PlanRecommendation.svelte'
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict, locale } from '@/lib/i18n'
  import { localeMeta } from '@/lib/i18n/locales'
  import { formatPlanBilling, formatPlanLimits, type BillingInterval } from '@/lib/pricing'
  import { publicAlphaCatalog, registrationUrl } from '@/lib/site'

  export let headingLevel: 1 | 2 = 2

  // Monthly is the default; yearly costs ten monthly payments and carries over to checkout.
  let interval: BillingInterval = 'month'
  const intervals: BillingInterval[] = ['month', 'year']

  $: headingTag = headingLevel === 1 ? 'h1' : 'h2'
  $: planTag = headingLevel === 1 ? 'h2' : 'h3'

  $: plans = publicAlphaCatalog.map((meta) => {
    const copy = $dict.pricing.plans[meta.slug]
    const limitLines = formatPlanLimits(meta.limits, $dict.pricing.limits, localeMeta($locale).bcp47)
    return {
      ...meta,
      billing: formatPlanBilling(meta, interval, $dict.pricing, localeMeta($locale).bcp47),
      features: 'features' in copy ? [...limitLines, ...copy.features] : limitLines,
      cta: copy.cta,
      href: meta.slug === 'enterprise' ? '/contact'
        : `${registrationUrl}?plan=${meta.slug}${interval === 'year' && meta.billingIntervals.includes('year') ? '&interval=annual' : ''}`,
    }
  })
</script>

<section class="pricing-section" id="pricing">
  <div class="pricing-section__head">
    <div class="pricing-section__intro">
      <svelte:element this={headingTag} class="pricing-section__title">
        {$dict.pricing.titleLead}
      </svelte:element>
    </div>
    <div class="ts-segmented pricing-section__interval" role="group" aria-label={$dict.pricing.intervals.label}>
      {#each intervals as option (option)}
        <button type="button" aria-pressed={interval === option} on:click={() => (interval = option)}>
          {$dict.pricing.intervals[option]}
        </button>
      {/each}
    </div>
  </div>

  <div class="pricing-grid">
    {#each plans as plan (plan.slug)}
      <article
        id={plan.slug}
        class="pricing-card"
        data-plan={plan.slug}
        data-popular={plan.popular}
        class:is-unavailable={!plan.available}
        style:--plan-accent={plan.accent}
      >
        <div class="pricing-card__head">
          <svelte:element this={planTag} class="pricing-card__name">
            <span class="pricing-card__icon"><Icon name={plan.icon} className="pricing-card__icon-glyph" /></span>
            {plan.name}
          </svelte:element>
          {#if plan.popular}<PlanRecommendation />{/if}
        </div>
        <p class="pricing-card__price">
          <strong>{plan.billing.price}</strong>
          <span>{plan.billing.suffix}</span>
        </p>

        <p class="pricing-card__desc">{plan.billing.cadence}</p>

        <ul class="pricing-card__features">
          {#each plan.features as feature (feature)}
            <li>
              <Icon name="check" className="pricing-card__check" />
              <span>{feature}</span>
            </li>
          {/each}
        </ul>

        {#if plan.href.startsWith('https://')}
          <ExternalLink
            href={plan.href}
            class="ts-btn ts-btn-glass pricing-card__cta"
          >
            {plan.cta}
          </ExternalLink>
        {:else}
          <RouteLink href={plan.href} class="ts-btn ts-btn-glass pricing-card__cta">
            {plan.cta}
          </RouteLink>
        {/if}
      </article>
    {/each}
  </div>
</section>

<style>
  /* The triad row below belongs to this section, so the plans keep a smaller gap to it. */
  .pricing-section {
    padding: var(--ts-space-16) clamp(16px, 2.5vw, 40px) var(--ts-space-20);
  }

  .pricing-section__head {
    display: flex;
    align-items: flex-end;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: var(--ts-space-6);
  }

  .pricing-section__intro {
    display: grid;
    gap: var(--ts-space-3);
    max-width: 720px;
  }

  .pricing-section__title {
    margin: 0;
    font-size: 40px;
    font-weight: 600;
    line-height: 1.1;
    letter-spacing: -0.035em;
    color: var(--ts-text);
  }

  .pricing-section__interval {
    height: 36px;
  }

  .pricing-grid {
    display: grid;
    grid-template-columns: repeat(5, minmax(0, 1fr));
    gap: var(--ts-space-4);
    margin-top: var(--ts-space-10);
  }

  .pricing-card {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: var(--ts-space-4);
    min-width: 0;
    padding: var(--ts-space-6);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
    box-shadow: inset 0 2px 0 var(--plan-accent);
    transition:
      transform var(--ts-transition-normal),
      border-color var(--ts-transition-normal),
      box-shadow var(--ts-transition-normal);
  }

  .pricing-card:hover {
    transform: translateY(-3px);
    border-color: color-mix(in srgb, var(--plan-accent) 45%, transparent);
  }

  .pricing-card__head {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: var(--ts-space-2) var(--ts-space-3);
    min-height: 54px;
  }

  .pricing-card__icon {
    display: inline-grid;
    place-items: center;
    width: 28px;
    height: 28px;
    border-radius: var(--ts-radius-md);
    background: color-mix(in srgb, var(--plan-accent) 12%, transparent);
    color: var(--plan-accent);
  }

  :global(.pricing-card__icon-glyph) {
    width: 15px;
    height: 15px;
  }

  .pricing-card__name {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: var(--ts-space-2) var(--ts-space-3);
    margin: 0;
    font-size: 17px;
    font-weight: 600;
    color: var(--plan-accent);
  }

  .pricing-card__desc {
    margin: calc(-1 * var(--ts-space-2)) 0 0;
    font-size: 13px;
    line-height: 1.7;
    color: var(--ts-text-muted);
  }

  .pricing-card__price {
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: var(--ts-space-2);
    margin: 0;
  }

  .pricing-card__price strong {
    font: 600 44px / 1 var(--ts-font-mono);
    letter-spacing: -0.04em;
    font-variant-numeric: tabular-nums;
    color: var(--ts-text);
  }

  .pricing-card__price span {
    font-size: 13px;
    color: var(--ts-text-muted);
  }

  .pricing-card__features {
    display: grid;
    gap: var(--ts-space-3);
    margin: 0 0 var(--ts-space-2);
    padding: var(--ts-space-4) 0 0;
    border-top: 1px solid var(--ts-divider);
    list-style: none;
    font-size: 13px;
    color: var(--ts-text-muted);
  }

  .pricing-card__features li {
    display: flex;
    align-items: flex-start;
    gap: var(--ts-space-2);
  }

  .pricing-card__features li > span {
    min-width: 0;
    overflow-wrap: break-word;
  }

  :global(.pricing-card__check) {
    width: 14px;
    height: 14px;
    margin-top: 2px;
    color: var(--plan-accent);
  }

  :global(.pricing-card__cta) {
    width: 100%;
    margin-top: auto;
  }

  .pricing-card.is-unavailable :global(.pricing-card__cta) {
    background: transparent;
  }

  @media (prefers-reduced-motion: reduce) {
    .pricing-card {
      transition: none;
    }

    .pricing-card:hover {
      transform: none;
    }
  }

  @media (max-width: 1280px) {
    .pricing-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
  }

  @media (max-width: 640px) {
    .pricing-section {
      padding-block: var(--ts-space-12);
    }

    .pricing-section__title {
      font-size: 24px;
    }

    .pricing-grid {
      grid-template-columns: minmax(0, 1fr);
    }
  }
</style>
