<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import {
    REGIONS,
    WORLD_MAP_VIEWBOX,
    type Region,
  } from '@/lib/world-map.generated'
  import { launchPhase, registrationUrl } from '@/lib/site'
  import { dict } from '@/lib/i18n'

  const highlightIcons = ['globe', 'activity', 'code2'] as const
  $: highlights = $dict.hero.highlights.map((label, i) => ({
    icon: highlightIcons[i] ?? 'globe',
    label,
  }))

  const vbW = WORLD_MAP_VIEWBOX.width
  const vbH = WORLD_MAP_VIEWBOX.height

  // Flag artwork for the hero's region pins. sjc is the San Francisco Bay Area.
  const regionLabel: Record<string, string> = {
    gru: 'São Paulo',
    sjc: 'San Francisco Bay Area',
  }
  // One arc only, gru to sjc, as in the r4 spec.
  const arcPath = (a: Region, b: Region) => {
    const mx = (a.x + b.x) / 2
    const len = Math.hypot(b.x - a.x, b.y - a.y)
    const lift = Math.max(2.5, len * 0.18)
    const cy = Math.min(a.y, b.y) - lift - Math.abs(b.x - a.x) * 0.04
    return `M ${a.x} ${a.y} Q ${mx} ${cy} ${b.x} ${b.y}`
  }

  const gru = REGIONS.find((r) => r.id === 'gru')
  const sjc = REGIONS.find((r) => r.id === 'sjc')
  const arcD = gru && sjc ? arcPath(gru, sjc) : ''
</script>

<section class="hero">
  <div class="hero__globe" aria-hidden="true">
    <div class="hero__globe-frame">
      <div class="hero__globe-map"></div>

      <svg
        class="hero__globe-lines"
        viewBox="0 0 {vbW} {vbH}"
        preserveAspectRatio="none"
        aria-hidden="true"
      >
        <path pathLength="1" d={arcD} class="hero__line" />
      </svg>

      {#each REGIONS as r (r.id)}
        <span
          class="hero__pin"
          class:hero__pin--left={r.id === 'sjc'}
          style="left: {(r.x / vbW) * 100}%; top: {(r.y / vbH) * 100}%;"
          title={regionLabel[r.id] ?? r.city}
        >
          <span class="hero__pin-ring"></span>
          <span class="hero__pin-core"></span>
          <span class="hero__pin-label">
            {#if r.id === 'gru' || r.id === 'sjc'}<RegionFlag code={r.id} />{/if}
          </span>
        </span>
      {/each}
    </div>
  </div>

  <div class="hero__inner">
    <span class="hero__brand">
      <img
        src="/icon-blue.svg"
        alt=""
        class="hero__brand-mark"
        width="835"
        height="942"
        decoding="async"
        fetchpriority="high"
      />
      <span class="hero__brand-word">TerraScale</span>
    </span>

    <h1 class="hero__headline">
      {$dict.hero.headlineLead}<br />
      <span class="hero__accent">{$dict.hero.headlineAccent}</span>
    </h1>

    <p class="hero__subtitle">
      {$dict.hero.subtitle}
    </p>

    <div class="hero__ctas">
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary">
        {launchPhase === 'public-alpha' ? $dict.hero.ctaPrimary : $dict.hero.ctaPrelaunch}
        <Icon name="arrowRight" className="hero__cta-icon" />
      </ExternalLink>
      <ExternalLink
        href="https://docs.terrascale.tech"
        target="_blank"
        class="ts-btn ts-btn-glass"
      >
        <Icon name="bookOpen" className="hero__cta-icon" />
        {$dict.hero.ctaSecondary}
      </ExternalLink>
    </div>

    <ul class="hero__highlights" aria-label={$dict.hero.highlightsAria}>
      {#each highlights as h (h.label)}
        <li class="hero__highlight">
          <Icon name={h.icon} className="hero__highlight-icon" />
          <span>{h.label}</span>
        </li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .hero {
    position: relative;
    isolation: isolate;
    overflow: hidden;
    margin-top: -60px;
    padding: 140px 0 96px;
    display: flex;
    align-items: center;
  }

  .hero__globe {
    position: absolute;
    inset: 60px 0 0;
    z-index: -1;
    display: grid;
    place-items: center;
    pointer-events: none;
  }

  .hero__globe-frame {
    position: relative;
    width: max(100%, 1100px);
    aspect-ratio: 119 / 60;
  }

  .hero__globe-map,
  .hero__globe-lines {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
  }

  .hero__globe-map {
    background: rgb(255 255 255 / 0.16);
    -webkit-mask: url(/world-map.svg) center / 100% 100% no-repeat;
    mask: url(/world-map.svg) center / 100% 100% no-repeat;
  }

  .hero__globe-lines {
    overflow: visible;
  }

  .hero__line {
    fill: none;
    stroke: var(--ts-primary);
    stroke-width: 0.22;
    stroke-linecap: round;
    stroke-dasharray: 1;
    stroke-dashoffset: 1;
    animation: ts-line-draw 1.6s cubic-bezier(0.2, 0.8, 0.2, 1) 0.3s forwards;
  }

  @keyframes ts-line-draw {
    to { stroke-dashoffset: 0; }
  }

  .hero__pin {
    position: absolute;
    width: 10px;
    height: 10px;
    translate: -50% -50%;
    border-radius: 50%;
    background: var(--ts-primary);
    box-shadow: 0 0 0 3px var(--ts-bg-base);
    pointer-events: none;
  }

  .hero__pin-core,
  .hero__pin-ring {
    position: absolute;
    border-radius: 50%;
  }

  .hero__pin-core {
    inset: 0;
  }

  .hero__pin-ring {
    inset: -6px;
    border: 1px solid var(--ts-primary);
    opacity: 0;
    animation: ts-pin-ping 2.4s cubic-bezier(0.2, 0.8, 0.2, 1) infinite;
  }

  @keyframes ts-pin-ping {
    0% { opacity: 0.9; scale: 0.6; }
    70%, 100% { opacity: 0; scale: 1.8; }
  }

  .hero__pin-label {
    position: absolute;
    left: 16px;
    top: 50%;
    translate: 0 -50%;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 3px 6px;
    border: 1px solid var(--ts-glass-border);
    border-radius: 4px;
    background: var(--ts-bg-card);
    line-height: 1;
  }

  .hero__pin--left .hero__pin-label {
    left: auto;
    right: 16px;
  }

  .hero__inner {
    position: relative;
    z-index: 1;
    width: 100%;
    padding: 0 clamp(16px, 2.5vw, 40px);
    display: grid;
    justify-items: center;
    gap: 24px;
    text-align: center;
  }

  .hero__brand {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  .hero__brand-mark {
    width: 44px;
    height: 44px;
    display: block;
  }

  .hero__brand-word {
    font-size: 32px;
    font-weight: 600;
    line-height: 1;
    letter-spacing: -0.015em;
    color: var(--ts-text);
  }

  .hero__headline {
    font-size: clamp(44px, 6.4vw, 96px);
    font-weight: 600;
    line-height: 1;
    margin: 8px 0 0;
    color: var(--ts-text);
    letter-spacing: -0.035em;
  }

  .hero__accent {
    color: var(--ts-primary);
  }

  .hero__subtitle {
    font-family: var(--ts-font-accent);
    font-size: 16px;
    color: var(--ts-text-muted);
    line-height: 1.7;
    max-width: 60ch;
    margin: 0;
    font-weight: 400;
  }

  .hero__ctas {
    display: flex;
    gap: 8px;
    justify-content: center;
    flex-wrap: wrap;
    margin-top: 8px;
  }

  .hero :global(.hero__ctas .ts-btn) {
    height: 40px;
    padding: 0 20px;
  }

  .hero :global(.ts-btn-glass) {
    background: var(--ts-bg-card);
    border-color: rgb(255 255 255 / 0.17);
    backdrop-filter: none;
    -webkit-backdrop-filter: none;
  }

  .hero :global(.ts-btn-glass:hover) {
    background: var(--ts-divider);
    border-color: rgba(var(--ts-primary-rgb), 0.5);
  }

  :global(.hero__cta-icon) {
    width: 14px;
    height: 14px;
  }

  .hero__highlights {
    list-style: none;
    margin: 16px 0 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 8px;
  }

  .hero__highlight {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    height: 32px;
    padding: 0 12px;
    border-radius: 100px;
    background: var(--ts-bg-card);
    border: 1px solid var(--ts-glass-border);
    font-family: var(--ts-font-accent);
    font-size: 13px;
    font-weight: 400;
    color: var(--ts-text-muted);
  }

  :global(.hero__highlight-icon) {
    width: 15px;
    height: 15px;
    color: var(--ts-primary-light);
    display: inline-block;
  }

  @media (max-width: 640px) {
    .hero {
      padding: 108px 0 64px;
    }
    .hero__globe { inset: 60px 0 auto; }
    .hero__globe-frame { width: 900px; }
    .hero__pin-label { display: none; }
    .hero__brand-mark {
      width: 32px;
      height: 32px;
    }
    .hero__brand-word {
      font-size: 24px;
    }
    .hero__subtitle {
      font-size: 14px;
    }
    .hero__ctas {
      width: 100%;
      flex-wrap: nowrap;
    }
    .hero :global(.hero__ctas .ts-btn) {
      flex: 1;
      width: 100%;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .hero__line {
      animation: none;
      stroke-dashoffset: 0;
    }
    .hero__pin-ring {
      animation: none !important;
      display: none;
    }
  }
</style>
