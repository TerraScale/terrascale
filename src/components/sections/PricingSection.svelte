<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import SectionHeader from '@/components/sections/SectionHeader.svelte'
  import AnimatedPrice from '@/components/AnimatedPrice.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict, locale } from '@/lib/i18n'
  import { localeMeta } from '@/lib/i18n/locales'
  import { formatPlanLimits } from '@/lib/pricing'
  import {
    publicAlphaCatalog,
    registrationUrl,
    type PlanLimits,
    type PublicAlphaPlanSlug,
  } from '@/lib/site'

  export let headingLevel: 1 | 2 = 2

  type Plan = {
    name: string
    slug: PublicAlphaPlanSlug
    icon: AppIconName
    price: number
    annualPrice: number
    limits: PlanLimits
    description: string
    features: string[]
    cta: string
    href: string
    popular: boolean
  }

  const planMeta: Array<Omit<Plan, 'description' | 'features' | 'cta' | 'href'>> = publicAlphaCatalog.map(
    (catalogPlan) => ({
      name: catalogPlan.name,
      slug: catalogPlan.slug,
      price: catalogPlan.price,
      annualPrice: catalogPlan.annualPrice,
      limits: catalogPlan.limits,
      popular: catalogPlan.popular,
      icon: catalogPlan.icon,
    }),
  )

  let billing: 'monthly' | 'annual' = 'monthly'

  $: suffix = billing === 'annual' ? $dict.pricing.suffixAnnual : $dict.pricing.suffixMonthly

  $: plans = planMeta.map((meta) => {
    const copy = $dict.pricing.plans[meta.slug]
    const limitLines = formatPlanLimits(meta.limits, $dict.pricing.limits, localeMeta($locale).bcp47)
    return {
      ...meta,
      description: copy.description,
      features: 'features' in copy ? [...limitLines, ...copy.features] : limitLines,
      cta: copy.cta,
      href: meta.slug === 'enterprise' ? '/contact' : `${registrationUrl}?plan=${meta.slug}`,
    }
  })
</script>

<section class="ts-section pricing-section" id="pricing">
  <div class="ts-container">
    <SectionHeader
      eyebrow={$dict.pricing.eyebrow}
      subtitle={$dict.pricing.subtitle}
      {headingLevel}
    >
      <svelte:fragment slot="title">
        {$dict.pricing.titleLead}
        <span class="ts-h-muted">{$dict.pricing.titleAccent}</span>
      </svelte:fragment>
    </SectionHeader>

    <div class="pricing-toggle">
      <div class="ts-segmented" role="group" aria-label={$dict.pricing.toggleLabel}>
        <button type="button" aria-pressed={billing === 'monthly'} on:click={() => (billing = 'monthly')}>
          {$dict.pricing.monthly}
        </button>
        <button type="button" aria-pressed={billing === 'annual'} on:click={() => (billing = 'annual')}>
          {$dict.pricing.annual}
        </button>
      </div>
      <span class="ts-badge" data-state="operational">{$dict.pricing.save}</span>
    </div>

    <div class="pricing-grid">
      {#each plans as plan (plan.name)}
        <div class="ts-panel pricing-card" data-plan={plan.slug} data-popular={plan.popular}>
          <div class="pricing-card__header">
            <Icon name={plan.icon} className="pricing-card__icon" />
            <h3 class="pricing-card__name">{plan.name}</h3>
            {#if plan.popular}
              <span class="ts-badge ts-badge-accent pricing-card__badge">{$dict.pricing.badge}</span>
            {/if}
          </div>
          <p class="pricing-card__desc">{plan.description}</p>

          <div class="pricing-card__price-row">
            <div class="pricing-card__price ts-num">
              <AnimatedPrice value={billing === 'annual' ? plan.annualPrice : plan.price} />
            </div>
            <div class="pricing-card__suffix">{suffix}</div>
          </div>

          <ul class="pricing-card__features">
            {#each plan.features as feature (feature)}
              <li>
                <Icon name="check" className="pricing-card__check" />
                <span>{feature}</span>
              </li>
            {/each}
          </ul>

          {#if plan.href.startsWith('https://')}
            <ExternalLink href={plan.href} class={`ts-btn ts-btn-block ${plan.popular ? 'ts-btn-primary' : 'ts-btn-secondary'}`}>
              {plan.cta}
            </ExternalLink>
          {:else}
            <RouteLink href={plan.href} class={`ts-btn ts-btn-block ${plan.popular ? 'ts-btn-primary' : 'ts-btn-secondary'}`}>
              {plan.cta}
            </RouteLink>
          {/if}
        </div>
      {/each}
    </div>
  </div>
</section>

<style>
  .pricing-section {
    padding-bottom: var(--ts-s10);
  }

  .pricing-toggle {
    display: flex;
    align-items: center;
    gap: var(--ts-s3);
    margin: var(--ts-s10) 0 var(--ts-s8);
  }

  .pricing-grid {
    display: grid;
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: var(--ts-s4);
  }

  .pricing-card {
    display: flex;
    flex-direction: column;
    gap: var(--ts-s4);
    padding: var(--ts-s6);
    min-width: 0;
  }

  .pricing-card__header {
    display: flex;
    align-items: center;
    gap: var(--ts-s2);
  }

  .pricing-card :global(.pricing-card__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
    color: var(--ts-ink-2);
  }

  .pricing-card__name {
    font-size: var(--ts-fs-lg);
  }

  .pricing-card__badge {
    margin-left: auto;
  }

  .pricing-card__desc {
    font-size: var(--ts-fs-sm);
    color: var(--ts-ink-2);
    min-height: 2.6em;
  }

  .pricing-card__price-row {
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: var(--ts-s1);
    padding-bottom: var(--ts-s4);
    border-bottom: 1px solid var(--ts-line);
  }

  .pricing-card__price {
    font-family: var(--ts-font-num);
    font-size: var(--ts-fs-3xl);
    font-weight: var(--ts-display-weight);
    line-height: 1;
    letter-spacing: -0.02em;
    color: var(--ts-ink);
  }

  .pricing-card__suffix {
    font-size: var(--ts-fs-sm);
    color: var(--ts-ink-3);
  }

  .pricing-card__features {
    list-style: none;
    margin: 0;
    padding: 0;
    flex: 1;
    display: grid;
    align-content: start;
    gap: var(--ts-s2);
    font-size: var(--ts-fs-sm);
    color: var(--ts-ink);
  }

  .pricing-card__features li {
    display: flex;
    align-items: flex-start;
    gap: var(--ts-s2);
  }

  .pricing-card__features :global(.pricing-card__check) {
    width: var(--ts-icon);
    height: var(--ts-icon);
    margin-top: 1px;
    color: var(--ts-accent);
  }

  @media (max-width: 1100px) {
    .pricing-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
  }

  @media (max-width: 640px) {
    .pricing-grid {
      grid-template-columns: minmax(0, 1fr);
    }
  }
</style>
