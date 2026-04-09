<script lang="ts">
  import { cubicOut } from 'svelte/easing'
  import { tweened } from 'svelte/motion'
  import { onMount } from 'svelte'

  let className = ''

  export { className as class }

  export let value: string
  export let duration = 1.5

  let element: HTMLSpanElement
  let reduceMotion = false

  const match = value.match(/^([<>]?)(\d+(?:\.\d+)?)(.*?)$/)
  const prefix = match?.[1] ?? ''
  const numericValue = match ? Number.parseFloat(match[2]) : 0
  const suffix = match?.[3] ?? ''
  const hasDecimal = value.includes('.')
  const counter = tweened(0)

  $: displayValue =
    !match || reduceMotion
      ? value
      : `${prefix}${hasDecimal ? $counter.toFixed(2) : Math.round($counter)}${suffix}`

  onMount(() => {
    reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches

    if (!match || reduceMotion || typeof IntersectionObserver === 'undefined') {
      return
    }

    const observer = new IntersectionObserver(
      ([entry]) => {
        if (!entry?.isIntersecting) {
          return
        }

        counter.set(numericValue, {
          duration: Math.round(duration * 1000),
          easing: cubicOut,
        })

        observer.disconnect()
      },
      { rootMargin: '-50px 0px' },
    )

    observer.observe(element)

    return () => observer.disconnect()
  })
</script>

<span bind:this={element} class={className}>
  {displayValue}
</span>
