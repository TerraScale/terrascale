<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'

  type TermSection = {
    title: string
    body: string
  }

  const sections: TermSection[] = [
    {
      title: '1. Service access',
      body: 'TerraScale provides hosted database, authentication, billing, and related developer services. Access may depend on plan limits, region availability, operational safeguards, and the technical requirements of the platform.',
    },
    {
      title: '2. Accounts and administration',
      body: 'Customers are responsible for maintaining accurate account information, protecting credentials, and ensuring that administrators and machine credentials are authorized for the projects they manage.',
    },
    {
      title: '3. Acceptable use',
      body: 'Customers may not use the service to attack, disrupt, reverse engineer, overload, or unlawfully access TerraScale, other customers, third-party systems, or data they do not control.',
    },
    {
      title: '4. Billing and usage',
      body: 'Paid plans may include base fees, metered usage, storage, operations, data transfer, support levels, and other plan-specific charges. Taxes, refunds, trials, credits, and payment failure handling should be reviewed by counsel before publication.',
    },
    {
      title: '5. Customer data and backups',
      body: 'Customers retain responsibility for the data they submit to the service. Backup and restore features are operational recovery tools and should be tested against each customer workload before relying on them for business continuity.',
    },
    {
      title: '6. Availability and support',
      body: 'Availability targets, support channels, response times, maintenance windows, and service credits must be documented in a final service-level agreement or order form before they become binding commitments.',
    },
    {
      title: '7. Changes and termination',
      body: 'TerraScale may need to update features, plans, regions, security controls, or technical requirements. Final terms should define customer notice, export, deletion, suspension, and termination rights.',
    },
    {
      title: '8. Disclaimers and liability',
      body: 'Warranty disclaimers, liability limits, indemnity, governing law, dispute resolution, and regulatory obligations require legal review and must be tailored to the final contracting entity and customer market.',
    },
  ]
</script>

<section class="terms-hero">
  <div class="terms-hero__inner">
    <div class="terms-hero__eyebrow">
      <Icon name="bookOpen" className="terms-hero__eyebrow-icon" />
      Terms
    </div>
    <h1 class="terms-hero__title">
      SaaS terms template<br />
      <span class="ts-text-gradient">for legal review.</span>
    </h1>
    <p class="terms-hero__subtitle">
      This page is a non-final terms skeleton for counsel and product review. It is not legal
      advice, not final legal text, and not a binding agreement until TerraScale publishes approved
      terms through the proper contracting path.
    </p>
  </div>
</section>

<section class="terms-notice">
  <div class="terms-notice__inner ts-glass">
    <Icon name="alertCircle" className="terms-notice__icon" />
    <div>
      <h2 class="terms-notice__title">Template status</h2>
      <p class="terms-notice__text">
        Legal counsel should review every section before publication, including jurisdiction,
        customer data handling, billing language, service-level commitments, and liability limits.
      </p>
    </div>
  </div>
</section>

<section class="terms-body">
  <div class="terms-body__inner">
    <div class="terms-body__sidebar">
      <div class="terms-body__eyebrow">Draft outline</div>
      <p class="terms-body__summary">
        Use this skeleton to align the public site with the product surface while keeping legal
        commitments clearly marked as pending review.
      </p>
      <RouteLink href="/contact" class="ts-btn ts-btn-glass">
        Questions
        <Icon name="messageSquare" className="terms-body__button-icon" />
      </RouteLink>
    </div>

    <div class="terms-list">
      {#each sections as section (section.title)}
        <article class="terms-item ts-glass">
          <h2 class="terms-item__title">{section.title}</h2>
          <p class="terms-item__body">{section.body}</p>
        </article>
      {/each}
    </div>
  </div>
</section>

<style>
  .terms-hero {
    position: relative;
    overflow: hidden;
    padding: 112px 0 64px;
  }

  .terms-hero::before {
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

  .terms-hero__inner,
  .terms-notice__inner,
  .terms-body__inner {
    position: relative;
    max-width: 1120px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .terms-hero__inner {
    text-align: center;
  }

  .terms-hero__eyebrow,
  .terms-body__eyebrow {
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

  :global(.terms-hero__eyebrow-icon) {
    width: 12px;
    height: 12px;
  }

  .terms-hero__title {
    font-size: clamp(2.8rem, 6vw + 0.5rem, 5.2rem);
    font-weight: 700;
    color: var(--ts-text);
    line-height: 0.98;
    letter-spacing: -0.035em;
    margin: 24px 0;
  }

  .terms-hero__subtitle,
  .terms-notice__text,
  .terms-body__summary,
  .terms-item__body {
    font-family: var(--ts-font-accent);
    color: var(--ts-text-muted);
    line-height: 1.7;
  }

  .terms-hero__subtitle {
    max-width: 820px;
    margin: 0 auto;
    font-size: 18px;
  }

  .terms-notice {
    padding: 0 0 48px;
  }

  .terms-notice__inner {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 16px;
    align-items: start;
    border-radius: 14px;
    padding: 24px 28px;
    border-color: rgba(var(--ts-secondary-rgb), 0.28);
    background:
      radial-gradient(ellipse 60% 120% at 0% 0%, rgba(var(--ts-secondary-rgb), 0.12), transparent 65%),
      var(--ts-glass-bg);
  }

  :global(.terms-notice__icon) {
    width: 22px;
    height: 22px;
    color: var(--ts-secondary);
    margin-top: 2px;
  }

  .terms-notice__title {
    color: var(--ts-text);
    font-size: 20px;
    margin: 0 0 6px;
  }

  .terms-notice__text {
    font-size: 15px;
    margin: 0;
  }

  .terms-body {
    padding: 16px 0 96px;
  }

  .terms-body__inner {
    display: grid;
    grid-template-columns: 300px minmax(0, 1fr);
    gap: 28px;
    align-items: start;
  }

  .terms-body__sidebar {
    position: sticky;
    top: 104px;
    border-left: 1px solid rgba(var(--ts-primary-rgb), 0.28);
    padding-left: 18px;
  }

  .terms-body__summary {
    font-size: 14.5px;
    margin: 18px 0 20px;
  }

  :global(.terms-body__button-icon) {
    width: 14px;
    height: 14px;
  }

  .terms-list {
    display: grid;
    gap: 12px;
  }

  .terms-item {
    border-radius: 12px;
    padding: 24px;
  }

  .terms-item__title {
    color: var(--ts-text);
    font-size: 19px;
    font-weight: 700;
    margin: 0 0 10px;
  }

  .terms-item__body {
    font-size: 14.5px;
    margin: 0;
  }

  @media (max-width: 860px) {
    .terms-body__inner {
      grid-template-columns: 1fr;
    }
    .terms-body__sidebar {
      position: static;
    }
  }

  @media (max-width: 640px) {
    .terms-hero {
      padding: 72px 0 48px;
    }
    .terms-hero__inner,
    .terms-notice__inner,
    .terms-body__inner {
      padding-left: 20px;
      padding-right: 20px;
    }
    .terms-notice__inner {
      grid-template-columns: 1fr;
    }
    .terms-body {
      padding-bottom: 72px;
    }
  }
</style>
