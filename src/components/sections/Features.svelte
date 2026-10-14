<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'

  const featureIcons: AppIconName[] = ['database', 'globe', 'target', 'activity', 'shieldCheck']
  const regionCodes = ['gru', 'sjc', 'fra'] as const
  const isRegion = (bullet: string): bullet is (typeof regionCodes)[number] =>
    (regionCodes as readonly string[]).includes(bullet)
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

    <dl class="features__spec">
      {#each features as feature (feature.title)}
        <div class="spec-row">
          <dt class="spec-row__label">
            <span class="spec-row__icon">
              <Icon name={feature.icon} className="spec-row__icon-svg" />
            </span>
            {feature.title}
          </dt>
          <dd class="spec-row__text">{feature.text}</dd>
          <dd class="spec-row__tags">
            {#each feature.bullets as bullet (bullet)}
              {#if isRegion(bullet)}
                <RegionFlag code={bullet} />
              {:else}
                <span class="spec-row__tag">{bullet}</span>
              {/if}
            {/each}
          </dd>
        </div>
      {/each}
    </dl>
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

  .features__spec {
    margin: 0;
    border: 1px solid var(--ts-divider);
    border-radius: 10px;
    background: var(--ts-bg-surface);
  }

  .spec-row {
    display: grid;
    grid-template-columns: 220px minmax(0, 1fr);
    gap: 12px 32px;
    align-items: start;
    padding: 24px 32px;
  }

  .spec-row + .spec-row {
    border-top: 1px solid var(--ts-divider);
  }

  .spec-row__label {
    display: flex;
    align-items: center;
    gap: 12px;
    font-size: 16px;
    font-weight: 600;
    color: var(--ts-text);
    letter-spacing: -0.015em;
  }

  .spec-row__icon {
    flex-shrink: 0;
    width: 32px;
    height: 32px;
    border-radius: 6px;
    background: color-mix(in oklab, var(--hue) 16%, transparent);
    color: var(--hue);
    display: grid;
    place-items: center;
  }

  /* Non-semantic icon hues (dark chart series 1 to 5); status colours are reserved for status. */
  .spec-row:nth-child(1) { --hue: #4797e4; }
  .spec-row:nth-child(2) { --hue: #00a1dd; }
  .spec-row:nth-child(3) { --hue: #925086; }
  .spec-row:nth-child(4) { --hue: #7f5794; }
  .spec-row:nth-child(5) { --hue: #4797e4; }

  :global(.spec-row__icon-svg) {
    width: 16px;
    height: 16px;
  }

  .spec-row__text {
    margin: 0;
    padding-top: 4px;
    font-size: 14px;
    color: var(--ts-text-muted);
    line-height: 1.7;
    max-width: 80ch;
  }

  .spec-row__tags {
    grid-column: 2;
    margin: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 8px 12px;
    align-items: center;
  }

  .spec-row__tag {
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

  @media (max-width: 640px) {
    .features {
      padding: 64px 0;
    }
    .features__head {
      margin-bottom: 32px;
    }
    .spec-row {
      grid-template-columns: minmax(0, 1fr);
      padding: 20px;
    }
    .spec-row__tags {
      grid-column: auto;
    }
  }
</style>
