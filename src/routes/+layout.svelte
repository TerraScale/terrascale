<script lang="ts">
  import '@/index.css'
  import Footer from '@/components/Footer.svelte'
  import Header from '@/components/Header.svelte'
</script>

<svelte:head>
  <title>TerraScale | Database Infrastructure That Scales Effortlessly</title>
  <meta
    name="description"
    content="TerraScale is a developer-first database platform for teams that need secure global scale without infrastructure drag."
  />
</svelte:head>

<div class="min-h-screen bg-[#050508] text-[#F8FAFC] dark">
  <Header />

  <main>
    <slot />
  </main>

  <Footer />
</div>
