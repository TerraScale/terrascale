<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'

const { localeIndex } = useData()
const prefix = computed(() => localeIndex.value === 'root' ? '' : `/${localeIndex.value}`)
const entries = [
  { title: 'SQL', link: '/concepts/sql', tone: 'blue', icon: 'rocket' },
  { title: 'SQL over HTTP', link: '/guides/sql-http', tone: 'cyan', icon: 'filter' },
  { title: 'Authentication', link: '/guides/authentication', tone: 'violet', icon: 'key-round' },
  { title: 'SDKs and limits', link: '/guides/sdks', tone: 'indigo', icon: 'book-open' },
]

function openSearch() {
  window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true }))
}

</script>

<template>
  <div class="docs-home">
    <section class="docs-hero">
      <h1>Build with TerraBase</h1>
      <button class="docs-search" type="button" aria-label="Search documentation" @click="openSearch">
        <span class="docs-search-text">Search guides, SDKs and API endpoints</span>
        <kbd>Ctrl K</kbd>
      </button>
      <div class="docs-actions">
        <a class="docs-primary" :href="`${prefix}/getting-started/javascript`">Read the quickstart <span aria-hidden="true">→</span></a>
      </div>
    </section>
    <section class="docs-block" aria-labelledby="docs-start">
      <h2 id="docs-start">Start building</h2>
      <div class="docs-cards">
        <a v-for="entry in entries" :key="entry.link" class="docs-card" :href="`${prefix}${entry.link}`" :data-tone="entry.tone">
          <span class="docs-tile" aria-hidden="true"><svg viewBox="0 0 24 24"><use :href="`/brand/docs-icons.svg#i-${entry.icon}`" /></svg></span>
          <h3>{{ entry.title }} <span aria-hidden="true">→</span></h3>
        </a>
      </div>
    </section>
  </div>
</template>
