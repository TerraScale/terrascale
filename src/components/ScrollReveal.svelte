<script lang="ts">
  import { reveal, type RevealDirection } from '@/lib/motion/reveal'

  let className = ''

  export { className as class }

  export let direction: RevealDirection = 'up'
  export let delay = 0
  export let duration = 0.6
  export let once = true
  export let margin = '-80px 0px'
</script>

<div use:reveal={{ direction, delay, duration, once, margin }} class={className}>
  <slot />
</div>
