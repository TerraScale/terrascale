<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'

  $: perks = $dict.cta.perks
</script>

<section class="cta">
  <div class="cta__inner">
    <div class="cta__card ts-card">
      <h2 class="cta__headline">
        {$dict.cta.headlineLead} <span class="ts-accent-text">{$dict.cta.headlineAccent}</span>
      </h2>
      <p class="cta__text">
        {$dict.cta.text}
      </p>

      <div class="cta__actions">
        <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary cta__primary">
          {$dict.cta.ctaPrimary}
          <Icon name="arrowRight" className="cta__icon" />
        </ExternalLink>
        <RouteLink href="/contact" class="ts-btn ts-btn-secondary cta__secondary">
          <Icon name="messageSquare" className="cta__icon" />
          {$dict.cta.ctaSecondary}
        </RouteLink>
      </div>

      <ul class="cta__perks" aria-label={$dict.cta.perksAria}>
        {#each perks as p (p)}
          <li>
            <Icon name="checkCircle" className="cta__perk-icon" />
            {p}
          </li>
        {/each}
      </ul>
    </div>
  </div>
</section>

<style>
  .cta {
    position: relative;
    padding: 84px 0 112px;
    overflow: hidden;
  }

  .cta__inner {
    position: relative;
    max-width: 960px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .cta__card {
    position: relative;
    border-radius: var(--ts-radius-xl);
    padding: 48px 40px 40px;
    text-align: center;
    overflow: hidden;
  }

  .cta__headline {
    font-size: clamp(2rem, 3vw + 0.5rem, 3.2rem);
    font-weight: 700;
    color: var(--ts-ink);
    line-height: 1.12;
    letter-spacing: -0.025em;
    margin: 0 0 16px;
  }

  .cta__text {
    font-family: var(--ts-font-sans);
    font-size: 16px;
    color: var(--ts-ink-2);
    line-height: 1.6;
    max-width: 560px;
    margin: 0 auto 28px;
  }

  .cta__actions {
    display: flex;
    justify-content: center;
    gap: 12px;
    flex-wrap: wrap;
    margin-bottom: 24px;
  }

  :global(.cta__primary) {
    padding: 15px 30px;
  }

  :global(.cta__secondary) {
    padding: 15px 28px;
  }

  :global(.cta__icon) {
    width: 14px;
    height: 14px;
  }

  .cta__perks {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    justify-content: center;
    gap: 22px;
    flex-wrap: wrap;
    font-family: var(--ts-font-sans);
    font-size: 12px;
    color: var(--ts-ink-2);
  }

  .cta__perks li {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-weight: 600;
    letter-spacing: 0.02em;
  }

  :global(.cta__perk-icon) {
    width: 14px;
    height: 14px;
    color: var(--ts-success);
  }

  @media (max-width: 640px) {
    .cta {
      padding: 48px 0 72px;
    }
    .cta__inner {
      padding: 0 16px;
    }
    .cta__card {
      padding: 36px 24px 28px;
    }
    .cta__perks {
      gap: 12px;
      font-size: 11px;
    }
  }
</style>
