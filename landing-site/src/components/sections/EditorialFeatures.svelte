<script lang="ts">
  import EditorialHeader from '@/components/sections/EditorialHeader.svelte'
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const featureIcons: AppIconName[] = ['database', 'globe', 'target', 'activity', 'database', 'shieldCheck']
  const featureRegions = [[], [], ['gru', 'sjc', 'fra']] as const

  $: features = $dict.features.items.map((item, i) => ({
    icon: featureIcons[i] ?? 'zap',
    regions: featureRegions[i] ?? [],
    num: String(i + 1).padStart(2, '0'),
    ...item,
  }))
</script>

<section class="ed-features ts-section" id="features" use:reveal>
  <div class="ts-container">
    <EditorialHeader index="01" lead={$dict.features.titleLead} accent={$dict.features.titleAccent} />

    <ul class="ed-features__list">
      {#each features as feature (feature.headline)}
        <li class="ed-feature">
          <span class="ed-feature__num">{feature.num}</span>
          <div class="ed-feature__body">
            <div class="ed-feature__row">
              <span class="ed-feature__icon">
                <Icon name={feature.icon} className="ed-feature__icon-svg" />
              </span>
              <h3 class="ed-feature__headline">{feature.headline}</h3>
            </div>
            {#if feature.regions.length}
              <div class="ed-feature__tags">
                {#each feature.regions as code (code)}
                  <span class="ed-feature__tag"><RegionFlag {code} /></span>
                {/each}
              </div>
            {/if}
          </div>
        </li>
      {/each}
    </ul>
  </div>
</section>

<style>
  .ed-features__list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    column-gap: clamp(40px, 6vw, 96px);
  }

  .ed-feature {
    display: grid;
    grid-template-columns: 48px minmax(0, 1fr);
    gap: 16px;
    padding-block: 28px;
    border-top: 1px solid var(--ts-divider);
  }

  .ed-feature__num {
    font-family: var(--ts-font-mono);
    font-size: 13px;
    letter-spacing: 0.08em;
    color: var(--ts-text-muted);
    padding-top: 7px;
  }

  .ed-feature__body {
    display: flex;
    flex-direction: column;
    gap: 16px;
    min-width: 0;
  }

  .ed-feature__row {
    display: flex;
    align-items: flex-start;
    gap: 16px;
  }

  .ed-feature__icon {
    flex-shrink: 0;
    display: grid;
    place-items: center;
    width: 32px;
    height: 32px;
    border-radius: var(--ts-radius-md);
    background: rgb(var(--ts-primary-rgb) / 0.12);
    color: var(--ts-primary-light);
  }

  :global(.ed-feature__icon-svg) {
    width: 16px;
    height: 16px;
  }

  .ed-feature__headline {
    margin: 0;
    padding-top: 4px;
    font-size: 21px;
    line-height: 1.35;
    font-weight: 500;
    letter-spacing: -0.015em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .ed-feature__tags {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-left: 48px;
  }

  .ed-feature__tag {
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

  @media (max-width: 900px) {
    .ed-features__list {
      grid-template-columns: minmax(0, 1fr);
    }
  }

  @media (max-width: 640px) {
    .ed-feature {
      padding-block: 22px;
    }

    .ed-feature__tags {
      margin-left: 0;
    }
  }
</style>
