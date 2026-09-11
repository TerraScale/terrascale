import type { AppIconName } from '@/lib/icons'

export type RoutePath = '/' | '/about' | '/contact' | '/pricing'

export type NavKey = 'home' | 'about' | 'pricing' | 'contact'

export type NavEntry = {
  key: NavKey
  href: RoutePath
  icon: AppIconName
}

export const navigation: NavEntry[] = [
  { key: 'home', href: '/', icon: 'home' },
  { key: 'about', href: '/about', icon: 'users' },
  { key: 'pricing', href: '/pricing', icon: 'dollarSign' },
  { key: 'contact', href: '/contact', icon: 'mail' },
]

export const docsLink = {
  href: 'https://docs.terrascale.tech',
  icon: 'bookOpen' as AppIconName,
}

export const registrationUrl = 'https://dash.terrascale.tech/register'

export const loginUrl = 'https://dash.terrascale.tech/login'

export const publicAlphaCatalog = [
  {
    name: 'Starter',
    slug: 'starter',
    icon: 'star' as AppIconName,
    accent: 'var(--ts-secondary)',
    accentRgb: 'var(--ts-secondary-rgb)',
    popular: false,
    price: 5,
    projects: 5,
    databases: 10,
    members: 5,
    storageGb: 5,
    operationUnits: 1_000_000,
  },
  {
    name: 'Pro',
    slug: 'pro',
    icon: 'trendingUp' as AppIconName,
    accent: 'var(--ts-primary)',
    accentRgb: 'var(--ts-primary-rgb)',
    popular: true,
    price: 25,
    projects: 25,
    databases: 50,
    members: 25,
    storageGb: 50,
    operationUnits: 10_000_000,
  },
  {
    name: 'Enterprise',
    slug: 'enterprise',
    icon: 'shieldCheck' as AppIconName,
    accent: 'var(--ts-tertiary)',
    accentRgb: 'var(--ts-tertiary-rgb)',
    popular: false,
    price: 500,
    projects: null,
    databases: null,
    members: null,
    storageGb: null,
    operationUnits: null,
  },
] as const

export type PublicAlphaPlanSlug = (typeof publicAlphaCatalog)[number]['slug']

export const registrationPolicyLinks = [
  { key: 'terms', href: 'https://docs.terrascale.tech/legal/terms/' },
  { key: 'privacy', href: 'https://docs.terrascale.tech/legal/privacy/' },
  { key: 'acceptableUse', href: 'https://docs.terrascale.tech/legal/terms/#acceptable-use' },
  { key: 'dataProcessingTerms', href: 'https://docs.terrascale.tech/legal/dpa/' },
] as const

export const launchPhase =
  import.meta.env.PUBLIC_TERRASCALE_LAUNCH_PHASE === 'public-alpha' ? 'public-alpha' : 'prelaunch'

export const getStartedLink = {
  href: registrationUrl,
  icon: 'arrowRight' as AppIconName,
}

export type FooterColumnKey = 'product' | 'company' | 'resources' | 'legal'

export type FooterColumn = {
  key: FooterColumnKey
  links: { key: string; href: string }[]
}

export const footerColumns: FooterColumn[] = [
  {
    key: 'product',
    links: [
      { key: 'features', href: '/#features' },
      { key: 'pricing', href: '/pricing' },
      { key: 'roadmap', href: 'https://docs.terrascale.tech' },
      { key: 'documentation', href: 'https://docs.terrascale.tech' },
    ],
  },
  {
    key: 'company',
    links: [
      { key: 'about', href: '/about' },
      { key: 'blog', href: 'https://docs.terrascale.tech/blog/' },
      { key: 'contact', href: '/contact' },
    ],
  },
  {
    key: 'resources',
    links: [
      { key: 'docs', href: 'https://docs.terrascale.tech' },
      { key: 'apiReference', href: 'https://docs.terrascale.tech/getting-started/' },
      { key: 'status', href: 'https://status.terrascale.tech' },
      { key: 'community', href: 'https://discord.gg/8Zr2Nw9g' },
      { key: 'security', href: 'https://docs.terrascale.tech/legal/security/' },
      { key: 'support', href: 'https://docs.terrascale.tech/legal/support/' },
    ],
  },
  {
    key: 'legal',
    links: [
      { key: 'terms', href: 'https://docs.terrascale.tech/legal/terms/' },
      { key: 'privacy', href: 'https://docs.terrascale.tech/legal/privacy/' },
      { key: 'acceptableUse', href: 'https://docs.terrascale.tech/legal/terms/#acceptable-use' },
      { key: 'dataProcessingTerms', href: 'https://docs.terrascale.tech/legal/dpa/' },
      { key: 'support', href: 'https://docs.terrascale.tech/legal/support/' },
    ],
  },
]

export type SocialKey = 'github' | 'discord' | 'x' | 'linkedin' | 'email'

export type SocialLink = {
  key: SocialKey
  href: string
  icon: AppIconName
  color: string
  bg: string
  border: string
}

export const socialLinks: SocialLink[] = [
  {
    key: 'github',
    href: 'https://git.terrascale.tech/TerraScale',
    icon: 'brandGithub',
    color: '#F1F5F9',
    bg: 'rgba(241,245,249,0.08)',
    border: 'rgba(241,245,249,0.18)',
  },
  {
    key: 'discord',
    href: 'https://discord.gg/8Zr2Nw9g',
    icon: 'brandDiscord',
    color: '#5865F2',
    bg: 'rgba(88,101,242,0.12)',
    border: 'rgba(88,101,242,0.35)',
  },
  {
    key: 'x',
    href: 'https://twitter.com/terrascale',
    icon: 'brandX',
    color: '#FFFFFF',
    bg: 'rgba(255,255,255,0.06)',
    border: 'rgba(255,255,255,0.18)',
  },
  {
    key: 'linkedin',
    href: 'https://linkedin.com/company/terrascale',
    icon: 'brandLinkedin',
    color: '#0A66C2',
    bg: 'rgba(10,102,194,0.14)',
    border: 'rgba(10,102,194,0.38)',
  },
  {
    key: 'email',
    href: 'mailto:support@terrascale.tech',
    icon: 'mail',
    color: '#10B981',
    bg: 'rgba(16,185,129,0.12)',
    border: 'rgba(16,185,129,0.35)',
  },
]
