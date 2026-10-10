import { defineConfig } from 'vitepress'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { LOCALES } from './supportedLocales.mjs'
import { ZERO_CONFIGURATION_ROUTE } from './routes.mjs'

// TerraScale product docs — docs.terrascale.tech.
// Static build (npm run build), default theme, dark default. No design-system
// work here; product UI styling lives in ts-dashboard (D-80).
//
// i18n: 10 first-class locales (en root + 9 prefixed). English is the source
// of truth; `node scripts/i18n-sync.mjs` guarantees every English page has a
// counterpart in every locale (reviewed translation or bannered English
// fallback). Sidebar labels are read from each page's own frontmatter title
// so they always match the page language; section headers and nav chrome
// come from STRINGS below.

const docsDir = join(dirname(fileURLToPath(import.meta.url)), '..')

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
    } catch {
      /* missing file: fall through */
    }
  }
  return rel.split('/').pop() || link
}

// Shared page trees (English links). Per locale they are prefixed and
// relabeled from each page's frontmatter. The legal pages are unpublished
// drafts (noindex, Draft banner) and stay out of the nav and sidebar until
// the legal entity details exist.
const TREES: Record<string, { groups: { header: string; items: string[] }[] }> = {
  '/getting-started/': { groups: [{ header: 'quickstart', items: ['/getting-started/', '/getting-started/csharp'] }] },
  '/concepts/': {
    groups: [
      {
        header: 'concepts',
        items: [
          '/concepts/replication',
          '/concepts/sync',
          '/concepts/sql',
          '/concepts/pricing-philosophy',
        ],
      },
    ],
  },
  '/guides/': { groups: [{ header: 'guides', items: ['/guides/', '/guides/atlas', '/guides/sdks', ZERO_CONFIGURATION_ROUTE] }] },
  '/blog/': {
    groups: [
      {
        header: 'drafts',
        items: [
          '/blog/reads-are-free',
          '/blog/benchmarks',
          '/blog/replication',
          '/blog/database-per-customer',
        ],
      },
    ],
  },
  '/pricing': {
    groups: [{ header: 'pricingGen', items: ['/pricing'] }],
  },
  '/pricing/': {
    groups: [{ header: 'pricingLimits', items: ['/pricing/'] }],
  },
  '/limits': { groups: [{ header: 'limitsGen', items: ['/limits'] }] },
  '/snippets': { groups: [{ header: 'snippets', items: ['/snippets'] }] },
  '/changelog': { groups: [{ header: 'changelog', items: ['/changelog'] }] },
}

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
  footer: string
}

// English nav keeps the existing dropdown structure.
const EN_NAV = [
  { text: 'Getting started', link: '/getting-started/' },
  {
    text: 'Concepts',
    items: [
      { text: 'Replication model', link: '/concepts/replication' },
      { text: 'Sync model', link: '/concepts/sync' },
      { text: 'SQL', link: '/concepts/sql' },
      { text: 'Pricing philosophy', link: '/concepts/pricing-philosophy' },
    ],
  },
  { text: 'Guides', link: '/guides/' },
  { text: 'Pricing & limits', link: '/pricing/' },
  { text: 'Snippets', link: '/snippets' },
  {
    text: 'Blog',
    items: [
      { text: 'Reads are free: how (draft)', link: '/blog/reads-are-free' },
      { text: 'Benchmark publication policy', link: '/blog/benchmarks' },
      { text: 'Choosing durability (draft)', link: '/blog/replication' },
      { text: 'A database per customer (draft)', link: '/blog/database-per-customer' },
    ],
  },
  { text: 'Changelog', link: '/changelog' },
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
    pricingGen: 'Pricing (draft, generated)',
    pricingLimits: 'Pricing & limits',
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
  footer:
    'TerraScale developer documentation. Preparing for public self-service launch.',
}

const flatNav = (
  t: Record<string, string>,
): { text: string; link: string }[] => [
  { text: t.gettingStarted, link: '/getting-started/' },
  { text: t.guides, link: '/guides/' },
  { text: t.pricing, link: '/pricing/' },
  { text: t.snippets, link: '/snippets' },
  { text: t.changelog, link: '/changelog' },
]

const STRINGS: Record<string, LocaleStrings> = {
  en: EN_STRINGS,
  es: {
    nav: flatNav({
      gettingStarted: 'Primeros pasos',
      guides: 'Guías',
      account: 'Cuenta',
      reference: 'Referencia',
      roadmap: 'Hoja de ruta',
      pricing: 'Precios y límites',
      snippets: 'Fragmentos',
      blog: 'Blog',
      changelog: 'Cambios',
      status: 'Estado',
    }),
    headers: {
      quickstart: 'Primeros pasos',
      concepts: 'Conceptos',
      guides: 'Guías',
      sdks: 'SDK',
      account: 'Cuenta',
      apiRef: 'Referencia de API',
      prodRef: 'Referencia del producto',
      roadmap: 'Hoja de ruta',
      about: 'Acerca de',
      drafts: 'Publicaciones de lanzamiento (borradores)',
      pricingGen: 'Precios (borrador, generado)',
      pricingLimits: 'Precios y límites',
      limitsGen: 'Límites (generado)',
      snippets: 'Fragmentos',
      support: 'Soporte',
      security: 'Seguridad',
      changelog: 'Cambios',
    },
    langMenu: 'Cambiar idioma',
    sidebarMenu: 'Menú',
    returnTop: 'Volver arriba',
    onThisPage: 'En esta página',
    prev: 'Página anterior',
    next: 'Página siguiente',
    updated: 'Última actualización',
    footer:
      'Documentación de TerraScale para bases de datos, autenticación, facturación, SDK, CLI y panel. En preparación para el lanzamiento público.',
  },
  'pt-br': {
    nav: flatNav({
      gettingStarted: 'Primeiros passos',
      guides: 'Guias',
      account: 'Conta',
      reference: 'Referência',
      roadmap: 'Roteiro',
      pricing: 'Preços e limites',
      snippets: 'Snippets',
      blog: 'Blog',
      changelog: 'Alterações',
      status: 'Status',
    }),
    headers: {
      quickstart: 'Primeiros passos',
      concepts: 'Conceitos',
      guides: 'Guias',
      sdks: 'SDKs',
      account: 'Conta',
      apiRef: 'Referência da API',
      prodRef: 'Referência do produto',
      roadmap: 'Roteiro',
      about: 'Sobre',
      drafts: 'Posts de lançamento (rascunhos)',
      pricingGen: 'Preços (rascunho, gerado)',
      pricingLimits: 'Preços e limites',
      limitsGen: 'Limites (gerado)',
      snippets: 'Snippets',
      support: 'Suporte',
      security: 'Segurança',
      changelog: 'Alterações',
    },
    langMenu: 'Trocar idioma',
    sidebarMenu: 'Menu',
    returnTop: 'Voltar ao topo',
    onThisPage: 'Nesta página',
    prev: 'Página anterior',
    next: 'Próxima página',
    updated: 'Última atualização',
    footer:
      'Documentação do TerraScale para banco de dados, autenticação, cobrança, SDK, CLI e painel. Em preparação para o lançamento público.',
  },
  fr: {
    nav: flatNav({
      gettingStarted: 'Démarrage',
      guides: 'Guides',
      account: 'Compte',
      reference: 'Référence',
      roadmap: 'Feuille de route',
      pricing: 'Tarifs et limites',
      snippets: 'Extraits',
      blog: 'Blog',
      changelog: 'Journal',
      status: 'Statut',
    }),
    headers: {
      quickstart: 'Démarrage',
      concepts: 'Concepts',
      guides: 'Guides',
      sdks: 'SDK',
      account: 'Compte',
      apiRef: 'Référence API',
      prodRef: 'Référence produit',
      roadmap: 'Feuille de route',
      about: 'À propos',
      drafts: 'Articles de lancement (brouillons)',
      pricingGen: 'Tarifs (brouillon, généré)',
      pricingLimits: 'Tarifs et limites',
      limitsGen: 'Limites (généré)',
      snippets: 'Extraits',
      support: 'Assistance',
      security: 'Sécurité',
      changelog: 'Journal',
    },
    langMenu: 'Changer de langue',
    sidebarMenu: 'Menu',
    returnTop: 'Haut de page',
    onThisPage: 'Sur cette page',
    prev: 'Page précédente',
    next: 'Page suivante',
    updated: 'Dernière mise à jour',
    footer:
      'Infrastructure pour base de données, authentification, files, facturation, SDK, CLI et tableau de bord. Pré-lancement, bêta sur liste d\u2019attente.',
  },
  de: {
    nav: flatNav({
      gettingStarted: 'Erste Schritte',
      guides: 'Anleitungen',
      account: 'Konto',
      reference: 'Referenz',
      roadmap: 'Roadmap',
      pricing: 'Preise und Limits',
      snippets: 'Snippets',
      blog: 'Blog',
      changelog: 'Changelog',
      status: 'Status',
    }),
    headers: {
      quickstart: 'Erste Schritte',
      concepts: 'Konzepte',
      guides: 'Anleitungen',
      sdks: 'SDKs',
      account: 'Konto',
      apiRef: 'API-Referenz',
      prodRef: 'Produktreferenz',
      roadmap: 'Roadmap',
      about: 'Über uns',
      drafts: 'Launch-Beiträge (Entwürfe)',
      pricingGen: 'Preise (Entwurf, generiert)',
      pricingLimits: 'Preise und Limits',
      limitsGen: 'Limits (generiert)',
      snippets: 'Snippets',
      support: 'Support',
      security: 'Sicherheit',
      changelog: 'Changelog',
    },
    langMenu: 'Sprache wechseln',
    sidebarMenu: 'Menü',
    returnTop: 'Nach oben',
    onThisPage: 'Auf dieser Seite',
    prev: 'Vorherige Seite',
    next: 'Nächste Seite',
    updated: 'Zuletzt aktualisiert',
    footer:
      'TerraScale-Entwicklerdokumentation. Pre-Launch, Wartelisten-Beta.',
  },
  'zh-cn': {
    nav: flatNav({
      gettingStarted: '快速上手',
      guides: '指南',
      account: '账户',
      reference: '参考',
      roadmap: '路线图',
      pricing: '定价与限额',
      snippets: '代码片段',
      blog: '博客',
      changelog: '更新日志',
      status: '状态',
    }),
    headers: {
      quickstart: '快速上手',
      concepts: '概念',
      guides: '指南',
      sdks: 'SDK',
      account: '账户',
      apiRef: 'API 参考',
      prodRef: '产品参考',
      roadmap: '路线图',
      about: '关于',
      drafts: '发布文章（草稿）',
      pricingGen: '定价（草稿，生成）',
      pricingLimits: '定价与限额',
      limitsGen: '限额（生成）',
      snippets: '代码片段',
      support: '支持',
      security: '安全',
      changelog: '更新日志',
    },
    langMenu: '切换语言',
    sidebarMenu: '菜单',
    returnTop: '回到顶部',
    onThisPage: '本页目录',
    prev: '上一页',
    next: '下一页',
    updated: '最后更新',
    footer: '面向数据库、认证、队列、计费、SDK、CLI 与控制台工作流的基础设施。预发布，等候名单内测。',
  },
  hi: {
    nav: flatNav({
      gettingStarted: 'शुरुआत करें',
      guides: 'मार्गदर्शिकाएँ',
      account: 'खाता',
      reference: 'संदर्भ',
      roadmap: 'रोडमैप',
      pricing: 'मूल्य और सीमाएँ',
      snippets: 'स्निपेट',
      blog: 'ब्लॉग',
      changelog: 'परिवर्तन-लॉग',
      status: 'स्थिति',
    }),
    headers: {
      quickstart: 'शुरुआत करें',
      concepts: 'अवधारणाएँ',
      guides: 'मार्गदर्शिकाएँ',
      sdks: 'SDK',
      account: 'खाता',
      apiRef: 'API संदर्भ',
      prodRef: 'उत्पाद संदर्भ',
      roadmap: 'रोडमैप',
      about: 'परिचय',
      drafts: 'लॉन्च लेख (मसौदे)',
      pricingGen: 'मूल्य (मसौदा, जनरेटेड)',
      pricingLimits: 'मूल्य और सीमाएँ',
      limitsGen: 'सीमाएँ (जनरेटेड)',
      snippets: 'स्निपेट',
      support: 'सहायता',
      security: 'सुरक्षा',
      changelog: 'परिवर्तन-लॉग',
    },
    langMenu: 'भाषा बदलें',
    sidebarMenu: 'मेनू',
    returnTop: 'ऊपर जाएँ',
    onThisPage: 'इस पृष्ठ पर',
    prev: 'पिछला पृष्ठ',
    next: 'अगला पृष्ठ',
    updated: 'अंतिम अद्यतन',
    footer:
      'डेटाबेस, प्रमाणीकरण, कतार, बिलिंग, SDK, CLI और डैशबोर्ड वर्कफ़्लो हेतु बुनियादी ढांचा। प्री-लॉन्च, प्रतीक्षा-सूची बीटा।',
  },
  ar: {
    nav: flatNav({
      gettingStarted: 'البدء',
      guides: 'الأدلة',
      account: 'الحساب',
      reference: 'المرجع',
      roadmap: 'خارطة الطريق',
      pricing: 'الأسعار والحدود',
      snippets: 'مقاطع',
      blog: 'المدونة',
      changelog: 'سجل التغييرات',
      status: 'الحالة',
    }),
    headers: {
      quickstart: 'البدء',
      concepts: 'المفاهيم',
      guides: 'الأدلة',
      sdks: 'SDK',
      account: 'الحساب',
      apiRef: 'مرجع API',
      prodRef: 'مرجع المنتج',
      roadmap: 'خارطة الطريق',
      about: 'حول',
      drafts: 'مقالات الإطلاق (مسودات)',
      pricingGen: 'الأسعار (مسودة، مولّدة)',
      pricingLimits: 'الأسعار والحدود',
      limitsGen: 'الحدود (مولّدة)',
      snippets: 'مقاطع',
      support: 'الدعم',
      security: 'الأمان',
      changelog: 'سجل التغييرات',
    },
    langMenu: 'تغيير اللغة',
    sidebarMenu: 'القائمة',
    returnTop: 'العودة للأعلى',
    onThisPage: 'في هذه الصفحة',
    prev: 'الصفحة السابقة',
    next: 'الصفحة التالية',
    updated: 'آخر تحديث',
    footer:
      'بنية تحتية لتدفقات قواعد البيانات والمصادقة والقوائم والفوترة وSDK وCLI ولوحة التحكم. ما قبل الإطلاق، نسخة تجريبية بقائمة انتظار.',
  },
  ru: {
    nav: flatNav({
      gettingStarted: 'Начало работы',
      guides: 'Руководства',
      account: 'Аккаунт',
      reference: 'Справочник',
      roadmap: 'Дорожная карта',
      pricing: 'Цены и лимиты',
      snippets: 'Сниппеты',
      blog: 'Блог',
      changelog: 'Изменения',
      status: 'Статус',
    }),
    headers: {
      quickstart: 'Начало работы',
      concepts: 'Концепции',
      guides: 'Руководства',
      sdks: 'SDK',
      account: 'Аккаунт',
      apiRef: 'Справочник API',
      prodRef: 'Справочник продукта',
      roadmap: 'Дорожная карта',
      about: 'О проекте',
      drafts: 'Запусковые статьи (черновики)',
      pricingGen: 'Цены (черновик, сгенерировано)',
      pricingLimits: 'Цены и лимиты',
      limitsGen: 'Лимиты (сгенерировано)',
      snippets: 'Сниппеты',
      support: 'Поддержка',
      security: 'Безопасность',
      changelog: 'Изменения',
    },
    langMenu: 'Сменить язык',
    sidebarMenu: 'Меню',
    returnTop: 'Наверх',
    onThisPage: 'На этой странице',
    prev: 'Назад',
    next: 'Далее',
    updated: 'Обновлено',
    footer:
      'Инфраструктура для рабочих процессов баз данных, аутентификации, очередей, биллинга, SDK, CLI и панели. Предзапуск, бета по списку ожидания.',
  },
  ja: {
    nav: flatNav({
      gettingStarted: 'はじめに',
      guides: 'ガイド',
      account: 'アカウント',
      reference: 'リファレンス',
      roadmap: 'ロードマップ',
      pricing: '料金と制限',
      snippets: 'スニペット',
      blog: 'ブログ',
      changelog: '変更履歴',
      status: 'ステータス',
    }),
    headers: {
      quickstart: 'はじめに',
      concepts: 'コンセプト',
      guides: 'ガイド',
      sdks: 'SDK',
      account: 'アカウント',
      apiRef: 'API リファレンス',
      prodRef: '製品リファレンス',
      roadmap: 'ロードマップ',
      about: '概要',
      drafts: 'ローンチ記事（下書き）',
      pricingGen: '料金（下書き、生成）',
      pricingLimits: '料金と制限',
      limitsGen: '制限（生成）',
      snippets: 'スニペット',
      support: 'サポート',
      security: 'セキュリティ',
      changelog: '変更履歴',
    },
    langMenu: '言語を変更',
    sidebarMenu: 'メニュー',
    returnTop: 'トップへ戻る',
    onThisPage: 'このページ',
    prev: '前へ',
    next: '次へ',
    updated: '最終更新',
    footer:
      'データベース、認証、キュー、課金、SDK、CLI、ダッシュボードのワークフロー基盤。プレローンチ、ウェイティングリスト制ベータ。',
  },
}

function prefixItems(items: string[], prefix: string): string[] {
  return items.map((l) => (l.startsWith('http') ? l : `${prefix}${l}`))
}

function sidebarFor(code: string) {
  const s = STRINGS[code]
  const prefix = code === 'en' ? '' : `/${code}`
  const out: Record<string, { text: string; items: { text: string; link: string }[] }[]> = {}
  for (const [match, tree] of Object.entries(TREES)) {
    const key = match === '/' ? match : `${prefix}${match}`
    out[key] = tree.groups.map((g) => ({
      text: s.headers[g.header] ?? g.header,
      items: prefixItems(g.items, prefix).map((link) => ({
        text: titleOf(code === 'en' ? '' : code, link),
        link,
      })),
    }))
  }
  return out
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
    footer: { message: s.footer, copyright: 'TerraScale' },
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
    'Multi-region document database: leaderless CRDT replication, CDN-backed live queries, SQL you already know. Metered in operation units and storage across selected regions, with hard plan allowances.',
  appearance: 'dark',
  lastUpdated: true,
  srcDir: '.',
  head: [['link', { rel: 'icon', href: '/favicon.svg', type: 'image/svg+xml' }]],
  locales: localesConfig,
  themeConfig: {
    siteTitle: 'TerraScale',
    socialLinks: [
      { icon: 'github', link: 'https://git.terrascale.tech/TerraScale' },
      { icon: 'discord', link: 'https://discord.gg/terrascale' },
    ],
  },
})
