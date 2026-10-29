<script lang="ts">
  import { tweened } from 'svelte/motion'
  import { cubicOut } from 'svelte/easing'

  export let value: number
  export let prefix: string = '$'
  export let duration: number = 650

  const display = tweened(value, { duration, easing: cubicOut })

  $: display.set(value)

  const format = (n: number) => {
    const rounded = Math.round(n * 100) / 100
    return Number.isInteger(rounded) ? rounded.toString() : rounded.toFixed(2)
  }
</script>

<span class="animated-price">
  <span class="animated-price__prefix">{prefix}</span><span class="animated-price__value"
    >{format($display)}</span
  >
</span>

<style>
  .animated-price {
    display: inline-flex;
    align-items: baseline;
    font-variant-numeric: tabular-nums;
  }

  .animated-price__prefix {
    margin-right: 2px;
  }
</style>
