<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
</script>

<section class="cta">
  <div class="cta__inner">
    <div class="cta__badge">
      <span class="pulse-dot cta__badge-dot"></span>
      Free 30-day trial
    </div>

    <h2 class="cta__headline">
      Ship your next product<br />
      <span class="ts-text-gradient">on infrastructure that scales.</span>
    </h2>
    <p class="cta__text">
      Join thousands of developers deploying to TerraScale. Set up in minutes, scale to millions.
    </p>

    <div class="cta__actions">
      <RouteLink href="/contact" class="ts-btn ts-btn-primary cta-shine cta__primary">
        Start Building Now
        <Icon name="arrowRight" className="cta__icon" />
      </RouteLink>
      <RouteLink href="/contact" class="ts-btn ts-btn-glass cta__secondary">
        <Icon name="messageSquare" className="cta__icon" />
        Talk to Sales
      </RouteLink>
    </div>
  </div>
</section>

<style>
  .cta {
    position: relative;
    padding: 120px 0;
    overflow: hidden;
  }

  .cta__inner {
    position: relative;
    max-width: 900px;
    margin: 0 auto;
    padding: 0 40px;
    text-align: center;
  }

  .cta__badge {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 6px 14px;
    border-radius: 4px;
    margin-bottom: 24px;
    background: var(--ts-glass-bg);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    border: 1px solid var(--ts-glass-border);
    color: var(--ts-text);
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.14em;
  }

  .cta__badge-dot {
    width: 6px;
    height: 6px;
    border-radius: 3px;
    background: var(--ts-secondary);
    box-shadow: 0 0 8px var(--ts-secondary);
    display: inline-block;
  }

  .cta__headline {
    font-size: clamp(2rem, 3vw + 0.5rem, 3.5rem);
    font-weight: 700;
    color: var(--ts-text);
    line-height: 1.1;
    letter-spacing: -0.025em;
    margin: 0 0 22px;
  }

  .cta__text {
    font-family: var(--ts-font-accent);
    font-size: 19px;
    color: var(--ts-text-muted);
    line-height: 1.65;
    max-width: 640px;
    margin: 0 auto 40px;
  }

  .cta__actions {
    display: flex;
    justify-content: center;
    gap: 14px;
    flex-wrap: wrap;
  }

  :global(.cta__primary) {
    padding: 16px 36px;
    box-shadow: 0 6px 22px rgba(var(--ts-primary-rgb), 0.45);
  }

  :global(.cta__secondary) {
    padding: 16px 36px;
  }

  :global(.cta__icon) {
    width: 15px;
    height: 15px;
  }

  @media (max-width: 720px) {
    .cta {
      padding: 80px 0;
    }
    .cta__inner {
      padding: 0 20px;
    }
  }
</style>
