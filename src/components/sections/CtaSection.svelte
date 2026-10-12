<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import ExternalLink from '@/components/ExternalLink.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import { dict } from '@/lib/i18n'
  import { registrationUrl } from '@/lib/site'

  $: perks = $dict.cta.perks
</script>

<section class="ts-section cta">
  <div class="ts-container cta__inner">
      <h2 class="cta__headline">
        {$dict.cta.headlineLead} <span class="ts-h-muted">{$dict.cta.headlineAccent}</span>
      </h2>
      <p class="ts-lede cta__text">
        {$dict.cta.text}
      </p>

      <div class="ts-actions">
        <ExternalLink href={registrationUrl} class="ts-btn ts-btn-primary ts-btn-lg">
          {$dict.cta.ctaPrimary}
          <Icon name="arrowRight" className="cta__icon" />
        </ExternalLink>
        <RouteLink href="/contact" class="ts-btn ts-btn-secondary ts-btn-lg">
          <Icon name="messageSquare" className="cta__icon" />
          {$dict.cta.ctaSecondary}
        </RouteLink>
      </div>

      <ul class="cta__perks" aria-label={$dict.cta.perksAria}>
        {#each perks as p (p)}
          <li>
            <Icon name="checkCircle" className="cta__perk-icon" />
            {p}
          </li>
        {/each}
      </ul>
  </div>
</section>

<style>
  .cta {
    border-top: 1px solid var(--ts-line);
  }

  .cta__inner {
    display: grid;
    gap: var(--ts-s5);
    justify-items: start;
  }

  .cta__headline {
    max-width: 760px;
    font-size: var(--ts-fs-4xl);
  }

  .cta__text {
    margin-top: 0;
  }

  .cta :global(.cta__icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
  }

  .cta__perks {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    gap: var(--ts-s5);
    flex-wrap: wrap;
    font-size: var(--ts-fs-sm);
    color: var(--ts-ink-2);
  }

  .cta__perks li {
    display: inline-flex;
    align-items: center;
    gap: var(--ts-s2);
  }

  .cta__perks :global(.cta__perk-icon) {
    width: var(--ts-icon);
    height: var(--ts-icon);
    color: var(--ts-success);
  }

  @media (max-width: 640px) {
    .cta__headline {
      font-size: var(--ts-fs-3xl);
    }
  }
</style>
