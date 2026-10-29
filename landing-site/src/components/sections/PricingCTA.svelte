<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'
</script>

<section class="pricing-cta">
  <div class="pricing-cta__card">
    <div>
      <h2 class="pricing-cta__title">{$dict.pricing.finalCta.titleLead} {$dict.pricing.finalCta.titleAccent}</h2>
      <p class="pricing-cta__text">{$dict.pricing.finalCta.text}</p>
    </div>
    <div class="pricing-cta__actions">
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary pricing-cta__btn">
        {$dict.pricing.finalCta.cta}
        <Icon name="arrowRight" className="pricing-cta__icon" />
      </ExternalLink>
      <ExternalLink href="https://docs.terrascale.tech" class="ts-btn ts-btn-glass pricing-cta__btn">
        {$dict.pricing.faq.readDocs}
      </ExternalLink>
    </div>
  </div>
</section>

<style>
  .pricing-cta {
    padding: var(--ts-space-20) clamp(16px, 2.5vw, 40px);
    border-top: 1px solid var(--ts-divider);
  }

  .pricing-cta__card {
    display: flex;
    align-items: center;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: var(--ts-space-8);
    padding: var(--ts-space-10);
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-xl);
    background: var(--ts-bg-surface);
  }

  .pricing-cta__title {
    margin: 0;
    font-size: 32px;
    font-weight: 600;
    letter-spacing: -0.035em;
    color: var(--ts-text);
  }

  .pricing-cta__text {
    margin: var(--ts-space-2) 0 0;
    font-size: 16px;
    color: var(--ts-text-muted);
  }

  .pricing-cta__actions {
    display: flex;
    flex-wrap: wrap;
    gap: var(--ts-space-3);
  }

  :global(.pricing-cta__btn) {
    height: 44px;
    padding: 0 var(--ts-space-5);
  }

  :global(.pricing-cta__icon) {
    width: 16px;
    height: 16px;
  }

  @media (max-width: 640px) {
    .pricing-cta {
      padding-block: var(--ts-space-12);
    }

    .pricing-cta__card {
      padding: var(--ts-space-6);
    }

    .pricing-cta__title {
      font-size: 24px;
    }

    .pricing-cta__actions {
      width: 100%;
    }

    :global(.pricing-cta__btn) {
      width: 100%;
    }
  }
</style>
