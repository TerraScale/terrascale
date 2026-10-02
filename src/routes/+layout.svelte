<script lang="ts">
  import { onMount } from 'svelte'
  import { page } from '$app/stores'
  import '@/index.css'
  import Footer from '@/components/Footer.svelte'
  import Header from '@/components/Header.svelte'
  import { initLocale } from '@/lib/i18n'

  const publicOrigin = 'https://terrascale.tech'
  const metadataByPath: Record<string, { title: string; description: string }> = {
    '/': {
      title: 'TerraScale Public Alpha | Regional Document Database',
      description:
        'TerraScale Public Alpha provides a typed document database with asynchronous replication across São Paulo, San Jose, and Frankfurt.',
    },
    '/about': {
      title: 'About TerraScale',
      description:
        'Learn how TerraScale Public Alpha provides a regional document database with asynchronous replication across selected launch regions.',
    },
    '/contact': {
      title: 'Contact TerraScale',
      description:
        'Get in touch with TerraScale for support, sales questions, or guidance on regional database workflows.',
    },
    '/pricing': {
      title: 'TerraScale Public Alpha pricing',
      description:
        'Compare the Starter and Pro Public Alpha plans and review the unavailable Enterprise plan.',
    },
    '/404': {
      title: '404 | TerraScale',
      description: 'The requested TerraScale page could not be found.',
    },
  }

  $: canonicalPath = $page.url.pathname === '/' ? '/' : $page.url.pathname.replace(/\/$/, '')
  $: canonicalUrl = `${publicOrigin}${canonicalPath}`
  $: metadata = metadataByPath[canonicalPath] ?? metadataByPath['/404']

  onMount(() => {
    initLocale()
  })
</script>

<svelte:head>
  <title>{metadata.title}</title>
  <meta name="description" content={metadata.description} />
  <link rel="canonical" href={canonicalUrl} />
  <meta property="og:type" content="website" />
  <meta property="og:title" content={metadata.title} />
  <meta property="og:description" content={metadata.description} />
  <meta property="og:url" content={canonicalUrl} />
</svelte:head>

<div class="min-h-screen bg-[#050508] text-[#F8FAFC] dark">
  <Header />

  <main>
    <slot />
  </main>

  <Footer />
</div>
