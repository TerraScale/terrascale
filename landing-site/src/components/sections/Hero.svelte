<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import {
    REGIONS,
    WORLD_MAP_VIEWBOX,
  } from '@/lib/world-map.generated'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'

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
      {$dict.hero.headlineAccent}
    </h1>

    <div class="hero__cta">
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary hero__cta-btn">
        {$dict.hero.cta}
        <Icon name="arrowRight" className="hero__cta-icon" />
      </ExternalLink>
    </div>
  </div>

  <!-- A dotted world map laid back in perspective under the copy. Each region's flag stands
       on a pole above its pin, so no copy ever covers a label. -->
  <div class="hero__world" aria-hidden="true">
    <div class="hero__stage">
      <div class="hero__plane" style={planeStyle}>
        <div class="hero__map-fade">
          <div class="hero__map"></div>

        </div>

        {#each REGIONS as r (r.id)}
          {@const code = flagCode(r.id)}
          <span
            class="hero__pin"
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
  </div>

</section>

<style>
  /* The floating nav overlaps the hero, so the map shows through its frost. The first line of
     content sits as far below the nav as the first section of every other page. On landscape
     screens the hero fills the first screen: the copy and spacing shrink on short screens, and
     the map band takes whatever height is left. */
  .hero {
    /* One scene at every width: the plane, its perspective and the band it sits in all scale
       with --plane-w, so the 3D look only changes size. The plane is never wider than this. */
    --plane-max: clamp(640px, 132vw, 2300px);
    --tilt: 58deg;
    --perspective: 0.9;
    /* Fixed headroom above the farthest pin for its flag, so it never reaches the call to action. */
    --flag-room: 56px;
    --hero-gap: clamp(14px, 2.5svh, 24px);
    position: relative;
    isolation: isolate;
    overflow: hidden;
    margin-top: -60px;
    min-height: 100svh;
    padding: calc(60px + clamp(16px, 3svh, 36px)) 0 clamp(24px, 4svh, 72px);
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: var(--hero-gap);
  }

  .hero__inner {
    position: relative;
    z-index: 1;
    width: 100%;
    padding-inline: clamp(16px, 2.5vw, 40px);
    display: grid;
    justify-items: center;
    gap: var(--hero-gap);
    text-align: center;
  }

  .hero__brand {
    display: flex;
    align-items: center;
    gap: clamp(12px, 1.2vw, 18px);
  }

  .hero__brand-mark {
    width: auto;
    height: clamp(52px, min(5.4vw, 6.5svh), 80px);
    display: block;
  }

  .hero__brand-word {
    font-size: clamp(38px, min(4.2vw, 5.2svh), 64px);
    font-weight: 600;
    line-height: 1;
    letter-spacing: -0.03em;
    color: var(--ts-text);
  }

  .hero__headline {
    font-size: clamp(40px, min(6.4vw, 7.5svh), 96px);
    font-weight: 600;
    line-height: 1.02;
    margin: clamp(4px, 1.2svh, 12px) 0 0;
    color: var(--ts-text);
    letter-spacing: -0.035em;
    text-wrap: balance;
    /* The one gradient on the page: on the hero heading text, never on a background. */
    background: linear-gradient(90deg, #fff, #9b9b9b);
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
    /* The gradient spans each line rather than the whole block. */
    -webkit-box-decoration-break: clone;
    box-decoration-break: clone;
  }

  .hero__cta {
    display: grid;
    justify-items: center;
    gap: 12px;
  }

  :global(.hero__cta-btn) {
    height: 40px;
    padding: 0 16px;
    font-size: 16px;
    font-weight: 600;
    transition:
      background-color 300ms cubic-bezier(0.22, 1, 0.36, 1),
      transform 300ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  :global(.hero__cta-btn:hover) {
    transform: translateY(-1px);
  }

  :global(.hero__cta-btn:active) {
    transform: scale(0.98);
  }

  :global(.hero__cta-icon) {
    width: 16px;
    height: 16px;
    transition: transform 300ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  :global(.hero__cta-btn:hover .hero__cta-icon) {
    transform: translateX(2px);
  }

  /* ---------- 3D map ---------- */

  /* The band the map is seen in. The plane's faded far edge reaches up behind the copy; the
     pins and flags stay inside the band. It grows into the height the copy leaves, up to what
     the widest plane needs. */
  .hero__world {
    position: relative;
    width: 100%;
    flex: 1 1 0;
    min-height: 220px;
    max-height: calc(var(--plane-max) * 0.2 + var(--flag-room) + 16px);
    margin-top: -24px;
    container-type: size;
    pointer-events: none;
    animation: ts-world-in 1.6s cubic-bezier(0.2, 0.8, 0.2, 1) both;
  }

  @keyframes ts-world-in {
    from { opacity: 0; translate: 0 48px; }
  }

  /* On screen the pins span about a fifth of the plane's width from top to bottom, so the plane
     is sized to fit them and their flags in the band's height. */
  .hero__stage {
    --plane-w: min(var(--plane-max), (100cqh - var(--flag-room) - 16px) * 5);
    position: absolute;
    inset: 0;
    perspective: calc(var(--plane-w) * var(--perspective));
    perspective-origin: 50% 30%;
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

  .hero__map {
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

  /* A zero-size anchor on the map; the dot and ring lie flat on it, the flag stands up. */
  .hero__pin {
    position: absolute;
    width: 0;
    height: 0;
    transform-style: preserve-3d;
    --pin-delay: 0.3s;
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

  /* Portrait screens are taller than the widest plane needs, so the hero keeps its natural height
     and the band the height of the widest plane instead of filling the screen. */
  @media (orientation: portrait), (max-width: 640px) {
    .hero {
      min-height: 0;
    }
    .hero__world {
      flex: none;
      height: calc(var(--plane-max) * 0.2 + var(--flag-room) + 16px);
      min-height: 0;
    }
  }

  @media (max-width: 640px) {
    .hero {
      --plane-max: 170vw;
      --flag-room: 52px;
      --hero-gap: 20px;
      padding: 84px 0 56px;
    }
    .hero__headline {
      margin-top: 4px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.hero__cta-btn),
    :global(.hero__cta-icon) {
      transition: none;
    }
    :global(.hero__cta-btn:hover),
    :global(.hero__cta-btn:hover .hero__cta-icon) {
      transform: none;
    }
    .hero__world,
    .hero__plane,
    .hero__pin-core,
    .hero__flag-label,
    .hero__flag-pole {
      animation: none;
    }
    .hero__pin-ring {
      display: none;
    }
  }
</style>
