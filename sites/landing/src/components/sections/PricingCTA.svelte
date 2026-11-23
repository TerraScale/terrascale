<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'
</script>

<section class="pricing-cta">
  <div class="pricing-cta__inner">
    <h2 class="pricing-cta__title">{$dict.pricing.finalCta.title}</h2>
    <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary pricing-cta__btn">
      {$dict.pricing.finalCta.cta}
    </ExternalLink>
  </div>
</section>

<style>
  .pricing-cta {
    padding: var(--ts-space-20) clamp(16px, 2.5vw, 40px);
    border-top: 1px solid var(--ts-divider);
  }

  .pricing-cta__inner {
    display: grid;
    justify-items: center;
    gap: var(--ts-space-6);
    text-align: center;
  }

  .pricing-cta__title {
    margin: 0;
    font-size: clamp(30px, 3.2vw, 48px);
    font-weight: 600;
    letter-spacing: -0.035em;
    color: var(--ts-text);
    line-height: 1.08;
    text-wrap: balance;
  }

  :global(.pricing-cta__btn) {
    min-height: 44px;
    height: auto;
    max-width: 100%;
    padding: var(--ts-space-3) var(--ts-space-6);
    font-size: 16px;
    white-space: normal;
  }

  @media (max-width: 640px) {
    .pricing-cta {
      padding-block: var(--ts-space-12);
    }

    :global(.pricing-cta__btn) {
      width: 100%;
    }
  }
</style>
