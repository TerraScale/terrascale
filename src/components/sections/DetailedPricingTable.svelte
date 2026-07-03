<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'

  const rows = [
    {
      item: 'Enterprise Level Support',
      price: '$499',
      unit: '/month',
      details: 'Base platform access for Enterprise deployments and advanced support.',
    },
    {
      item: 'Storage Usage',
      price: '$1',
      unit: '/GB',
      details: 'Usage-based storage pricing on top of the base platform fee.',
    },
    {
      item: 'Database Operations',
      price: '$1',
      unit: '/ 1M ops',
      details: 'Scales with workload volume for read, write, and replication activity.',
    },
  ]
</script>

<section id="detailed-pricing" class="detailed">
  <div class="detailed__inner">
    <div class="detailed__intro">
      <div class="detailed__eyebrow">Detailed Pricing</div>

      <h2 class="detailed__title">Clear pricing for <span class="ts-text-gradient">custom deployments</span></h2>
      <p class="detailed__text">
        Usage-based costs, support scopes, and deployment requirements are broken out below so
        enterprise teams can estimate spend before talking to sales.
      </p>
    </div>

    <div class="detailed__table ts-glass">
      <div class="detailed__head">
        <div>Item</div>
        <div>Pricing</div>
        <div>Details</div>
      </div>
      {#each rows as row, i (row.item)}
        <div class="detailed__row" class:is-last={i === rows.length - 1}>
          <div class="detailed__item">{row.item}</div>
          <div class="detailed__price">
            <span class="detailed__price-value ts-text-gradient">{row.price}</span>
            <span class="detailed__price-unit">{row.unit}</span>
          </div>
          <div class="detailed__details">{row.details}</div>
        </div>
      {/each}
    </div>

    <div class="detailed__docs-wrap">
      <ExternalLink
        href="https://docs.terrascale.tech/pricing"
        target="_blank"
        rel="noopener noreferrer"
        class="detailed__docs"
      >
        <Icon name="bookOpen" className="detailed__docs-icon" />
        Full Pricing Docs
        <Icon name="externalLink" className="detailed__docs-icon" />
      </ExternalLink>
    </div>
  </div>
</section>

<style>
  .detailed {
    padding: 60px 0;
  }

  .detailed__inner {
    max-width: 1120px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .detailed__intro {
    text-align: center;
    margin-bottom: 32px;
  }

  .detailed__eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 5px 12px;
    border-radius: 4px;
    margin-bottom: 16px;
    background: rgba(var(--ts-primary-rgb), 0.1);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.25);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-accent);
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.14em;
  }

  .detailed__title {
    font-size: 34px;
    font-weight: 700;
    color: var(--ts-text);
    letter-spacing: -0.02em;
    margin: 0 0 12px;
  }

  .detailed__text {
    font-family: var(--ts-font-accent);
    font-size: 15px;
    color: var(--ts-text-muted);
    line-height: 1.65;
    max-width: 720px;
    margin: 0 auto;
  }

  :global(.detailed__docs) {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 10px 18px;
    border-radius: 6px;
    background: rgba(255, 255, 255, 0.04);
    border: 1px solid var(--ts-glass-border);
    color: var(--ts-text);
    font-family: var(--ts-font-accent);
    font-size: 12px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    text-decoration: none;
    transition:
      border-color 150ms,
      background 150ms;
  }

  .detailed__docs-wrap {
    display: flex;
    justify-content: flex-end;
    margin-top: 24px;
  }

  :global(.detailed__docs:hover) {
    background: rgba(var(--ts-primary-rgb), 0.08);
    border-color: rgba(var(--ts-primary-rgb), 0.35);
  }

  :global(.detailed__docs-icon) {
    width: 13px;
    height: 13px;
  }

  .detailed__table {
    border-radius: 10px;
    overflow: hidden;
    border: 1px solid var(--ts-glass-border);
  }

  .detailed__head {
    display: grid;
    grid-template-columns: 1.2fr 0.8fr 2fr;
    padding: 14px 24px;
    background: rgba(var(--ts-primary-rgb), 0.08);
    border-bottom: 1px solid var(--ts-glass-border);
    font-family: var(--ts-font-accent);
    font-size: 10px;
    font-weight: 700;
    color: var(--ts-primary-light);
    text-transform: uppercase;
    letter-spacing: 0.14em;
  }

  .detailed__row {
    display: grid;
    grid-template-columns: 1.2fr 0.8fr 2fr;
    padding: 20px 24px;
    border-bottom: 1px solid var(--ts-glass-border);
    align-items: center;
  }

  .detailed__row.is-last {
    border-bottom: none;
  }

  .detailed__item {
    font-size: 15px;
    font-weight: 600;
    color: var(--ts-text);
  }

  .detailed__price {
    display: flex;
    align-items: baseline;
    gap: 4px;
  }

  .detailed__price-value {
    font-family: var(--ts-font-mono);
    font-size: 22px;
    font-weight: 700;
  }

  .detailed__price-unit {
    font-family: var(--ts-font-accent);
    font-size: 12px;
    color: var(--ts-text-muted);
  }

  .detailed__details {
    font-family: var(--ts-font-accent);
    font-size: 13px;
    color: var(--ts-text-muted);
    line-height: 1.55;
  }

  @media (max-width: 760px) {
    .detailed__inner {
      padding: 0 20px;
    }
    .detailed__title {
      font-size: 26px;
    }
    .detailed__head {
      display: none;
    }
    .detailed__row {
      grid-template-columns: 1fr;
      gap: 8px;
      padding: 18px 16px;
    }
  }
</style>
