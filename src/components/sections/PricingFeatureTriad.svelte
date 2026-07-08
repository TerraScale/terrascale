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
  <div class="triad__inner">
    <div class="triad__grid">
      {#each items as it (it.title)}
        <div class="triad__card ts-glass">
          <div class="triad__icon">
            <Icon name={it.icon} className="triad__icon-svg" />
          </div>
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
  .triad {
    padding: 40px 0 80px;
  }

  .triad__inner {
    max-width: 1120px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .triad__grid {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 16px;
  }

  .triad__card {
    display: flex;
    align-items: flex-start;
    gap: 14px;
    padding: 22px;
    border-radius: 10px;
  }

  .triad__icon {
    width: 40px;
    height: 40px;
    border-radius: 6px;
    flex-shrink: 0;
    background: rgba(var(--ts-primary-rgb), 0.14);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.3);
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--ts-primary-light);
  }

  :global(.triad__icon-svg) {
    width: 18px;
    height: 18px;
    color: var(--ts-primary-light);
  }

  .triad__title {
    font-size: 15px;
    font-weight: 700;
    color: var(--ts-text);
    margin-bottom: 4px;
  }

  .triad__text {
    font-family: var(--ts-font-accent);
    font-size: 13px;
    color: var(--ts-text-muted);
    line-height: 1.5;
  }

  @media (max-width: 860px) {
    .triad__inner {
      padding: 0 20px;
    }
    .triad__grid {
      grid-template-columns: 1fr;
    }
  }
</style>
