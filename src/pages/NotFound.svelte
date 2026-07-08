<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
</script>

<section class="notfound">
  <div class="notfound__glow" aria-hidden="true"></div>
  <div class="notfound__grid" aria-hidden="true"></div>

  <div class="notfound__inner">
    <div class="notfound__eyebrow">
      <Icon name="alertCircle" className="notfound__eyebrow-icon" />
      {$dict.notFound.eyebrow}
    </div>

    <div class="notfound__code ts-text-gradient">404</div>
    <h1 class="notfound__title">{$dict.notFound.title}</h1>
    <p class="notfound__text">
      {$dict.notFound.text}
    </p>

    <div class="notfound__actions">
      <RouteLink href="/" class="ts-btn ts-btn-primary cta-shine">
        <Icon name="home" className="notfound__icon" />
        {$dict.notFound.backHome}
      </RouteLink>
      <RouteLink href="/contact" class="ts-btn ts-btn-glass">
        <Icon name="messageSquare" className="notfound__icon" />
        {$dict.notFound.contactSupport}
      </RouteLink>
    </div>

    <div class="notfound__links">
      <span class="notfound__links-label">{$dict.notFound.linksLabel}</span>
      <RouteLink href="/" class="notfound__link">{$dict.notFound.links.home}</RouteLink>
      <RouteLink href="/pricing" class="notfound__link">{$dict.notFound.links.pricing}</RouteLink>
      <RouteLink href="/contact" class="notfound__link">{$dict.notFound.links.contact}</RouteLink>
    </div>
  </div>
</section>

<style>
  .notfound {
    position: relative;
    overflow: hidden;
    min-height: calc(100vh - 72px);
    display: flex;
    align-items: center;
  }

  .notfound__glow {
    position: absolute;
    inset: 0;
    pointer-events: none;
    background:
      radial-gradient(ellipse 70% 50% at 50% 20%, var(--ts-hero-glow-top) 0%, transparent 60%),
      radial-gradient(ellipse 40% 40% at 80% 60%, var(--ts-hero-glow-accent) 0%, transparent 60%);
  }

  .notfound__grid {
    position: absolute;
    inset: 0;
    opacity: 0.4;
    pointer-events: none;
    background-image:
      linear-gradient(var(--ts-grid-color) 1px, transparent 1px),
      linear-gradient(90deg, var(--ts-grid-color) 1px, transparent 1px);
    background-size: 32px 32px;
  }

  .notfound__inner {
    position: relative;
    z-index: 1;
    max-width: 720px;
    margin: 0 auto;
    padding: 80px 40px;
    text-align: center;
  }

  .notfound__eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 5px 12px;
    border-radius: 4px;
    margin-bottom: 24px;
    background: rgba(var(--ts-primary-rgb), 0.1);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.25);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.14em;
  }

  :global(.notfound__eyebrow-icon) {
    width: 11px;
    height: 11px;
  }

  .notfound__code {
    font-family: var(--ts-font-mono);
    font-size: clamp(6rem, 18vw, 11rem);
    font-weight: 700;
    line-height: 1;
    letter-spacing: -0.04em;
    margin-bottom: 16px;
  }

  .notfound__title {
    font-size: clamp(1.8rem, 3vw + 0.5rem, 2.6rem);
    font-weight: 700;
    color: var(--ts-text);
    letter-spacing: -0.02em;
    line-height: 1.1;
    margin: 0 0 16px;
  }

  .notfound__text {
    font-family: var(--ts-font-accent);
    font-size: 17px;
    color: var(--ts-text-muted);
    line-height: 1.6;
    margin: 0 0 36px;
  }

  .notfound__actions {
    display: flex;
    gap: 12px;
    justify-content: center;
    flex-wrap: wrap;
    margin-bottom: 48px;
  }

  :global(.notfound__icon) {
    width: 14px;
    height: 14px;
  }

  .notfound__links {
    display: inline-flex;
    align-items: center;
    gap: 20px;
    padding-top: 28px;
    border-top: 1px solid var(--ts-glass-border);
    flex-wrap: wrap;
    justify-content: center;
  }

  .notfound__links-label {
    font-family: var(--ts-font-accent);
    font-size: 11px;
    color: var(--ts-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.14em;
    font-weight: 600;
  }

  :global(.notfound__link) {
    font-family: var(--ts-font-accent);
    font-size: 13px;
    font-weight: 600;
    color: var(--ts-primary-light);
    text-decoration: none;
    transition: color 150ms ease;
    position: relative;
  }

  :global(.notfound__link:hover) {
    color: var(--ts-text);
  }

  @media (max-width: 640px) {
    .notfound__inner {
      padding: 48px 20px;
    }
  }
</style>
