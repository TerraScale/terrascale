<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'

  const stats = [
    { v: '99.99%', l: 'Uptime' },
    { v: '20ms', l: 'Latency' },
    { v: '1M+', l: 'Queries/sec' },
    { v: '20+', l: 'Regions' },
  ]
</script>

<section class="hero">
  <div
    class="hero-atmo-overlay hero-atmo-glow"
    aria-hidden="true"
  ></div>
  <div class="hero-grid-overlay" aria-hidden="true"></div>
  <div class="hero-scan-overlay" aria-hidden="true"></div>

  <div class="hero__inner">
    <div class="hero__badge">
      <span class="pulse-dot hero__badge-dot"></span>
      Database as a Service
    </div>

    <h1 class="hero__headline">
      Scale your data,<br />
      <span class="ts-text-gradient">effortlessly.</span>
    </h1>

    <p class="hero__subtitle">
      Production-grade database infrastructure with sub-20ms latency.
    </p>

    <div class="hero__ctas">
      <RouteLink href="/contact" class="ts-btn ts-btn-primary cta-shine">
        Start Free
        <Icon name="arrowRight" className="hero__cta-icon" />
      </RouteLink>
      <ExternalLink
        href="https://docs.terrascale.tech"
        target="_blank"
        rel="noopener noreferrer"
        class="ts-btn ts-btn-glass"
      >
        View Docs
      </ExternalLink>
    </div>

    <div class="hero__stats" id="stats">
      {#each stats as s (s.l)}
        <div class="hero__stat">
          <div class="hero__stat-value ts-text-gradient">{s.v}</div>
          <div class="hero__stat-label">{s.l}</div>
        </div>
      {/each}
    </div>
  </div>
</section>

<style>
  .hero {
    position: relative;
    overflow: hidden;
    min-height: calc(100vh - 72px);
    display: flex;
    align-items: center;
  }

  .hero-atmo-glow {
    background:
      radial-gradient(ellipse 80% 60% at 50% 0%, var(--ts-hero-glow-top) 0%, transparent 65%),
      radial-gradient(ellipse 55% 45% at 15% 35%, var(--ts-hero-glow-mid) 0%, transparent 55%),
      radial-gradient(ellipse 55% 45% at 85% 60%, var(--ts-hero-glow-mid) 0%, transparent 55%),
      radial-gradient(ellipse 45% 40% at 50% 85%, var(--ts-hero-glow-accent) 0%, transparent 55%);
  }

  .hero__inner {
    position: relative;
    z-index: 1;
    width: 100%;
    max-width: 1240px;
    margin: 0 auto;
    padding: 60px 40px;
    text-align: center;
  }

  .hero__badge {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 32px;
    padding: 6px 14px;
    border-radius: 100px;
    background: rgba(var(--ts-primary-rgb), 0.08);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.22);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 500;
    letter-spacing: 0.08em;
    color: var(--ts-primary-light);
    text-transform: uppercase;
  }

  .hero__badge-dot {
    width: 6px;
    height: 6px;
    border-radius: 3px;
    background: var(--ts-secondary);
    box-shadow: 0 0 8px var(--ts-secondary);
  }

  .hero__headline {
    font-size: clamp(2.8rem, 5vw + 1rem, 6rem);
    font-weight: 700;
    line-height: 1.02;
    margin: 0 0 24px;
    color: var(--ts-text);
    letter-spacing: -0.035em;
  }

  .hero__subtitle {
    font-family: var(--ts-font-accent);
    font-size: clamp(1rem, 0.8vw + 0.75rem, 1.2rem);
    color: var(--ts-text-muted);
    line-height: 1.55;
    max-width: 560px;
    margin: 0 auto 44px;
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

  .hero__stats {
    margin-top: 96px;
    display: flex;
    justify-content: center;
    gap: 56px;
    flex-wrap: wrap;
    border-top: 1px solid var(--ts-glass-border);
    padding-top: 40px;
  }

  .hero__stat-value {
    font-size: 32px;
    font-weight: 700;
    line-height: 1;
    letter-spacing: -0.02em;
    font-family: var(--ts-font-mono);
  }

  .hero__stat-label {
    font-family: var(--ts-font-accent);
    font-size: 10px;
    color: var(--ts-text-muted);
    margin-top: 6px;
    text-transform: uppercase;
    letter-spacing: 0.16em;
    font-weight: 500;
  }

  @media (max-width: 720px) {
    .hero__inner {
      padding: 48px 20px;
    }
    .hero__stats {
      gap: 32px;
      margin-top: 64px;
    }
  }
</style>
