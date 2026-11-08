<script lang="ts">
  import EditorialHeader from '@/components/sections/EditorialHeader.svelte'
  import RegionFlag from '@/components/RegionFlag.svelte'
  import { dict } from '@/lib/i18n'
  import { reveal } from '@/lib/reveal'

  const stepRegions = [['gru', 'sjc', 'fra']] as const

  $: steps = $dict.howItWorks.steps.map((step, i) => ({
    num: String(i + 1).padStart(2, '0'),
    regions: stepRegions[i] ?? [],
    ...step,
  }))
</script>

<section class="ed-how ts-section" id="how-it-works" use:reveal>
  <div class="ts-container">
    <EditorialHeader index="02" lead={$dict.howItWorks.titleLead} accent={$dict.howItWorks.titleAccent} />

    <ol class="ed-how__steps">
      {#each steps as step (step.title)}
        <li class="ed-how__step">
          <span class="ed-how__num" aria-hidden="true">{step.num}</span>
          <div class="ed-how__body">
            <h3 class="ed-how__title">{step.title}</h3>
            <p class="ed-how__text">{step.text}</p>
            {#if step.regions.length}
              <div class="ed-how__tags">
                {#each step.regions as code (code)}
                  <span class="ed-how__tag"><RegionFlag {code} /></span>
                {/each}
              </div>
            {/if}
          </div>
        </li>
      {/each}
    </ol>
  </div>
</section>

<style>
  .ed-how__steps {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .ed-how__step {
    display: grid;
    grid-template-columns: 96px minmax(0, 1fr);
    gap: clamp(24px, 4vw, 64px);
    padding-block: clamp(28px, 3.5vw, 44px);
    border-top: 1px solid var(--ts-divider);
  }

  .ed-how__num {
    font-family: var(--ts-font-mono);
    font-size: clamp(32px, 3.5vw, 48px);
    font-weight: 500;
    line-height: 1;
    letter-spacing: -0.03em;
    color: var(--ts-heading-muted);
  }

  .ed-how__body {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 12px;
    max-width: 720px;
    min-width: 0;
  }

  .ed-how__title {
    margin: 0;
    font-size: clamp(20px, 2.2vw, 28px);
    line-height: 1.25;
    font-weight: 600;
    letter-spacing: -0.02em;
    color: var(--ts-text);
  }

  .ed-how__text {
    margin: 0;
    font-size: 15px;
    line-height: 1.7;
    color: var(--ts-text-muted);
    text-wrap: pretty;
  }

  .ed-how__tags {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .ed-how__tag {
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

  @media (max-width: 640px) {
    .ed-how__step {
      grid-template-columns: minmax(0, 1fr);
      gap: 16px;
      padding-block: 24px;
    }
  }
</style>
