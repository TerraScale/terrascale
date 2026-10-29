import type { AppIconName } from '@/lib/icons'
import type { BillingInterval } from '@/lib/pricing'
import pricingJson from '../../static/pricing.json'
import regionsJson from '../../static/regions.json'

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

// Plan prices, allowances, and metering live in static/pricing.json, which is
// also published at https://terrascale.tech/pricing.json for the docs,
// dashboard, and status surfaces. Only presentation metadata lives here.
export const pricingCatalog = pricingJson
export const regionCatalog = regionsJson

export type PlanLimits = {
  projects: number | null
  databases: number | null
  members: number | null
  storageGb: number | null
  operationUnitsPerMonth: number | null
  egressGbPerMonth: number | null
}

// Per-plan ceilings TerraBase enforces; null means no plan ceiling.
export type PlanEntitlements = {
  requestsPerSecond: number | null
  burstRequests: number | null
  concurrentRequests: number | null
  queryTimeoutSeconds: number | null
  storageLimitGb: number | null
  pitrDays: number
  backupRetentionDays: number
  regionsPerDatabase: number
  support: string
}

export type PublicAlphaPlanSlug = 'free' | 'starter' | 'pro' | 'team' | 'enterprise'

const planPresentation: Record<
  PublicAlphaPlanSlug,
  { icon: AppIconName; popular: boolean }
> = {
  free: { icon: 'zap', popular: false },
  starter: { icon: 'star', popular: false },
  pro: { icon: 'trendingUp', popular: true },
  team: { icon: 'users', popular: false },
  enterprise: { icon: 'shieldCheck', popular: false },
}

export const publicAlphaCatalog = pricingJson.plans.map((plan) => ({
  name: plan.name,
  slug: plan.id as PublicAlphaPlanSlug,
  ...planPresentation[plan.id as PublicAlphaPlanSlug],
  accent: 'var(--ts-primary)',
  accentRgb: 'var(--ts-primary-rgb)',
  available: plan.available,
  price: plan.priceMonthlyUsd,
  annualPrice: plan.priceAnnualUsd,
  billingIntervals: plan.billingIntervals as BillingInterval[],
  limits: plan.limits as PlanLimits,
  entitlements: plan.entitlements as PlanEntitlements,
}))

export const launchRegions = regionsJson.regions

export const getStartedLink = {
  href: registrationUrl,
  icon: 'arrowRight' as AppIconName,
}

export type FooterColumnKey = 'product' | 'company' | 'resources'

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
      { key: 'changelog', href: 'https://docs.terrascale.tech/changelog/' },
      { key: 'documentation', href: 'https://docs.terrascale.tech' },
    ],
  },
  {
    key: 'company',
    links: [
      { key: 'about', href: '/about' },
      { key: 'contact', href: '/contact' },
      { key: 'terms', href: 'https://docs.terrascale.tech/legal/terms' },
      { key: 'privacy', href: 'https://docs.terrascale.tech/legal/privacy' },
      { key: 'dpa', href: 'https://docs.terrascale.tech/legal/dpa' },
    ],
  },
  {
    key: 'resources',
    links: [
      { key: 'apiReference', href: 'https://api.terrascale.tech/scalar/' },
      { key: 'status', href: 'https://status.terrascale.tech' },
      { key: 'support', href: 'https://docs.terrascale.tech/legal/support' },
      { key: 'supportEmail', href: 'mailto:support@terrascale.tech' },
      { key: 'community', href: 'https://discord.gg/8Zr2Nw9g' },
    ],
  },
]

export type SocialKey = 'github' | 'discord' | 'x' | 'linkedin' | 'email'

export type SocialLink = {
  key: SocialKey
  href: string
  icon: AppIconName
}

export const socialLinks: SocialLink[] = [
  {
    key: 'github',
    href: 'https://github.com/TerraScale',
    icon: 'brandGithub',
  },
  {
    key: 'discord',
    href: 'https://discord.gg/8Zr2Nw9g',
    icon: 'brandDiscord',
  },
  {
    key: 'x',
    href: 'https://twitter.com/terrascale',
    icon: 'brandX',
  },
  {
    key: 'linkedin',
    href: 'https://linkedin.com/company/terrascale',
    icon: 'brandLinkedin',
  },
  {
    key: 'email',
    href: 'mailto:support@terrascale.tech',
    icon: 'mail',
  },
]
