<script lang="ts">
  import { onMount } from 'svelte'
  import '@/index.css'
  import Footer from '@/components/Footer.svelte'
  import Header from '@/components/Header.svelte'
  import { dict, initLocale } from '@/lib/i18n'

  // Resolve the saved/browser locale after hydration (deferred so prerendered
  // English HTML matches the first client render — no hydration mismatch).
  onMount(() => {
    initLocale()
  })
</script>

<svelte:head>
  <title>{$dict.meta.title}</title>
  <meta name="description" content={$dict.meta.description} />
</svelte:head>

<div class="min-h-screen bg-[#050508] text-[#F8FAFC] dark">
  <Header />

  <main>
    <slot />
  </main>

  <Footer />
</div>
