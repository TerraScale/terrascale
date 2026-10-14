<script setup>
import { computed, onBeforeUnmount, ref } from 'vue'
import { useData } from 'vitepress'

const { localeIndex } = useData()
const prefix = computed(() => localeIndex.value === 'root' ? '' : `/${localeIndex.value}`)
const entries = [
  { number: '01', title: 'Write your first document', detail: 'Install the JavaScript SDK and run a complete CRUD journey.', link: '/getting-started/javascript', tone: 'blue' },
  { number: '02', title: 'Query and paginate', detail: 'Filter JSON documents, preserve versions, and follow opaque cursors.', link: '/guides/queries', tone: 'cyan' },
  { number: '03', title: 'Connect safely', detail: 'Scope credentials to your application and handle failed requests.', link: '/guides/authentication', tone: 'violet' },
  { number: '04', title: 'Understand the contract', detail: 'Check SDK availability, service limits, and replication boundaries.', link: '/guides/sdks', tone: 'indigo' },
]

// Opens the local search through the same Ctrl K keydown that VitePress listens for on window.
function openSearch() {
  window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true }))
}

const code = ref(null)
const copyStatus = ref('')
let statusTimer
async function copyCode() {
  try {
    await navigator.clipboard.writeText(code.value.textContent)
    copyStatus.value = 'Copied'
  } catch {
    copyStatus.value = 'Copy failed'
  }
  clearTimeout(statusTimer)
  statusTimer = setTimeout(() => { copyStatus.value = '' }, 2000)
}
onBeforeUnmount(() => clearTimeout(statusTimer))
</script>

<template>
  <div class="docs-home">
    <section class="docs-hero">
      <span class="docs-eyebrow">Developer documentation</span>
      <h1>Build with TerraBase</h1>
      <p class="docs-intro">Store JSON documents. Query the data you need. Keep access scoped to your project.</p>
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
      <span class="docs-status">Pre-launch</span>
      <span>JavaScript SDK <code>0.0.3</code> is published. Hosted access and SDK qualification have separate availability checks.</span>
    </div>
    <section class="docs-block" aria-labelledby="docs-start">
      <div class="docs-section-head">
        <h2 id="docs-start">Start building</h2>
        <span>From installation to safe writes</span>
      </div>
      <div class="docs-cards">
        <a v-for="entry in entries" :key="entry.number" class="docs-card" :href="`${prefix}${entry.link}`" :data-tone="entry.tone">
          <span class="docs-tile" aria-hidden="true">{{ entry.number }}</span>
          <h3>{{ entry.title }} <span aria-hidden="true">→</span></h3>
          <p>{{ entry.detail }}</p>
        </a>
      </div>
    </section>
    <section class="docs-block" aria-labelledby="docs-api">
      <div class="docs-section-head">
        <h2 id="docs-api">A small, explicit API</h2>
        <a :href="`${prefix}/getting-started/javascript`">Full example <span aria-hidden="true">→</span></a>
      </div>
      <div class="docs-code-frame">
        <div class="docs-code-label">
          <span>JavaScript · a provisioned database and collection</span>
          <button class="docs-copy" type="button" aria-label="Copy code" @click="copyCode">Copy</button>
          <span class="docs-copy-status" role="status">{{ copyStatus }}</span>
        </div>
        <pre ref="code" class="docs-code" tabindex="0" role="region" aria-label="JavaScript document example"><code><span class="docs-code-keyword">const</span> posts = client.database(<span class="docs-code-string">'quickstart'</span>).collection(<span class="docs-code-string">'posts'</span>)
<span class="docs-code-keyword">const</span> created = <span class="docs-code-keyword">await</span> posts.create({ title: <span class="docs-code-string">'Hello TerraBase'</span> })
<span class="docs-code-keyword">const</span> current = <span class="docs-code-keyword">await</span> posts.get(created.id)</code></pre>
      </div>
    </section>
    <p class="docs-home-foot">TerraScale uses TerraBase through its SDKs. Application data belongs in your own scoped database.</p>
  </div>
</template>
