import type { Messages } from '@/lib/i18n'
import type { PlanEntitlements, PlanLimits } from '@/lib/site'

type LimitCopy = Messages['pricing']['limits']

const fill = (template: string, value: string) => template.replace('{n}', value)

const plural = ([one, other]: readonly string[], n: number, format: Intl.NumberFormat) =>
  fill(n === 1 ? one : other, format.format(n))

// Decimal units, matching how allowances are billed: 0.1 GB renders as 100 MB.
const formatStorage = (gb: number, format: Intl.NumberFormat) =>
  gb < 1 ? `${format.format(Math.round(gb * 1000))} MB` : `${format.format(gb)} GB`

type CompareCopy = Messages['pricing']['compare']
type EntitlementPlan = { slug: string; entitlements: PlanEntitlements }

// Renders the entitlement rows of the comparison table from the canonical
// entitlements in static/pricing.json. Null renders as "no plan ceiling".
export function formatEntitlementRows(plans: EntitlementPlan[], copy: CompareCopy, bcp47: string) {
  const format = new Intl.NumberFormat(bcp47)
  const ceiling = (n: number | null, render: (n: number) => string = (v) => format.format(v)) =>
    n === null ? copy.noCeiling : render(n)
  const days = (n: number) => plural(copy.days, n, format)
  const row = (label: string, cell: (e: PlanEntitlements) => string) =>
    ({ label, cells: plans.map((plan) => cell(plan.entitlements)) })
  return [
    row(copy.requestsPerSecond, (e) => ceiling(e.requestsPerSecond)),
    row(copy.burstRequests, (e) => ceiling(e.burstRequests)),
    row(copy.concurrentRequests, (e) => ceiling(e.concurrentRequests)),
    row(copy.queryTimeout, (e) => ceiling(e.queryTimeoutSeconds, (n) => fill(copy.seconds, format.format(n)))),
    row(copy.storageLimit, (e) => ceiling(e.storageLimitGb, (n) => formatStorage(n, format))),
    row(copy.pitr, (e) => days(e.pitrDays)),
    row(copy.backupRetention, (e) => days(e.backupRetentionDays)),
    row(copy.regionsPerDatabase, (e) => format.format(e.regionsPerDatabase)),
    row(copy.support, (e) => copy.supportTargets[e.support as keyof CompareCopy['supportTargets']]),
  ]
}

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

export type BillingInterval = 'month' | 'year'
type BilledPlan = { price: number; annualPrice: number | null; available: boolean; billingIntervals: readonly BillingInterval[] }

const currency = (bcp47: string) => (n: number) => new Intl.NumberFormat(bcp47, {
  style: 'currency', currency: 'USD', minimumFractionDigits: Number.isInteger(n) ? 0 : 2,
  maximumFractionDigits: 2,
}).format(n)

// Months of the monthly price that yearly billing saves, when every paid plan saves the same.
export function annualSavingMonths(plans: readonly BilledPlan[]): number | null {
  const months = [...new Set(plans.filter((plan) => plan.annualPrice !== null && plan.billingIntervals.includes('year'))
    .map((plan) => Math.round(12 - plan.annualPrice! / plan.price)))]
  return months.length === 1 && months[0] > 0 ? months[0] : null
}

// Paid plans sell monthly and yearly, and the chosen interval sets the headline price.
// Free and Enterprise read the same on either interval.
export function formatPlanBilling(plan: BilledPlan, interval: BillingInterval, copy: Messages['pricing'], bcp47: string) {
  const usd = currency(bcp47)
  if (plan.price === 0) return { price: usd(0), suffix: '', cadence: copy.cadence.free }
  if (!plan.available) return { price: usd(plan.price), suffix: copy.suffixUsage, cadence: copy.cadence.contact }
  if (interval === 'year' && plan.annualPrice !== null && plan.billingIntervals.includes('year')) {
    return {
      price: usd(plan.annualPrice),
      suffix: copy.suffixAnnual,
      cadence: copy.cadence.year
        .replace('{monthly}', usd(Math.round((plan.annualPrice / 12) * 100) / 100))
        .replace('{months}', String(annualSavingMonths([plan]) ?? 0)),
    }
  }
  return { price: usd(plan.price), suffix: copy.suffixMonthly, cadence: copy.cadence.month }
}

// Every price a plan is sold at, for the comparison table.
export function formatPlanCadences(plan: BilledPlan, copy: Messages['pricing'], bcp47: string) {
  const usd = currency(bcp47)
  if (plan.price === 0) return copy.cadence.free
  if (!plan.available || plan.annualPrice === null) return copy.cadence.contact
  return copy.cadence.both.replace('{monthly}', usd(plan.price)).replace('{annual}', usd(plan.annualPrice))
}
