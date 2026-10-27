<script lang="ts">
  import { onMount } from 'svelte'
  import { page } from '$app/state'
  import type { Snippet } from 'svelte'

  import '@/index.css'
  import Footer from '@/components/Footer.svelte'
  import Header from '@/components/Header.svelte'
  import { initLocale } from '@/lib/i18n'

  let { children }: { children: Snippet } = $props()

  const publicOrigin = 'https://terrascale.tech'
  const metadataByPath: Record<string, { title: string; description: string }> = {
    '/': {
      title: 'TerraScale Public Alpha | Regional Document Database',
      description:
        'TerraScale Public Alpha provides a typed document database with asynchronous replication across São Paulo, the San Francisco Bay Area, and Frankfurt.',
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
        'Free requires no card. Starter is $30/year ($2.50/month equivalent), Pro $20/month, Team $50/month. Enterprise $999/month support plus usage: contact sales. Operation units over a paid allowance bill on the next invoice.',
    },
    '/404': {
      title: '404 | TerraScale',
      description: 'The requested TerraScale page could not be found.',
    },
  }

  const canonicalPath = $derived(page.url.pathname === '/' ? '/' : page.url.pathname.replace(/\/$/, ''))
  const canonicalUrl = $derived(`${publicOrigin}${canonicalPath}`)
  const metadata = $derived(metadataByPath[canonicalPath] ?? metadataByPath['/404'])

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

<div class="min-h-screen">
  <Header />

  <main style="padding-top: 60px">
    {@render children()}
  </main>

  <Footer />
</div>
