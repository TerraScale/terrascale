<script lang="ts">
  import ExternalLink from '@/components/ExternalLink.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'

  const connections = ['psql', 'HTTP', 'WebSocket', 'C# SDK', 'TypeScript SDK', 'CLI', 'MCP']
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
    margin: 16px 0 0;
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
