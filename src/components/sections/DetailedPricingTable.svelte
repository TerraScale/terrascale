<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict } from '@/lib/i18n'
  import { publicAlphaCatalog } from '@/lib/site'

  const enterprisePrice = publicAlphaCatalog.find((plan) => plan.slug === 'enterprise')?.price
  const rowPrices = ['Included', 'Hard limit', `$${enterprisePrice}`]
  $: rows = $dict.pricing.detailed.rows.map((row, i) => ({
    ...row,
    price: rowPrices[i] ?? '',
  }))
</script>

<section id="detailed-pricing" class="ts-section detailed">
  <div class="ts-container detailed__inner">
    <div class="detailed__intro">
      <span class="ts-eyebrow"><span class="ts-dot" aria-hidden="true"></span>{$dict.pricing.detailed.eyebrow}</span>
      <h2 class="detailed__title">{$dict.pricing.detailed.titleLead} <span class="ts-h-muted">{$dict.pricing.detailed.titleAccent}</span></h2>
      <p class="ts-lede">{$dict.pricing.detailed.text}</p>
    </div>

    <div class="ts-table-wrap">
      <table class="ts-table detailed__table">
        <thead>
          <tr>
            <th scope="col">{$dict.pricing.detailed.headItem}</th>
            <th scope="col">{$dict.pricing.detailed.headPricing}</th>
            <th scope="col">{$dict.pricing.detailed.headDetails}</th>
          </tr>
        </thead>
        <tbody>
          {#each rows as row (row.item)}
            <tr>
              <td class="detailed__item">{row.item}</td>
              <td class="detailed__price"><span class="ts-num">{row.price}</span> <span class="detailed__unit">{row.unit}</span></td>
              <td class="detailed__details">{row.details}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>

    <ExternalLink href="https://docs.terrascale.tech/pricing/" target="_blank" class="ts-btn ts-btn-secondary">
      <Icon name="bookOpen" className="detailed__icon" />
      {$dict.pricing.detailed.docsLink}
      <Icon name="externalLink" className="detailed__icon" />
    </ExternalLink>
  </div>
</section>

<style>
  .detailed {
    border-top: 1px solid var(--ts-line);
  }

  .detailed__inner {
    display: grid;
    gap: var(--ts-s8);
    justify-items: start;
  }

  .detailed__intro {
    display: grid;
    gap: var(--ts-s4);
    max-width: var(--ts-prose-max);
  }

  .detailed__intro :global(.ts-lede) {
    margin-top: 0;
  }

  .detailed__title {
    font-size: var(--ts-fs-4xl);
  }

  .ts-table-wrap {
    width: 100%;
  }

  .detailed__table td {
    height: auto;
    padding-block: var(--ts-s3);
    white-space: normal;
    vertical-align: top;
  }

  .detailed__item {
    font-weight: 500;
  }

  .detailed__price {
    white-space: nowrap !important;
    font-family: var(--ts-font-num);
    color: var(--ts-ink);
  }

  .detailed__unit {
    font-family: var(--ts-font-sans);
    font-size: var(--ts-fs-sm);
    color: var(--ts-ink-3);
  }

  .detailed__details {
    color: var(--ts-ink-2) !important;
    font-size: var(--ts-fs-sm);
  }

  .detailed :global(.detailed__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  @media (max-width: 640px) {
    .detailed__title {
      font-size: var(--ts-fs-3xl);
    }
  }
</style>
