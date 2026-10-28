<script lang="ts">
  import { onMount } from 'svelte'
  import { dict } from '@/lib/i18n'

  $: lines = $dict.tagline.lines.map((line) => line.split(' '))

  let heading: HTMLElement
  // Words only start muted once the script runs, so the prerendered tagline is fully legible.
  let armed = false

  onMount(() => {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    armed = true

    let frame = 0
    // A word lights up once its middle rises above this share of the viewport height.
    const update = () => {
      frame = 0
      const trigger = window.innerHeight * 0.62
      for (const word of heading.querySelectorAll<HTMLElement>('.tagline__word')) {
        const box = word.getBoundingClientRect()
        word.classList.toggle('is-lit', box.top + box.height / 2 < trigger)
      }
    }
    const schedule = () => {
      if (!frame) frame = requestAnimationFrame(update)
    }

    update()
    window.addEventListener('scroll', schedule, { passive: true })
    window.addEventListener('resize', schedule)
    return () => {
      if (frame) cancelAnimationFrame(frame)
      window.removeEventListener('scroll', schedule)
      window.removeEventListener('resize', schedule)
    }
  })
</script>

<section class="tagline">
  <h2 class="tagline__text" class:is-armed={armed} bind:this={heading}>
    {#each lines as words, l (l)}
      <span class="tagline__line">
        {#each words as word, w (w)}<span class="tagline__word">{word}</span>{' '}{/each}
      </span>
    {/each}
  </h2>
</section>

<style>
  .tagline {
    padding: 160px clamp(16px, 2.5vw, 40px);
    border-top: 1px solid var(--ts-divider);
  }

  .tagline__text {
    max-width: 1120px;
    margin: 0 auto;
    font-size: 60px;
    font-weight: 600;
    line-height: 1.1;
    letter-spacing: -0.035em;
    text-align: center;
    color: var(--ts-text);
    text-wrap: balance;
  }

  .tagline__line {
    display: block;
  }

  .tagline__word {
    transition: opacity 500ms cubic-bezier(0.22, 1, 0.36, 1);
  }

  .is-armed .tagline__word {
    opacity: 0.3;
  }

  .is-armed .tagline__word:global(.is-lit) {
    opacity: 1;
  }

  @media (max-width: 1024px) {
    .tagline__text {
      font-size: 48px;
    }
  }

  @media (max-width: 640px) {
    .tagline {
      padding-block: 96px;
    }
    .tagline__text {
      font-size: 36px;
    }
  }
</style>
