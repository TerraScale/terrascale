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
        <span class="ts-accent-text">{$dict.features.titleAccent}</span>
      </svelte:fragment>
    </SectionHeader>

    <div class="features__grid">
      {#each features as feature (feature.title)}
        <article class="feature-card ts-card">
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
    transition: border-color var(--ts-base) ease;
  }

  .feature-card:hover {
    border-color: var(--ts-line-strong);
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
    background: color-mix(in srgb, var(--ts-accent) 14%, transparent);
    border: 1px solid color-mix(in srgb, var(--ts-accent) 30%, transparent);
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--ts-accent);
  }

  :global(.feature-card__icon-svg) {
    width: 20px;
    height: 20px;
    color: var(--ts-accent);
  }

  .feature-card__title {
    font-size: 19px;
    font-weight: 700;
    color: var(--ts-ink);
    margin: 0;
    letter-spacing: -0.01em;
  }

  .feature-card__text {
    font-size: 14.5px;
    color: var(--ts-ink-2);
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
    background: color-mix(in srgb, var(--ts-accent) 8%, transparent);
    border: 1px solid color-mix(in srgb, var(--ts-accent) 18%, transparent);
    color: var(--ts-accent);
    font-family: var(--ts-font-mono);
    font-size: 10.5px;
    font-weight: 600;
  }

  .feature-card__bullet-dot {
    width: 4px;
    height: 4px;
    border-radius: 2px;
    background: var(--ts-accent);
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
