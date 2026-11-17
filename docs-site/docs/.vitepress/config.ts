import { defineConfig } from 'vitepress'
import { createCssVariablesTheme } from 'shiki'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { LOCALES } from './supportedLocales.mjs'
import { ZERO_CONFIGURATION_ROUTE } from './routes.mjs'
const docsDir = join(dirname(fileURLToPath(import.meta.url)), '..')
const SITE = 'https://docs.terrascale.tech'
const noindexPages = new Set<string>()

const cleanPath = (path: string) => path.replace(/(^|\/)index\.html$/, '$1').replace(/\.html$/, '')
function pageUrl(relativePath: string): string {
  return `${SITE}/${relativePath.replace(/(^|\/)index\.md$/, '$1').replace(/\.md$/, '')}`
}

function titleOf(localePrefix: string, link: string): string {
  const rel = link.replace(/\/$/, '/index').replace(/^\//, '')
  for (const loc of [localePrefix, '']) {
    try {
      const text = readFileSync(join(docsDir, loc, `${rel}.md`), 'utf8')
      const fm = text.match(/^---\s*\n(.*?)\n---\s*\n?/s)
      if (fm) {
        const m = fm[1].match(/^title:\s*(.+?)\s*$/m)
        if (m) return m[1].trim()
      }
      const heading = text.match(/^# (.+)$/m)
      if (heading) return heading[1]
    } catch {}
  }
  return rel.split('/').pop() || link
}

const GROUPS = [
  { header: 'quickstart', items: ['/getting-started/', '/getting-started/javascript', '/getting-started/csharp', '/getting-started/go', '/getting-started/python', '/getting-started/rust'] },
  { header: 'sdks', items: ['/guides/sdks', '/guides/authentication', '/guides/sensitive-actions', '/guides/authentication-capabilities', '/guides/end-user-authentication', '/guides/sql-http', '/guides/postgres-clients', '/guides/orm-drizzle', '/guides/orm-prisma', '/guides/orm-sqlalchemy', '/guides/orm-go', '/guides/orm-dotnet', '/guides/rust-sql', '/guides/errors'] },
  { header: 'guides', items: ['/guides/dashboard', '/guides/multi-region-writes-and-conflicts', '/guides/change-sign-in-email', '/guides/organization-access', '/guides/single-sign-on', '/guides/mcp', '/guides/indexes', '/guides/functions', '/guides/file-storage', '/guides/monitoring', '/guides/backups-and-restore', '/guides/whole-database-recovery', '/guides/branches', '/guides/agent-databases', '/guides/rename-database', '/guides/migrations', '/guides/import', ZERO_CONFIGURATION_ROUTE] },
  { header: 'concepts', items: ['/concepts/sql', '/concepts/regions', '/concepts/replication'] },
  { header: 'pricingLimits', items: ['/pricing/', '/limits', '/legal/sla', '/legal/support', '/legal/data-deletion', '/changelog'] },
]

interface NavLeaf {
  text: string
  link: string
}
interface NavBranch {
  text: string
  items: NavLeaf[]
}
type NavItem = NavLeaf | NavBranch
interface LocaleStrings {
  nav: NavItem[]
  headers: Record<string, string>
  langMenu: string
  sidebarMenu: string
  returnTop: string
  onThisPage: string
  prev: string
  next: string
  updated: string
}

const EN_NAV = [
  { text: 'Documentation', link: '/getting-started/' },
  { text: 'SDKs', link: '/guides/sdks' },
  { text: 'Pricing & limits', link: '/pricing/' },
]

const EN_STRINGS: LocaleStrings = {
  nav: EN_NAV,
  headers: {
    quickstart: 'Getting started',
    concepts: 'Concepts',
    guides: 'Guides',
    sdks: 'SDKs',
    account: 'Account',
    apiRef: 'API reference',
    prodRef: 'Product reference',
    roadmap: 'Roadmap',
    about: 'About',
    drafts: 'Launch posts (drafts)',
    pricingGen: 'Pricing (generated)',
    pricingLimits: 'Resources',
    limitsGen: 'Limits (generated)',
    snippets: 'Snippets',
    support: 'Support',
    security: 'Security',
    changelog: 'Changelog',
  },
  langMenu: 'Change language',
  sidebarMenu: 'Menu',
  returnTop: 'Return to top',
  onThisPage: 'On this page',
  prev: 'Previous page',
  next: 'Next page',
  updated: 'Last updated',
}

const STRINGS: Record<string, LocaleStrings> = {
  en: EN_STRINGS,
}

function sidebarFor(code: string) {
  const prefix = code === 'en' ? '' : `/${code}`
  return GROUPS.map((group) => ({
    text: STRINGS[code].headers[group.header] ?? group.header,
    items: group.items.map((link) => ({
      text: titleOf(code === 'en' ? '' : code, link),
      link: `${prefix}${link}`,
    })),
  }))
}

function navFor(code: string) {
  const prefix = code === 'en' ? '' : `/${code}`
  const withPrefix = (link: string) => (link.startsWith('http') ? link : `${prefix}${link}`)
  return STRINGS[code].nav.map((item) =>
    'items' in item
      ? { text: item.text, items: item.items.map((sub) => ({ ...sub, link: withPrefix(sub.link) })) }
      : { text: item.text, link: withPrefix(item.link) },
  )
}

function themeFor(code: string) {
  const s = STRINGS[code]
  return {
    nav: navFor(code),
    sidebar: sidebarFor(code),
    outline: { level: [2, 3] as [number, number], label: s.onThisPage },
    docFooter: { prev: s.prev, next: s.next },
    lastUpdatedText: s.updated,
    langMenuLabel: s.langMenu,
    sidebarMenuLabel: s.sidebarMenu,
    returnToTopLabel: s.returnTop,
  }
}

const localesConfig: Record<
  string,
  { label: string; lang: string; dir?: 'rtl'; link?: string; themeConfig: ReturnType<typeof themeFor> }
> = {}
for (const l of LOCALES) {
  const key = l.code === 'en' ? 'root' : l.code
  localesConfig[key] = {
    label: l.label,
    lang: l.lang,
    ...(l.dir === 'rtl' ? { dir: 'rtl' as const } : {}),
    ...(l.code === 'en' ? {} : { link: `/${l.prefix}/` }),
    themeConfig: themeFor(l.code),
  }
}

export default defineConfig({
  title: 'TerraScale',
  description:
    'TerraScale developer documentation: SQL, authentication and service limits.',
  appearance: 'dark',
  vite: {
    build: {
      rollupOptions: {
        output: { hashCharacters: 'hex' },
      },
    },
    plugins: [{
      name: 'workers-module-preload',
      transform(source, id) {
        if (!id.endsWith('/vitepress/dist/client/app/composables/preFetch.js')) return
        const prefetch = 'link.rel = `prefetch`'
        if (!source.includes(prefetch)) throw new Error('VitePress route preload source changed')
        return source.replace(prefetch, 'link.rel = `modulepreload`')
      },
    }],
  },
  markdown: {
    config(md) {
      const renderCode = md.renderer.rules.code_inline!
      md.renderer.rules.code_inline = (tokens, index, options, env, renderer) => {
        const method = tokens[index].content.match(/^(GET|POST|PUT|PATCH|DELETE)(?:\s|$)/)?.[1]
        if (method) {
          tokens[index].attrJoin('class', 'docs-method')
          tokens[index].attrSet('data-method', method)
        }
        return renderCode(tokens, index, options, env, renderer)
      }
    },
  },
  lastUpdated: true,
  srcDir: '.',
  head: [['link', { rel: 'icon', href: '/favicon.svg', type: 'image/svg+xml' }], ['link', { rel: 'preload', href: '/fonts/geist-variable.woff2', as: 'font', type: 'font/woff2', crossorigin: 'anonymous' }]],
  sitemap: {
    hostname: SITE,
    transformItems: (items) =>
      items
        .map((item) => ({
          ...item,
          url: cleanPath(item.url),
          links: item.links?.map((link) => ({ ...link, url: cleanPath(link.url) })),
        }))
        .filter((item) => !noindexPages.has(`${SITE}/${item.url}`)),
  },
  transformPageData(pageData) {
    const head = (pageData.frontmatter.head ?? []) as [string, Record<string, string>][]
    if (head.some(([tag, attrs]) => tag === 'meta' && attrs.name === 'robots' && attrs.content.includes('noindex')))
      noindexPages.add(pageUrl(pageData.relativePath))
  },
  transformHead({ pageData, title, description }) {
    if (pageData.isNotFound) return
    const url = pageUrl(pageData.relativePath)
    return [
      ['link', { rel: 'canonical', href: url }],
      ['meta', { property: 'og:type', content: 'website' }],
      ['meta', { property: 'og:site_name', content: 'TerraScale Docs' }],
      ['meta', { property: 'og:title', content: title }],
      ['meta', { property: 'og:description', content: description }],
      ['meta', { property: 'og:url', content: url }],
      ['meta', { name: 'twitter:card', content: 'summary' }],
    ]
  },
  locales: localesConfig,
  themeConfig: {
    siteTitle: 'Docs',
    logo: { light: '/brand/logo-terrascale-light.svg', dark: '/brand/logo-terrascale-dark.svg', alt: 'TerraScale' },
    search: { provider: 'local' },
    notFound: { quote: '', linkText: 'Back to the docs home' },
  },
})
