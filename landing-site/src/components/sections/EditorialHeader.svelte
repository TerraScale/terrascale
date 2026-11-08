<script lang="ts">
  export let index: string
  export let lead: string
  export let accent = ''
</script>

<header class="ed-head">
  <span class="ed-head__index">{index}</span>
  <h2 class="ed-head__title">{lead}{#if accent}{' '}<span class="ed-head__accent">{accent}</span>{/if}</h2>
</header>

<style>
  .ed-head {
    display: grid;
    grid-template-columns: 64px minmax(0, 1fr);
    gap: 16px;
    align-items: baseline;
    padding-top: 28px;
    border-top: 1px solid var(--ts-divider);
    margin-bottom: clamp(40px, 5vw, 64px);
  }

  .ed-head__index {
    font-family: var(--ts-font-mono);
    font-size: 13px;
    letter-spacing: 0.08em;
    color: var(--ts-text-muted);
  }

  .ed-head__title {
    margin: 0;
    font-size: clamp(32px, 4vw, 52px);
    font-weight: 600;
    line-height: 1.05;
    letter-spacing: -0.035em;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .ed-head__accent {
    color: var(--ts-heading-muted);
  }

  @media (max-width: 640px) {
    .ed-head {
      grid-template-columns: minmax(0, 1fr);
      gap: 8px;
      padding-top: 20px;
    }
  }
</style>
