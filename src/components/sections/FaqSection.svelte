<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'
  import { publicAlphaCatalog } from '@/lib/site'

  // The home page asks about the product, the pricing page about plans; both share one layout.
  export let source: 'home' | 'pricing' = 'pricing'

  const enterprisePriceUsd = String(publicAlphaCatalog.find((plan) => plan.slug === 'enterprise')?.price)
  $: copy = source === 'home' ? $dict.homeFaq : $dict.pricing.faq
  $: faqs = copy.items.map((item) => ({
    ...item,
    a: item.a.replace('{enterprisePriceUsd}', enterprisePriceUsd),
  }))

  let open = 0

  const toggle = (i: number) => {
    open = open === i ? -1 : i
  }
</script>

<section class="faq" use:reveal>
  <div class="faq__head">
    <span class="ts-eyebrow">{copy.eyebrow}</span>
    <h2 class="faq__title">{copy.titleLead} {copy.titleAccent}</h2>
    <p class="faq__subtitle">{copy.subtitle}</p>
    <h3 class="faq__footer-title">{copy.footerTitle}</h3>
    <p class="faq__footer-text">{copy.footerText}</p>
    <div class="faq__actions">
      <RouteLink href="/contact" class="ts-btn ts-btn-glass">
        <Icon name="messageSquare" className="faq__action-icon" />
        {copy.contactSales}
      </RouteLink>
      <ExternalLink href="https://docs.terrascale.tech" target="_blank" class="ts-btn ts-btn-glass">
        <Icon name="bookOpen" className="faq__action-icon" />
        {copy.readDocs}
      </ExternalLink>
    </div>
  </div>

  <div class="faq__list">
    {#each faqs as faq, i (faq.q)}
      <div class="faq__item">
        <button
          type="button"
          class="faq__button"
          aria-expanded={open === i}
          aria-controls={`faq-panel-${i}`}
          on:click={() => toggle(i)}
        >
          <span>{faq.q}</span>
          <Icon name="chevronDown" className="faq__chev" />
        </button>
        <div id={`faq-panel-${i}`} class="faq__panel" hidden={open !== i}>
          {faq.a}
        </div>
      </div>
    {/each}
  </div>
</section>

<style>
  .faq {
    display: grid;
    grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
    gap: var(--ts-space-16);
    align-items: start;
    padding: var(--ts-space-20) clamp(16px, 2.5vw, 40px);
    border-top: 1px solid var(--ts-divider);
  }

  .faq__head {
    display: grid;
    gap: var(--ts-space-3);
    max-width: 720px;
  }

  .faq__title {
    margin: 0;
    font-size: 40px;
    font-weight: 600;
    line-height: 1.1;
    letter-spacing: -0.035em;
    color: var(--ts-text);
  }

  .faq__subtitle,
  .faq__footer-text {
    margin: 0;
    font-size: 16px;
    line-height: 1.7;
    color: var(--ts-text-muted);
  }

  .faq__footer-title {
    margin: var(--ts-space-4) 0 0;
    font-size: 16px;
    font-weight: 500;
    color: var(--ts-text);
  }

  .faq__actions {
    display: flex;
    flex-wrap: wrap;
    gap: var(--ts-space-2);
    margin-top: var(--ts-space-3);
  }

  :global(.faq__action-icon) {
    width: 14px;
    height: 14px;
  }

  .faq__list {
    border-top: 1px solid var(--ts-divider);
  }

  .faq__item {
    border-bottom: 1px solid var(--ts-divider);
  }

  .faq__button {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--ts-space-4);
    width: 100%;
    padding: var(--ts-space-5) 0;
    border: 0;
    background: transparent;
    color: var(--ts-text);
    font-size: 14px;
    font-weight: 500;
    text-align: left;
    cursor: pointer;
  }

  .faq__button:focus-visible {
    border-radius: var(--ts-radius-sm);
  }

  :global(.faq__chev) {
    width: 16px;
    height: 16px;
    color: var(--ts-text-muted);
    transition: rotate var(--ts-transition-fast);
  }

  .faq__button[aria-expanded='true'] :global(.faq__chev) {
    rotate: 180deg;
  }

  .faq__panel {
    max-width: 70ch;
    padding-bottom: var(--ts-space-5);
    font-size: 14px;
    line-height: 1.7;
    color: var(--ts-text-muted);
  }

  @media (max-width: 1280px) {
    .faq {
      grid-template-columns: minmax(0, 1fr);
      gap: var(--ts-space-10);
    }
  }

  @media (max-width: 640px) {
    .faq {
      padding-block: var(--ts-space-12);
    }

    .faq__title {
      font-size: 24px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.faq__chev) {
      transition: none;
    }
  }
</style>
