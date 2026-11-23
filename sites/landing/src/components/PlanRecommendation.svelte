<script lang="ts">
  import { dict } from '@/lib/i18n'
</script>

<span class="plan-recommendation">{$dict.pricing.recommended}</span>

<style>
  .plan-recommendation {
    display: inline-block;
    max-width: 100%;
    padding: 2px 6px;
    border: 1px solid var(--ts-glass-border);
    border-radius: var(--ts-radius-sm);
    color: var(--ts-text-muted);
    font: 500 11px / 1.5 var(--ts-font-body);
    letter-spacing: normal;
    text-transform: none;
  }
</style>
