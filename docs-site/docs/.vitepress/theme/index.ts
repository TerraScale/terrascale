import { h } from 'vue'
import type { Theme } from 'vitepress'
import DefaultTheme from 'vitepress/theme-without-fonts'
import DocsHome from './DocsHome.vue'
import DocsLayout from './DocsLayout.vue'
import './dashboard-tokens.css'
import './style.css'
import './shell-v3.css'
import './prose-v3.css'
import './home-v3.css'

export default {
  extends: DefaultTheme,
  Layout: DocsLayout,
  enhanceApp({ app }) {
    app.component('DocsHome', DocsHome)
  },
} satisfies Theme
