<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
</script>

<section class="pricing-cta">
  <div class="pricing-cta__inner">
    <h2 class="pricing-cta__title">{$dict.pricing.finalCta.titleLead} <span class="ts-text-gradient">{$dict.pricing.finalCta.titleAccent}</span></h2>
    <p class="pricing-cta__text">
      {$dict.pricing.finalCta.text}
    </p>
    <RouteLink href="https://dash.terrascale.tech/signup" class="ts-btn ts-btn-primary cta-shine">
      {$dict.pricing.finalCta.cta}
      <Icon name="arrowRight" className="pricing-cta__icon" />
    </RouteLink>
  </div>
</section>

<style>
  .pricing-cta {
    position: relative;
    padding: 80px 0 100px;
    overflow: hidden;
  }

  .pricing-cta__inner {
    position: relative;
    max-width: 720px;
    margin: 0 auto;
    padding: 0 40px;
    text-align: center;
  }

  .pricing-cta__title {
    font-size: clamp(2rem, 2.5vw + 1rem, 3rem);
    font-weight: 700;
    color: var(--ts-text);
    letter-spacing: -0.025em;
    line-height: 1.12;
    margin: 0 0 14px;
  }

  .pricing-cta__text {
    font-family: var(--ts-font-accent);
    font-size: 17px;
    color: var(--ts-text-muted);
    margin: 0 0 28px;
  }

  :global(.pricing-cta__icon) {
    width: 14px;
    height: 14px;
  }

  @media (max-width: 640px) {
    .pricing-cta__inner {
      padding: 0 20px;
    }
  }
</style>
