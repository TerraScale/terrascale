<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import HeroMap from '@/components/HeroMap.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'
</script>

<section class="hero">
  <div class="ts-container hero__inner">
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
      {$dict.hero.headline}
    </h1>

    <div class="hero__cta">
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary hero__cta-btn">
        {$dict.hero.cta}
        <Icon name="arrowRight" className="hero__cta-icon" />
      </ExternalLink>
    </div>
  </div>

  <div class="ts-container hero__world">
    <HeroMap />
  </div>
</section>

<style>
  .hero {
    --hero-gap: 16px;
    padding-block: 24px;
    display: grid;
    justify-items: center;
    gap: 24px;
  }

  .hero__inner {
    display: grid;
    justify-items: center;
    gap: var(--hero-gap);
    text-align: center;
  }

  .hero__world {
    min-width: 0;
  }

  .hero__brand {
    display: flex;
    align-items: center;
    gap: clamp(12px, 1.2vw, 18px);
  }

  .hero__brand-mark {
    width: auto;
    height: 44px;
    display: block;
  }

  .hero__brand-word {
    font-size: 34px;
    font-weight: 600;
    line-height: 1;
    letter-spacing: -0.03em;
    color: var(--ts-text);
  }

  .hero__headline {
    max-width: 100%;
    font-size: clamp(36px, 5vw, 60px);
    font-weight: 600;
    line-height: 1.02;
    margin: 0;
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

  @media (prefers-reduced-motion: reduce) {
    :global(.hero__cta-btn),
    :global(.hero__cta-icon) {
      transition: none;
    }
    :global(.hero__cta-btn:hover),
    :global(.hero__cta-btn:active),
    :global(.hero__cta-btn:hover .hero__cta-icon) {
      transform: none;
    }
  }
</style>
