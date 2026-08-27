<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import SectionHeader from '@/components/sections/SectionHeader.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'

  const featureIcons: AppIconName[] = ['zap', 'shield', 'globe', 'trendingUp']
  $: features = $dict.features.items.map((item, i) => ({
    icon: featureIcons[i] ?? 'zap',
    ...item,
  }))
</script>

<section class="features" id="features">
  <div class="features__inner">
    <SectionHeader
      eyebrow={$dict.features.eyebrow}
      subtitle={$dict.features.subtitle}
    >
      <svelte:fragment slot="title">
        {$dict.features.titleLead}<br />
        <span class="ts-text-gradient">{$dict.features.titleAccent}</span>
      </svelte:fragment>
    </SectionHeader>

    <div class="features__grid">
      {#each features as feature (feature.title)}
        <article class="feature-card ts-glass">
          <div class="feature-card__head">
            <div class="feature-card__icon">
              <Icon name={feature.icon} className="feature-card__icon-svg" />
            </div>
            <h3 class="feature-card__title">{feature.title}</h3>
          </div>

          <p class="feature-card__text">{feature.text}</p>
          <div class="feature-card__bullets">
            {#each feature.bullets as bullet (bullet)}
              <span class="feature-card__bullet">
                <span class="feature-card__bullet-dot"></span>
                {bullet}
              </span>
            {/each}
          </div>
        </article>
      {/each}
    </div>
  </div>
</section>

<style>
  .features {
    padding: 80px 0;
    position: relative;
  }

  .features__inner {
    position: relative;
    max-width: 1240px;
    margin: 0 auto;
    padding: 0 40px;
  }

  .features__grid {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 18px;
    margin-top: 48px;
  }

  .feature-card {
    position: relative;
    overflow: hidden;
    border-radius: 12px;
    padding: 28px;
    transition:
      transform 250ms ease,
      box-shadow 250ms ease,
      border-color 250ms ease;
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04);
  }

  .feature-card:hover {
    transform: translateY(-2px);
    box-shadow:
      0 10px 28px rgba(var(--ts-primary-rgb), 0.18),
      inset 0 1px 0 rgba(255, 255, 255, 0.06);
  }

  .feature-card__head {
    display: flex;
    align-items: center;
    gap: 14px;
    margin-bottom: 14px;
  }

  .feature-card__icon {
    flex-shrink: 0;
    width: 42px;
    height: 42px;
    border-radius: 8px;
    background: rgba(var(--ts-primary-rgb), 0.14);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.3);
    display: flex;
    align-items: center;
    justify-content: center;
    transition:
      background 260ms ease,
      border-color 260ms ease,
      box-shadow 260ms ease;
    color: var(--ts-primary-light);
  }

  .feature-card:hover .feature-card__icon {
    background: rgba(var(--ts-primary-rgb), 0.22);
    border-color: rgba(var(--ts-primary-rgb), 0.55);
    box-shadow:
      0 0 0 3px rgba(var(--ts-primary-rgb), 0.12),
      0 6px 16px rgba(var(--ts-primary-rgb), 0.25);
  }

  :global(.feature-card__icon-svg) {
    width: 20px;
    height: 20px;
    color: var(--ts-primary-light);
  }

  .feature-card__title {
    font-size: 19px;
    font-weight: 700;
    color: var(--ts-text);
    margin: 0;
    letter-spacing: -0.01em;
  }

  .feature-card__text {
    font-size: 14.5px;
    color: var(--ts-text-muted);
    line-height: 1.6;
    margin: 0 0 18px;
  }

  .feature-card__bullets {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }

  .feature-card__bullet {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    padding: 3px 8px;
    border-radius: 4px;
    background: rgba(var(--ts-primary-rgb), 0.08);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.18);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-mono);
    font-size: 10.5px;
    font-weight: 600;
  }

  .feature-card__bullet-dot {
    width: 4px;
    height: 4px;
    border-radius: 2px;
    background: var(--ts-primary);
  }

  @media (max-width: 640px) {
    .features {
      padding: 60px 0;
    }
    .features__grid {
      grid-template-columns: 1fr;
      gap: 14px;
      margin-top: 36px;
    }
    .features__inner {
      padding: 0 20px;
    }
    .feature-card {
      padding: 22px;
    }
  }
</style>
