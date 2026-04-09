<script lang="ts">
  import { reveal, type RevealDirection } from '@/lib/motion/reveal'

  let className = ''

  export { className as class }

  export let direction: RevealDirection = 'up'
  export let delay = 0
  export let duration = 0.38
  export let once = true
  export let margin = '0px 0px 120px 0px'
</script>

<div use:reveal={{ direction, delay, duration, once, margin }} class={className}>
  <slot />
</div>
