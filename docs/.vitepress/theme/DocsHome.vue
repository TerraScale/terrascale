<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'

const { localeIndex } = useData()
const prefix = computed(() => localeIndex.value === 'root' ? '' : `/${localeIndex.value}`)
const entries = [
  { number: '01', title: 'Explore SQL', detail: 'Start with the SQL overview.', link: '/concepts/sql', tone: 'blue', icon: 'rocket' },
  { number: '02', title: 'SQL over HTTP', detail: 'Read the SQL HTTP guide.', link: '/guides/sql-http', tone: 'cyan', icon: 'filter' },
  { number: '03', title: 'Connect safely', detail: 'Scope credentials to your application and handle failed requests.', link: '/guides/authentication', tone: 'violet', icon: 'key-round' },
  { number: '04', title: 'Understand the contract', detail: 'Check SDK availability and service limits.', link: '/guides/sdks', tone: 'indigo', icon: 'book-open' },
]

function openSearch() {
  window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true }))
}

</script>

<template>
  <div class="docs-home">
    <section class="docs-hero">
      <span class="docs-eyebrow">Developer documentation</span>
      <h1>Build with TerraBase</h1>
      <p class="docs-intro">Explore SQL and keep access scoped to your project.</p>
      <button class="docs-search" type="button" aria-label="Search documentation" @click="openSearch">
        <span class="docs-search-text">Search guides, SDKs and API endpoints</span>
        <kbd>Ctrl K</kbd>
      </button>
      <div class="docs-actions">
        <a class="docs-primary" :href="`${prefix}/getting-started/javascript`">Read the quickstart <span aria-hidden="true">→</span></a>
        <a :href="`${prefix}/guides/sdks`">Compare SDKs</a>
      </div>
    </section>
    <div class="docs-release" role="note">
      <span class="docs-badge"><span class="docs-dot" aria-hidden="true"></span>Pre-launch</span>
      <span>The guides are being updated for the SQL product.</span>
    </div>
    <section class="docs-block" aria-labelledby="docs-start">
      <div class="docs-section-head">
        <h2 id="docs-start">Start building</h2>
        <span>From installation to safe writes</span>
      </div>
      <div class="docs-cards">
        <a v-for="entry in entries" :key="entry.number" class="docs-card" :href="`${prefix}${entry.link}`" :data-tone="entry.tone">
          <span class="docs-tile" aria-hidden="true"><svg viewBox="0 0 24 24"><use :href="`/brand/docs-icons.svg#i-${entry.icon}`" /></svg></span>
          <h3>{{ entry.title }} <span aria-hidden="true">→</span></h3>
          <p>{{ entry.detail }}</p>
        </a>
      </div>
    </section>
    <p class="docs-home-foot">TerraScale uses TerraBase through its SDKs. Application data belongs in your own scoped database.</p>
  </div>
</template>
