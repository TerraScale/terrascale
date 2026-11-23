<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'
  import { registrationUrl } from '@/lib/site'
</script>

<section class="cta" use:reveal>
  <div class="cta__inner">
    <h2 class="cta__headline">{$dict.cta.title}</h2>
    <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary cta__primary">
      {$dict.cta.ctaPrimary}
    </ExternalLink>
  </div>
</section>

<style>
  .cta {
    padding: 80px 0;
    border-top: 1px solid var(--ts-divider);
  }

  .cta__inner {
    display: grid;
    justify-items: center;
    gap: var(--ts-space-6);
    max-width: 960px;
    margin: 0 auto;
    padding: 0 clamp(16px, 2.5vw, 40px);
    text-align: center;
  }

  .cta__headline {
    font-size: clamp(30px, 3.2vw, 48px);
    font-weight: 600;
    color: var(--ts-text);
    line-height: 1.08;
    letter-spacing: -0.035em;
    margin: 0;
    text-wrap: balance;
  }

  :global(.cta__primary) {
    min-height: 44px;
    height: auto;
    max-width: 100%;
    padding: var(--ts-space-3) var(--ts-space-6);
    font-size: 16px;
    white-space: normal;
  }

  @media (max-width: 640px) {
    .cta {
      padding: 64px 0;
    }
    :global(.cta__primary) {
      width: 100%;
    }
  }
</style>
