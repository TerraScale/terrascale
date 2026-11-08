<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'

  const results = [
    { email: 'growth@example.com', seats: 24 },
    { email: 'team@example.com', seats: 12 },
    { email: 'ops@example.com', seats: 10 },
  ]
</script>

<section class="ht">
  <div class="ts-container ht__inner">
    <h1 class="ht__headline">{$dict.hero.headline}</h1>

    <div class="ht__ctas">
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary ht__cta">
        {$dict.hero.cta}
        <Icon name="arrowRight" className="ht__cta-icon" />
      </ExternalLink>
      <RouteLink href="/pricing" class="ts-btn ts-btn-glass ht__cta">
        {$dict.pricingTeaser.cta}
      </RouteLink>
    </div>

    <div class="ht__panel">
      <div class="ht__chrome">
        <span class="ht__dots" aria-hidden="true">
          <i></i><i></i><i></i>
        </span>
        <span class="ht__file">app — pg.terrascale.tech</span>
        <span class="ht__chip">SQL</span>
      </div>

      <div class="ht__split">
        <pre class="ht__query" aria-label="SQL query"><code><span class="tok-keyword">SELECT</span> email, seats
<span class="tok-keyword">FROM</span> customers
<span class="tok-keyword">WHERE</span> seats &gt;= <span class="tok-num">10</span>
<span class="tok-keyword">ORDER BY</span> seats <span class="tok-keyword">DESC</span>
<span class="tok-keyword">LIMIT</span> <span class="tok-num">25</span>;<span class="ht__caret" aria-hidden="true"></span></code></pre>

        <div class="ht__result">
          <table class="ht__table">
            <thead>
              <tr>
                <th scope="col">email</th>
                <th scope="col">seats</th>
              </tr>
            </thead>
            <tbody>
              {#each results as row (row.email)}
                <tr>
                  <td class="ht__cell-email">{row.email}</td>
                  <td class="ht__cell-seats">{row.seats}</td>
                </tr>
              {/each}
            </tbody>
          </table>
          <p class="ht__status">{results.length} rows · home region gru</p>
        </div>
      </div>
    </div>
  </div>
</section>

<style>
  .ht {
    padding-block: 56px 72px;
  }

  .ht__inner {
    display: grid;
    justify-items: center;
    gap: 24px;
    text-align: center;
  }

  .ht__headline {
    max-width: 820px;
    margin: 0;
    font-size: clamp(36px, 5vw, 60px);
    font-weight: 600;
    line-height: 1.02;
    letter-spacing: -0.035em;
    text-wrap: balance;
    background: linear-gradient(90deg, #fff, #9b9b9b);
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
    -webkit-box-decoration-break: clone;
    box-decoration-break: clone;
  }

  .ht__ctas {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 12px;
  }

  :global(.ht__cta) {
    height: 40px;
    padding: 0 18px;
    font-size: 15px;
    transition:
      background-color 300ms cubic-bezier(0.22, 1, 0.36, 1),
      border-color 300ms cubic-bezier(0.22, 1, 0.36, 1),
      transform 300ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  :global(.ht__cta:hover) {
    transform: translateY(-1px);
  }

  :global(.ht__cta:active) {
    transform: scale(0.98);
  }

  :global(.ht__cta-icon) {
    width: 15px;
    height: 15px;
    transition: transform 300ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  :global(.ht__cta:hover .ht__cta-icon) {
    transform: translateX(2px);
  }

  .ht__panel {
    width: 100%;
    max-width: 880px;
    margin-top: 8px;
    border: 1px solid var(--ts-glass-border);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
    overflow: hidden;
    text-align: left;
    box-shadow: 0 24px 60px rgb(0 0 0 / 0.35);
  }

  .ht__chrome {
    display: flex;
    align-items: center;
    gap: 12px;
    min-height: 44px;
    padding: 0 14px;
    border-bottom: 1px solid var(--ts-divider);
  }

  .ht__dots {
    display: inline-flex;
    gap: 6px;
  }

  .ht__dots i {
    width: 10px;
    height: 10px;
    border-radius: 50%;
    background: var(--ts-divider);
  }

  .ht__file {
    font: 400 12px / 1 var(--ts-font-mono);
    color: var(--ts-text-muted);
  }

  .ht__chip {
    margin-left: auto;
    display: inline-flex;
    align-items: center;
    height: 22px;
    padding: 0 8px;
    border: 1px solid rgba(var(--ts-primary-rgb), 0.35);
    border-radius: var(--ts-radius-sm);
    color: var(--ts-primary-light);
    font: 500 11px / 1 var(--ts-font-mono);
    letter-spacing: 0.04em;
  }

  .ht__split {
    display: grid;
    grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
  }

  .ht__query {
    margin: 0;
    padding: 20px;
    border-right: 1px solid var(--ts-divider);
    overflow-x: auto;
    font: 400 13px / 1.8 var(--ts-font-mono);
    color: var(--ts-text);
    tab-size: 2;
  }

  .ht__caret {
    display: inline-block;
    width: 8px;
    height: 15px;
    margin-left: 2px;
    vertical-align: text-bottom;
    background: var(--ts-primary-light);
    animation: ht-blink 1.1s steps(1) infinite;
  }

  @keyframes ht-blink {
    50% {
      opacity: 0;
    }
  }

  .ht__result {
    min-width: 0;
    display: grid;
    grid-template-rows: 1fr auto;
  }

  .ht__table {
    width: 100%;
    border-collapse: collapse;
    font: 400 13px / 1 var(--ts-font-mono);
  }

  .ht__table th {
    padding: 12px 20px 10px;
    border-bottom: 1px solid var(--ts-divider);
    color: var(--ts-text-muted);
    font-weight: 400;
    text-align: left;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    font-size: 11px;
  }

  .ht__table td {
    padding: 10px 20px;
    color: var(--ts-text);
    white-space: nowrap;
  }

  .ht__table tbody tr + tr td {
    border-top: 1px solid var(--ts-divider);
  }

  .ht__cell-email {
    color: var(--ts-syn-string);
  }

  .ht__cell-seats {
    color: var(--ts-syn-number);
    font-variant-numeric: tabular-nums;
  }

  .ht__status {
    margin: 0;
    padding: 10px 20px;
    border-top: 1px solid var(--ts-divider);
    color: var(--ts-text-muted);
    font: 400 12px / 1 var(--ts-font-mono);
  }

  @media (max-width: 767px) {
    .ht {
      padding-block: 40px 56px;
    }

    .ht__split {
      grid-template-columns: minmax(0, 1fr);
    }

    .ht__query {
      border-right: 0;
      border-bottom: 1px solid var(--ts-divider);
    }

    .ht__table th,
    .ht__table td {
      padding-inline: 14px;
    }

    .ht__status {
      padding-inline: 14px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.ht__cta),
    :global(.ht__cta-icon) {
      transition: none;
    }

    :global(.ht__cta:hover),
    :global(.ht__cta:active),
    :global(.ht__cta:hover .ht__cta-icon) {
      transform: none;
    }

    .ht__caret {
      animation: none;
    }
  }
</style>
