<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const cardIcons: AppIconName[] = ['database', 'globe', 'shieldCheck', 'activity', 'bot', 'shield']

  $: items = $dict.features.items.map((item, i) => ({
    icon: cardIcons[i] ?? 'zap',
    ...item,
  }))
</script>

<section class="lf ts-section" id="features" use:reveal>
  <div class="ts-container">
    <div class="lf__head">
      <h2 class="ts-section-title">
        {$dict.features.titleLead}<br />
        <span class="lf__accent">{$dict.features.titleAccent}</span>
      </h2>
    </div>

    <ul class="lf__grid">
      {#each items as item (item.headline)}
        <li class="lf__card">
          <span class="lf__icon">
            <Icon name={item.icon} className="lf__icon-svg" />
          </span>
          <h3 class="lf__title">{item.headline}</h3>
          <p class="lf__text">{item.text}</p>
        </li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .lf {
    border-top: 1px solid var(--ts-divider);
  }

  .lf__head {
    max-width: 680px;
    margin: 0 auto var(--ts-section-gap);
    text-align: center;
  }

  .lf__accent {
    color: var(--ts-primary-light);
  }

  .lf__grid {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 20px;
  }

  .lf__card {
    display: flex;
    flex-direction: column;
    gap: 14px;
    padding: 28px 24px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
    box-shadow: 0 1px 2px rgb(16 24 40 / 0.04);
    transition:
      transform 260ms cubic-bezier(0.22, 1, 0.36, 1),
      box-shadow 260ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  .lf__card:hover {
    transform: translateY(-2px);
    box-shadow: 0 12px 28px rgb(16 24 40 / 0.08);
  }

  .lf__icon {
    display: grid;
    place-items: center;
    width: 36px;
    height: 36px;
    border-radius: var(--ts-radius-md);
    background: rgba(var(--ts-primary-light-rgb), 0.09);
    border: 1px solid rgba(var(--ts-primary-light-rgb), 0.22);
    color: var(--ts-primary-light);
  }

  :global(.lf__icon-svg) {
    width: 17px;
    height: 17px;
  }

  .lf__title {
    margin: 0;
    font-size: 17px;
    line-height: 24px;
    font-weight: 600;
    letter-spacing: -0.015em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .lf__text {
    margin: 0;
    font-size: 14px;
    line-height: 1.6;
    color: var(--ts-text-muted);
    text-wrap: pretty;
  }

  @media (max-width: 900px) {
    .lf__grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
  }

  @media (max-width: 640px) {
    .lf__grid {
      grid-template-columns: minmax(0, 1fr);
    }

    .lf__card {
      padding: 22px 20px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .lf__card,
    .lf__card:hover {
      transition: none;
      transform: none;
    }
  }
</style>
