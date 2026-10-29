<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const featureIcons: AppIconName[] = ['database', 'globe', 'target', 'activity', 'shieldCheck']
  const regionCodes = ['gru', 'sjc', 'fra'] as const
  const isRegion = (bullet: string): bullet is (typeof regionCodes)[number] =>
    (regionCodes as readonly string[]).includes(bullet)
  $: features = $dict.features.items.map((item, i) => ({
    icon: featureIcons[i] ?? 'zap',
    ...item,
  }))
</script>

<section class="features" id="features" use:reveal>
  <div class="features__inner">
    <div class="features__head">
      <span class="ts-eyebrow">{$dict.features.eyebrow}</span>
      <h2 class="features__title">
        {$dict.features.titleLead}<br />
        <span class="features__accent">{$dict.features.titleAccent}</span>
      </h2>
      <p class="features__subtitle">{$dict.features.subtitle}</p>
    </div>

    <ul class="features__grid">
      {#each features as feature (feature.title)}
        <li class="feature">
          <div class="feature__label">
            <span class="feature__icon">
              <Icon name={feature.icon} className="feature__icon-svg" />
            </span>
            {feature.title}
          </div>
          <h3 class="feature__headline">{feature.headline}</h3>
          <p class="feature__text">{feature.text}</p>
          <div class="feature__tags">
            {#each feature.bullets as bullet (bullet)}
              {#if isRegion(bullet)}
                <span class="feature__tag"><RegionFlag code={bullet} /></span>
              {:else}
                <span class="feature__tag">{bullet}</span>
              {/if}
            {/each}
          </div>
        </li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .features {
    padding: 96px 0;
    border-top: 1px solid var(--ts-divider);
  }

  .features__inner {
    padding: 0 clamp(16px, 2.5vw, 40px);
  }

  .features__head {
    display: grid;
    justify-items: center;
    gap: 16px;
    max-width: 680px;
    margin: 0 auto 48px;
    text-align: center;
  }

  .features__title {
    font-size: 48px;
    font-weight: 600;
    color: var(--ts-text);
    line-height: 1;
    letter-spacing: -0.035em;
    margin: 0;
    text-wrap: balance;
  }

  .features__accent {
    color: var(--ts-primary-light);
  }

  .features__subtitle {
    font-size: 16px;
    color: var(--ts-text-muted);
    line-height: 24px;
    margin: 0;
    text-wrap: pretty;
  }

  /* Two wide cards over three narrow ones. */
  .features__grid {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(6, minmax(0, 1fr));
    gap: 16px;
  }

  .feature {
    grid-column: span 2;
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 32px;
    border: 1px solid var(--ts-divider);
    border-radius: 16px;
    background: var(--ts-bg-surface);
    transition: border-color 300ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  .feature:nth-child(-n + 2) {
    grid-column: span 3;
  }

  .feature:hover {
    border-color: var(--ts-glass-border);
  }

  .feature__label {
    display: flex;
    align-items: center;
    gap: 12px;
    font-family: var(--ts-font-mono);
    font-size: 12px;
    line-height: 16px;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--ts-text-muted);
  }

  .feature__icon {
    flex-shrink: 0;
    width: 32px;
    height: 32px;
    border-radius: 8px;
    background: rgba(var(--ts-primary-light-rgb), 0.12);
    color: var(--ts-primary-light);
    display: grid;
    place-items: center;
  }

  :global(.feature__icon-svg) {
    width: 16px;
    height: 16px;
  }

  .feature__headline {
    margin: 12px 0 0;
    font-size: 20px;
    line-height: 28px;
    font-weight: 600;
    letter-spacing: -0.015em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .feature__text {
    margin: 0;
    font-size: 14px;
    color: var(--ts-text-muted);
    line-height: 24px;
    text-wrap: pretty;
  }

  .feature__tags {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-top: auto;
    padding-top: 12px;
  }

  .feature__tag {
    display: inline-flex;
    align-items: center;
    height: 24px;
    padding: 0 8px;
    border: 1px solid var(--ts-divider);
    border-radius: 4px;
    color: var(--ts-text-muted);
    font-family: var(--ts-font-mono);
    font-size: 12px;
    line-height: 1;
  }

  @media (max-width: 1024px) {
    .features__grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
    .feature,
    .feature:nth-child(-n + 2) {
      grid-column: span 1;
    }
    .feature:last-child {
      grid-column: span 2;
    }
  }

  @media (max-width: 640px) {
    .features {
      padding: 64px 0;
    }
    .features__head {
      margin-bottom: 32px;
    }
    .features__title {
      font-size: 30px;
      line-height: 36px;
    }
    .features__grid {
      grid-template-columns: minmax(0, 1fr);
    }
    .feature,
    .feature:nth-child(-n + 2),
    .feature:last-child {
      grid-column: auto;
      padding: 24px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .feature {
      transition: none;
    }
  }
</style>
