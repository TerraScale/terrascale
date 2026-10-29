<script lang="ts">
  import Icon from '@/components/Icon.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import type { AppIconName } from '@/lib/icons'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const stepIcons: AppIconName[] = ['database', 'globe', 'zap']
  // Step one picks the home region from the serving regions.
  const stepRegions = [['gru', 'sjc', 'fra']] as const

  $: steps = $dict.howItWorks.steps.map((step, i) => ({
    ...step,
    icon: stepIcons[i] ?? 'zap',
    regions: stepRegions[i] ?? [],
    tags: step.tags ?? [],
  }))
</script>

<section class="how" id="how-it-works" use:reveal>
  <div class="how__inner">
    <div class="how__head">
      <span class="ts-eyebrow">{$dict.howItWorks.eyebrow}</span>
      <h2 class="how__title">
        {$dict.howItWorks.titleLead}<br />
        <span class="how__accent">{$dict.howItWorks.titleAccent}</span>
      </h2>
      <p class="how__subtitle">{$dict.howItWorks.subtitle}</p>
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
          {#if step.regions.length || step.tags.length}
            <div class="how__step-tags">
              {#each step.regions as code (code)}
                <span class="how__tag"><RegionFlag {code} /></span>
              {/each}
              {#each step.tags as tag (tag)}
                <span class="how__tag">{tag}</span>
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
    padding: 96px 0;
    border-top: 1px solid var(--ts-divider);
  }

  .how__inner {
    padding: 0 clamp(16px, 2.5vw, 40px);
  }

  .how__head {
    display: grid;
    justify-items: center;
    gap: 16px;
    max-width: 680px;
    margin: 0 auto 48px;
    text-align: center;
  }

  .how__title {
    margin: 0;
    font-size: 48px;
    font-weight: 600;
    line-height: 1;
    letter-spacing: -0.035em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .how__accent {
    color: var(--ts-primary-light);
  }

  .how__subtitle {
    margin: 0;
    font-size: 16px;
    line-height: 24px;
    color: var(--ts-text-muted);
    text-wrap: pretty;
  }

  .how__steps {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 16px;
  }

  .how__step {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 32px;
    border: 1px solid var(--ts-divider);
    border-radius: 16px;
    background: var(--ts-bg-surface);
    transition: border-color 300ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  .how__step:hover {
    border-color: var(--ts-glass-border);
  }

  .how__step-top {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 12px;
  }

  .how__step-icon {
    display: grid;
    place-items: center;
    width: 40px;
    height: 40px;
    border-radius: 8px;
    background: rgba(var(--ts-primary-light-rgb), 0.12);
    color: var(--ts-primary-light);
  }

  :global(.how__step-glyph) {
    width: 20px;
    height: 20px;
  }

  .how__step-num {
    font: 400 30px / 36px var(--ts-font-mono);
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
    padding-top: 12px;
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

  @media (max-width: 1024px) {
    .how__steps {
      grid-template-columns: minmax(0, 1fr);
    }
  }

  @media (max-width: 640px) {
    .how {
      padding: 64px 0;
    }
    .how__head {
      margin-bottom: 32px;
    }
    .how__title {
      font-size: 30px;
      line-height: 36px;
    }
    .how__step {
      padding: 24px;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .how__step {
      transition: none;
    }
  }
</style>
