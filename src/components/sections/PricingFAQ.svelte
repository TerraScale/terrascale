<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict } from '@/lib/i18n'
  import { publicAlphaCatalog } from '@/lib/site'

  const enterprisePriceUsd = String(publicAlphaCatalog.find((plan) => plan.slug === 'enterprise')?.price)
  $: faqs = $dict.pricing.faq.items.map((item) => ({
    ...item,
    a: item.a.replace('{enterprisePriceUsd}', enterprisePriceUsd),
  }))

  let open = 0

  const toggle = (i: number) => {
    open = open === i ? -1 : i
  }
</script>

<section class="faq">
  <div class="faq__inner">
    <div class="faq__head">
      <h2 class="faq__title">{$dict.pricing.faq.titleLead} <span class="ts-text-gradient">{$dict.pricing.faq.titleAccent}</span></h2>
      <p class="faq__subtitle">{$dict.pricing.faq.subtitle}</p>
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
      <h3 class="faq__footer-title">{$dict.pricing.faq.footerTitle}</h3>
      <p class="faq__footer-text">
        {$dict.pricing.faq.footerText}
      </p>
      <div class="faq__footer-actions">
        <RouteLink href="/contact" class="ts-btn ts-btn-primary ts-btn-compact cta-shine">
          <Icon name="messageSquare" className="faq__footer-icon" />
          {$dict.pricing.faq.contactSales}
        </RouteLink>
        <ExternalLink
          href="https://docs.terrascale.tech"
          target="_blank"
          class="ts-btn ts-btn-glass ts-btn-compact"
        >
          <Icon name="bookOpen" className="faq__footer-icon" />
          {$dict.pricing.faq.readDocs}
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
