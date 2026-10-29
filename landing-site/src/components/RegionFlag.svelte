<script lang="ts">
  export let code: 'gru' | 'sjc' | 'fra'
</script>

<span class="region-flag">
  <svg viewBox="0 0 16 12" aria-hidden="true">
    {#if code === 'gru'}
      <rect width="16" height="12" fill="#009b3a" />
      <path d="M8 1.6 14.4 6 8 10.4 1.6 6z" fill="#fedf00" />
      <circle cx="8" cy="6" r="2.5" fill="#002776" />
    {:else if code === 'fra'}
      <rect width="16" height="4" fill="#000" />
      <rect y="4" width="16" height="4" fill="#dd0000" />
      <rect y="8" width="16" height="4" fill="#ffce00" />
    {:else}
      <rect width="16" height="12" fill="#fff" />
      {#each [0, 2, 4, 6, 8, 10] as stripe}
        <rect y={stripe * 12 / 13} width="16" height={12 / 13} fill="#b22234" />
      {/each}
      <rect width="7" height="6.46" fill="#3c3b6e" />
    {/if}
  </svg>
  <span>{code}</span>
</span>

<style>
  .region-flag { display: inline-flex; align-items: center; gap: 4px; line-height: 1; }
  svg { width: 16px; height: 12px; border-radius: 2px; box-shadow: 0 0 0 1px rgb(255 255 255 / 0.14); flex: none; }
  span span { font: 400 12px / 1 var(--ts-font-mono); color: var(--ts-text-muted); }
</style>
