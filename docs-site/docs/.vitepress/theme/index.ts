import { h } from 'vue'
import type { Theme } from 'vitepress'
import DefaultTheme from 'vitepress/theme-without-fonts'
import DocsHome from './DocsHome.vue'
import DocsLayout from './DocsLayout.vue'
import SdkQueryTabs from './SdkQueryTabs.vue'
import { installSdkTabs } from '../../../../../src/TerraScale.Dashboard/web/assets/js/sdk-tabs.js'
import '../../../../../src/TerraScale.Dashboard/web/assets/css/sdk-tabs.css'
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
    app.component('SdkQueryTabs', SdkQueryTabs)
  },
  setup() {
    if (typeof document !== 'undefined') installSdkTabs()
  },
} satisfies Theme
