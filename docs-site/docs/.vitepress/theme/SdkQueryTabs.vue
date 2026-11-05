<script setup lang="ts">
import { ref, useId } from 'vue'
import { SDK_SAMPLES, SDK_LANGUAGES } from '../../../../src/TerraScale.Dashboard/web/assets/js/sdk-examples.js'

const id = useId()
const languages = SDK_LANGUAGES
const selected = ref(languages[0].id)
</script>

<template>
  <div class="sdk-code-group">
    <div class="sdk-tabs" role="tablist" aria-label="SDK language">
      <button v-for="language in languages" :key="language.id" type="button" role="tab"
        :id="`${id}-tab-${language.id}`" :aria-controls="`${id}-panel-${language.id}`"
        :aria-selected="selected === language.id" :tabindex="selected === language.id ? 0 : -1"
        @click="selected = language.id">
        <img :src="language.icon" width="16" height="16" alt="" aria-hidden="true" />
        {{ language.label }}
      </button>
    </div>
    <pre v-for="language in languages" :key="language.id" role="tabpanel" tabindex="0"
      :id="`${id}-panel-${language.id}`" :aria-labelledby="`${id}-tab-${language.id}`"
      :hidden="selected !== language.id"><code>{{ SDK_SAMPLES[language.id] }}</code></pre>
  </div>
</template>

<style>
.sdk-code-group {
  margin: 16px 0;
  border: 1px solid var(--ts-line);
  border-radius: var(--ts-radius-lg);
  background: var(--ts-surface);
  overflow: hidden;
}
.sdk-code-group .sdk-tabs { padding-inline: 8px; border-bottom: 1px solid var(--ts-line); }
.sdk-code-group pre { margin: 0; padding: 20px 16px; overflow: auto; font-size: 13px; line-height: 1.7; }
.vp-doc .sdk-code-group pre > code { padding: 0; background: none; border: 0; color: var(--ts-ink); }
</style>
