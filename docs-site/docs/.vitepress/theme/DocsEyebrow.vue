<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vitepress'
import { useSidebar } from 'vitepress/theme'

// Page header eyebrow: the sidebar group that holds the current page.
const { sidebarGroups } = useSidebar()
const route = useRoute()
const norm = (path: string) => path.replace(/\.html$/, '').replace(/\/index$/, '/')
const group = computed(() =>
  sidebarGroups.value.find((g) => g.items?.some((item) => item.link && norm(item.link) === norm(route.path)))?.text,
)
</script>

<template>
  <p v-if="group" class="docs-eyebrow">{{ group }}</p>
</template>
