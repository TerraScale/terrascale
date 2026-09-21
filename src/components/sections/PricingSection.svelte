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
              <span class="pricing-card__badge-label">{$dict.pricing.badge}</span>
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
    padding: clamp(3.5rem, 6.5vw, 5.5rem) 0;
    position: relative;
  }

  .pricing-section__inner {
    position: relative;
    max-width: 1240px;
    margin: 0 auto;
    padding: 0 clamp(0.75rem, 3vw, 2.5rem);
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

  /* Product requirement: the three plan cards stay on a single row at every
     viewport width, so the card content scales down instead of the grid
     wrapping into multiple rows. */
  .pricing-grid {
    --pricing-pad: clamp(0.5rem, 2vw, 1.5rem);
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: clamp(0.375rem, 1.3vw, 1rem);
    margin-top: clamp(1.5rem, 3.4vw, 3rem);
  }

  .pricing-card {
    position: relative;
    min-width: 0;
    padding: var(--pricing-pad);
    border-radius: clamp(0.5rem, 1vw, 0.75rem);
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
    top: clamp(-0.8125rem, -1.1vw, -0.5rem);
    left: 50%;
    transform: translateX(-50%);
    background: var(--plan-accent);
    color: #fff;
    padding: clamp(0.1875rem, 0.42vw, 0.3125rem) clamp(0.4375rem, 1vw, 0.875rem);
    border-radius: 4px;
    font-family: var(--ts-font-accent);
    font-size: 0.625rem;
    font-weight: 800;
    text-transform: uppercase;
    letter-spacing: clamp(0.06em, 0.16vw, 0.14em);
    display: inline-flex;
    align-items: center;
    gap: clamp(0.1875rem, 0.5vw, 0.375rem);
    white-space: nowrap;
    box-shadow: 0 4px 16px rgba(var(--plan-accent-rgb), 0.4);
  }

  :global(.pricing-card__badge-icon) {
    width: clamp(0.5rem, 0.9vw, 0.6875rem);
    height: clamp(0.5rem, 0.9vw, 0.6875rem);
    color: #fff;
  }

  .pricing-card__header {
    display: flex;
    align-items: center;
    gap: clamp(0.375rem, 0.8vw, 0.625rem);
    margin-bottom: clamp(0.25rem, 0.6vw, 0.375rem);
  }

  .pricing-card__icon {
    width: clamp(1.25rem, 2.8vw, 2.125rem);
    height: clamp(1.25rem, 2.8vw, 2.125rem);
    border-radius: clamp(0.25rem, 0.5vw, 0.375rem);
    background: rgba(var(--plan-accent-rgb), 0.14);
    border: 1px solid rgba(var(--plan-accent-rgb), 0.3);
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
    color: var(--plan-accent);
  }

  :global(.pricing-card__icon-svg) {
    width: clamp(0.6875rem, 1.5vw, 1.125rem);
    height: clamp(0.6875rem, 1.5vw, 1.125rem);
    color: var(--plan-accent);
  }

  .pricing-card__name {
    font-size: clamp(0.75rem, 0.9vw + 0.55rem, 1.125rem);
    font-weight: 700;
    color: var(--ts-text);
    overflow-wrap: break-word;
  }

  .pricing-card__desc {
    font-family: var(--ts-font-accent);
    font-size: 0.75rem;
    color: var(--ts-text-muted);
    margin: 0 0 clamp(0.625rem, 1.6vw, 1.25rem);
    line-height: 1.5;
    overflow-wrap: break-word;
  }

  .pricing-card__price-row {
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: clamp(0.1875rem, 0.6vw, 0.5rem);
    margin-bottom: clamp(0.25rem, 0.7vw, 0.5rem);
  }

  .pricing-card__price {
    font-family: var(--ts-font-mono);
    font-size: clamp(0.8125rem, 2.4vw + 0.25rem, 2.125rem);
    font-weight: 700;
    line-height: 1;
    color: var(--plan-accent);
    letter-spacing: -0.02em;
  }

  .pricing-card__suffix {
    font-family: var(--ts-font-accent);
    font-size: clamp(0.75rem, 0.4vw + 0.6rem, 0.8125rem);
    color: var(--ts-text-muted);
    font-weight: 500;
    overflow-wrap: break-word;
  }

  .pricing-card__divider {
    height: 1px;
    width: 100%;
    margin: clamp(0.5rem, 1.5vw, 1.125rem) 0;
    background: linear-gradient(90deg, transparent, var(--ts-glass-border), transparent);
  }

  .pricing-card__features {
    list-style: none;
    padding: 0;
    margin: 0 0 clamp(0.625rem, 1.6vw, 1.25rem);
    flex: 1;
  }

  .pricing-card__features li {
    display: flex;
    align-items: center;
    gap: clamp(0.25rem, 0.7vw, 0.5rem);
    margin-bottom: clamp(0.3125rem, 0.85vw, 0.625rem);
    font-size: clamp(0.75rem, 0.3vw + 0.68rem, 0.8125rem);
    color: var(--ts-text);
  }

  .pricing-card__features li > span:last-child {
    min-width: 0;
    overflow-wrap: break-word;
  }

  .pricing-card__check {
    width: clamp(0.75rem, 1.5vw, 1.125rem);
    height: clamp(0.75rem, 1.5vw, 1.125rem);
    border-radius: 4px;
    background: rgba(var(--plan-accent-rgb), 0.16);
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
    color: var(--plan-accent);
  }

  :global(.pricing-card__check-icon) {
    width: clamp(0.5rem, 0.9vw, 0.6875rem);
    height: clamp(0.5rem, 0.9vw, 0.6875rem);
    color: var(--plan-accent);
  }

  :global(.pricing-card__cta) {
    width: 100%;
    padding: clamp(0.4375rem, 1vw, 0.75rem) clamp(0.25rem, 1.2vw, 1rem);
    background: rgba(255, 255, 255, 0.04);
    border: 1px solid var(--ts-glass-border);
    color: var(--ts-text);
    font-family: var(--ts-font-accent);
    font-size: clamp(0.75rem, 0.3vw + 0.68rem, 0.8125rem);
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: clamp(0.02em, 0.08vw, 0.08em);
    line-height: 1.35;
    text-align: center;
    border-radius: 6px;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: clamp(0.25rem, 0.7vw, 0.5rem);
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
    width: clamp(0.5625rem, 1vw, 0.8125rem);
    height: clamp(0.5625rem, 1vw, 0.8125rem);
    flex-shrink: 0;
  }

  /* Below ~35rem the badge pill is wider than its column, and shrinking its
     label below 0.625rem would make it unreadable. Keep the accent-colored star
     mark and leave the label for assistive technology. */
  @media (max-width: 560px) {
    .pricing-card__badge {
      top: -0.6875rem;
      padding: 0.25rem;
      border-radius: 999px;
    }

    .pricing-card__badge-label {
      position: absolute;
      width: 1px;
      height: 1px;
      margin: -1px;
      padding: 0;
      overflow: hidden;
      clip-path: inset(50%);
      white-space: nowrap;
    }
  }

  @media (max-width: 420px) {
    .pricing-card__header {
      flex-direction: column;
      align-items: flex-start;
      gap: 0.25rem;
    }
  }
</style>
