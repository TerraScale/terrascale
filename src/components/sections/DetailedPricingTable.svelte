<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import { dict, locale } from '@/lib/i18n'
  import { localeMeta } from '@/lib/i18n/locales'
  import { pricingCatalog, publicAlphaCatalog } from '@/lib/site'
  import type { PublicAlphaPlanSlug } from '@/lib/site'

  const enterprisePrice = publicAlphaCatalog.find((plan) => plan.slug === 'enterprise')?.price
  const comparePlans = publicAlphaCatalog.filter((plan) => plan.slug !== 'enterprise')
  const hasTrial = (slug: PublicAlphaPlanSlug) => (pricingCatalog.plans.find((plan) => plan.id === slug)?.trialDays ?? 0) > 0

  $: compare = $dict.pricing.compare
  $: rowPrices = [compare.included, compare.hardLimit, `$${enterprisePrice}`]
  $: format = new Intl.NumberFormat(localeMeta($locale).bcp47)
  $: count = (n: number | null) => (n === null ? '—' : format.format(n))
  $: storage = (gb: number | null) => {
    if (gb === null) return '—'
    return gb < 1 ? `${format.format(Math.round(gb * 1000))} MB` : `${format.format(gb)} GB`
  }
  $: limitRows = [
    { label: compare.projects, cells: comparePlans.map((plan) => count(plan.limits.projects)) },
    { label: compare.databases, cells: comparePlans.map((plan) => count(plan.limits.databases)) },
    { label: compare.members, cells: comparePlans.map((plan) => count(plan.limits.members)) },
    { label: compare.storage, cells: comparePlans.map((plan) => storage(plan.limits.storageGb)) },
    { label: compare.operationUnits, cells: comparePlans.map((plan) => count(plan.limits.operationUnitsPerMonth)) },
    { label: compare.trial, cells: comparePlans.map((plan) => (hasTrial(plan.slug) ? compare.trialIncluded : '—')) },
    {
      label: compare.support,
      cells: comparePlans.map((plan) => {
        if (plan.slug === 'starter') return compare.supportStarter
        if (plan.slug === 'pro') return compare.supportPro
        return '—'
      }),
    },
  ]
  $: rules = $dict.pricing.detailed.rows.map((row, i) => ({
    ...row,
    price: rowPrices[i] ?? '',
  }))
</script>

<section id="detailed-pricing" class="detailed">
  <div class="detailed__intro">
    <span class="ts-eyebrow">{compare.eyebrow}</span>
    <h2 class="detailed__title">{compare.title}</h2>
  </div>

  <div class="detailed__wrap">
    <table class="detailed__table">
      <thead>
        <tr>
          <th scope="col">{compare.head}</th>
          {#each comparePlans as plan (plan.slug)}
            <th scope="col">{plan.name}</th>
          {/each}
        </tr>
      </thead>
      <tbody>
        {#each limitRows as row (row.label)}
          <tr>
            <th scope="row">{row.label}</th>
            {#each row.cells as cell}
              <td>{cell}</td>
            {/each}
          </tr>
        {/each}
      </tbody>
    </table>
  </div>

  <div class="detailed__notes">
    <span class="ts-eyebrow">{$dict.pricing.detailed.eyebrow}</span>
    <h3 class="detailed__notes-title">
      {$dict.pricing.detailed.titleLead} {$dict.pricing.detailed.titleAccent}
    </h3>
    <p class="detailed__text">{$dict.pricing.detailed.text}</p>
    <dl class="detailed__rules">
      {#each rules as row (row.item)}
        <div>
          <dt>{row.item}</dt>
          <dd><span class="detailed__price">{row.price} <span>{row.unit}</span></span> {row.details}</dd>
        </div>
      {/each}
    </dl>
  </div>

  <ExternalLink href="https://docs.terrascale.tech/pricing/" target="_blank" class="detailed__docs">
    <Icon name="bookOpen" className="detailed__docs-icon" />
    {$dict.pricing.detailed.docsLink}
    <Icon name="externalLink" className="detailed__docs-icon" />
  </ExternalLink>
</section>

<style>
  .detailed {
    padding: var(--ts-space-20) clamp(16px, 2.5vw, 40px);
    border-top: 1px solid var(--ts-divider);
  }

  .detailed__intro {
    display: grid;
    gap: var(--ts-space-3);
    max-width: 720px;
    margin-bottom: var(--ts-space-10);
  }

  .detailed__title {
    margin: 0;
    font-size: 40px;
    font-weight: 600;
    line-height: 1.1;
    letter-spacing: -0.035em;
    color: var(--ts-text);
  }

  .detailed__text {
    margin: 0;
    font-size: 16px;
    line-height: 1.7;
    color: var(--ts-text-muted);
  }

  .detailed__wrap {
    overflow-x: auto;
  }

  .detailed__table {
    width: 100%;
    border-collapse: collapse;
    font-size: 14px;
  }

  .detailed__table th,
  .detailed__table td {
    padding: var(--ts-space-3) var(--ts-space-4);
    border-bottom: 1px solid var(--ts-divider);
    text-align: right;
    vertical-align: middle;
  }

  .detailed__table th:first-child,
  .detailed__table td:first-child {
    text-align: left;
  }

  .detailed__table thead th {
    padding-block: var(--ts-space-2);
    font: 400 11.5px / 1.5 var(--ts-font-mono);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    color: var(--ts-text-muted);
  }

  .detailed__table tbody th {
    font-weight: 500;
    color: var(--ts-text);
    white-space: nowrap;
  }

  .detailed__table td {
    font-family: var(--ts-font-mono);
    font-variant-numeric: tabular-nums;
    color: var(--ts-text);
    white-space: nowrap;
  }

  .detailed__table tbody tr:hover {
    background: rgb(255 255 255 / 0.05);
  }

  .detailed__notes {
    display: grid;
    gap: var(--ts-space-3);
    max-width: 720px;
    margin-top: var(--ts-space-10);
  }

  .detailed__notes-title {
    margin: 0;
    font-size: 20px;
    font-weight: 600;
    line-height: 1.2;
    color: var(--ts-text);
  }

  .detailed__rules {
    display: grid;
    gap: var(--ts-space-3);
    margin: 0;
    font-size: 14px;
  }

  .detailed__rules div {
    display: grid;
    gap: var(--ts-space-1);
  }

  .detailed__rules dt {
    font-weight: 500;
    color: var(--ts-text);
  }

  .detailed__rules dd {
    margin: 0;
    line-height: 1.7;
    color: var(--ts-text-muted);
  }

  .detailed__price {
    font-family: var(--ts-font-mono);
    font-variant-numeric: tabular-nums;
    color: var(--ts-text);
  }

  .detailed__price span {
    color: var(--ts-text-muted);
  }

  :global(.detailed__docs) {
    display: inline-flex;
    align-items: center;
    gap: var(--ts-space-2);
    margin-top: var(--ts-space-6);
    color: var(--ts-primary-light);
    font-size: 14px;
    font-weight: 500;
  }

  :global(.detailed__docs:hover) {
    text-decoration: underline;
  }

  :global(.detailed__docs-icon) {
    width: 14px;
    height: 14px;
  }

  @media (max-width: 640px) {
    .detailed {
      padding-block: var(--ts-space-12);
    }

    .detailed__title {
      font-size: 24px;
    }

    .detailed__table {
      font-size: 12px;
    }

    .detailed__table th,
    .detailed__table td {
      padding: var(--ts-space-3) var(--ts-space-2);
      white-space: normal;
    }

    .detailed__table td {
      white-space: nowrap;
    }
  }
</style>
