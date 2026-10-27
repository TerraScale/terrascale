<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'

  const itemIcons: AppIconName[] = ['database', 'activity', 'shieldCheck']
  $: items = $dict.pricing.triad.map((item, i) => ({
    icon: itemIcons[i] ?? 'shieldCheck',
    ...item,
  }))
</script>

<section class="triad" id="usage">
  <div class="triad__head">
    <span class="ts-eyebrow">{$dict.pricing.usage.eyebrow}</span>
    <h2 class="triad__heading">{$dict.pricing.usage.title}</h2>
  </div>
  <div class="triad__grid">
    {#each items as it (it.title)}
      <article class="triad__card">
        <span class="triad__icon">
          <Icon name={it.icon} className="triad__icon-svg" />
        </span>
        <h3 class="triad__title">{it.title}</h3>
        <p class="triad__text">{it.text}</p>
      </article>
    {/each}
  </div>
</section>

<style>
  /* Closes the pricing section on the shared rhythm (see .ts-section in index.css). */
  .triad {
    padding: var(--ts-space-20) clamp(16px, 2.5vw, 40px);
    border-top: 1px solid var(--ts-divider);
  }

  .triad__head {
    display: grid;
    gap: var(--ts-space-3);
    max-width: 720px;
    margin-bottom: var(--ts-space-10);
  }

  .triad__heading {
    margin: 0;
    font-size: 40px;
    font-weight: 600;
    line-height: 1.1;
    letter-spacing: -0.035em;
    color: var(--ts-text);
  }

  /* Stat-style row: hairlines above and below, vertical hairlines between items. */
  .triad__grid {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    border-top: 1px solid var(--ts-divider);
    border-left: 1px solid var(--ts-divider);
  }

  .triad__card {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: var(--ts-space-3);
    min-width: 0;
    padding: var(--ts-space-8) var(--ts-space-6);
    border-right: 1px solid var(--ts-divider);
    border-bottom: 1px solid var(--ts-divider);
  }

  .triad__icon {
    display: inline-grid;
    place-items: center;
    width: 40px;
    height: 40px;
    border-radius: var(--ts-radius-lg);
    background: color-mix(in oklab, var(--hue) 16%, transparent);
    color: color-mix(in oklab, var(--hue) 60%, var(--ts-text));
  }

  /* Non-semantic icon hues (dark chart series 6, 1, 3); status colours are reserved for status. */
  .triad__card:nth-child(1) { --hue: #565bc3; }
  .triad__card:nth-child(2) { --hue: #4797e4; }
  .triad__card:nth-child(3) { --hue: #00a1dd; }

  :global(.triad__icon-svg) {
    width: 20px;
    height: 20px;
  }

  .triad__title {
    margin: var(--ts-space-2) 0 0;
    font-size: 16px;
    font-weight: 500;
    color: var(--ts-text);
  }

  .triad__text {
    margin: 0;
    font-size: 14px;
    line-height: 1.7;
    color: var(--ts-text-muted);
  }

  @media (max-width: 900px) {
    .triad__grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
  }

  @media (max-width: 640px) {
    .triad {
      padding-block: var(--ts-space-12);
    }

    .triad__head {
      margin-bottom: var(--ts-space-8);
    }

    .triad__heading {
      font-size: 24px;
    }

    .triad__grid {
      grid-template-columns: minmax(0, 1fr);
    }

    .triad__card {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      gap: var(--ts-space-1) var(--ts-space-4);
      padding: var(--ts-space-5) var(--ts-space-4);
    }

    .triad__icon {
      grid-row: span 2;
    }

    .triad__title {
      margin: 0;
      font-size: 14px;
    }

    .triad__text {
      font-size: 13px;
    }
  }
</style>
