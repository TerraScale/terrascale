<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const featureIcons: AppIconName[] = ['database', 'globe', 'target', 'activity', 'database', 'shieldCheck']
  // The regions card shows the serving regions it describes.
  const featureRegions = [[], [], ['gru', 'sjc', 'fra']] as const
  $: features = $dict.features.items.map((item, i) => ({
    icon: featureIcons[i] ?? 'zap',
    regions: featureRegions[i] ?? [],
    ...item,
  }))
</script>

<section class="features ts-section" id="features" use:reveal>
  <div class="ts-container">
    <div class="features__head">
      <h2 class="ts-section-title">
        {$dict.features.titleLead}<br />
        <span class="features__accent">{$dict.features.titleAccent}</span>
      </h2>
    </div>

    <ul class="features__grid">
      {#each features as feature (feature.headline)}
        <li class="feature">
          <span class="feature__icon">
            <Icon name={feature.icon} className="feature__icon-svg" />
          </span>
          <h3 class="feature__headline">{feature.headline}</h3>
          {#if feature.regions.length}
            <div class="feature__tags">
              {#each feature.regions as code (code)}
                <span class="feature__tag"><RegionFlag {code} /></span>
              {/each}
            </div>
          {/if}
        </li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .features {
    border-top: 1px solid var(--ts-divider);
  }

  .features__head {
    max-width: 680px;
    margin: 0 auto var(--ts-section-gap);
    text-align: center;
  }

  .features__accent {
    color: var(--ts-primary-light);
  }

  /* Six capabilities in two rows. */
  .features__grid {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(6, minmax(0, 1fr));
    gap: 24px;
  }

  .feature {
    grid-column: span 2;
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding-block: 20px 0;
    border-top: 1px solid var(--ts-divider);
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
    margin: 0;
    font-size: 20px;
    line-height: 28px;
    font-weight: 600;
    letter-spacing: -0.015em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .feature__tags {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-top: auto;
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

  @media (max-width: 900px) {
    .features__grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
    .feature {
      grid-column: span 1;
    }
  }

  @media (max-width: 640px) {
    .features__grid {
      grid-template-columns: minmax(0, 1fr);
    }
    .feature {
      grid-column: auto;
    }
  }
</style>
