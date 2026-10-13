<script setup lang="ts">
import { computed } from 'vue'
import { useData, withBase } from 'vitepress'

const { theme, site, localeIndex } = useData()
const home = computed(() => withBase(site.value.locales[localeIndex.value]?.link ?? '/'))
</script>

<template>
  <main class="docs-404">
    <div class="docs-empty">
      <span class="docs-tile" aria-hidden="true">
        <svg class="docs-icon" viewBox="0 0 24 24"><path d="m13.5 8.5-5 5" /><path d="m8.5 8.5 5 5" /><circle cx="11" cy="11" r="8" /><path d="m21 21-4.3-4.3" /></svg>
      </span>
      <h1><span class="docs-404-code">{{ theme.notFound?.code ?? '404' }}</span> {{ theme.notFound?.title ?? 'Page not found' }}</h1>
      <p>{{ theme.notFound?.quote }}</p>
      <a class="docs-btn docs-btn-secondary" :href="home">{{ theme.notFound?.linkText ?? 'Back to the docs home' }}</a>
    </div>
  </main>
</template>
