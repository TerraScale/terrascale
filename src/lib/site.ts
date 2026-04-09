import type { AppIconName } from '@/lib/icons'

export type RoutePath = '/' | '/about' | '/contact' | '/pricing'

type IconEntry = {
  icon: AppIconName
}

type RouteEntry = IconEntry & {
  name: string
  href: RoutePath
}

type ExternalLinkEntry = IconEntry & {
  name: string
  href: string
}

type FooterHighlight = IconEntry & {
  label: string
  className: string
}

type SupportChannel = IconEntry & {
  label: string
  href: string
}

export const navigation: RouteEntry[] = [
  { name: 'Home', href: '/' as RoutePath, icon: 'home' },
  { name: 'Pricing', href: '/pricing' as RoutePath, icon: 'dollarSign' },
  { name: 'Contact', href: '/contact' as RoutePath, icon: 'messageCircle' },
  { name: 'About', href: '/about' as RoutePath, icon: 'users' },
]

export const headerLinks: ExternalLinkEntry[] = [
  { name: 'Docs', href: 'https://docs.terrascale.tech', icon: 'bookOpen' },
  { name: 'Status', href: 'https://status.terrascale.tech', icon: 'activity' },
  { name: 'Dashboard', href: 'https://dash.terrascale.tech', icon: 'layoutDashboard' },
]

export const socialLinks: ExternalLinkEntry[] = [
  { name: 'Discord', href: 'https://discord.gg/terrascale', icon: 'brandDiscord' },
  { name: 'Twitter', href: 'https://twitter.com/terrascale', icon: 'brandX' },
  { name: 'LinkedIn', href: 'https://linkedin.com/company/terrascale', icon: 'brandLinkedin' },
  { name: 'GitHub', href: 'https://github.com/terrascale', icon: 'brandGithub' },
]

export const footerLinks = {
  Product: [
    { name: 'Features', href: '/#features' },
    { name: 'Pricing', href: '/pricing' },
    { name: 'Documentation', href: 'https://docs.terrascale.tech' },
    { name: 'API Reference', href: 'https://docs.terrascale.tech/api' },
  ],
  Company: [
    { name: 'About', href: '/about' },
    { name: 'Contact', href: '/contact' },
    { name: 'Status', href: 'https://status.terrascale.tech' },
    { name: 'Blog', href: 'https://blog.terrascale.tech' },
  ],
  Resources: [
    { name: 'Help Center', href: 'https://help.terrascale.tech' },
    { name: 'Community', href: 'https://discord.gg/terrascale' },
    { name: 'Security', href: '/security' },
    { name: 'Terms', href: '/terms' },
  ],
}

export const footerHighlights: FooterHighlight[] = [
  {
    label: '99.99% Uptime',
    icon: 'zap',
    className: 'ts-badge ts-badge-secondary ts-rounded-sm text-[#10B981]',
  },
  {
    label: 'SOC 2 Compliant',
    icon: 'shieldCheck',
    className: 'ts-badge ts-badge-accent ts-rounded-sm text-[#0891B2]',
  },
]

export const supportChannels: SupportChannel[] = [
  { label: 'support@terrascale.tech', href: 'mailto:support@terrascale.tech', icon: 'mail' },
  { label: 'Discord Community', href: 'https://discord.gg/terrascale', icon: 'brandDiscord' },
]
