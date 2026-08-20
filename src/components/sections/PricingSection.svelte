<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import SectionHeader from '@/components/sections/SectionHeader.svelte'
  import AnimatedPrice from '@/components/AnimatedPrice.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'

  type Plan = {
    name: string
    icon: AppIconName
    accent: string
    accentRgb: string
    price: number
    description: string
    features: string[]
    cta: string
    href: string
    popular: boolean
  }

  // Prices, names, and registration choices match the Public Alpha catalog.
  const planMeta: Array<Omit<Plan, 'description' | 'features' | 'cta' | 'href'>> = [
    {
      name: 'Developer',
      icon: 'zap',
      accent: 'var(--ts-secondary)',
      accentRgb: 'var(--ts-secondary-rgb)',
      price: 0,
      popular: false,
    },
    {
      name: 'Starter',
      icon: 'star',
      accent: 'var(--ts-secondary)',
      accentRgb: 'var(--ts-secondary-rgb)',
      price: 5,
      popular: false,
    },
    {
      name: 'Pro',
      icon: 'trendingUp',
      accent: 'var(--ts-primary)',
      accentRgb: 'var(--ts-primary-rgb)',
      price: 25,
      popular: true,
    },
    {
      name: 'Enterprise',
      icon: 'shieldCheck',
      accent: 'var(--ts-tertiary)',
      accentRgb: 'var(--ts-tertiary-rgb)',
      price: 500,
      popular: false,
    },
  ]

  $: plans = planMeta.map((meta, i) => {
    const copy = $dict.pricing.plans[i]
    return {
      ...meta,
      description: copy.description,
      features: copy.features,
      cta: copy.cta,
      href: meta.name === 'Enterprise' ? '/contact' : `${registrationUrl}?plan=${meta.name.toLowerCase()}`,
    }
  })
</script>

<section class="pricing-section" id="pricing">
  <div class="pricing-section__inner">
    <SectionHeader
      eyebrow={$dict.pricing.eyebrow}
      subtitle={$dict.pricing.subtitle}
    >
      <svelte:fragment slot="title">
        {$dict.pricing.titleLead}<br />
        <span class="ts-text-gradient">{$dict.pricing.titleAccent}</span>
      </svelte:fragment>
    </SectionHeader>

    <div class="pricing-grid">
      {#each plans as plan (plan.name)}
        <div
          class="pricing-card"
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
              <AnimatedPrice value={plan.price} />
            </div>
            <div class="pricing-card__suffix">{$dict.pricing.suffixMonthly}</div>
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

          <RouteLink href={plan.href} class="pricing-card__cta">
            {plan.cta}
            <Icon name="arrowRight" className="pricing-card__cta-icon" />
          </RouteLink>
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

  .pricing-grid {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 20px;
    margin-top: 48px;
  }

  .pricing-card {
    position: relative;
    padding: 32px;
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
    gap: 12px;
    margin-bottom: 6px;
  }

  .pricing-card__icon {
    width: 40px;
    height: 40px;
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
    font-size: 22px;
    font-weight: 700;
    color: var(--ts-text);
  }

  .pricing-card__desc {
    font-family: var(--ts-font-accent);
    font-size: 13px;
    color: var(--ts-text-muted);
    margin: 0 0 28px;
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
    font-size: 44px;
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
    margin: 24px 0;
    background: linear-gradient(90deg, transparent, var(--ts-glass-border), transparent);
  }

  .pricing-card__features {
    list-style: none;
    padding: 0;
    margin: 0 0 28px;
    flex: 1;
  }

  .pricing-card__features li {
    display: flex;
    align-items: center;
    gap: 10px;
    margin-bottom: 12px;
    font-size: 14px;
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
    padding: 14px 20px;
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
