<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'
</script>

<section class="ts-section pricing-cta">
  <div class="ts-container pricing-cta__inner">
    <h2 class="pricing-cta__title">{$dict.pricing.finalCta.titleLead} <span class="ts-h-muted">{$dict.pricing.finalCta.titleAccent}</span></h2>
    <p class="ts-lede pricing-cta__text">
      {$dict.pricing.finalCta.text}
    </p>
    <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary ts-btn-lg">
      {$dict.pricing.finalCta.cta}
      <Icon name="arrowRight" className="pricing-cta__icon" />
    </ExternalLink>
  </div>
</section>

<style>
  .pricing-cta {
    border-top: 1px solid var(--ts-line);
  }

  .pricing-cta__inner {
    display: grid;
    gap: var(--ts-s4);
    justify-items: start;
  }

  .pricing-cta__title {
    font-size: var(--ts-fs-4xl);
  }

  .pricing-cta__text {
    margin-top: 0;
  }

  .pricing-cta :global(.pricing-cta__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  @media (max-width: 640px) {
    .pricing-cta__title {
      font-size: var(--ts-fs-3xl);
    }
  }
</style>
