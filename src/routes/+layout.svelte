<script lang="ts">
  import '@/index.css'
  import Footer from '@/components/Footer.svelte'
  import Header from '@/components/Header.svelte'
</script>

<svelte:head>
  <title>TerraScale | Developer Infrastructure For Data Workflows</title>
  <meta
    name="description"
    content="TerraScale is developer-first infrastructure for typed database, auth, queue, and payment workflows across the current launch-region model."
  />
</svelte:head>

<div class="min-h-screen bg-[#050508] text-[#F8FAFC] dark">
  <Header />

  <main>
    <slot />
  </main>

  <Footer />
</div>
