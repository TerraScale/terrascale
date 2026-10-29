<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict, locale } from '@/lib/i18n'
  import { localeMeta } from '@/lib/i18n/locales'
  import { annualSavingMonths, formatPlanBilling, formatPlanLimits, type BillingInterval } from '@/lib/pricing'
  import { publicAlphaCatalog, registrationUrl } from '@/lib/site'

  export let headingLevel: 1 | 2 = 2

  // Monthly is the default; yearly costs ten monthly payments and carries over to checkout.
  let interval: BillingInterval = 'month'
  const intervals: BillingInterval[] = ['month', 'year']
  const savingMonths = annualSavingMonths(publicAlphaCatalog)

  $: headingTag = headingLevel === 1 ? 'h1' : 'h2'
  $: planTag = headingLevel === 1 ? 'h2' : 'h3'

  $: plans = publicAlphaCatalog.map((meta) => {
    const copy = $dict.pricing.plans[meta.slug]
    const limitLines = formatPlanLimits(meta.limits, $dict.pricing.limits, localeMeta($locale).bcp47)
    return {
      ...meta,
      billing: formatPlanBilling(meta, interval, $dict.pricing, localeMeta($locale).bcp47),
      description: copy.description,
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
      <span class="ts-eyebrow">{$dict.pricing.eyebrow}</span>
      <svelte:element this={headingTag} class="pricing-section__title">
        {$dict.pricing.titleLead} {$dict.pricing.titleAccent}
      </svelte:element>
      <p class="pricing-section__subtitle">{$dict.pricing.subtitle}</p>
      <p class="pricing-section__subtitle">{$dict.pricing.status}</p>
    </div>
    <div class="ts-segmented pricing-section__interval" role="group" aria-label={$dict.pricing.intervals.label}>
      {#each intervals as option (option)}
        <button type="button" aria-pressed={interval === option} on:click={() => (interval = option)}>
          {$dict.pricing.intervals[option]}
          {#if option === 'year' && savingMonths}
            <span class="pricing-section__saving">{$dict.pricing.intervals.saving.replace('{months}', String(savingMonths))}</span>
          {/if}
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
        class:is-popular={plan.popular}
        class:is-unavailable={!plan.available}
        style:--plan-accent={plan.accent}
        style:--plan-accent-rgb={plan.accentRgb}
      >
        <svelte:element this={planTag} class="pricing-card__name">
          <span class="pricing-card__icon"><Icon name={plan.icon} className="pricing-card__icon-glyph" /></span>
          {plan.name}
          {#if plan.popular}
            <span class="pricing-card__badge">{$dict.pricing.badge}</span>
          {:else if !plan.available}
            <span class="pricing-card__badge pricing-card__badge--neutral">{$dict.pricing.plans.enterprise.cta}</span>
          {/if}
        </svelte:element>
        <p class="pricing-card__desc">{plan.description}</p>

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
            class={`ts-btn ${plan.popular ? 'ts-btn-primary' : 'ts-btn-glass'} pricing-card__cta`}
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
    background:
      radial-gradient(60% 50% at 70% 75%, rgba(var(--ts-primary-rgb), 0.12), transparent 70%),
      radial-gradient(40% 40% at 15% 85%, rgba(var(--ts-secondary-rgb), 0.07), transparent 70%),
      radial-gradient(35% 40% at 95% 60%, rgba(var(--ts-tertiary-rgb), 0.07), transparent 70%);
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

  .pricing-section__saving {
    color: var(--ts-success);
    font-size: 12px;
  }

  .pricing-section__subtitle {
    margin: 0;
    font-size: 16px;
    line-height: 1.7;
    color: var(--ts-text-muted);
  }

  .pricing-grid {
    display: grid;
    grid-template-columns: repeat(5, minmax(0, 1fr));
    gap: var(--ts-space-4);
    margin-top: var(--ts-space-10);
  }

  .pricing-card {
    /* Icons and checks need >=3:1 on the surface; lift the tier accent toward white. */
    --plan-ink: color-mix(in oklab, var(--plan-accent) 65%, white);
    position: relative;
    display: flex;
    flex-direction: column;
    gap: var(--ts-space-4);
    min-width: 0;
    padding: var(--ts-space-6);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background:
      radial-gradient(120% 60% at 0% 0%, rgba(var(--plan-accent-rgb), 0.14), transparent 60%),
      var(--ts-bg-surface);
    box-shadow: inset 0 2px 0 rgba(var(--plan-accent-rgb), 0.85);
    transition:
      transform var(--ts-transition-normal),
      border-color var(--ts-transition-normal),
      box-shadow var(--ts-transition-normal);
  }

  .pricing-card:hover {
    transform: translateY(-3px);
    border-color: rgba(var(--plan-accent-rgb), 0.45);
  }

  .pricing-card.is-popular {
    border-color: transparent;
    background:
      radial-gradient(120% 60% at 50% 0%, rgba(var(--ts-primary-rgb), 0.28), transparent 65%) padding-box,
      linear-gradient(var(--ts-bg-surface), var(--ts-bg-surface)) padding-box,
      linear-gradient(140deg, var(--ts-primary-light), var(--ts-secondary) 45%, var(--ts-tertiary)) border-box;
    box-shadow:
      0 0 0 1px rgba(var(--ts-primary-rgb), 0.25),
      0 18px 50px -12px rgba(var(--ts-primary-rgb), 0.55);
  }

  .pricing-card__icon {
    display: inline-grid;
    place-items: center;
    width: 28px;
    height: 28px;
    border-radius: var(--ts-radius-md);
    background: rgba(var(--plan-accent-rgb), 0.18);
    color: var(--plan-ink);
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
    color: var(--ts-text);
  }

  .pricing-card__badge {
    display: inline-flex;
    align-items: center;
    height: 22px;
    padding: 0 var(--ts-space-2);
    border-radius: var(--ts-radius-sm);
    background: linear-gradient(90deg, var(--ts-primary), color-mix(in oklab, var(--ts-primary) 55%, var(--ts-secondary)));
    color: #fff;
    font-size: 12px;
    font-weight: 500;
    line-height: 1.5;
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

  .is-popular .pricing-card__price strong {
    background: linear-gradient(120deg, #fff 20%, var(--ts-primary-light));
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
  }

  .pricing-card__badge--neutral {
    color: var(--ts-text-muted);
    background: rgba(255, 255, 255, 0.04);
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
    color: var(--plan-ink);
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
