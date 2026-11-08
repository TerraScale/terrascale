<script lang="ts">
  import EditorialHeader from '@/components/sections/EditorialHeader.svelte'
  import Icon from '@/components/Icon.svelte'
  import PlanRecommendation from '@/components/PlanRecommendation.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict, locale } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'
  import { localeMeta } from '@/lib/i18n/locales'
  import { formatPlanBilling, formatPlanLimits } from '@/lib/pricing'
  import { publicAlphaCatalog } from '@/lib/site'

  $: plans = publicAlphaCatalog
    .filter((plan) => plan.available)
    .map((plan) => ({
      ...plan,
      billing: formatPlanBilling(plan, 'month', $dict.pricing, localeMeta($locale).bcp47),
      summary: formatPlanLimits({ ...plan.limits, members: null }, $dict.pricing.limits, localeMeta($locale).bcp47).join(' · '),
    }))
</script>

<section class="ed-pricing ts-section" id="pricing" use:reveal>
  <div class="ts-container">
    <div class="ed-pricing__head">
      <EditorialHeader index="04" lead={$dict.pricingTeaser.title} accent="" />
      <RouteLink href="/pricing" class="ts-btn ts-btn-glass ed-pricing__compare">
        {$dict.pricingTeaser.cta}
        <Icon name="arrowRight" className="ed-pricing__compare-icon" />
      </RouteLink>
    </div>

    <ul class="ed-pricing__list">
      {#each plans as plan (plan.slug)}
        <li>
          <RouteLink
            href={`/pricing#${plan.slug}`}
            class="ed-pricing__row"
            style={`--plan-accent: ${plan.accent}`}
          >
            <span class="ed-pricing__name">
              {plan.name}
              {#if plan.popular}<PlanRecommendation />{/if}
            </span>
            <span class="ed-pricing__limits">{plan.summary}</span>
            <span class="ed-pricing__price">
              <strong class="ts-num">{plan.billing.price}</strong>
              <span class="ed-pricing__suffix">{plan.billing.suffix}</span>
            </span>
            <Icon name="arrowRight" className="ed-pricing__row-icon" />
          </RouteLink>
        </li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .ed-pricing__head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 24px;
  }

  .ed-pricing__head :global(.ed-head) {
    flex: 1;
  }

  .ed-pricing__compare {
    margin-top: 28px;
    flex-shrink: 0;
  }

  :global(.ed-pricing__compare-icon) {
    width: 14px;
    height: 14px;
  }

  .ed-pricing__list {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  :global(.ed-pricing__row) {
    display: grid;
    grid-template-columns: minmax(120px, 1fr) minmax(0, 2fr) auto 20px;
    gap: clamp(16px, 3vw, 40px);
    align-items: center;
    padding-block: 24px;
    border-top: 1px solid var(--ts-divider);
    color: var(--ts-text);
    text-decoration: none;
    transition: background-color var(--ts-transition-fast);
  }

  :global(.ed-pricing__row:hover) {
    background: var(--ts-bg-surface);
  }

  .ed-pricing__name {
    display: inline-flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
    font-size: 19px;
    font-weight: 600;
    letter-spacing: -0.015em;
    color: var(--plan-accent);
  }

  .ed-pricing__limits {
    font-size: 14px;
    line-height: 1.6;
    color: var(--ts-text-muted);
    overflow-wrap: anywhere;
  }

  .ed-pricing__price {
    display: inline-flex;
    align-items: baseline;
    gap: 8px;
    white-space: nowrap;
  }

  .ed-pricing__price strong {
    font-family: var(--ts-font-mono);
    font-size: clamp(24px, 2.5vw, 34px);
    font-weight: 500;
    letter-spacing: -0.03em;
    color: var(--ts-text);
  }

  .ed-pricing__suffix {
    font-size: 13px;
    color: var(--ts-text-muted);
  }

  :global(.ed-pricing__row-icon) {
    width: 16px;
    height: 16px;
    color: var(--ts-text-muted);
    transition: transform var(--ts-transition-slow), color var(--ts-transition-fast);
  }

  :global(.ed-pricing__row:hover .ed-pricing__row-icon) {
    transform: translateX(4px);
    color: var(--ts-text);
  }

  @media (max-width: 900px) {
    :global(.ed-pricing__row) {
      grid-template-columns: minmax(0, 1fr) auto;
      grid-template-areas:
        'name icon'
        'limits limits'
        'price price';
      row-gap: 8px;
    }

    .ed-pricing__name { grid-area: name; }
    .ed-pricing__limits { grid-area: limits; }
    .ed-pricing__price { grid-area: price; }
    :global(.ed-pricing__row-icon) { grid-area: icon; }
  }

  @media (max-width: 640px) {
    .ed-pricing__head {
      flex-direction: column;
    }

    .ed-pricing__compare {
      margin-top: 0;
      width: 100%;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.ed-pricing__row-icon),
    :global(.ed-pricing__row:hover .ed-pricing__row-icon) {
      transform: none;
    }
  }
</style>
