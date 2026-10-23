import type { Messages } from '@/lib/i18n'
import type { PlanLimits } from '@/lib/site'

type LimitCopy = Messages['pricing']['limits']

const fill = (template: string, value: string) => template.replace('{n}', value)

const plural = ([one, other]: readonly string[], n: number, format: Intl.NumberFormat) =>
  fill(n === 1 ? one : other, format.format(n))

// Decimal units, matching how allowances are billed: 0.1 GB renders as 100 MB.
const formatStorage = (gb: number, format: Intl.NumberFormat) =>
  gb < 1 ? `${format.format(Math.round(gb * 1000))} MB` : `${format.format(gb)} GB`

// Renders the allowance lines of a plan card from the canonical limits in
// static/pricing.json. Custom (null) allowances are omitted.
export function formatPlanLimits(limits: PlanLimits, copy: LimitCopy, bcp47: string): string[] {
  const format = new Intl.NumberFormat(bcp47)
  const lines: string[] = []
  if (limits.projects !== null) lines.push(plural(copy.projects, limits.projects, format))
  if (limits.databases !== null) lines.push(plural(copy.databases, limits.databases, format))
  if (limits.members !== null) lines.push(plural(copy.members, limits.members, format))
  if (limits.storageGb !== null) lines.push(fill(copy.storage, formatStorage(limits.storageGb, format)))
  if (limits.operationUnitsPerMonth !== null) {
    lines.push(fill(copy.operationUnits, format.format(limits.operationUnitsPerMonth)))
  }
  if (limits.egressGbPerMonth !== null) {
    lines.push(fill(copy.egress, formatStorage(limits.egressGbPerMonth, format)))
  }
  return lines
}

// Starter is annual-only; a monthly equivalent must never become a monthly offer.
export function formatPlanBilling(
  plan: { price: number; annualPrice: number | null; billingInterval: 'free' | 'year' | 'month' | 'contact' },
  copy: Messages['pricing'],
  bcp47: string,
) {
  const usd = (n: number) => new Intl.NumberFormat(bcp47, {
    style: 'currency', currency: 'USD', minimumFractionDigits: Number.isInteger(n) ? 0 : 2,
    maximumFractionDigits: 2,
  }).format(n)
  const suffix = {
    free: '', year: copy.suffixAnnual, month: copy.suffixMonthly, contact: copy.suffixUsage,
  }[plan.billingInterval]
  return {
    price: usd(plan.billingInterval === 'year' ? plan.annualPrice! : plan.price),
    suffix,
    cadence: copy.cadence[plan.billingInterval]
      .replace('{annual}', plan.annualPrice === null ? '' : usd(plan.annualPrice))
      .replace('{monthly}', usd(plan.price)),
  }
}
