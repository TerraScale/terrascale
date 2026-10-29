<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'
import DefaultTheme from 'vitepress/theme-without-fonts'

const { Layout } = DefaultTheme
const { page, theme, localeIndex } = useData()
const normalize = path => path.replace(/\.(md|html)$/, '').replace(/\/index$/, '/').replace(/\/$/, '')
const group = computed(() => theme.value.sidebar.find(item =>
  item.items?.some(link => normalize(link.link) === normalize(`/${page.value.relativePath}`))))
const home = computed(() => localeIndex.value === 'root' ? '/' : `/${localeIndex.value}/`)
</script>

<template>
  <Layout>
    <template #nav-bar-title-before>
      <img class="docs-brand-mark" src="/favicon.svg" alt="TerraScale" />
      <strong class="docs-brand-word" aria-hidden="true">TerraScale</strong>
    </template>
    <template #doc-before>
      <nav v-if="group" class="docs-breadcrumb" aria-label="Breadcrumb">
        <a :href="home">Docs</a><span aria-hidden="true">/</span>
        <a :href="group.items[0].link">{{ group.text }}</a><span aria-hidden="true">/</span>
        <span aria-current="page">{{ page.title }}</span>
      </nav>
    </template>
  </Layout>
</template>
