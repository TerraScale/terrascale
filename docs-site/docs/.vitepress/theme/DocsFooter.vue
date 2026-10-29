<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useData, withBase } from 'vitepress'

const { theme } = useData()
const year = new Date().getFullYear()
// Statuspage indicator -> shared data-state contract. Unknown until the public status feed answers.
const STATES: Record<string, string> = { none: 'operational', minor: 'degraded', major: 'partial', critical: 'outage', maintenance: 'maintenance' }
const state = ref('none')
const label = ref('System status')
onMounted(async () => {
  try {
    const res = await fetch('https://status.terrascale.tech/api/v1/status.json')
    const { status } = await res.json()
    if (STATES[status?.indicator] && status.description) {
      state.value = STATES[status.indicator]
      label.value = status.description
    }
  } catch {
    // Offline or blocked: keep the neutral "System status" link.
  }
})
</script>

<template>
  <footer class="docs-footer">
    <a :href="withBase('/')" class="docs-footer-brand" aria-label="TerraScale docs home">
      <img class="docs-logo-dark" :src="withBase('/brand/logo-terrascale-principal.svg')" alt="TerraScale" height="18">
      <img class="docs-logo-light" :src="withBase('/brand/logo-terrascale-blue.svg')" alt="TerraScale" height="18">
    </a>
    <span>© {{ year }} TerraScale</span>
    <nav aria-label="Footer">
      <a href="https://terrascale.tech">terrascale.tech</a>
      <a href="https://github.com/TerraScale">GitHub</a>
    </nav>
    <a class="docs-status-link" href="https://status.terrascale.tech" :data-state="state">
      <span class="docs-dot" :class="{ 'docs-dot-live': state !== 'none' }" aria-hidden="true"></span>{{ label }}
    </a>
    <p v-if="theme.footer?.message" class="docs-footer-note">{{ theme.footer.message }}</p>
  </footer>
</template>
