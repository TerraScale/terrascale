<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import SectionHeader from '@/components/sections/SectionHeader.svelte'
  import AnimatedPrice from '@/components/AnimatedPrice.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import {
    launchPhase,
    publicAlphaCatalog,
    registrationUrl,
    type PublicAlphaPlanSlug,
  } from '@/lib/site'

  export let headingLevel: 1 | 2 = 2

  type Plan = {
    name: string
    slug: PublicAlphaPlanSlug
    icon: AppIconName
    accent: string
    accentRgb: string
    price: number
    annualPrice: number
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
      accent: catalogPlan.accent,
      accentRgb: catalogPlan.accentRgb,
      price: catalogPlan.price,
      annualPrice: catalogPlan.annualPrice,
      popular: catalogPlan.popular,
      icon: catalogPlan.icon,
    }),
  )

  let billing: 'monthly' | 'annual' = 'monthly'

  $: suffix = billing === 'annual' ? $dict.pricing.suffixAnnual : $dict.pricing.suffixMonthly

  $: plans = planMeta.map((meta) => {
    const copy = $dict.pricing.plans[meta.slug]
    return {
      ...meta,
      description: copy.description,
      features: copy.features,
      cta: meta.slug === 'enterprise' || launchPhase === 'public-alpha' ? copy.cta : $dict.hero.ctaPrelaunch,
      href: meta.slug === 'enterprise' ? '/contact' : `${registrationUrl}?plan=${meta.slug}`,
    }
  })
</script>

<section class="pricing-section" id="pricing">
  <div class="pricing-section__inner">
    <SectionHeader
      eyebrow={$dict.pricing.eyebrow}
      subtitle={$dict.pricing.subtitle}
      {headingLevel}
    >
      <svelte:fragment slot="title">
        {$dict.pricing.titleLead}<br />
        <span class="ts-text-gradient">{$dict.pricing.titleAccent}</span>
      </svelte:fragment>
    </SectionHeader>

    <div class="pricing-toggle-wrap">
      <div class="pricing-toggle" role="tablist" aria-label={$dict.pricing.toggleLabel}>
        <div class="pricing-toggle__thumb" class:is-annual={billing === 'annual'} aria-hidden="true"></div>
        <button
          type="button"
          class="pricing-toggle__btn"
          class:is-active={billing === 'monthly'}
          role="tab"
          aria-selected={billing === 'monthly'}
          on:click={() => (billing = 'monthly')}
        >
          {$dict.pricing.monthly}
        </button>
        <button
          type="button"
          class="pricing-toggle__btn"
          class:is-active={billing === 'annual'}
          role="tab"
          aria-selected={billing === 'annual'}
          on:click={() => (billing = 'annual')}
        >
          {$dict.pricing.annual}
        </button>
        <span class="pricing-toggle__save" class:is-active={billing === 'annual'}>
          <Icon name="check" className="pricing-toggle__save-icon" />
          {$dict.pricing.save}
        </span>
      </div>
    </div>

    <div class="pricing-grid">
      {#each plans as plan (plan.name)}
        <div
          class="pricing-card"
          data-plan={plan.slug}
          data-plan-icon={plan.icon}
          data-popular={plan.popular}
          class:is-popular={plan.popular}
          style={`--plan-accent: ${plan.accent}; --plan-accent-rgb: ${plan.accentRgb};`}
        >
          {#if plan.popular}
            <div class="pricing-card__badge">
              <Icon name="star" className="pricing-card__badge-icon" />
              {$dict.pricing.badge}
            </div>
          {/if}

          <div class="pricing-card__header">
            <div class="pricing-card__icon">
              <Icon name={plan.icon} className="pricing-card__icon-svg" />
            </div>
            <div class="pricing-card__name">{plan.name}</div>
          </div>
          <p class="pricing-card__desc">{plan.description}</p>

          <div class="pricing-card__price-row">
            <div class="pricing-card__price">
              <AnimatedPrice value={billing === 'annual' ? plan.annualPrice : plan.price} />
            </div>
            <div class="pricing-card__suffix">{suffix}</div>
          </div>

          <div class="pricing-card__divider"></div>

          <ul class="pricing-card__features">
            {#each plan.features as feature (feature)}
              <li>
                <span class="pricing-card__check">
                  <Icon name="check" className="pricing-card__check-icon" />
                </span>
                <span>{feature}</span>
              </li>
            {/each}
          </ul>

          {#if plan.href.startsWith('https://')}
            <ExternalLink href={plan.href} class="pricing-card__cta">
              {plan.cta}
              <Icon name="arrowRight" className="pricing-card__cta-icon" />
            </ExternalLink>
          {:else}
            <RouteLink href={plan.href} class="pricing-card__cta">
              {plan.cta}
              <Icon name="arrowRight" className="pricing-card__cta-icon" />
            </RouteLink>
          {/if}
        </div>
      {/each}
    </div>
  </div>
</section>

<style>
  .pricing-section {
    padding: 88px 0;
    position: relative;
  }

  .pricing-section__inner {
    position: relative;
    max-width: 1240px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .pricing-toggle-wrap {
    display: flex;
    justify-content: center;
    margin: 40px 0 56px;
  }

  .pricing-toggle {
    position: relative;
    display: inline-flex;
    background: var(--ts-glass-bg);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    border: 1px solid var(--ts-glass-border);
    border-radius: 999px;
    padding: 5px;
  }

  .pricing-toggle__thumb {
    position: absolute;
    top: 5px;
    bottom: 5px;
    left: 5px;
    width: calc(50% - 5px);
    background: var(--ts-gradient-primary);
    border-radius: 999px;
    box-shadow: 0 4px 14px rgba(var(--ts-primary-rgb), 0.35);
    transition: transform 240ms cubic-bezier(0.4, 0, 0.2, 1);
  }

  .pricing-toggle__thumb.is-annual {
    transform: translateX(100%);
  }

  .pricing-toggle__btn {
    position: relative;
    z-index: 1;
    padding: 11px 30px;
    border: none;
    background: transparent;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-accent);
    font-size: 12px;
    font-weight: 700;
    cursor: pointer;
    text-transform: uppercase;
    letter-spacing: 0.1em;
    transition: color 180ms ease;
    border-radius: 999px;
  }

  .pricing-toggle__btn.is-active {
    color: #fff;
  }

  .pricing-toggle__save {
    position: absolute;
    top: -11px;
    right: -14px;
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 4px 9px;
    border-radius: 999px;
    background: linear-gradient(135deg, #10b981 0%, #059669 100%);
    color: #fff;
    font-family: var(--ts-font-accent);
    font-size: 10px;
    font-weight: 800;
    text-transform: uppercase;
    letter-spacing: 0.1em;
    box-shadow: 0 4px 12px rgba(16, 185, 129, 0.4);
    border: 1.5px solid var(--ts-bg-base);
    white-space: nowrap;
    transform: scale(1);
    transition: transform 200ms ease;
  }

  .pricing-toggle__save.is-active {
    transform: scale(1.05);
  }

  :global(.pricing-toggle__save-icon) {
    width: 9px;
    height: 9px;
    color: #fff;
  }

  .pricing-grid {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 16px;
    margin-top: 48px;
  }

  .pricing-card {
    position: relative;
    padding: 24px;
    border-radius: 12px;
    background: var(--ts-glass-bg);
    border: 1px solid var(--ts-glass-border);
    backdrop-filter: blur(14px);
    -webkit-backdrop-filter: blur(14px);
    transition:
      transform 250ms ease,
      box-shadow 250ms ease,
      border-color 250ms ease;
    display: flex;
    flex-direction: column;
  }

  .pricing-card:hover {
    transform: translateY(-4px);
    box-shadow: 0 20px 48px rgba(var(--plan-accent-rgb), 0.22);
  }

  .pricing-card.is-popular {
    background: linear-gradient(
      180deg,
      rgba(var(--ts-primary-rgb), 0.06) 0%,
      var(--ts-glass-bg) 50%
    );
    border: 1.5px solid rgba(var(--plan-accent-rgb), 0.45);
    box-shadow: 0 8px 30px rgba(var(--plan-accent-rgb), 0.12);
  }

  .pricing-card__badge {
    position: absolute;
    top: -13px;
    left: 50%;
    transform: translateX(-50%);
    background: var(--plan-accent);
    color: #fff;
    padding: 5px 14px;
    border-radius: 4px;
    font-family: var(--ts-font-accent);
    font-size: 10px;
    font-weight: 800;
    text-transform: uppercase;
    letter-spacing: 0.14em;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    box-shadow: 0 4px 16px rgba(var(--plan-accent-rgb), 0.4);
  }

  :global(.pricing-card__badge-icon) {
    width: 11px;
    height: 11px;
    color: #fff;
  }

  .pricing-card__header {
    display: flex;
    align-items: center;
    gap: 10px;
    margin-bottom: 6px;
  }

  .pricing-card__icon {
    width: 34px;
    height: 34px;
    border-radius: 6px;
    background: rgba(var(--plan-accent-rgb), 0.14);
    border: 1px solid rgba(var(--plan-accent-rgb), 0.3);
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--plan-accent);
  }

  :global(.pricing-card__icon-svg) {
    width: 18px;
    height: 18px;
    color: var(--plan-accent);
  }

  .pricing-card__name {
    font-size: 18px;
    font-weight: 700;
    color: var(--ts-text);
  }

  .pricing-card__desc {
    font-family: var(--ts-font-accent);
    font-size: 12px;
    color: var(--ts-text-muted);
    margin: 0 0 20px;
    line-height: 1.5;
  }

  .pricing-card__price-row {
    display: flex;
    align-items: baseline;
    gap: 8px;
    margin-bottom: 8px;
  }

  .pricing-card__price {
    font-family: var(--ts-font-mono);
    font-size: 34px;
    font-weight: 700;
    line-height: 1;
    color: var(--plan-accent);
    letter-spacing: -0.02em;
  }

  .pricing-card__suffix {
    font-family: var(--ts-font-accent);
    font-size: 13px;
    color: var(--ts-text-muted);
    font-weight: 500;
  }

  .pricing-card__divider {
    height: 1px;
    width: 100%;
    margin: 18px 0;
    background: linear-gradient(90deg, transparent, var(--ts-glass-border), transparent);
  }

  .pricing-card__features {
    list-style: none;
    padding: 0;
    margin: 0 0 20px;
    flex: 1;
  }

  .pricing-card__features li {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 10px;
    font-size: 13px;
    color: var(--ts-text);
  }

  .pricing-card__check {
    width: 18px;
    height: 18px;
    border-radius: 4px;
    background: rgba(var(--plan-accent-rgb), 0.16);
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
    color: var(--plan-accent);
  }

  :global(.pricing-card__check-icon) {
    width: 11px;
    height: 11px;
    color: var(--plan-accent);
  }

  :global(.pricing-card__cta) {
    width: 100%;
    padding: 12px 16px;
    background: rgba(255, 255, 255, 0.04);
    border: 1px solid var(--ts-glass-border);
    color: var(--ts-text);
    font-family: var(--ts-font-accent);
    font-size: 13px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    border-radius: 6px;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    text-decoration: none;
    transition:
      transform 180ms ease,
      background 180ms ease,
      box-shadow 180ms ease,
      border-color 180ms ease;
  }

  :global(.pricing-card__cta:hover) {
    transform: translateY(-1px);
    background: rgba(255, 255, 255, 0.07);
    border-color: rgba(var(--ts-primary-rgb), 0.35);
  }

  .pricing-card.is-popular :global(.pricing-card__cta) {
    background: var(--plan-accent);
    border-color: transparent;
    color: #fff;
    box-shadow: 0 6px 18px rgba(var(--plan-accent-rgb), 0.4);
  }

  .pricing-card.is-popular :global(.pricing-card__cta:hover) {
    box-shadow: 0 10px 26px rgba(var(--plan-accent-rgb), 0.55);
  }

  :global(.pricing-card__cta-icon) {
    width: 13px;
    height: 13px;
  }

  @media (max-width: 960px) {
    .pricing-section {
      padding: 64px 0;
    }
    .pricing-section__inner {
      padding: 0 20px;
    }
    .pricing-grid {
      grid-template-columns: 1fr;
    }
  }
</style>
