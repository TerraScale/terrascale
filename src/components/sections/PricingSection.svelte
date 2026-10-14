<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict, locale } from '@/lib/i18n'
  import { localeMeta } from '@/lib/i18n/locales'
  import { formatPlanLimits } from '@/lib/pricing'
  import { launchPhase, publicAlphaCatalog, registrationUrl } from '@/lib/site'

  export let headingLevel: 1 | 2 = 2

  let billing: 'monthly' | 'annual' = 'monthly'

  $: headingTag = headingLevel === 1 ? 'h1' : 'h2'
  $: planTag = headingLevel === 1 ? 'h2' : 'h3'
  $: suffix = billing === 'annual' ? $dict.pricing.suffixAnnual : $dict.pricing.suffixMonthly

  $: plans = publicAlphaCatalog.map((meta) => {
    const copy = $dict.pricing.plans[meta.slug]
    const limitLines = formatPlanLimits(meta.limits, $dict.pricing.limits, localeMeta($locale).bcp47)
    return {
      ...meta,
      description: copy.description,
      features: 'features' in copy ? [...limitLines, ...copy.features] : limitLines,
      cta: meta.slug === 'enterprise' || launchPhase === 'public-alpha' ? copy.cta : $dict.hero.ctaPrelaunch,
      href: meta.slug === 'enterprise' ? '/contact' : `${registrationUrl}?plan=${meta.slug}`,
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
    </div>

    <div class="pricing-cycle">
      <div class="pricing-toggle" role="group" aria-label={$dict.pricing.toggleLabel}>
        <button type="button" aria-pressed={billing === 'monthly'} on:click={() => (billing = 'monthly')}>
          {$dict.pricing.monthly}
        </button>
        <button type="button" aria-pressed={billing === 'annual'} on:click={() => (billing = 'annual')}>
          {$dict.pricing.annual}
        </button>
      </div>
      <span class="pricing-cycle__save">{$dict.pricing.annual}: {$dict.pricing.save}</span>
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
      >
        <svelte:element this={planTag} class="pricing-card__name">
          {plan.name}
          {#if plan.popular}
            <span class="pricing-card__badge">{$dict.pricing.badge}</span>
          {:else if !plan.available}
            <span class="pricing-card__badge pricing-card__badge--neutral">{$dict.pricing.plans.enterprise.features[1]}</span>
          {/if}
        </svelte:element>
        <p class="pricing-card__desc">{plan.description}</p>

        <p class="pricing-card__price">
          <strong>${billing === 'annual' && plan.available ? plan.annualPrice : plan.price}</strong>
          <span>{plan.available ? suffix : $dict.pricing.detailed.rows[2].unit}</span>
        </p>

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

  .pricing-section__subtitle {
    margin: 0;
    font-size: 16px;
    line-height: 1.7;
    color: var(--ts-text-muted);
  }

  .pricing-cycle {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: var(--ts-space-3);
  }

  .pricing-toggle {
    display: inline-flex;
    gap: 2px;
    height: 32px;
    padding: 2px;
    border: 1px solid var(--ts-glass-border);
    border-radius: var(--ts-radius-md);
    background: var(--ts-bg-surface);
  }

  .pricing-toggle button {
    padding: 0 var(--ts-space-3);
    border: 0;
    border-radius: var(--ts-radius-sm);
    background: transparent;
    color: var(--ts-text-muted);
    font-size: 13px;
    font-weight: 500;
    cursor: pointer;
    transition:
      color var(--ts-transition-fast),
      background-color var(--ts-transition-fast);
  }

  .pricing-toggle button:hover,
  .pricing-toggle button[aria-pressed='true'] {
    color: var(--ts-text);
  }

  .pricing-toggle button[aria-pressed='true'] {
    background: #2c2c2c;
  }

  .pricing-cycle__save {
    font-size: 14px;
    color: var(--ts-text-muted);
  }

  .pricing-grid {
    display: grid;
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: var(--ts-space-4);
    margin-top: var(--ts-space-10);
  }

  .pricing-card {
    display: flex;
    flex-direction: column;
    gap: var(--ts-space-4);
    min-width: 0;
    padding: var(--ts-space-6);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
  }

  .pricing-card.is-popular {
    border-color: color-mix(in oklab, var(--ts-primary) 70%, transparent);
  }

  .pricing-card__name {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: var(--ts-space-3);
    margin: 0;
    font-size: 16px;
    font-weight: 500;
    color: var(--ts-text);
  }

  .pricing-card__badge {
    display: inline-flex;
    align-items: center;
    height: 22px;
    padding: 0 var(--ts-space-2);
    border-radius: var(--ts-radius-sm);
    background: rgba(var(--ts-primary-light-rgb), 0.16);
    color: var(--ts-primary-light);
    font-size: 12px;
    font-weight: 500;
    white-space: nowrap;
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
    font: 500 40px / 1 var(--ts-font-mono);
    letter-spacing: -0.04em;
    color: var(--ts-text);
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
  }

  :global(.pricing-card__cta) {
    width: 100%;
    margin-top: auto;
  }

  .pricing-card.is-unavailable :global(.pricing-card__cta) {
    background: transparent;
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
