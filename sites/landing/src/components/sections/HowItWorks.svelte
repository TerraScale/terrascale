<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const stepIcons: AppIconName[] = ['database', 'globe', 'zap']
  const stepRegions = [['gru', 'sjc', 'fra']] as const

  $: steps = $dict.howItWorks.steps.map((step, i) => ({
    ...step,
    icon: stepIcons[i] ?? 'zap',
    regions: stepRegions[i] ?? [],
  }))
</script>

<section class="how ts-section" id="how-it-works" use:reveal>
  <div class="ts-container">
    <div class="how__head">
      <h2 class="ts-section-title">
        {$dict.howItWorks.titleLead}<br />
        <span class="how__accent">{$dict.howItWorks.titleAccent}</span>
      </h2>
    </div>

    <ol class="how__steps">
      {#each steps as step, i (step.title)}
        <li class="how__step">
          <div class="how__step-top">
            <span class="how__step-icon"><Icon name={step.icon} className="how__step-glyph" /></span>
            <span class="how__step-num" aria-hidden="true">{String(i + 1).padStart(2, '0')}</span>
          </div>
          <h3 class="how__step-title">{step.title}</h3>
          <p class="how__step-text">{step.text}</p>
          {#if step.regions.length}
            <div class="how__step-tags">
              {#each step.regions as code (code)}
                <span class="how__tag"><RegionFlag {code} /></span>
              {/each}
            </div>
          {/if}
        </li>
      {/each}
    </ol>
  </div>
</section>

<style>
  .how {
    border-top: 1px solid var(--ts-divider);
  }

  .how__head {
    max-width: 680px;
    margin: 0 auto var(--ts-section-gap);
    text-align: center;
  }

  .how__accent {
    color: var(--ts-primary-light);
  }

  .how__steps {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 24px;
  }

  .how__step {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding-block: 20px 0;
    border-top: 1px solid var(--ts-divider);
  }

  .how__step-top {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }

  .how__step-icon {
    display: grid;
    place-items: center;
    width: 32px;
    height: 32px;
    border-radius: 8px;
    background: rgba(var(--ts-primary-light-rgb), 0.12);
    color: var(--ts-primary-light);
  }

  :global(.how__step-glyph) {
    width: 16px;
    height: 16px;
  }

  .how__step-num {
    font: 400 24px / 32px var(--ts-font-mono);
    color: var(--ts-text-muted);
    opacity: 0.5;
    letter-spacing: -0.02em;
  }

  .how__step-title {
    margin: 0;
    font-size: 20px;
    line-height: 28px;
    font-weight: 600;
    letter-spacing: -0.015em;
    color: var(--ts-text);
  }

  .how__step-text {
    margin: 0;
    font-size: 14px;
    line-height: 24px;
    color: var(--ts-text-muted);
    text-wrap: pretty;
  }

  .how__step-tags {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-top: auto;
  }

  .how__tag {
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

  @media (max-width: 767px) {
    .how__steps {
      grid-template-columns: minmax(0, 1fr);
    }
  }
</style>
