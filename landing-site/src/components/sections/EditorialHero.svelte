<script lang="ts">
  import EditorialTerminal from '@/components/sections/EditorialTerminal.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'
  import { REGIONS } from '@/lib/world-map.generated'

  const regionCodes = ['gru', 'sjc', 'fra'] as const
</script>

<section class="ed-hero">
  <div class="ts-container ed-hero__inner">
    <div class="ed-hero__copy">
      <ul class="ed-hero__regions" aria-label="Serving regions">
        {#each regionCodes as code (code)}
          <li class="ed-hero__region"><RegionFlag {code} /></li>
        {/each}
      </ul>

      <h1 class="ed-hero__headline">{$dict.hero.headline}</h1>

      <div class="ed-hero__actions">
        <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary ed-hero__cta">
          {$dict.hero.cta}
          <Icon name="arrowRight" className="ed-hero__cta-icon" />
        </ExternalLink>
        <RouteLink href="/pricing" class="ed-hero__compare">
          {$dict.pricingTeaser.cta}
          <Icon name="arrowRight" className="ed-hero__compare-icon" />
        </RouteLink>
      </div>
    </div>

    <div class="ed-hero__panel">
      <EditorialTerminal />
    </div>
  </div>
</section>

<style>
  .ed-hero {
    padding-block: clamp(56px, 9vh, 112px) clamp(48px, 7vh, 88px);
  }

  .ed-hero__inner {
    display: grid;
    grid-template-columns: minmax(0, 5fr) minmax(0, 6fr);
    gap: clamp(40px, 6vw, 96px);
    align-items: center;
  }

  .ed-hero__copy {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: clamp(24px, 3vw, 40px);
    min-width: 0;
  }

  .ed-hero__regions {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .ed-hero__region {
    display: inline-flex;
    align-items: center;
    height: 28px;
    padding: 0 10px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-sm);
    background: var(--ts-bg-surface);
  }

  .ed-hero__headline {
    margin: 0;
    max-width: 12ch;
    font-size: clamp(56px, 7.5vw, 108px);
    font-weight: 600;
    line-height: 0.98;
    letter-spacing: -0.045em;
    text-wrap: balance;
    background: linear-gradient(90deg, #fff, #9b9b9b);
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
    -webkit-box-decoration-break: clone;
    box-decoration-break: clone;
  }

  .ed-hero__actions {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 16px 24px;
  }

  :global(.ed-hero__cta) {
    height: 44px;
    padding: 0 22px;
    font-size: 15px;
    transition:
      background-color var(--ts-transition-fast),
      transform var(--ts-transition-slow);
  }

  :global(.ed-hero__cta-icon) {
    width: 16px;
    height: 16px;
    transition: transform var(--ts-transition-slow);
  }

  :global(.ed-hero__cta:hover .ed-hero__cta-icon) {
    transform: translateX(3px);
  }

  .ed-hero__compare {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-mono);
    font-size: 13px;
    letter-spacing: 0.02em;
  }

  .ed-hero__compare:hover {
    color: var(--ts-text);
  }

  :global(.ed-hero__compare-icon) {
    width: 14px;
    height: 14px;
    transition: transform var(--ts-transition-slow);
  }

  .ed-hero__compare:hover :global(.ed-hero__compare-icon) {
    transform: translateX(3px);
  }

  .ed-hero__panel {
    min-width: 0;
  }

  @media (max-width: 1023px) {
    .ed-hero__inner {
      grid-template-columns: minmax(0, 1fr);
      gap: 48px;
    }
  }

  @media (max-width: 640px) {
    .ed-hero {
      padding-block: 40px 48px;
    }

    .ed-hero__headline {
      font-size: clamp(44px, 12vw, 64px);
    }

    :global(.ed-hero__cta) {
      width: 100%;
    }

    .ed-hero__actions {
      align-items: stretch;
      flex-direction: column;
    }

    .ed-hero__compare {
      justify-content: center;
    }
  }
</style>
