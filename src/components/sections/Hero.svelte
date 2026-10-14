<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import {
    REGIONS,
    WORLD_MAP_VIEWBOX,
    type Region,
  } from '@/lib/world-map.generated'
  import { dict } from '@/lib/i18n'

  const highlightIcons = ['globe', 'activity', 'code2', 'bot'] as const
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
  const arcPath = (a: Region, b: Region) => {
    const mx = (a.x + b.x) / 2
    const len = Math.hypot(b.x - a.x, b.y - a.y)
    const lift = Math.max(2.5, len * 0.18)
    const cy = Math.min(a.y, b.y) - lift - Math.abs(b.x - a.x) * 0.04
    return `M ${a.x} ${a.y} Q ${mx} ${cy} ${b.x} ${b.y}`
  }

  // Full replication mesh: gru draws out to sjc and fra, then sjc links to fra
  // once both have landed. Pulses are staggered so they never fire together.
  const byId = (id: string) => REGIONS.find((r) => r.id === id)
  const arcs = [
    ['gru', 'sjc', 0.4],
    ['gru', 'fra', 0.4],
    ['sjc', 'fra', 1.9],
  ].flatMap(([from, to, delay], i) => {
    const a = byId(from as string)
    const b = byId(to as string)
    return a && b
      ? [{
          id: `${from}-${to}`,
          d: arcPath(a, b),
          // Same curve traced back, so replication pulses run both ways.
          backD: arcPath(b, a),
          style: `--draw-delay: ${delay}s; --pulse-delay: ${2 + i * 1.2}s`,
        }]
      : []
  })
</script>

<section class="hero">
  <div class="hero__stage">
  <div class="hero__globe" aria-hidden="true">
    <div class="hero__globe-frame">
      <div class="hero__globe-map"></div>

      <svg
        class="hero__globe-lines"
        viewBox="0 0 {vbW} {vbH}"
        preserveAspectRatio="none"
        aria-hidden="true"
      >
        {#each arcs as arc (arc.id)}
          <g style={arc.style}>
            <path pathLength="1" d={arc.d} class="hero__line hero__line--halo" />
            <path pathLength="1" d={arc.d} class="hero__line" />
            <path pathLength="1" d={arc.d} class="hero__pulse" />
            <path pathLength="1" d={arc.backD} class="hero__pulse hero__pulse--back" />
          </g>
        {/each}
      </svg>

      {#each REGIONS as r (r.id)}
        <span
          class="hero__pin"
          class:hero__pin--arrival={r.id !== 'gru'}
          class:hero__pin--label-up={r.id === 'fra'}
          style="left: {(r.x / vbW) * 100}%; top: {(r.y / vbH) * 100}%;"
          title={regionLabel[r.id] ?? r.city}
        >
          <span class="hero__pin-ring"></span>
          <span class="hero__pin-core"></span>
          <span class="hero__pin-label">
            {#if r.id === 'gru' || r.id === 'sjc' || r.id === 'fra'}<RegionFlag code={r.id} />{/if}
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
  </div>
  </div>

  <ul class="hero__highlights" aria-label={$dict.hero.highlightsAria}>
    {#each highlights as h (h.label)}
      <li class="hero__highlight">
        <Icon name={h.icon} className="hero__highlight-icon" />
        <span>{h.label}</span>
      </li>
    {/each}
  </ul>
</section>

<style>
  .hero {
    position: relative;
    isolation: isolate;
    overflow: hidden;
    margin-top: -60px;
    padding: 84px 0 72px;
    display: grid;
    justify-items: center;
    gap: 32px;
  }

  /* The map and the copy share one grid cell; the chips sit below the map. */
  .hero__stage {
    display: grid;
    width: 100%;
  }

  .hero__stage > * {
    grid-area: 1 / 1;
  }

  .hero__globe {
    z-index: -1;
    display: grid;
    place-items: center;
    pointer-events: none;
  }

  /* The map spans the viewport width (capped), so it is never cropped. */
  .hero__globe-frame {
    position: relative;
    width: min(100% - 32px, 1600px);
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
    animation: ts-map-in 1.2s ease-out both;
  }

  @keyframes ts-map-in {
    from { opacity: 0; scale: 0.985; }
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
    animation: ts-line-draw 1.6s cubic-bezier(0.65, 0, 0.35, 1) var(--draw-delay) forwards;
  }

  .hero__line--halo {
    stroke-width: 0.9;
    opacity: 0.18;
  }

  @keyframes ts-line-draw {
    to { stroke-dashoffset: 0; }
  }

  /* A short bright dash that travels the arc; the gap (2) is longer than the
     path (1), so only one dash is ever on screen. */
  .hero__pulse {
    fill: none;
    stroke: var(--ts-primary-light);
    stroke-width: 0.4;
    stroke-linecap: round;
    stroke-dasharray: 0.07 2;
    stroke-dashoffset: 0.07;
    opacity: 0;
    animation: ts-pulse 3.6s cubic-bezier(0.45, 0, 0.55, 1) var(--pulse-delay) infinite;
  }

  .hero__pulse--back {
    animation-delay: calc(var(--pulse-delay) + 1.8s);
  }

  @keyframes ts-pulse {
    0% { stroke-dashoffset: 0.07; opacity: 0; }
    8%, 44% { opacity: 1; }
    50%, 100% { stroke-dashoffset: -1; opacity: 0; }
  }

  .hero__pin {
    position: absolute;
    width: 10px;
    height: 10px;
    translate: -50% -50%;
    border-radius: 50%;
    background: var(--ts-primary);
    box-shadow: 0 0 0 3px var(--ts-bg-base), 0 0 16px 2px rgba(var(--ts-primary-rgb), 0.55);
    pointer-events: none;
    /* gru pops in as the arcs start, sjc and fra as they land. */
    --pin-delay: 0.3s;
    animation: ts-pin-pop 0.6s cubic-bezier(0.34, 1.56, 0.64, 1) var(--pin-delay) both;
  }

  .hero__pin--arrival {
    --pin-delay: 1.8s;
  }

  @keyframes ts-pin-pop {
    from { opacity: 0; scale: 0; }
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
    animation: ts-pin-ping 2.4s cubic-bezier(0.2, 0.8, 0.2, 1) var(--pin-delay) infinite;
  }

  @keyframes ts-pin-ping {
    0% { opacity: 0.9; scale: 0.6; }
    70%, 100% { opacity: 0; scale: 1.8; }
  }

  /* Labels sit left of the pins so gru's clears the hero CTAs. */
  .hero__pin-label {
    position: absolute;
    right: 16px;
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

  /* fra sits right behind the headline, so its label goes above the pin. */
  .hero__pin--label-up .hero__pin-label {
    right: auto;
    left: 50%;
    top: auto;
    bottom: 26px;
    translate: -50% 0;
  }

  .hero__inner {
    position: relative;
    z-index: 1;
    align-self: start;
    padding-top: clamp(24px, 6vw, 88px);
    width: 100%;
    padding-inline: clamp(16px, 2.5vw, 40px);
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

  /* The map and arcs run behind this copy: brighter text plus a soft scrim
     halo keeps it above WCAG AA over the dots. */
  .hero__subtitle {
    font-family: var(--ts-font-accent);
    font-size: 18px;
    color: color-mix(in srgb, var(--ts-text) 88%, transparent);
    line-height: 1.65;
    max-width: 60ch;
    margin: 0;
    font-weight: 450;
    text-shadow:
      0 0 2px var(--ts-bg-base),
      0 0 8px var(--ts-bg-base),
      0 0 18px var(--ts-bg-base);
  }

  .hero__highlights {
    list-style: none;
    margin: 0;
    padding: 0 16px;
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
      padding: 76px 0 56px;
      gap: 24px;
    }
    /* Edge to edge on phones, behind the brand and headline. */
    .hero__globe {
      place-items: start center;
    }
    .hero__globe-frame {
      width: 100%;
    }
    .hero__pin-label { display: none; }
    .hero__brand-mark {
      width: 32px;
      height: 32px;
    }
    .hero__brand-word {
      font-size: 24px;
    }
    .hero__subtitle {
      font-size: 15px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .hero__line {
      animation: none;
      stroke-dashoffset: 0;
    }
    .hero__globe-map,
    .hero__pin {
      animation: none;
    }
    .hero__pulse {
      display: none;
    }
    .hero__pin-ring {
      animation: none !important;
      display: none;
    }
  }
</style>
