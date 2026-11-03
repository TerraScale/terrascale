<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'
  import { registrationUrl } from '@/lib/site'
</script>

<section class="cta" use:reveal>
  <div class="cta__inner">
    <div class="cta__card">
      <h2 class="cta__headline">
        {$dict.cta.headlineLead} <span class="cta__accent">{$dict.cta.headlineAccent}</span>
      </h2>

      <div class="cta__actions">
        <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary cta__primary">
          {$dict.cta.ctaPrimary}
          <Icon name="arrowRight" className="cta__icon" />
        </ExternalLink>
      </div>
    </div>
  </div>
</section>

<style>
  .cta {
    padding: 80px 0;
    border-top: 1px solid var(--ts-divider);
  }

  .cta__accent {
    color: var(--ts-link);
  }

  .cta__inner {
    padding: 0 clamp(16px, 2.5vw, 40px);
  }

  .cta__card {
    display: grid;
    justify-items: center;
    gap: 20px;
    max-width: 960px;
    margin: 0 auto;
    padding: 64px 32px;
    border: 1px solid var(--ts-divider);
    border-radius: 14px;
    background: var(--ts-bg-surface);
    text-align: center;
  }

  .cta__headline {
    font-size: clamp(30px, 3.2vw, 48px);
    font-weight: 600;
    color: var(--ts-text);
    line-height: 1.08;
    letter-spacing: -0.035em;
    margin: 0;
  }

  .cta__actions {
    display: flex;
    justify-content: center;
    flex-wrap: wrap;
    gap: 12px;
  }

  :global(.cta__primary),
  :global(.cta__secondary) {
    height: 40px;
    padding: 0 clamp(16px, 2.5vw, 40px);
    font-size: 14px;
  }

  :global(.cta__icon) {
    width: 14px;
    height: 14px;
  }

  @media (max-width: 640px) {
    .cta {
      padding: 64px 0;
    }
    .cta__inner {
      padding: 0 clamp(16px, 2.5vw, 40px);
    }
    .cta__card {
      padding: 40px 20px;
    }
    .cta__actions {
      width: 100%;
    }
    :global(.cta__primary),
    :global(.cta__secondary) {
      width: 100%;
    }
  }
</style>
