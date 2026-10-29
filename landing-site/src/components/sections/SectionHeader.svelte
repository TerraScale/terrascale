<script lang="ts">
  export let eyebrow: string
  export let title = ''
  export let subtitle = ''
  export let headingLevel: 1 | 2 = 2

  $: headingTag = headingLevel === 1 ? 'h1' : 'h2'
</script>

<div class="section-header">
  <span class="ts-eyebrow"><span class="ts-dot" aria-hidden="true"></span>{eyebrow}</span>
  <svelte:element this={headingTag} class="section-header__title">
    <slot name="title">{title}</slot>
  </svelte:element>
  {#if subtitle}
    <p class="ts-lede">{subtitle}</p>
  {/if}
</div>

<style>
  .section-header {
    display: grid;
    gap: var(--ts-s4);
    justify-items: start;
    max-width: var(--ts-prose-max);
  }

  .section-header__title {
    font-size: var(--ts-fs-4xl);
    font-weight: var(--ts-heading-weight);
    letter-spacing: var(--ts-heading-tracking);
  }

  .section-header :global(.ts-lede) {
    margin-top: 0;
  }

  @media (max-width: 640px) {
    .section-header__title {
      font-size: var(--ts-fs-3xl);
    }
  }
</style>
