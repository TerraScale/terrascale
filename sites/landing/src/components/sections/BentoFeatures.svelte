<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import { tokenize, CODE_SAMPLES } from '@/lib/highlight'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const featureIcons: AppIconName[] = ['database', 'globe', 'target', 'activity', 'database', 'shieldCheck']
  const featureRegions = [[], [], ['gru', 'sjc', 'fra']] as const
  const codeSample = CODE_SAMPLES.typescript
  const codeTokens = tokenize(codeSample, 'typescript')

  $: features = $dict.features.items.map((item, i) => ({
    icon: featureIcons[i] ?? 'zap',
    regions: featureRegions[i] ?? [],
    wide: i === 0,
    full: i === 5,
    ...item,
  }))
</script>

<section class="bento ts-section" id="features" use:reveal>
  <div class="ts-container">
    <h2 class="bento__title">
      {$dict.features.titleLead}
      <span class="bento__accent">{$dict.features.titleAccent}</span>
    </h2>

    <ul class="bento__grid">
      {#each features as feature (feature.headline)}
        <li class="bento__card" class:bento__card--wide={feature.wide} class:bento__card--full={feature.full}>
          <div class="bento__head">
            <span class="bento__icon">
              <Icon name={feature.icon} className="bento__icon-svg" />
            </span>
            <h3 class="bento__headline">{feature.headline}</h3>
          </div>
          {#if feature.wide}
            <pre class="bento__code" aria-hidden="true"><code>{#each codeTokens as t, i (i)}{#if t.className}<span class={t.className}>{t.text}</span>{:else}{t.text}{/if}{/each}</code></pre>
          {/if}
          {#if feature.regions.length}
            <div class="bento__tags">
              {#each feature.regions as code (code)}
                <span class="bento__tag"><RegionFlag {code} /></span>
              {/each}
            </div>
          {/if}
        </li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .bento__title {
    margin: 0 0 clamp(32px, 4vw, 48px);
    max-width: 680px;
    font-size: clamp(28px, 3vw, 40px);
    font-weight: 600;
    line-height: 1.15;
    letter-spacing: -0.03em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .bento__accent {
    color: var(--ts-primary-light);
  }

  .bento__grid {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(6, minmax(0, 1fr));
    gap: 16px;
  }

  .bento__card {
    grid-column: span 2;
    display: flex;
    flex-direction: column;
    gap: 16px;
    min-width: 0;
    padding: 24px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-lg);
    background: var(--ts-bg-surface);
    transition:
      border-color var(--ts-transition-fast),
      transform var(--ts-transition-slow);
  }

  .bento__card:hover {
    border-color: rgb(var(--ts-primary-rgb) / 0.4);
    transform: translateY(-2px);
  }

  .bento__card--wide {
    grid-column: span 4;
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(0, 1.2fr);
    gap: 24px;
    align-items: center;
  }

  .bento__card--full {
    grid-column: span 6;
  }

  .bento__head {
    display: flex;
    align-items: flex-start;
    gap: 14px;
    min-width: 0;
  }

  .bento__card--wide .bento__head {
    flex-direction: column;
    align-items: flex-start;
    gap: 16px;
  }

  .bento__icon {
    flex-shrink: 0;
    display: grid;
    place-items: center;
    width: 34px;
    height: 34px;
    border-radius: var(--ts-radius-md);
    background: rgb(var(--ts-primary-rgb) / 0.12);
    color: var(--ts-primary-light);
  }

  :global(.bento__icon-svg) {
    width: 17px;
    height: 17px;
  }

  .bento__headline {
    margin: 0;
    font-size: 19px;
    line-height: 1.4;
    font-weight: 500;
    letter-spacing: -0.015em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .bento__code {
    margin: 0;
    width: 100%;
    max-width: 100%;
    min-width: 0;
    padding: 16px;
    max-height: 240px;
    overflow: hidden;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-md);
    background: var(--ts-code-bg);
    font-family: var(--ts-font-mono);
    font-size: 11.5px;
    line-height: 1.7;
    color: var(--ts-text);
    white-space: pre;
  }

  .bento__code code {
    display: block;
    width: max-content;
    min-width: 100%;
    font: inherit;
  }

  .bento__tags {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .bento__tag {
    display: inline-flex;
    align-items: center;
    height: 24px;
    padding: 0 8px;
    border: 1px solid var(--ts-divider);
    border-radius: var(--ts-radius-sm);
    color: var(--ts-text-muted);
    font-family: var(--ts-font-mono);
    font-size: 12px;
    line-height: 1;
  }

  @media (max-width: 1023px) {
    .bento__card,
    .bento__card--wide,
    .bento__card--full {
      grid-column: span 3;
    }

    .bento__card--wide {
      display: flex;
      flex-direction: column;
      gap: 16px;
    }

    .bento__code {
      max-height: 180px;
    }
  }

  @media (max-width: 720px) {
    .bento__card,
    .bento__card--wide,
    .bento__card--full {
      grid-column: span 6;
    }

    .bento__code {
      font-size: 11px;
      max-height: 160px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .bento__card {
      transition: none;
    }

    .bento__card:hover {
      transform: none;
    }
  }
</style>
