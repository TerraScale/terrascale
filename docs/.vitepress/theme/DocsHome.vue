<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'

const { localeIndex } = useData()
const prefix = computed(() => localeIndex.value === 'root' ? '' : `/${localeIndex.value}`)
const entries = [
  { number: '01', title: 'Write your first document', detail: 'Install the JavaScript SDK and run a complete CRUD journey.', link: '/getting-started/javascript' },
  { number: '02', title: 'Query and paginate', detail: 'Filter JSON documents, preserve versions, and follow opaque cursors.', link: '/guides/queries' },
  { number: '03', title: 'Connect safely', detail: 'Scope credentials to your application and handle failed requests.', link: '/guides/authentication' },
  { number: '04', title: 'Understand the contract', detail: 'Check SDK availability, service limits, and replication boundaries.', link: '/guides/sdks' },
]
</script>

<template>
  <div class="docs-home">
    <div class="docs-eyebrow">Developer documentation</div>
    <h1>Build with TerraBase</h1>
    <p class="docs-intro">Store JSON documents. Query the data you need. Keep access scoped to your project.</p>
    <div class="docs-actions">
      <a class="docs-btn docs-btn-primary docs-btn-lg" :href="`${prefix}/getting-started/javascript`">Read the quickstart <svg class="docs-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12h14" /><path d="m12 5 7 7-7 7" /></svg></a>
      <a class="docs-btn docs-btn-secondary docs-btn-lg" :href="`${prefix}/guides/sdks`">Compare SDKs</a>
    </div>
    <div class="docs-release" role="note">
      <span class="docs-badge"><span class="docs-dot" aria-hidden="true"></span>Pre-launch</span>
      <span>JavaScript SDK <code>0.0.3</code> is published. Hosted access and SDK qualification have separate availability checks.</span>
    </div>
    <div class="docs-section-head">
      <h2>Start building</h2>
      <span>From installation to safe writes</span>
    </div>
    <div class="docs-paths">
      <a v-for="entry in entries" :key="entry.number" :href="`${prefix}${entry.link}`">
        <span class="docs-path-number" aria-hidden="true">{{ entry.number }}</span>
        <div><h3>{{ entry.title }}</h3><p>{{ entry.detail }}</p></div>
        <svg class="docs-icon docs-path-arrow" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 18 6-6-6-6" /></svg>
      </a>
    </div>
    <div class="docs-section-head">
      <h2>A small, explicit API</h2>
      <a :href="`${prefix}/getting-started/javascript`">Full example</a>
    </div>
    <div class="docs-code">
      <div class="docs-code-label">JavaScript · a provisioned database and collection</div>
      <pre tabindex="0" role="region" aria-label="JavaScript document example"><code><span class="docs-code-keyword">const</span> posts = client.database(<span class="docs-code-string">'quickstart'</span>).collection(<span class="docs-code-string">'posts'</span>)
<span class="docs-code-keyword">const</span> created = <span class="docs-code-keyword">await</span> posts.create({ title: <span class="docs-code-string">'Hello TerraBase'</span> })
<span class="docs-code-keyword">const</span> current = <span class="docs-code-keyword">await</span> posts.get(created.id)</code></pre>
    </div>
    <p class="docs-home-foot">TerraScale uses TerraBase through its SDKs. Application data belongs in your own scoped database.</p>
  </div>
</template>
