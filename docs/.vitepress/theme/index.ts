import { h } from 'vue'
import type { Theme } from 'vitepress'
import DefaultTheme from 'vitepress/theme-without-fonts'
import DocsHome from './DocsHome.vue'
import DocsEyebrow from './DocsEyebrow.vue'
import DocsFooter from './DocsFooter.vue'
import DocsNotFound from './DocsNotFound.vue'
import './tokens.css'
import './style.css'

export default {
  extends: DefaultTheme,
  Layout: () =>
    h(DefaultTheme.Layout, null, {
      'doc-before': () => h(DocsEyebrow),
      'doc-after': () => h(DocsFooter),
      'not-found': () => [h(DocsNotFound), h(DocsFooter)],
    }),
  enhanceApp({ app }) {
    app.component('DocsHome', DocsHome)
  },
} satisfies Theme
