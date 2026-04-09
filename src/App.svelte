<script lang="ts">
  import { onMount, tick } from 'svelte'
  import Footer from '@/components/Footer.svelte'
  import Header from '@/components/Header.svelte'
  import { currentLocation, startRouter } from '@/lib/router'
  import About from '@/pages/About.svelte'
  import Contact from '@/pages/Contact.svelte'
  import Home from '@/pages/Home.svelte'
  import NotFound from '@/pages/NotFound.svelte'
  import Pricing from '@/pages/Pricing.svelte'

  onMount(() => startRouter())

  async function syncViewport(hash: string) {
    await tick()

    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    const behavior = reduceMotion ? 'auto' : 'smooth'

    if (hash) {
      const target = document.querySelector(hash)

      if (target instanceof HTMLElement) {
        target.scrollIntoView({ behavior, block: 'start' })
        return
      }
    }

    window.scrollTo({ top: 0, behavior })
  }

  $: void syncViewport($currentLocation.hash)
</script>

<svelte:head>
  <title>TerraScale | Database Infrastructure That Scales Effortlessly</title>
</svelte:head>

<div class="min-h-screen bg-[#050508] text-[#F8FAFC] dark">
  <Header />

  <main>
    {#if $currentLocation.pathname === '/'}
      <Home />
    {:else if $currentLocation.pathname === '/about'}
      <About />
    {:else if $currentLocation.pathname === '/contact'}
      <Contact />
    {:else if $currentLocation.pathname === '/pricing'}
      <Pricing />
    {:else}
      <NotFound />
    {/if}
  </main>

  <Footer />
</div>
