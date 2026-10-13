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

<section class="ts-section features" id="features">
  <div class="ts-container">
    <SectionHeader
      eyebrow={$dict.features.eyebrow}
      subtitle={$dict.features.subtitle}
    >
      <svelte:fragment slot="title">
        {$dict.features.titleLead}
        <span class="ts-h-muted">{$dict.features.titleAccent}</span>
      </svelte:fragment>
    </SectionHeader>

    <div class="features__grid">
      {#each features as feature (feature.title)}
        <article class="feature">
          <div class="feature__head">
            <span class="ts-icon-tile"><Icon name={feature.icon} /></span>
            <h3 class="feature__title">{feature.title}</h3>
          </div>

          <p class="feature__text">{feature.text}</p>
          <div class="feature__bullets">
            {#each feature.bullets as bullet (bullet)}
              <span class="ts-badge ts-badge-outline">{bullet}</span>
            {/each}
          </div>
        </article>
      {/each}
    </div>
  </div>
</section>

<style>
  .features__grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: var(--ts-s8) var(--ts-s10);
    margin-top: var(--ts-s8);
  }

  /* Flat items split by a top hairline (no boxes). */
  .feature {
    display: grid;
    align-content: start;
    gap: var(--ts-s3);
    padding-top: var(--ts-s6);
    border-top: 1px solid var(--ts-line);
  }

  .feature__head {
    display: flex;
    align-items: center;
    gap: var(--ts-s3);
  }

  .feature__title {
    font-size: var(--ts-fs-xl);
  }

  .feature__text {
    color: var(--ts-ink-2);
    line-height: var(--ts-lh);
  }

  .feature__bullets {
    display: flex;
    flex-wrap: wrap;
    gap: var(--ts-s2);
  }

  @media (max-width: 860px) {
    .features__grid {
      grid-template-columns: minmax(0, 1fr);
    }
  }
</style>
