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

<section class="ts-section faq">
  <div class="ts-container faq__inner">
    <div class="faq__head">
      <h2 class="faq__title">{$dict.pricing.faq.titleLead} <span class="ts-h-muted">{$dict.pricing.faq.titleAccent}</span></h2>
      <p class="ts-lede faq__subtitle">{$dict.pricing.faq.subtitle}</p>
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
      <div class="ts-actions">
        <RouteLink href="/contact" class="ts-btn ts-btn-primary">
          <Icon name="messageSquare" className="faq__footer-icon" />
          {$dict.pricing.faq.contactSales}
        </RouteLink>
        <ExternalLink
          href="https://docs.terrascale.tech"
          target="_blank"
          class="ts-btn ts-btn-secondary"
        >
          <Icon name="bookOpen" className="faq__footer-icon" />
          {$dict.pricing.faq.readDocs}
        </ExternalLink>
      </div>
    </div>
  </div>
</section>

<style>
  .faq__inner {
    display: grid;
    gap: var(--ts-s8);
  }

  .faq__head {
    display: grid;
    gap: var(--ts-s4);
    max-width: var(--ts-prose-max);
  }

  .faq__title {
    font-size: var(--ts-fs-4xl);
  }

  .faq__subtitle {
    margin-top: 0;
  }

  /* Flat accordion: hairlines between questions, no boxes. */
  .faq__list {
    border-top: 1px solid var(--ts-line);
  }

  .faq__item {
    border-bottom: 1px solid var(--ts-line);
  }

  .faq__button {
    width: 100%;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--ts-s4);
    min-height: 56px;
    padding: var(--ts-s3) 0;
    border: 0;
    background: transparent;
    color: var(--ts-ink);
    font: 500 var(--ts-fs-lg) / var(--ts-lh) var(--ts-font-sans);
    text-align: left;
    cursor: pointer;
  }

  .faq__button:hover {
    color: var(--ts-accent);
  }

  .faq__chev {
    display: inline-flex;
    color: var(--ts-ink-3);
    transition: transform var(--ts-base) var(--ts-ease);
  }

  .faq__chev.is-open {
    transform: rotate(180deg);
  }

  .faq__chev :global(.faq__chev-icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .faq__panel {
    max-height: 0;
    overflow: hidden;
    transition: max-height var(--ts-base) var(--ts-ease);
  }

  .faq__panel.is-open {
    max-height: 320px;
  }

  .faq__panel-inner {
    padding: 0 0 var(--ts-s5);
    max-width: var(--ts-prose-max);
    color: var(--ts-ink-2);
    line-height: var(--ts-lh-prose);
  }

  .faq__footer {
    display: grid;
    gap: var(--ts-s3);
    justify-items: start;
  }

  .faq__footer-title {
    font-size: var(--ts-fs-xl);
  }

  .faq__footer-text {
    color: var(--ts-ink-2);
  }

  .faq__footer :global(.faq__footer-icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  @media (max-width: 640px) {
    .faq__title {
      font-size: var(--ts-fs-3xl);
    }
  }
</style>
