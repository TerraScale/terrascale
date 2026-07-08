<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import type { AppIconName } from '@/lib/icons'

  type SecurityItem = {
    icon: AppIconName
    title: string
    text: string
  }

  const controls: SecurityItem[] = [
    {
      icon: 'shieldCheck',
      title: 'TLS everywhere',
      text: 'Public service traffic is expected to use HTTPS/TLS, and local development follows the same boundary through generated service endpoints.',
    },
    {
      icon: 'database',
      title: 'Per-project database isolation',
      text: 'Project data is separated at the database boundary so teams can reason about tenancy, access, and backup scope one project at a time.',
    },
    {
      icon: 'code2',
      title: 'API keys and M2M access',
      text: 'Programmatic access uses scoped API keys and machine-to-machine credentials designed for rotation instead of long-lived shared secrets.',
    },
    {
      icon: 'users',
      title: 'Owner-only admin controls',
      text: 'Administrative capabilities are intended to be enforced by backend owner/admin checks, not by hidden navigation or client-only state.',
    },
    {
      icon: 'clock',
      title: 'Backups and restore',
      text: 'Backup workflows are designed around recoverability, including side-by-side restore so teams can inspect recovered data before cutover.',
    },
  ]
</script>

<section class="security-hero">
  <div class="security-hero__inner">
    <div class="security-hero__eyebrow">
      <Icon name="shieldCheck" className="security-hero__eyebrow-icon" />
      Security
    </div>
    <h1 class="security-hero__title">
      Security controls<br />
      <span class="ts-text-gradient">described plainly.</span>
    </h1>
    <p class="security-hero__subtitle">
      TerraScale's security posture is built around encrypted transport, project isolation,
      scoped credentials, backend-enforced administration, and practical recovery workflows. We
      keep this page factual and do not claim compliance certifications that are not published.
    </p>
  </div>
</section>

<section class="security-controls">
  <div class="security-controls__inner">
    <div class="security-controls__grid">
      {#each controls as control (control.title)}
        <article class="security-card ts-glass">
          <div class="security-card__icon">
            <Icon name={control.icon} className="security-card__icon-svg" />
          </div>
          <h2 class="security-card__title">{control.title}</h2>
          <p class="security-card__text">{control.text}</p>
        </article>
      {/each}
    </div>
  </div>
</section>

<section class="security-practice">
  <div class="security-practice__inner">
    <div class="security-practice__content">
      <div class="security-practice__eyebrow">Operating model</div>
      <h2 class="security-practice__title">Designed for narrow access and recoverable changes.</h2>
      <p class="security-practice__text">
        TerraScale treats production access as an explicit operational boundary. Customer-facing
        product flows should expose only the data required for the signed-in user or owning project,
        while sensitive operations stay behind backend authorization checks and auditable service
        paths.
      </p>
    </div>
    <div class="security-practice__panel ts-glass">
      <div class="security-practice__row">
        <Icon name="checkCircle" className="security-practice__row-icon" />
        Rotate API keys and machine credentials when ownership or deployment scope changes.
      </div>
      <div class="security-practice__row">
        <Icon name="checkCircle" className="security-practice__row-icon" />
        Restore backups beside existing environments before promoting recovered data.
      </div>
      <div class="security-practice__row">
        <Icon name="checkCircle" className="security-practice__row-icon" />
        Keep owner-only controls enforced by server-side authorization.
      </div>
    </div>
  </div>
</section>

<section class="security-cta">
  <div class="security-cta__inner ts-glass">
    <div class="security-cta__content">
      <h2 class="security-cta__title">Need a security review?</h2>
      <p class="security-cta__text">
        Contact TerraScale with your architecture, region, and access-control requirements. We can
        walk through the current product surface and the controls that apply to your workload.
      </p>
    </div>
    <div class="security-cta__action">
      <RouteLink href="/contact" class="ts-btn ts-btn-primary cta-shine">
        Contact Security
        <Icon name="arrowRight" className="security-cta__icon" />
      </RouteLink>
    </div>
  </div>
</section>

<style>
  .security-hero {
    position: relative;
    overflow: hidden;
    padding: 112px 0 76px;
  }

  .security-hero::before {
    content: '';
    position: absolute;
    inset: 0;
    pointer-events: none;
    opacity: 0.55;
    background-image:
      linear-gradient(var(--ts-grid-color) 1px, transparent 1px),
      linear-gradient(90deg, var(--ts-grid-color) 1px, transparent 1px);
    background-size: 32px 32px;
  }

  .security-hero__inner,
  .security-controls__inner,
  .security-practice__inner,
  .security-cta__inner {
    position: relative;
    max-width: 1120px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .security-hero__inner {
    text-align: center;
  }

  .security-hero__eyebrow,
  .security-practice__eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 5px 12px;
    border-radius: 4px;
    background: rgba(var(--ts-primary-rgb), 0.1);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.25);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.14em;
  }

  :global(.security-hero__eyebrow-icon) {
    width: 12px;
    height: 12px;
  }

  .security-hero__title {
    font-size: clamp(2.8rem, 6vw + 0.5rem, 5.4rem);
    font-weight: 700;
    color: var(--ts-text);
    line-height: 0.98;
    letter-spacing: -0.035em;
    margin: 24px 0;
  }

  .security-hero__subtitle,
  .security-card__text,
  .security-practice__text,
  .security-cta__text {
    font-family: var(--ts-font-accent);
    color: var(--ts-text-muted);
    line-height: 1.7;
  }

  .security-hero__subtitle {
    max-width: 820px;
    margin: 0 auto;
    font-size: 18px;
  }

  .security-controls {
    padding: 64px 0 48px;
  }

  .security-controls__grid {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 16px;
  }

  .security-card {
    border-radius: 12px;
    padding: 26px;
    min-height: 236px;
    transition:
      transform 250ms ease,
      box-shadow 250ms ease,
      border-color 250ms ease;
  }

  .security-card:hover {
    transform: translateY(-2px);
    box-shadow:
      0 10px 28px rgba(var(--ts-primary-rgb), 0.18),
      inset 0 1px 0 rgba(255, 255, 255, 0.06);
  }

  .security-card__icon {
    width: 42px;
    height: 42px;
    border-radius: 8px;
    background: rgba(var(--ts-primary-rgb), 0.14);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.3);
    color: var(--ts-primary-light);
    display: flex;
    align-items: center;
    justify-content: center;
    margin-bottom: 18px;
  }

  :global(.security-card__icon-svg) {
    width: 20px;
    height: 20px;
  }

  .security-card__title {
    color: var(--ts-text);
    font-size: 19px;
    font-weight: 700;
    margin: 0 0 10px;
  }

  .security-card__text {
    font-size: 14.5px;
    margin: 0;
  }

  .security-practice {
    padding: 32px 0 64px;
  }

  .security-practice__inner {
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(320px, 0.72fr);
    gap: 36px;
    align-items: center;
  }

  .security-practice__title,
  .security-cta__title {
    color: var(--ts-text);
    font-weight: 700;
    letter-spacing: -0.02em;
    line-height: 1.12;
  }

  .security-practice__title {
    font-size: clamp(1.9rem, 2.2vw + 0.5rem, 3rem);
    margin: 18px 0 16px;
  }

  .security-practice__text {
    font-size: 16px;
    margin: 0;
  }

  .security-practice__panel {
    border-radius: 12px;
    padding: 24px;
    display: grid;
    gap: 14px;
  }

  .security-practice__row {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 10px;
    align-items: start;
    color: var(--ts-text);
    font-family: var(--ts-font-accent);
    font-size: 14px;
    line-height: 1.55;
  }

  :global(.security-practice__row-icon) {
    width: 16px;
    height: 16px;
    color: var(--ts-secondary);
    margin-top: 2px;
  }

  .security-cta {
    padding: 0 0 96px;
  }

  .security-cta__inner {
    border-radius: 16px;
    padding: 36px 40px;
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 28px;
    align-items: start;
    background:
      radial-gradient(ellipse 70% 120% at 0% 0%, rgba(var(--ts-primary-rgb), 0.14), transparent 65%),
      var(--ts-glass-bg);
  }

  .security-cta__content {
    min-width: 0;
  }

  .security-cta__action {
    align-self: end;
  }

  .security-cta__title {
    font-size: clamp(1.6rem, 1.8vw + 0.5rem, 2.3rem);
    margin: 0 0 10px;
  }

  .security-cta__text {
    font-size: 15px;
    margin: 0;
  }

  :global(.security-cta__icon) {
    width: 14px;
    height: 14px;
  }

  @media (max-width: 960px) {
    .security-controls__grid,
    .security-practice__inner,
    .security-cta__inner {
      grid-template-columns: 1fr;
    }
  }

  @media (max-width: 640px) {
    .security-hero {
      padding: 72px 0 56px;
    }
    .security-hero__inner,
    .security-controls__inner,
    .security-practice__inner,
    .security-cta__inner {
      padding-left: 20px;
      padding-right: 20px;
    }
    .security-controls {
      padding: 48px 0 36px;
    }
    .security-cta {
      padding-bottom: 72px;
    }
  }
</style>
