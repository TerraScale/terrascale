<script lang="ts">
  import EditorialHeader from '@/components/sections/EditorialHeader.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'
  import { publicAlphaCatalog } from '@/lib/site'

  const enterprisePriceUsd = String(publicAlphaCatalog.find((plan) => plan.slug === 'enterprise')?.price)
  $: copy = $dict.homeFaq
  $: faqs = copy.items.map((item) => ({
    ...item,
    a: item.a.replace('{enterprisePriceUsd}', enterprisePriceUsd),
  }))

  let open = 0

  const toggle = (i: number) => {
    open = open === i ? -1 : i
  }
</script>

<section class="ed-faq ts-section" use:reveal>
  <div class="ts-container ed-faq__inner">
    <div class="ed-faq__aside">
      <EditorialHeader index="05" lead={copy.titleLead} accent={copy.titleAccent} />
      <RouteLink href="/contact" class="ts-btn ts-btn-glass">
        <Icon name="messageSquare" className="ed-faq__action-icon" />
        {copy.contactSales}
      </RouteLink>
    </div>

    <div class="ed-faq__list">
      {#each faqs as faq, i (faq.q)}
        <div class="ed-faq__item">
          <button
            type="button"
            class="ed-faq__button"
            aria-expanded={open === i}
            aria-controls={`ed-faq-panel-${i}`}
            on:click={() => toggle(i)}
          >
            <span class="ed-faq__q">
              <span class="ed-faq__num">{String(i + 1).padStart(2, '0')}</span>
              <span>{faq.q}</span>
            </span>
            <Icon name="chevronDown" className="ed-faq__chev" />
          </button>
          <div id={`ed-faq-panel-${i}`} class="ed-faq__panel" hidden={open !== i}>
            {faq.a}
          </div>
        </div>
      {/each}
    </div>
  </div>
</section>

<style>
  .ed-faq__inner {
    display: grid;
    grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
    gap: clamp(40px, 6vw, 96px);
    align-items: start;
  }

  .ed-faq__aside {
    display: grid;
    gap: 24px;
    justify-items: start;
    position: sticky;
    top: 96px;
  }

  .ed-faq__aside :global(.ed-head) {
    margin-bottom: 0;
    width: 100%;
  }

  :global(.ed-faq__action-icon) {
    width: 14px;
    height: 14px;
  }

  .ed-faq__list {
    min-width: 0;
  }

  .ed-faq__item {
    border-top: 1px solid var(--ts-divider);
  }

  .ed-faq__item:last-child {
    border-bottom: 1px solid var(--ts-divider);
  }

  .ed-faq__button {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 24px;
    width: 100%;
    padding: 20px 0;
    border: 0;
    background: transparent;
    color: var(--ts-text);
    font-size: 15px;
    font-weight: 500;
    text-align: left;
    cursor: pointer;
  }

  .ed-faq__q {
    display: flex;
    align-items: baseline;
    gap: 16px;
    min-width: 0;
  }

  .ed-faq__num {
    font-family: var(--ts-font-mono);
    font-size: 12px;
    letter-spacing: 0.08em;
    color: var(--ts-text-muted);
    flex: none;
  }

  :global(.ed-faq__chev) {
    width: 16px;
    height: 16px;
    flex-shrink: 0;
    color: var(--ts-text-muted);
    transition: rotate var(--ts-transition-fast);
  }

  .ed-faq__button[aria-expanded='true'] :global(.ed-faq__chev) {
    rotate: 180deg;
    color: var(--ts-primary-light);
  }

  .ed-faq__panel {
    max-width: 70ch;
    padding: 0 0 20px 28px;
    font-size: 14px;
    line-height: 1.7;
    color: var(--ts-text-muted);
    text-wrap: pretty;
  }

  @media (max-width: 1023px) {
    .ed-faq__inner {
      grid-template-columns: minmax(0, 1fr);
      gap: 40px;
    }

    .ed-faq__aside {
      position: static;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.ed-faq__chev) {
      transition: none;
    }
  }
</style>
