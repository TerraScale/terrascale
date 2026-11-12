<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import HeroMap from '@/components/HeroMap.svelte'
  import Icon from '@/components/Icon.svelte'
  import { dict } from '@/lib/i18n'
  import { publicAlphaCatalog, registrationUrl } from '@/lib/site'
  import { REGIONS } from '@/lib/world-map.generated'

  const freePrice = publicAlphaCatalog.find((plan) => plan.slug === 'free')?.price ?? 0
  const statementsPerTransaction = 256

  const stats = [
    { value: String(REGIONS.length), label: $dict.homeStats.regions },
    { value: 'any', label: $dict.homeStats.writeRegion },
    { value: `$${freePrice}`, label: $dict.pricing.cadence.free },
    { value: String(statementsPerTransaction), label: $dict.homeStats.statements },
  ]
</script>

<section class="map-hero">
  <div class="map-hero__sky" aria-hidden="true">
    <HeroMap />
  </div>

  <div class="ts-container map-hero__inner">
    <h1 class="map-hero__headline">{$dict.hero.headline}</h1>
    <div class="map-hero__cta">
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary map-hero__btn">
        {$dict.hero.cta}
        <Icon name="arrowRight" className="map-hero__btn-icon" />
      </ExternalLink>
    </div>
  </div>

  <div class="ts-container">
    <dl class="map-hero__stats">
      {#each stats as stat (stat.label)}
        <div class="map-hero__stat">
          <dt class="map-hero__stat-label">{stat.label}</dt>
          <dd class="map-hero__stat-value ts-num">{stat.value}</dd>
        </div>
      {/each}
    </dl>
  </div>
</section>

<style>
  .map-hero {
    position: relative;
    display: flex;
    flex-direction: column;
    justify-content: flex-end;
    min-height: clamp(560px, calc(88vh - 60px), 860px);
    padding-top: clamp(48px, 8vh, 96px);
    overflow: hidden;
  }

  .map-hero__sky {
    position: absolute;
    inset: 0;
    display: grid;
    place-items: center;
    opacity: 0.75;
    pointer-events: none;
    mask-image: radial-gradient(ellipse 90% 85% at 50% 45%, #000 0%, #000 62%, rgb(0 0 0 / 0.35) 85%, transparent 100%);
    -webkit-mask-image: radial-gradient(ellipse 90% 85% at 50% 45%, #000 0%, #000 62%, rgb(0 0 0 / 0.35) 85%, transparent 100%);
  }

  .map-hero__sky :global(.hero-map) {
    width: min(1500px, 100%);
  }

  .map-hero__inner {
    position: relative;
    z-index: 1;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: clamp(28px, 4vh, 44px);
    text-align: center;
    margin-block: auto;
  }

  .map-hero__headline {
    margin: 0;
    max-width: 14ch;
    font-size: clamp(44px, 6.5vw, 88px);
    font-weight: 600;
    line-height: 1.02;
    letter-spacing: -0.04em;
    text-wrap: balance;
    background: linear-gradient(90deg, #fff, #9b9b9b);
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
    -webkit-box-decoration-break: clone;
    box-decoration-break: clone;
  }

  .map-hero__cta {
    display: flex;
    justify-content: center;
  }

  :global(.map-hero__btn) {
    height: 46px;
    padding: 0 24px;
    font-size: 15px;
    box-shadow: 0 8px 32px rgb(var(--ts-primary-rgb) / 0.35);
    transition:
      background-color var(--ts-transition-fast),
      transform var(--ts-transition-slow),
      box-shadow var(--ts-transition-slow);
  }

  :global(.map-hero__btn:hover) {
    transform: translateY(-2px);
    box-shadow: 0 12px 40px rgb(var(--ts-primary-rgb) / 0.45);
  }

  :global(.map-hero__btn-icon) {
    width: 16px;
    height: 16px;
    transition: transform var(--ts-transition-slow);
  }

  :global(.map-hero__btn:hover .map-hero__btn-icon) {
    transform: translateX(3px);
  }

  .map-hero__stats {
    position: relative;
    z-index: 1;
    display: grid;
    grid-template-columns: repeat(4, minmax(0, 1fr));
    margin: clamp(32px, 6vh, 72px) 0 0;
    border-top: 1px solid var(--ts-glass-border);
  }

  .map-hero__stat {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 24px clamp(16px, 2vw, 32px);
    border-left: 1px solid var(--ts-divider);
  }

  .map-hero__stat:first-child {
    border-left: 0;
    padding-left: 0;
  }

  .map-hero__stat-label {
    font-family: var(--ts-font-mono);
    font-size: 12px;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    color: var(--ts-text-muted);
  }

  .map-hero__stat-value {
    margin: 0;
    font-family: var(--ts-font-mono);
    font-size: clamp(28px, 3vw, 40px);
    font-weight: 500;
    line-height: 1;
    letter-spacing: -0.03em;
    color: var(--ts-text);
  }

  @media (max-width: 900px) {
    .map-hero__stats {
      grid-template-columns: repeat(2, minmax(0, 1fr));
      border-top: 1px solid var(--ts-glass-border);
    }

    .map-hero__stat:nth-child(odd) {
      border-left: 0;
      padding-left: 0;
    }

    .map-hero__stat:nth-child(n + 3) {
      border-top: 1px solid var(--ts-divider);
    }
  }

  @media (max-width: 640px) {
    .map-hero {
      min-height: 0;
    }

    .map-hero__sky {
      position: static;
      margin-block: 8px 24px;
      mask-image: linear-gradient(180deg, transparent 0%, #000 18%, #000 82%, transparent 100%);
      -webkit-mask-image: linear-gradient(180deg, transparent 0%, #000 18%, #000 82%, transparent 100%);
    }

    .map-hero__sky :global(.hero-map) {
      width: 100%;
    }

    :global(.map-hero__btn) {
      width: 100%;
    }

    .map-hero__stats {
      margin-top: 40px;
    }

    .map-hero__stat {
      padding: 16px 0 16px 16px;
    }

    .map-hero__stat:nth-child(odd) {
      padding-left: 0;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.map-hero__btn),
    :global(.map-hero__btn-icon) {
      transition: none;
    }

    :global(.map-hero__btn:hover),
    :global(.map-hero__btn:hover .map-hero__btn-icon) {
      transform: none;
    }
  }
</style>
