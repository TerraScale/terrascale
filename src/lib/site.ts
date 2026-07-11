import type { AppIconName } from '@/lib/icons'

export type RoutePath = '/' | '/about' | '/contact' | '/pricing' | '/terms'

// Display labels are intentionally NOT stored here — they live in the i18n
// dictionaries (src/lib/i18n/messages/*.ts) and are resolved by key. This file
// remains the single source of truth for the locale-independent parts that are
// easy to get wrong: route hrefs, icon names, link ordering, and external URLs.

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

export const getStartedLink = {
  href: 'https://dash.terrascale.tech/login',
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
      { key: 'roadmap', href: 'https://docs.terrascale.tech/en/roadmap/' },
      { key: 'documentation', href: 'https://docs.terrascale.tech' },
    ],
  },
  {
    key: 'company',
    links: [
      { key: 'about', href: '/about' },
      { key: 'blog', href: 'https://docs.terrascale.tech/en/blog/' },
      { key: 'contact', href: '/contact' },
    ],
  },
  {
    key: 'resources',
    links: [
      { key: 'docs', href: 'https://docs.terrascale.tech' },
      { key: 'apiReference', href: 'https://docs.terrascale.tech/en/reference/api/' },
      { key: 'status', href: 'https://status.terrascale.tech' },
      { key: 'community', href: 'https://discord.gg/terrascale' },
      { key: 'support', href: '/contact' },
    ],
  },
  {
    key: 'legal',
    links: [
      { key: 'terms', href: '/terms' },
      { key: 'support', href: '/contact' },
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
    href: 'https://git.mariogk.com/Terrascale',
    icon: 'brandGithub',
    color: '#F1F5F9',
    bg: 'rgba(241,245,249,0.08)',
    border: 'rgba(241,245,249,0.18)',
  },
  {
    key: 'discord',
    href: 'https://discord.gg/terrascale',
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
