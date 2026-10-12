<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import {
    REGIONS,
    WORLD_MAP_VIEWBOX,
    type Region,
  } from '@/lib/world-map.generated'
  import { registrationUrl } from '@/lib/site'
  import { dict } from '@/lib/i18n'

  const highlightIcons = ['globe', 'activity', 'clock'] as const
  $: highlights = $dict.hero.highlights.map((label, i) => ({
    icon: highlightIcons[i] ?? 'globe',
    label,
  }))

  const vbW = WORLD_MAP_VIEWBOX.width
  const vbH = WORLD_MAP_VIEWBOX.height

  const ringPairs: Array<[Region, Region]> = REGIONS.map((r, i) => [
    r,
    REGIONS[(i + 1) % REGIONS.length],
  ])

  const arcPath = (a: Region, b: Region) => {
    const mx = (a.x + b.x) / 2
    const len = Math.hypot(b.x - a.x, b.y - a.y)
    const lift = Math.max(2.5, len * 0.18)
    const cy = Math.min(a.y, b.y) - lift - Math.abs(b.x - a.x) * 0.04
    return `M ${a.x} ${a.y} Q ${mx} ${cy} ${b.x} ${b.y}`
  }

  const connections = ringPairs.map(([from, to], i) => ({
    id: `${from.id}-${to.id}`,
    d: arcPath(from, to),
    delay: (i * 0.45).toFixed(2),
  }))

</script>

<section class="hero">
  <div class="hero__globe" aria-hidden="true">
    <div class="hero__globe-frame">
      <img
        src="/world-map.svg"
        alt=""
        class="hero__globe-map"
        decoding="async"
        loading="eager"
      />

      <svg
        class="hero__globe-lines"
        viewBox="0 0 {vbW} {vbH}"
        preserveAspectRatio="none"
        aria-hidden="true"
      >
        {#each connections as c (c.id)}
          <path d={c.d} class="hero__line" style="--delay: {c.delay}s;" />
        {/each}
      </svg>

      {#each REGIONS as r, i (r.id)}
        <span
          class="hero__pin"
          style="left: {(r.x / vbW) * 100}%; top: {(r.y / vbH) * 100}%; --delay: {(i * 0.3).toFixed(2)}s;"
          title={r.city}
        >
          <span class="hero__pin-ring"></span>
          <span class="hero__pin-core"></span>
        </span>
      {/each}
    </div>
  </div>

  <div class="hero__inner">
    <h1 class="hero__headline">
      {$dict.hero.headlineLead}<br />
      <span class="ts-accent-text">{$dict.hero.headlineAccent}</span>
    </h1>

    <p class="hero__subtitle">
      {$dict.hero.subtitle}
    </p>

    <div class="hero__ctas">
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary">
        {$dict.hero.ctaPrimary}
        <Icon name="arrowRight" className="hero__cta-icon" />
      </ExternalLink>
      <ExternalLink
        href="https://docs.terrascale.tech"
        target="_blank"
        class="ts-btn ts-btn-secondary"
      >
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
    min-height: clamp(580px, calc(78vh - 72px), 760px);
    display: flex;
    align-items: center;
    overflow: hidden;
  }

  .hero__globe {
    position: absolute;
    inset: 0;
    pointer-events: none;
    z-index: 0;
  }

  .hero__globe-frame {
    position: absolute;
    top: 50%;
    left: 50%;
    width: min(1600px, 100%);
    aspect-ratio: 119 / 60;
    transform: translate(-50%, -50%);
  }

  .hero__globe-map,
  .hero__globe-lines {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    user-select: none;
    -webkit-user-drag: none;
  }

  .hero__globe-map {
    opacity: 0.22;
    filter: saturate(0.85);
  }

  .hero__globe-lines {
    overflow: visible;
  }

  .hero__line {
    fill: none;
    stroke: color-mix(in srgb, var(--ts-accent) 50%, transparent);
    stroke-width: 0.18;
    stroke-linecap: round;
    stroke-dasharray: 2 3;
    animation: ts-line-flow 6s linear infinite;
    animation-delay: var(--delay, 0s);
    opacity: 0.85;
  }

  @keyframes ts-line-flow {
    from { stroke-dashoffset: 0; }
    to   { stroke-dashoffset: -20; }
  }

  .hero__pin {
    position: absolute;
    width: 0;
    height: 0;
    pointer-events: none;
  }

  .hero__pin-core {
    position: absolute;
    top: 0;
    left: 0;
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--ts-accent);
    box-shadow: 0 0 0 2px var(--ts-canvas);
    transform: translate(-50%, -50%);
  }

  .hero__pin-ring {
    position: absolute;
    top: 0;
    left: 0;
    width: 7px;
    height: 7px;
    border-radius: 50%;
    border: 1.5px solid color-mix(in srgb, var(--ts-accent) 80%, transparent);
    transform: translate(-50%, -50%);
    animation: ts-pin-pulse 2.6s cubic-bezier(0.2, 0.8, 0.2, 1) infinite;
    animation-delay: var(--delay, 0s);
  }

  @keyframes ts-pin-pulse {
    0%   { transform: translate(-50%, -50%) scale(1);   opacity: 0.9; }
    80%  { transform: translate(-50%, -50%) scale(3.8); opacity: 0;   }
    100% { transform: translate(-50%, -50%) scale(3.8); opacity: 0;   }
  }

  .hero__inner {
    position: relative;
    z-index: 1;
    width: 100%;
    max-width: 1240px;
    margin: 0 auto;
    padding: 40px 40px;
    text-align: center;
  }

  .hero__headline {
    font-size: clamp(2.4rem, 4.2vw + 1rem, 5rem);
    font-weight: 700;
    line-height: 1.02;
    margin: 0 0 20px;
    color: var(--ts-ink);
    letter-spacing: var(--ts-display-tracking);
  }

  .hero__subtitle {
    font-family: var(--ts-font-sans);
    font-size: clamp(1rem, 0.7vw + 0.75rem, 1.15rem);
    color: var(--ts-ink-2);
    line-height: 1.55;
    max-width: 580px;
    margin: 0 auto 32px;
    font-weight: 400;
  }

  .hero__ctas {
    display: flex;
    gap: 12px;
    justify-content: center;
    flex-wrap: wrap;
  }

  :global(.hero__cta-icon) {
    width: 14px;
    height: 14px;
  }

  .hero__highlights {
    list-style: none;
    margin: 48px auto 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(3, max-content);
    justify-content: center;
    column-gap: 14px;
    row-gap: 12px;
  }

  .hero__highlight {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 10px;
    padding: 9px 16px;
    border-radius: 100px;
    background: var(--ts-glass);
    border: 1px solid var(--ts-glass-line);
    backdrop-filter: blur(var(--ts-blur)) saturate(var(--ts-saturate));
    font-family: var(--ts-font-sans);
    font-size: 13px;
    font-weight: 600;
    letter-spacing: 0.04em;
    color: var(--ts-ink);
  }

  :global(.hero__highlight-icon) {
    width: 15px;
    height: 15px;
    color: var(--ts-accent);
    display: inline-block;
  }

  @media (max-width: 720px) {
    .hero {
      min-height: clamp(520px, 92vh, 680px);
    }
    .hero__inner {
      padding: 36px 20px;
    }
    .hero__globe-frame {
      width: 100%;
    }
    .hero__highlights {
      grid-template-columns: max-content;
      row-gap: 10px;
      margin-top: 40px;
    }
    .hero__highlight {
      padding: 8px 14px;
      font-size: 12px;
    }
    .hero__pin-core,
    .hero__pin-ring {
      width: 5px;
      height: 5px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .hero__line,
    .hero__pin-ring {
      animation: none !important;
    }
  }
</style>
