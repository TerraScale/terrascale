<script lang="ts">
  import RegionFlag from '@/components/RegionFlag.svelte'
  import { REGIONS, WORLD_MAP_VIEWBOX } from '@/lib/world-map.generated'

  const { width, height } = WORLD_MAP_VIEWBOX
  const flagCodes = ['gru', 'sjc', 'fra'] as const
</script>

<div class="hero-map" style="aspect-ratio: {width} / {height}">
  <img
    class="hero-map__land"
    src="/world-map.svg"
    alt=""
    width={width}
    height={height}
    decoding="async"
  />
  <ul class="hero-map__regions">
    {#each REGIONS as region, index (region.id)}
      {@const code = flagCodes.find((value) => value === region.id)}
      <li
        class="hero-map__pin"
        style="left: {region.x / width * 100}%; top: {region.y / height * 100}%; --pulse-delay: {-index * 0.8}s"
      >
        <span class="hero-map__ring" aria-hidden="true"></span>
        <span class="hero-map__core" aria-hidden="true"></span>
        <span class="hero-map__label" title={region.city}>
          {#if code}
            <RegionFlag {code} />
          {:else}
            {region.id}
          {/if}
          <span class="ts-sr-only"> — {region.city}</span>
        </span>
      </li>
    {/each}
  </ul>
</div>

<style>
  .hero-map {
    position: relative;
    /* Keep the entire geographic canvas visible, including on short laptops. */
    width: min(100%, var(--ts-prose-max), calc(46svh * 119 / 60));
    margin-inline: auto;
  }

  .hero-map__land {
    width: 100%;
    height: 100%;
    opacity: 0.28;
  }

  .hero-map__regions {
    position: absolute;
    inset: 0;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  .hero-map__pin {
    position: absolute;
    width: 0;
    height: 0;
  }

  .hero-map__core,
  .hero-map__ring {
    position: absolute;
    inset: -5px auto auto -5px;
    width: 10px;
    height: 10px;
    border-radius: 50%;
  }

  .hero-map__core {
    background: var(--ts-primary-light);
    box-shadow: 0 0 0 3px rgb(var(--ts-primary-rgb), 0.25);
  }

  .hero-map__ring {
    border: 1.5px solid var(--ts-primary-light);
    animation: region-pulse 2.8s ease-out var(--pulse-delay) infinite;
  }

  @keyframes region-pulse {
    0% { opacity: 0.85; transform: scale(1); }
    85%, 100% { opacity: 0; transform: scale(3.5); }
  }

  .hero-map__label {
    position: absolute;
    bottom: 14px;
    left: 0;
    transform: translateX(-50%);
    display: inline-flex;
    padding: 5px 7px;
    border: 1px solid var(--ts-glass-border);
    border-radius: var(--ts-radius-sm);
    background: var(--ts-bg-card);
    white-space: nowrap;
  }

  .hero-map__label :global(.region-flag > span) {
    color: var(--ts-text);
    font-size: 13px;
  }

  @media (prefers-reduced-motion: reduce) {
    .hero-map__ring {
      animation: none;
      display: none;
    }
  }
</style>
