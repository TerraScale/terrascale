<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'

  const itemIcons: AppIconName[] = ['shieldCheck', 'zap', 'trendingUp']
  $: items = $dict.pricing.triad.map((item, i) => ({
    icon: itemIcons[i] ?? 'shieldCheck',
    ...item,
  }))
</script>

<section class="triad">
  <div class="ts-container">
    <div class="triad__grid">
      {#each items as it (it.title)}
        <div class="triad__item">
          <span class="ts-icon-tile"><Icon name={it.icon} /></span>
          <div>
            <div class="triad__title">{it.title}</div>
            <div class="triad__text">{it.text}</div>
          </div>
        </div>
      {/each}
    </div>
  </div>
</section>

<style>
  /* Closes the pricing section on the shared rhythm (see .ts-section in index.css). */
  .triad {
    padding-bottom: var(--ts-s12);
  }

  /* Stat-style row: hairlines above and below, vertical hairlines between items. */
  .triad__grid {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    border-block: 1px solid var(--ts-line);
  }

  .triad__item {
    display: flex;
    gap: var(--ts-s3);
    padding: var(--ts-s5) var(--ts-s6);
    border-left: 1px solid var(--ts-line);
  }

  .triad__item:first-child {
    border-left: 0;
    padding-left: 0;
  }

  /* Title centred on the 32px icon tile; the text hangs under it. */
  .triad__title {
    display: flex;
    align-items: center;
    min-height: 32px;
    font-size: var(--ts-fs-md);
    font-weight: 500;
    color: var(--ts-ink);
  }

  .triad__text {
    margin-top: var(--ts-s1);
    font-size: var(--ts-fs-sm);
    color: var(--ts-ink-2);
  }

  @media (max-width: 860px) {
    .triad {
      padding-bottom: var(--ts-s8);
    }
    .triad__grid {
      grid-template-columns: minmax(0, 1fr);
    }
    .triad__item {
      border-left: 0;
      padding: var(--ts-s4) 0;
    }
    .triad__item + .triad__item {
      border-top: 1px solid var(--ts-line);
    }
  }
</style>
