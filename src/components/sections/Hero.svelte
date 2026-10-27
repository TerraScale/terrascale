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
  const flagCodes = ['gru', 'sjc', 'fra'] as const
  type FlagCode = (typeof flagCodes)[number]
  const flagCode = (id: string) => flagCodes.find((code) => code === id) as FlagCode | undefined

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

  // The map plane turns around the middle of the region triangle, which sits on the hero's centre line.
  const pivotX = REGIONS.reduce((sum, r) => sum + r.x, 0) / REGIONS.length / vbW
  const pivotY = REGIONS.reduce((sum, r) => sum + r.y, 0) / REGIONS.length / vbH
  const planeStyle = `--pivot-x: ${(pivotX * 100).toFixed(2)}%; --pivot-y: ${(pivotY * 100).toFixed(2)}%`
</script>

<section class="hero">
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

  <!-- A dotted world map laid back in perspective under the copy. Each region's flag stands
       on a pole above its pin, so no copy ever covers a label. -->
  <div class="hero__world" aria-hidden="true">
    <div class="hero__plane" style={planeStyle}>
      <div class="hero__map-fade">
        <div class="hero__map"></div>

        <svg
          class="hero__lines"
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
      </div>

      {#each REGIONS as r (r.id)}
        {@const code = flagCode(r.id)}
        <span
          class="hero__pin"
          class:hero__pin--arrival={r.id !== 'gru'}
          style="left: {(r.x / vbW) * 100}%; top: {(r.y / vbH) * 100}%; --depth: {(pivotY - r.y / vbH).toFixed(3)};"
          title={regionLabel[r.id] ?? r.city}
        >
          <span class="hero__pin-ring"></span>
          <span class="hero__pin-core"></span>
          {#if code}
            <span class="hero__flag">
              <span class="hero__flag-label"><RegionFlag {code} /></span>
              <span class="hero__flag-pole"></span>
            </span>
          {/if}
        </span>
      {/each}
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
  /* The floating nav overlaps the hero, so the map shows through its frost. The first line of
     content sits as far below the nav as the first section of every other page. */
  .hero {
    /* One scene at every width: the plane, its perspective and the band it sits in all scale
       with --plane-w, so the 3D look only changes size. */
    --plane-w: clamp(640px, 132vw, 2300px);
    --tilt: 58deg;
    --perspective: 0.9;
    /* Fixed headroom above the farthest pin for its flag, so it never reaches the subtitle. */
    --flag-room: 56px;
    position: relative;
    isolation: isolate;
    overflow: hidden;
    margin-top: -60px;
    padding: 96px 0 72px;
    display: grid;
    justify-items: center;
    gap: 24px;
  }

  .hero__inner {
    position: relative;
    z-index: 1;
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
    gap: clamp(12px, 1.2vw, 18px);
  }

  .hero__brand-mark {
    width: auto;
    height: clamp(52px, 5.4vw, 80px);
    display: block;
  }

  .hero__brand-word {
    font-size: clamp(38px, 4.2vw, 64px);
    font-weight: 600;
    line-height: 1;
    letter-spacing: -0.03em;
    color: var(--ts-text);
  }

  .hero__headline {
    font-size: clamp(40px, 6.4vw, 96px);
    font-weight: 600;
    line-height: 1.02;
    margin: 12px 0 0;
    color: var(--ts-text);
    letter-spacing: -0.035em;
    text-wrap: balance;
  }

  .hero__accent {
    color: var(--ts-primary);
  }

  /* The far edge of the map runs under this copy: brighter text plus a soft scrim halo keeps it
     above WCAG AA over the dots. */
  .hero__subtitle {
    font-family: var(--ts-font-accent);
    font-size: 18px;
    color: color-mix(in srgb, var(--ts-text) 88%, transparent);
    line-height: 1.65;
    max-width: 60ch;
    margin: 0;
    font-weight: 450;
    text-wrap: pretty;
    text-shadow:
      0 0 2px var(--ts-bg-base),
      0 0 8px var(--ts-bg-base),
      0 0 18px var(--ts-bg-base);
  }

  /* ---------- 3D map ---------- */

  /* The band the map is seen in. The plane's faded far edge reaches up behind the subtitle; the
     pins and flags stay inside the band. */
  .hero__world {
    position: relative;
    width: 100%;
    height: calc(var(--plane-w) * 0.2 + var(--flag-room) + 16px);
    margin-top: -24px;
    perspective: calc(var(--plane-w) * var(--perspective));
    perspective-origin: 50% 30%;
    pointer-events: none;
    animation: ts-world-in 1.6s cubic-bezier(0.2, 0.8, 0.2, 1) both;
  }

  @keyframes ts-world-in {
    from { opacity: 0; translate: 0 48px; }
  }

  /* The pivot (centre of the region triangle) sits on the band's centre, so the pins stay
     centred while the plane slowly turns like a turntable. */
  .hero__plane {
    position: absolute;
    left: 50%;
    top: calc(50% + var(--flag-room) / 2 - 8px);
    width: var(--plane-w);
    aspect-ratio: 119 / 60;
    transform-origin: var(--pivot-x) var(--pivot-y);
    transform-style: preserve-3d;
    translate: calc(-1 * var(--pivot-x)) calc(-1 * var(--pivot-y));
    transform: rotateX(var(--tilt)) rotateZ(-3deg);
    animation: ts-plane-turn 30s ease-in-out infinite alternate;
  }

  @keyframes ts-plane-turn {
    to { transform: rotateX(var(--tilt)) rotateZ(3deg); }
  }

  /* Fades the far edge and the sides into the canvas. Masking flattens, so the pins stay outside. */
  .hero__map-fade {
    position: absolute;
    inset: 0;
    -webkit-mask: radial-gradient(70% 95% at var(--pivot-x) 78%, #000 42%, transparent 100%);
    mask: radial-gradient(70% 95% at var(--pivot-x) 78%, #000 42%, transparent 100%);
  }

  .hero__map,
  .hero__lines {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
  }

  /* Dots are a mask, so one colour paints the whole map. */
  .hero__map {
    background: rgb(255 255 255 / 0.2);
    -webkit-mask: url(/world-map.svg) center / 100% 100% no-repeat;
    mask: url(/world-map.svg) center / 100% 100% no-repeat;
  }

  .hero__lines {
    overflow: visible;
  }

  .hero__line {
    fill: none;
    stroke: var(--ts-primary-light);
    stroke-width: 0.24;
    stroke-linecap: round;
    stroke-dasharray: 1;
    stroke-dashoffset: 1;
    animation: ts-line-draw 1.6s cubic-bezier(0.65, 0, 0.35, 1) var(--draw-delay) forwards;
  }

  .hero__line--halo {
    stroke: var(--ts-primary);
    stroke-width: 1;
    opacity: 0.22;
  }

  @keyframes ts-line-draw {
    to { stroke-dashoffset: 0; }
  }

  /* A short bright dash that travels the arc; the gap (2) is longer than the
     path (1), so only one dash is ever on screen. */
  .hero__pulse {
    fill: none;
    stroke: #fff;
    stroke-width: 0.45;
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

  /* A zero-size anchor on the map; the dot and ring lie flat on it, the flag stands up. */
  .hero__pin {
    position: absolute;
    width: 0;
    height: 0;
    transform-style: preserve-3d;
    /* gru pops in as the arcs start, sjc and fra as they land. */
    --pin-delay: 0.3s;
  }

  .hero__pin--arrival {
    --pin-delay: 1.8s;
  }

  .hero__pin-core,
  .hero__pin-ring {
    position: absolute;
    left: 0;
    top: 0;
    width: 14px;
    height: 14px;
    margin: -7px 0 0 -7px;
    border-radius: 50%;
  }

  .hero__pin-core {
    background: var(--ts-primary-light);
    box-shadow: 0 0 0 4px rgba(var(--ts-primary-rgb), 0.35), 0 0 22px 4px rgba(var(--ts-primary-rgb), 0.6);
    animation: ts-pin-pop 0.6s cubic-bezier(0.34, 1.56, 0.64, 1) var(--pin-delay) both;
  }

  @keyframes ts-pin-pop {
    from { opacity: 0; scale: 0; }
  }

  .hero__pin-ring {
    border: 2px solid var(--ts-primary-light);
    opacity: 0;
    animation: ts-pin-ping 2.4s cubic-bezier(0.2, 0.8, 0.2, 1) var(--pin-delay) infinite;
  }

  @keyframes ts-pin-ping {
    0% { opacity: 0.9; scale: 0.6; }
    70%, 100% { opacity: 0; scale: 3.2; }
  }

  /* Stands up from the pin to face the viewer. The scale undoes perspective, so far and near
     labels read at the same size: --depth is the pin's distance from the pivot in plane heights. */
  .hero__flag {
    position: absolute;
    left: 0;
    bottom: 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    transform-origin: 50% 100%;
    translate: -50% 0;
    transform:
      rotateX(calc(-1 * var(--tilt)))
      scale(calc(1 + var(--depth) * 60 / 119 * sin(var(--tilt)) / var(--perspective)));
  }

  .hero__flag-label {
    display: inline-flex;
    align-items: center;
    padding: 5px 8px;
    border: 1px solid var(--ts-glass-border);
    border-radius: var(--ts-radius-md);
    background: var(--ts-bg-card);
    box-shadow: 0 8px 24px -8px rgb(0 0 0 / 0.8);
    line-height: 1;
    white-space: nowrap;
    animation: ts-flag-in 0.5s cubic-bezier(0.2, 0.8, 0.2, 1) calc(var(--pin-delay) + 0.25s) both;
  }

  .hero__flag-label :global(.region-flag) {
    gap: 6px;
  }

  .hero__flag-label :global(.region-flag svg) {
    width: 20px;
    height: 15px;
  }

  .hero__flag-label :global(.region-flag > span) {
    font-size: 13px;
    color: var(--ts-text);
  }

  .hero__flag-pole {
    width: 1px;
    height: 24px;
    background: linear-gradient(rgb(255 255 255 / 0.45), var(--ts-primary-light));
    transform-origin: 50% 100%;
    animation: ts-pole-in 0.4s cubic-bezier(0.2, 0.8, 0.2, 1) var(--pin-delay) both;
  }

  @keyframes ts-flag-in {
    from { opacity: 0; translate: 0 10px; }
  }

  @keyframes ts-pole-in {
    from { scale: 1 0; }
  }

  /* ---------- Highlights ---------- */

  .hero__highlights {
    position: relative;
    z-index: 1;
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
    white-space: nowrap;
  }

  :global(.hero__highlight-icon) {
    width: 15px;
    height: 15px;
    color: var(--ts-primary-light);
    display: inline-block;
  }

  @media (max-width: 640px) {
    .hero {
      --plane-w: 170vw;
      --flag-room: 52px;
      padding: 84px 0 56px;
      gap: 20px;
    }
    .hero__inner {
      gap: 20px;
    }
    .hero__headline {
      margin-top: 4px;
    }
    .hero__subtitle {
      font-size: 15px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .hero__world,
    .hero__plane,
    .hero__pin-core,
    .hero__flag-label,
    .hero__flag-pole {
      animation: none;
    }
    .hero__line {
      animation: none;
      stroke-dashoffset: 0;
    }
    .hero__pulse,
    .hero__pin-ring {
      display: none;
    }
  }
</style>
