<script lang="ts">
  export let eyebrow: string
  export let title: string
  export let subtitle = ''
</script>

<div class="section-header">
  <div class="section-header__eyebrow">
    <span class="section-header__eyebrow-dot" aria-hidden="true"></span>
    {eyebrow}
  </div>
  <h2 class="section-header__title">{title}</h2>
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
    background: rgba(var(--ts-primary-rgb), 0.1);
    border: 1px solid rgba(var(--ts-primary-rgb), 0.25);
    color: var(--ts-primary-light);
    font-family: var(--ts-font-accent);
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
    background: var(--ts-primary);
  }

  .section-header__title {
    font-size: clamp(1.8rem, 2.5vw + 0.5rem, 3rem);
    font-weight: 700;
    color: var(--ts-text);
    letter-spacing: -0.02em;
    line-height: 1.12;
    margin: 0 0 16px;
  }

  .section-header__subtitle {
    font-family: var(--ts-font-accent);
    font-size: 18px;
    color: var(--ts-text-muted);
    line-height: 1.65;
    margin: 0;
  }
</style>
