import type { Theme } from 'vitepress'
import DefaultTheme from 'vitepress/theme-without-fonts'
import DocsHome from './DocsHome.vue'
import './dashboard-tokens.css'
import './style.css'
import './shell-v3.css'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('DocsHome', DocsHome)
  },
} satisfies Theme
