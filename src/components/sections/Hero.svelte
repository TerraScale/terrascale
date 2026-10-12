<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { REGIONS, WORLD_MAP_VIEWBOX } from '@/lib/world-map.generated'
  import { docsLink, registrationUrl } from '@/lib/site'
  import { dict } from '@/lib/i18n'

  const installCommand = 'npm i @terrascale/sdk'
  let copied = false
  let copyTimer: ReturnType<typeof setTimeout> | undefined

  const copyInstall = async () => {
    try {
      await navigator.clipboard.writeText(installCommand)
    } catch {
      return
    }
    copied = true
    clearTimeout(copyTimer)
    copyTimer = setTimeout(() => (copied = false), 1600)
  }
</script>

<section class="hero">
  <div class="hero__map" aria-hidden="true">
    <div class="hero__dots"></div>
    {#each REGIONS as r (r.id)}
      <span
        class="hero__pin"
        style="left: {(r.x / WORLD_MAP_VIEWBOX.width) * 100}%; top: {(r.y / WORLD_MAP_VIEWBOX.height) * 100}%;"
        title={r.city}
      ></span>
    {/each}
  </div>

  <div class="ts-container hero__inner">
    <span class="ts-eyebrow"><span class="ts-dot ts-dot-live"></span>{$dict.hero.eyebrow}</span>
    <h1 class="hero__headline">
      {$dict.hero.headlineLead}
      <span class="ts-h-muted">{$dict.hero.headlineAccent}</span>
    </h1>
    <p class="ts-lede hero__lede">{$dict.hero.subtitle}</p>
    <div class="ts-actions">
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary ts-btn-lg">
        {$dict.hero.ctaPrimary}
        <Icon name="arrowRight" className="hero__icon" />
      </ExternalLink>
      <ExternalLink href={docsLink.href} target="_blank" class="ts-btn ts-btn-secondary ts-btn-lg">
        <Icon name="bookOpen" className="hero__icon" />
        {$dict.hero.ctaSecondary}
      </ExternalLink>
    </div>
    <div class="hero__install">
      <span class="tok-prompt">$</span>
      <code>{installCommand}</code>
      <button
        type="button"
        class="ts-icon-btn"
        aria-label={copied ? $dict.hero.installCopied : $dict.hero.installCopy}
        on:click={copyInstall}
      >
        <Icon name={copied ? 'check' : 'copy'} className="hero__icon-sm" />
      </button>
    </div>
  </div>
</section>

<style>
  /* The floating nav overlaps the hero, so the map shows through its frost. */
  .hero {
    position: relative;
    overflow: hidden;
    margin-top: calc(-1 * (var(--ts-header-h) + var(--ts-s3)));
    padding: calc(var(--ts-header-h) + var(--ts-s20)) 0 var(--ts-s16);
    border-bottom: 1px solid var(--ts-line);
  }

  .hero__map {
    position: absolute;
    top: -48px;
    left: 34%;
    width: 74%;
    aspect-ratio: 119 / 60;
    pointer-events: none;
  }

  /* Dots are a mask, so the direction's line colour paints them. */
  .hero__dots {
    position: absolute;
    inset: 0;
    background: var(--ts-line-strong);
    -webkit-mask: url('/world-map.svg') center / 100% 100% no-repeat;
    mask: url('/world-map.svg') center / 100% 100% no-repeat;
  }

  .hero__pin {
    position: absolute;
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--ts-accent);
    translate: -50% -50%;
  }

  .hero__inner {
    position: relative;
    display: grid;
    gap: var(--ts-s5);
    justify-items: start;
  }

  .hero__headline {
    max-width: 760px;
    font-size: var(--ts-fs-5xl);
    font-weight: var(--ts-display-weight);
    letter-spacing: var(--ts-display-tracking);
    line-height: 1.04;
  }

  .hero__lede {
    max-width: 640px;
    margin-top: 0;
  }

  .hero :global(.hero__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .hero :global(.hero__icon-sm) {
    width: var(--ts-icon-sm);
    height: var(--ts-icon-sm);
  }

  .hero__install {
    display: inline-flex;
    align-items: center;
    gap: var(--ts-s3);
    height: var(--ts-control-h-lg);
    padding: 0 var(--ts-s1) 0 var(--ts-s4);
    border: 1px solid var(--ts-line-strong);
    border-radius: var(--ts-radius);
    background: var(--ts-surface);
    font: 400 var(--ts-fs-sm) / 1 var(--ts-font-mono);
    color: var(--ts-ink);
  }

  .hero__install code {
    font: inherit;
  }

  @media (max-width: 860px) {
    .hero {
      padding: calc(var(--ts-header-h) + var(--ts-s12)) 0 var(--ts-s10);
    }
    .hero__map {
      top: -20px;
      left: -40%;
      width: 180%;
    }
    .hero__headline {
      font-size: var(--ts-fs-4xl);
    }
  }
</style>
