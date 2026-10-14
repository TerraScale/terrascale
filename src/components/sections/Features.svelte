<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
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
    <div class="features__head">
      <span class="ts-eyebrow">{$dict.features.eyebrow}</span>
      <h2 class="features__title">
        {$dict.features.titleLead}<br />
        <span class="features__accent">{$dict.features.titleAccent}</span>
      </h2>
      <p class="features__subtitle">{$dict.features.subtitle}</p>
    </div>

    <div class="features__grid">
      {#each features as feature (feature.title)}
        <article class="feature-card">
          <div class="feature-card__head">
            <div class="feature-card__icon">
              <Icon name={feature.icon} className="feature-card__icon-svg" />
            </div>
            <h3 class="feature-card__title">{feature.title}</h3>
          </div>

          <p class="feature-card__text">{feature.text}</p>
          <div class="feature-card__bullets">
            {#each feature.bullets as bullet (bullet)}
              {#if bullet === 'gru' || bullet === 'sjc'}
                <RegionFlag code={bullet} />
              {:else}
                <span class="feature-card__bullet">{bullet}</span>
              {/if}
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
    border-top: 1px solid var(--ts-divider);
  }

  .features__inner {
    padding: 0 clamp(16px, 2.5vw, 40px);
  }

  .features__head {
    display: grid;
    justify-items: center;
    gap: 16px;
    max-width: 760px;
    margin: 0 auto 48px;
    text-align: center;
  }

  .features__title {
    font-size: clamp(30px, 3.2vw, 48px);
    font-weight: 600;
    color: var(--ts-text);
    line-height: 1.08;
    letter-spacing: -0.035em;
    margin: 0;
  }

  .features__accent {
    color: var(--ts-primary);
  }

  .features__subtitle {
    font-size: 16px;
    color: var(--ts-text-muted);
    line-height: 1.7;
    max-width: 56ch;
    margin: 0;
  }

  .features__grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 16px;
  }

  .feature-card {
    display: grid;
    grid-template-rows: auto 1fr auto;
    gap: 16px;
    padding: 32px;
    border: 1px solid var(--ts-divider);
    border-radius: 10px;
    background: var(--ts-bg-surface);
    transition: border-color 150ms ease;
  }

  .feature-card:hover {
    border-color: var(--ts-glass-border);
  }

  .feature-card__head {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  .feature-card__icon {
    flex-shrink: 0;
    width: 32px;
    height: 32px;
    border-radius: 6px;
    background: color-mix(in oklab, var(--hue) 16%, transparent);
    color: var(--hue);
    display: grid;
    place-items: center;
  }

  /* Non-semantic icon hues (dark chart series 1 to 4); status colours are reserved for status. */
  .feature-card:nth-child(1) { --hue: #4797e4; }
  .feature-card:nth-child(2) { --hue: #925086; }
  .feature-card:nth-child(3) { --hue: #00a1dd; }
  .feature-card:nth-child(4) { --hue: #7f5794; }

  :global(.feature-card__icon-svg) {
    width: 16px;
    height: 16px;
  }

  .feature-card__title {
    font-size: 20px;
    font-weight: 600;
    color: var(--ts-text);
    letter-spacing: -0.015em;
    margin: 0;
  }

  .feature-card__text {
    font-size: 14px;
    color: var(--ts-text-muted);
    line-height: 1.7;
    max-width: 64ch;
    margin: 0;
  }

  .feature-card__bullets {
    display: flex;
    flex-wrap: wrap;
    align-content: flex-start;
    gap: 8px;
  }

  .feature-card__bullet {
    display: inline-flex;
    align-items: center;
    height: 26px;
    padding: 0 8px;
    border: 1px solid var(--ts-divider);
    border-radius: 4px;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-mono);
    font-size: 12px;
    line-height: 1;
  }

  @media (max-width: 900px) {
    .features__grid {
      grid-template-columns: minmax(0, 1fr);
    }
  }

  @media (max-width: 640px) {
    .features {
      padding: 64px 0;
    }
    .features__inner {
      padding: 0 clamp(16px, 2.5vw, 40px);
    }
    .features__head {
      margin-bottom: 32px;
    }
    .feature-card {
      padding: 20px;
    }
  }
</style>
