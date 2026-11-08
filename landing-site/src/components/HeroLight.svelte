<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'

  const connections = ['psql', 'HTTP', 'WebSocket', 'C# SDK', 'TypeScript SDK', 'CLI', 'MCP']

  const results = [
    { email: 'growth@example.com', seats: 24 },
    { email: 'team@example.com', seats: 12 },
    { email: 'ops@example.com', seats: 10 },
  ]
</script>

<section class="hl">
  <div class="hl__backdrop" aria-hidden="true"></div>

  <div class="ts-container hl__inner">
    <span class="hl__brand">
      <img src="/icon-blue.svg" alt="" class="hl__brand-mark" width="835" height="942" decoding="async" fetchpriority="high" />
      <span class="hl__brand-word">TerraScale</span>
    </span>

    <h1 class="hl__headline">{$dict.hero.headline}</h1>

    <div class="hl__ctas">
      <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary hl__cta">
        {$dict.hero.cta}
        <Icon name="arrowRight" className="hl__cta-icon" />
      </ExternalLink>
      <RouteLink href="/pricing" class="ts-btn ts-btn-glass hl__cta">
        {$dict.pricingTeaser.cta}
      </RouteLink>
    </div>

    <div class="hl__panel">
      <div class="hl__panel-chrome">
        <span class="hl__dots" aria-hidden="true">
          <i></i><i></i><i></i>
        </span>
        <span class="hl__file">app — pg.terrascale.tech</span>
        <span class="hl__chip">SQL</span>
      </div>
      <div class="hl__panel-body">
        <pre class="hl__query" aria-label="SQL query"><code><span class="hlq-keyword">SELECT</span> email, seats
<span class="hlq-keyword">FROM</span> customers
<span class="hlq-keyword">WHERE</span> seats &gt;= <span class="hlq-num">10</span>
<span class="hlq-keyword">ORDER BY</span> seats <span class="hlq-keyword">DESC</span>
<span class="hlq-keyword">LIMIT</span> <span class="hlq-num">25</span>;</code></pre>
        <div class="hl__result">
          <table class="hl__table">
            <thead>
              <tr>
                <th scope="col">email</th>
                <th scope="col">seats</th>
              </tr>
            </thead>
            <tbody>
              {#each results as row (row.email)}
                <tr>
                  <td class="hl__cell-email">{row.email}</td>
                  <td class="hl__cell-seats">{row.seats}</td>
                </tr>
              {/each}
            </tbody>
          </table>
          <p class="hl__status">{results.length} rows · home region gru</p>
        </div>
      </div>
    </div>

    <ul class="hl__strip" aria-label="Ways to connect">
      {#each connections as connection (connection)}
        <li class="hl__strip-item">{connection}</li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .hl {
    position: relative;
    overflow: hidden;
    padding-block: 72px 64px;
  }

  .hl__backdrop {
    position: absolute;
    inset: 0;
    pointer-events: none;
    background:
      radial-gradient(ellipse 70% 55% at 50% -12%, rgb(33 88 232 / 0.09), transparent 70%),
      linear-gradient(rgb(16 24 40 / 0.035) 1px, transparent 1px),
      linear-gradient(90deg, rgb(16 24 40 / 0.035) 1px, transparent 1px);
    background-size:
      auto,
      100% 48px,
      48px 100%;
    -webkit-mask-image: linear-gradient(#000 40%, transparent 96%);
    mask-image: linear-gradient(#000 40%, transparent 96%);
  }

  .hl__inner {
    position: relative;
    display: grid;
    justify-items: center;
    gap: 24px;
    text-align: center;
  }

  .hl__brand {
    display: flex;
    align-items: center;
    gap: clamp(12px, 1.2vw, 18px);
  }

  .hl__brand-mark {
    width: auto;
    height: 40px;
    display: block;
  }

  .hl__brand-word {
    font-size: 30px;
    font-weight: 600;
    line-height: 1;
    letter-spacing: -0.03em;
    color: var(--ts-text);
  }

  .hl__headline {
    max-width: 820px;
    margin: 0;
    font-size: clamp(36px, 5vw, 60px);
    font-weight: 600;
    line-height: 1.02;
    letter-spacing: -0.035em;
    text-wrap: balance;
    background: linear-gradient(90deg, #000, #666);
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
    -webkit-box-decoration-break: clone;
    box-decoration-break: clone;
  }

  .hl__ctas {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 12px;
  }

  :global(.hl__cta) {
    height: 40px;
    padding: 0 18px;
    font-size: 15px;
    transition:
      background-color 300ms cubic-bezier(0.22, 1, 0.36, 1),
      border-color 300ms cubic-bezier(0.22, 1, 0.36, 1),
      transform 300ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  :global(.hl__cta:hover) {
    transform: translateY(-1px);
  }

  :global(.hl__cta:active) {
    transform: scale(0.98);
  }

  :global(.hl__cta-icon) {
    width: 15px;
    height: 15px;
    transition: transform 300ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  :global(.hl__cta:hover .hl__cta-icon) {
    transform: translateX(2px);
  }

  .hl__strip {
    list-style: none;
    margin: 20px 0 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 8px;
    max-width: 720px;
  }

  .hl__strip-item {
    display: inline-flex;
    align-items: center;
    height: 28px;
    padding: 0 12px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-md);
    background: var(--ts-bg-surface);
    color: var(--ts-text-muted);
    font: 500 12px / 1 var(--ts-font-mono);
  }

  .hl__panel {
    width: 100%;
    max-width: 760px;
    margin-top: 16px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
    overflow: hidden;
    text-align: left;
    box-shadow: 0 30px 70px rgb(16 24 40 / 0.12);
  }

  .hl__panel-chrome {
    display: flex;
    align-items: center;
    gap: 12px;
    min-height: 44px;
    padding: 0 14px;
    border-bottom: 1px solid var(--ts-divider);
  }

  .hl__dots {
    display: inline-flex;
    gap: 6px;
  }

  .hl__dots i {
    width: 10px;
    height: 10px;
    border-radius: 50%;
    background: rgb(16 24 40 / 0.12);
  }

  .hl__file {
    font: 400 12px / 1 var(--ts-font-mono);
    color: var(--ts-text-muted);
  }

  .hl__chip {
    margin-left: auto;
    display: inline-flex;
    align-items: center;
    height: 22px;
    padding: 0 8px;
    border: 1px solid rgba(var(--ts-primary-light-rgb), 0.35);
    border-radius: var(--ts-radius-sm);
    color: var(--ts-primary-light);
    font: 500 11px / 1 var(--ts-font-mono);
    letter-spacing: 0.04em;
  }

  .hl__panel-body {
    display: grid;
    grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
  }

  .hl__query {
    margin: 0;
    padding: 18px 20px;
    border-right: 1px solid var(--ts-divider);
    overflow-x: auto;
    font: 400 13px / 1.8 var(--ts-font-mono);
    color: var(--ts-text);
    tab-size: 2;
  }

  .hlq-keyword {
    color: var(--ts-primary-light);
  }

  .hlq-num {
    color: var(--ts-warning);
  }

  .hl__result {
    min-width: 0;
    display: grid;
    grid-template-rows: 1fr auto;
  }

  .hl__table {
    width: 100%;
    border-collapse: collapse;
    font: 400 13px / 1 var(--ts-font-mono);
  }

  .hl__table th {
    padding: 12px 20px 10px;
    border-bottom: 1px solid var(--ts-divider);
    color: var(--ts-text-muted);
    font-weight: 400;
    text-align: left;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    font-size: 11px;
  }

  .hl__table td {
    padding: 10px 20px;
    color: var(--ts-text);
    white-space: nowrap;
  }

  .hl__table tbody tr + tr td {
    border-top: 1px solid var(--ts-divider);
  }

  .hl__cell-email {
    color: var(--ts-secondary);
  }

  .hl__cell-seats {
    color: var(--ts-warning);
    font-variant-numeric: tabular-nums;
  }

  .hl__status {
    margin: 0;
    padding: 10px 20px;
    border-top: 1px solid var(--ts-divider);
    color: var(--ts-text-muted);
    font: 400 12px / 1 var(--ts-font-mono);
  }

  @media (max-width: 640px) {
    .hl {
      padding-block: 48px 48px;
    }

    .hl__brand-mark {
      height: 32px;
    }

    .hl__brand-word {
      font-size: 24px;
    }

    .hl__panel-body {
      grid-template-columns: minmax(0, 1fr);
    }

    .hl__query {
      border-right: 0;
      border-bottom: 1px solid var(--ts-divider);
    }

    .hl__table th,
    .hl__table td,
    .hl__status {
      padding-inline: 14px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.hl__cta),
    :global(.hl__cta-icon) {
      transition: none;
    }

    :global(.hl__cta:hover),
    :global(.hl__cta:active),
    :global(.hl__cta:hover .hl__cta-icon) {
      transform: none;
    }
  }
</style>
