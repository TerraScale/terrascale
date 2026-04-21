import type { AppIconName } from '@/lib/icons'

export type RoutePath = '/' | '/contact' | '/pricing'

type RouteEntry = {
  name: string
  href: RoutePath
}

export const navigation: RouteEntry[] = [
  { name: 'Home', href: '/' },
  { name: 'Pricing', href: '/pricing' },
  { name: 'Contact', href: '/contact' },
]

export type FooterColumn = {
  title: string
  links: { name: string; href: string }[]
}

export const footerColumns: FooterColumn[] = [
  {
    title: 'Product',
    links: [
      { name: 'Features', href: '/#features' },
      { name: 'Pricing', href: '/pricing' },
      { name: 'Changelog', href: 'https://docs.terrascale.tech/changelog' },
      { name: 'Roadmap', href: 'https://docs.terrascale.tech/roadmap' },
      { name: 'Documentation', href: 'https://docs.terrascale.tech' },
    ],
  },
  {
    title: 'Company',
    links: [
      { name: 'Blog', href: 'https://blog.terrascale.tech' },
      { name: 'Careers', href: 'https://careers.terrascale.tech' },
      { name: 'Contact', href: '/contact' },
      { name: 'Press Kit', href: 'https://docs.terrascale.tech/press' },
    ],
  },
  {
    title: 'Resources',
    links: [
      { name: 'Docs', href: 'https://docs.terrascale.tech' },
      { name: 'API Reference', href: 'https://docs.terrascale.tech/api' },
      { name: 'Status', href: 'https://status.terrascale.tech' },
      { name: 'Community', href: 'https://discord.gg/terrascale' },
      { name: 'Support', href: '/contact' },
    ],
  },
  {
    title: 'Legal',
    links: [
      { name: 'Privacy', href: 'https://docs.terrascale.tech/privacy' },
      { name: 'Terms', href: 'https://docs.terrascale.tech/terms' },
      { name: 'Security', href: 'https://docs.terrascale.tech/security' },
      { name: 'SLA', href: 'https://docs.terrascale.tech/sla' },
      { name: 'Compliance', href: 'https://docs.terrascale.tech/compliance' },
    ],
  },
]

export type SocialLink = {
  name: string
  href: string
  icon: AppIconName
  color: string
  bg: string
  border: string
}

export const socialLinks: SocialLink[] = [
  {
    name: 'GitHub',
    href: 'https://git.mariogk.com/Terrascale',
    icon: 'brandGithub',
    color: '#F1F5F9',
    bg: 'rgba(241,245,249,0.08)',
    border: 'rgba(241,245,249,0.18)',
  },
  {
    name: 'Discord',
    href: 'https://discord.gg/terrascale',
    icon: 'brandDiscord',
    color: '#5865F2',
    bg: 'rgba(88,101,242,0.12)',
    border: 'rgba(88,101,242,0.35)',
  },
  {
    name: 'X',
    href: 'https://twitter.com/terrascale',
    icon: 'brandX',
    color: '#FFFFFF',
    bg: 'rgba(255,255,255,0.06)',
    border: 'rgba(255,255,255,0.18)',
  },
  {
    name: 'LinkedIn',
    href: 'https://linkedin.com/company/terrascale',
    icon: 'brandLinkedin',
    color: '#0A66C2',
    bg: 'rgba(10,102,194,0.14)',
    border: 'rgba(10,102,194,0.38)',
  },
  {
    name: 'Email',
    href: 'mailto:hello@terrascale.tech',
    icon: 'mail',
    color: '#10B981',
    bg: 'rgba(16,185,129,0.12)',
    border: 'rgba(16,185,129,0.35)',
  },
]
