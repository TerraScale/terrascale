<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'

  const faqs = [
    {
      q: 'What is a Database Operation?',
      a: 'A Database Operation is any single read, write, update, or delete action against your database. Each query you execute — whether fetching a row, inserting a record, running an aggregate, or replicating to another region — counts as one or more operations depending on complexity.',
    },
    {
      q: 'What if I need more operations or storage?',
      a: `All plans support overage billing at usage rates ($1 per additional GB of storage, $1 per million additional operations). You'll never be cut off mid-request — we'll notify you well before you approach limits so you can upgrade or adjust.`,
    },
    {
      q: 'When can I change my subscription?',
      a: 'Anytime. Upgrades take effect immediately with prorated billing; downgrades apply at the start of your next billing cycle. No lock-in, no cancellation fees.',
    },
    {
      q: 'Can I export my data and leave anytime?',
      a: `Yes — your data is yours. Full exports are available anytime through the dashboard or API, and we never charge a cent to get it out. The only constraint is a per-request rate limit so a runaway export job can't destabilize the cluster for other tenants; there's no cap on total volume and no exit fee. If TerraScale isn't working for you, you leave with everything you put in.`,
    },
    {
      q: 'Do you offer custom enterprise solutions?',
      a: 'Yes. For teams with specific compliance requirements, custom SLAs, or dedicated infrastructure, our Enterprise plan is fully tailored. Contact sales to scope a deployment that fits your needs.',
    },
    {
      q: 'Why do you require a credit card for the free trial?',
      a: `We require a valid card to prevent abuse and ensure serious usage of our infrastructure. You won't be charged during the 30-day trial, and you can cancel anytime. This also makes the transition to a paid plan seamless if you choose to continue.`,
    },
  ]

  let open = 0

  const toggle = (i: number) => {
    open = open === i ? -1 : i
  }
</script>

<section class="faq">
  <div class="faq__inner">
    <div class="faq__head">
      <h2 class="faq__title">Frequently Asked Questions</h2>
      <p class="faq__subtitle">Get answers to common questions about our pricing and plans.</p>
    </div>

    <div class="faq__list">
      {#each faqs as faq, i (faq.q)}
        <div class="faq__item" class:is-open={open === i}>
          <button
            type="button"
            class="faq__button"
            aria-expanded={open === i}
            aria-controls={`faq-panel-${i}`}
            on:click={() => toggle(i)}
          >
            <span>{faq.q}</span>
            <span class="faq__chev" class:is-open={open === i}>
              <Icon name="chevronDown" className="faq__chev-icon" />
            </span>
          </button>
          <div
            id={`faq-panel-${i}`}
            class="faq__panel"
            class:is-open={open === i}
          >
            <div class="faq__panel-inner">{faq.a}</div>
          </div>
        </div>
      {/each}
    </div>

    <div class="faq__footer">
      <h3 class="faq__footer-title">Still have questions?</h3>
      <p class="faq__footer-text">
        Our team is here to help you find the perfect plan for your needs.
      </p>
      <div class="faq__footer-actions">
        <RouteLink href="/contact" class="ts-btn ts-btn-primary ts-btn-compact cta-shine">
          <Icon name="messageSquare" className="faq__footer-icon" />
          Contact Sales
        </RouteLink>
        <ExternalLink
          href="https://docs.terrascale.tech"
          target="_blank"
          rel="noopener noreferrer"
          class="ts-btn ts-btn-glass ts-btn-compact"
        >
          <Icon name="bookOpen" className="faq__footer-icon" />
          Read Documentation
        </ExternalLink>
      </div>
    </div>
  </div>
</section>

<style>
  .faq {
    padding: 100px 0;
  }

  .faq__inner {
    max-width: 880px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .faq__head {
    text-align: center;
    margin-bottom: 48px;
  }

  .faq__title {
    font-size: 38px;
    font-weight: 700;
    color: var(--ts-text);
    letter-spacing: -0.02em;
    margin: 0 0 12px;
  }

  .faq__subtitle {
    font-family: var(--ts-font-accent);
    font-size: 16px;
    color: var(--ts-text-muted);
    margin: 0;
  }

  .faq__list {
    display: grid;
    gap: 10px;
  }

  .faq__item {
    border-radius: 8px;
    overflow: hidden;
    border: 1px solid var(--ts-glass-border);
    background: var(--ts-glass-bg);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    transition: border-color 200ms ease;
  }

  .faq__item.is-open {
    border-color: rgba(var(--ts-primary-rgb), 0.35);
  }

  .faq__button {
    width: 100%;
    padding: 18px 22px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    background: transparent;
    border: none;
    cursor: pointer;
    text-align: left;
    color: var(--ts-text);
    font-family: var(--ts-font-body);
    font-size: 15px;
    font-weight: 600;
  }

  .faq__chev {
    flex-shrink: 0;
    color: var(--ts-text-muted);
    transition:
      transform 220ms cubic-bezier(0.22, 1, 0.36, 1),
      color 220ms ease;
  }

  .faq__chev.is-open {
    transform: rotate(180deg);
    color: var(--ts-primary-light);
  }

  :global(.faq__chev-icon) {
    width: 16px;
    height: 16px;
  }

  .faq__panel {
    max-height: 0;
    overflow: hidden;
    transition: max-height 220ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  .faq__panel.is-open {
    max-height: 320px;
  }

  .faq__panel-inner {
    padding: 0 22px 20px;
    font-family: var(--ts-font-accent);
    font-size: 14px;
    color: var(--ts-text-muted);
    line-height: 1.65;
  }

  .faq__footer {
    margin-top: 40px;
    padding: 28px;
    border-radius: 10px;
    text-align: center;
    background: linear-gradient(
      135deg,
      rgba(var(--ts-primary-rgb), 0.08) 0%,
      transparent 100%
    );
    border: 1px solid rgba(var(--ts-primary-rgb), 0.2);
  }

  .faq__footer-title {
    font-size: 18px;
    font-weight: 700;
    color: var(--ts-text);
    margin: 0 0 6px;
  }

  .faq__footer-text {
    font-family: var(--ts-font-accent);
    font-size: 13px;
    color: var(--ts-text-muted);
    margin: 0 0 18px;
  }

  .faq__footer-actions {
    display: flex;
    gap: 10px;
    justify-content: center;
    flex-wrap: wrap;
  }

  :global(.faq__footer-icon) {
    width: 13px;
    height: 13px;
  }

  @media (max-width: 640px) {
    .faq {
      padding: 64px 0;
    }
    .faq__inner {
      padding: 0 20px;
    }
    .faq__title {
      font-size: 28px;
    }
  }
</style>
