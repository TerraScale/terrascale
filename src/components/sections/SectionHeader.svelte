<script lang="ts">
  export let eyebrow: string
  export let title = ''
  export let subtitle = ''
  export let headingLevel: 1 | 2 = 2

  $: headingTag = headingLevel === 1 ? 'h1' : 'h2'
</script>

<div class="section-header">
  <div class="section-header__eyebrow">
    <span class="section-header__eyebrow-dot" aria-hidden="true"></span>
    {eyebrow}
  </div>
  <svelte:element this={headingTag} class="section-header__title">
    <slot name="title">{title}</slot>
  </svelte:element>
  {#if subtitle}
    <p class="section-header__subtitle">{subtitle}</p>
  {/if}
</div>

<style>
  .section-header {
    text-align: center;
    max-width: 720px;
    margin: 0 auto;
  }

  .section-header__eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 5px 12px;
    border-radius: 4px;
    margin-bottom: 20px;
    background: color-mix(in srgb, var(--ts-accent) 10%, transparent);
    border: 1px solid color-mix(in srgb, var(--ts-accent) 25%, transparent);
    color: var(--ts-accent);
    font-family: var(--ts-font-sans);
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.14em;
    line-height: 1;
  }

  .section-header__eyebrow-dot {
    width: 5px;
    height: 5px;
    border-radius: 3px;
    background: var(--ts-accent);
  }

  .section-header__title {
    font-size: clamp(1.8rem, 2.5vw + 0.5rem, 3rem);
    font-weight: 700;
    color: var(--ts-ink);
    letter-spacing: -0.02em;
    line-height: 1.12;
    margin: 0 0 16px;
  }

  .section-header__subtitle {
    font-family: var(--ts-font-sans);
    font-size: 18px;
    color: var(--ts-ink-2);
    line-height: 1.65;
    margin: 0;
  }
</style>
