import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { parse as parseHtml } from 'parse5'
import { scanProhibitedClaims, walk } from './validate-claims.mjs'

const defaultRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const root = path.resolve(process.env.TERRASCALE_VALIDATOR_ROOT ?? defaultRoot)
const failures = []

const sourceFiles = walk(path.join(root, 'src'), new Set(['.svelte', '.ts']))
failures.push(...scanProhibitedClaims(sourceFiles, root, { mode: 'source' }))
const renderedFiles = walk(path.join(root, 'build'), new Set(['.html']))
if (renderedFiles.length) failures.push(...scanProhibitedClaims(renderedFiles, root, { mode: 'rendered' }))

const pricing = fs.readFileSync(path.join(root, 'src/components/sections/PricingSection.svelte'), 'utf8')
const site = fs.readFileSync(path.join(root, 'src/lib/site.ts'), 'utf8')
const pricingCatalog = JSON.parse(fs.readFileSync(path.join(root, 'static/pricing.json'), 'utf8'))
const regionCatalog = JSON.parse(fs.readFileSync(path.join(root, 'static/regions.json'), 'utf8'))

// static/pricing.json is the canonical catalog (published at
// https://terrascale.tech/pricing.json). Its prices and allowances must stay
// on the confirmed Public Alpha plans: Hobby $0, Starter $5 (5 databases,
// 5 GB, 1M units/month), Pro $50 (25 databases, 25 GB, 100M units/month), and
// the Enterprise $500 support base.
const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value)

// The confirmed metering contract. Other surfaces render these strings, so
// any change must be a deliberate edit here and in pricing.json together.
export const confirmedMetering = {
  operationUnit: {
    bytes: 4096,
    definition: 'One operation unit is each started 4 KiB (4,096 bytes) of a logical object successfully read or written.',
    rules: [
      'Lists, queries, and batches count each item independently.',
      'Writes count once per selected region; reads count only in the serving region.',
      'Failed, denied, invalid, throttled, and not-found requests are not billed.',
    ],
  },
  storage: 'Stored bytes count once in every selected region.',
  allowances: 'Plans use hard allowances during Public Alpha. New billable work stops at the allowance. Reads, exports, deletion, and usage reduction remain available.',
}

export function validatePricingCatalog(catalog) {
  const catalogFailures = []
  // id, name, monthly, annual, available, trialDays, [projects, databases, members, storageGb, units]
  const expected = [
    ['hobby', 'Hobby', 0, 0, true, null, [1, 1, 1, 0.1, 100_000]],
    ['starter', 'Starter', 5, 50, true, 30, [5, 5, 5, 5, 1_000_000]],
    ['pro', 'Pro', 50, 500, true, 30, [25, 25, 25, 25, 100_000_000]],
    ['enterprise', 'Enterprise', 500, 5000, false, null, [null, null, null, null, null]],
  ]
  if (!isObject(catalog)) return ['pricing.json: the catalog must be a JSON object.']
  const plans = Array.isArray(catalog.plans) ? catalog.plans : []
  if (!Array.isArray(catalog.plans)) catalogFailures.push('pricing.json: plans must be an array.')
  if (catalog.currency !== 'USD') catalogFailures.push('pricing.json: currency must be USD.')
  plans.forEach((plan, index) => {
    if (!isObject(plan)) catalogFailures.push(`pricing.json: plans[${index}] must be an object.`)
    else if (!isObject(plan.limits)) catalogFailures.push(`pricing.json: plans[${index}].limits must be an object.`)
  })
  const ids = plans.map((plan) => (isObject(plan) ? plan.id : undefined))
  if (ids.join() !== expected.map(([id]) => id).join()) {
    catalogFailures.push(`pricing.json: plans must be ${expected.map(([id]) => id).join(', ')} in order.`)
  }
  for (const [id, name, monthly, annual, available, trialDays, limits] of expected) {
    const plan = plans.find((candidate) => isObject(candidate) && candidate.id === id)
    if (!plan) continue
    if (plan.name !== name) catalogFailures.push(`pricing.json: ${id} must be named ${name}.`)
    if (plan.priceMonthlyUsd !== monthly) catalogFailures.push(`pricing.json: unexpected ${name} monthly price ${plan.priceMonthlyUsd}.`)
    if (plan.priceAnnualUsd !== annual) catalogFailures.push(`pricing.json: unexpected ${name} annual price ${plan.priceAnnualUsd}.`)
    if (plan.available !== available) catalogFailures.push(`pricing.json: ${name} must have available ${available}.`)
    if (plan.trialDays !== trialDays) catalogFailures.push(`pricing.json: ${name} must have trialDays ${trialDays}.`)
    const actual = ['projects', 'databases', 'members', 'storageGb', 'operationUnitsPerMonth'].map((key) =>
      isObject(plan.limits) ? plan.limits[key] : undefined,
    )
    if (actual.some((value, i) => value !== limits[i])) {
      catalogFailures.push(`pricing.json: ${name} limits do not match the Public Alpha catalog.`)
    }
  }
  const metering = isObject(catalog.metering) ? catalog.metering : {}
  const unit = isObject(metering.operationUnit) ? metering.operationUnit : {}
  if (
    unit.bytes !== confirmedMetering.operationUnit.bytes ||
    unit.definition !== confirmedMetering.operationUnit.definition ||
    JSON.stringify(unit.rules) !== JSON.stringify(confirmedMetering.operationUnit.rules)
  ) {
    catalogFailures.push('pricing.json: operation units must keep the confirmed started-4 KiB read/write definition and rules.')
  }
  if (!isObject(metering.storage) || metering.storage.definition !== confirmedMetering.storage) {
    catalogFailures.push('pricing.json: storage must count stored bytes once in every selected region.')
  }
  if (
    !isObject(metering.allowances) ||
    metering.allowances.paidOverages !== false ||
    metering.allowances.definition !== confirmedMetering.allowances
  ) {
    catalogFailures.push('pricing.json: Public Alpha must keep hard allowances without paid overages.')
  }
  return catalogFailures
}

export function validateRegionCatalog(catalog) {
  const expected = [
    ['gru', 'São Paulo', 'sa-saopaulo-1'],
    ['sfo', 'San Jose', 'us-sanjose-1'],
    ['fra', 'Frankfurt', 'eu-frankfurt-1'],
  ]
  const regions = isObject(catalog) && Array.isArray(catalog.regions) ? catalog.regions : []
  const malformed = regions.flatMap((region, index) => (isObject(region) ? [] : [`regions.json: regions[${index}] must be an object.`]))
  if (malformed.length) return malformed
  const actual = regions.map(({ id, name, providerRegion }) => [id, name, providerRegion].join('|'))
  return actual.join() === expected.map((region) => region.join('|')).join()
    ? []
    : ['regions.json: launch regions must be gru São Paulo, sfo San Jose, and fra Frankfurt on their OCI regions.']
}

const textOf = (node, separator) =>
  node.nodeName === '#text' ? node.value : (node.childNodes ?? []).map((child) => textOf(child, separator)).join(separator)
const hasClass = (node, name) =>
  node.attrs?.some((attr) => attr.name === 'class' && attr.value.split(/\s+/).includes(name)) ?? false

function findByClass(node, name, found = []) {
  if (hasClass(node, name)) found.push(node)
  for (const child of node.childNodes ?? []) findByClass(child, name, found)
  if (node.content) findByClass(node.content, name, found)
  return found
}

const normalize = (text) => text.replace(/\s+/g, ' ').trim()

function findPlanCards(root) {
  const cards = []
  for (const card of findByClass(root, 'pricing-card')) {
    cards.push({
      plan: card.attrs.find((attr) => attr.name === 'data-plan')?.value ?? null,
      text: normalize(textOf(card, ' ')),
      prices: findByClass(card, 'pricing-card__price').map((price) => textOf(price, '').replace(/\s+/g, '')),
      features: findByClass(card, 'pricing-card__features').flatMap((list) =>
        (list.childNodes ?? []).filter((node) => node.nodeName === 'li').map((item) => normalize(textOf(item, ' ')).replace(/^[^\p{L}\p{N}]+/u, '')),
      ),
    })
  }
  return cards
}

// A feature line is an allowance line when it states a project, database,
// member, storage, or operation-unit quantity.
const allowancePattern = /^[\d.,]+\s*(?:MB|GB)?\s*(?:projects?|databases?|members?|across regions|operation units\/month)$/

// English allowance lines as PricingSection renders them from pricing.json.
export function expectedPlanLines(limits) {
  const number = (n) => n.toLocaleString('en-US')
  const count = (n, noun) => `${number(n)} ${noun}${n === 1 ? '' : 's'}`
  const lines = []
  if (limits.projects !== null) lines.push(count(limits.projects, 'project'))
  if (limits.databases !== null) lines.push(count(limits.databases, 'database'))
  if (limits.members !== null) lines.push(count(limits.members, 'member'))
  if (limits.storageGb !== null) {
    lines.push(`${limits.storageGb < 1 ? `${number(Math.round(limits.storageGb * 1000))} MB` : `${number(limits.storageGb)} GB`} across regions`)
  }
  if (limits.operationUnitsPerMonth !== null) lines.push(`${number(limits.operationUnitsPerMonth)} operation units/month`)
  return lines
}

// The prerendered pricing page must show exactly the plans, monthly prices,
// and allowances of pricing.json.
export function validateRenderedPricing(html, catalog) {
  const renderedFailures = []
  const cards = findPlanCards(parseHtml(html))
  const ids = catalog.plans.map((plan) => plan.id)
  const renderedIds = cards.map((card) => card.plan)
  if (renderedIds.includes(null)) renderedFailures.push('pricing.html: every pricing card must carry a data-plan id.')
  const duplicates = renderedIds.filter((id, index) => id !== null && renderedIds.indexOf(id) !== index)
  if (duplicates.length) renderedFailures.push(`pricing.html: duplicate plan cards ${[...new Set(duplicates)].join(', ')}.`)
  if (renderedIds.join() !== ids.join()) {
    renderedFailures.push(`pricing.html: rendered plan cards ${renderedIds.join(', ')} must be ${ids.join(', ')}.`)
  }
  for (const plan of catalog.plans) {
    const card = cards.find((candidate) => candidate.plan === plan.id)
    if (!card) continue
    if (card.prices.length !== 1 || card.prices[0] !== `$${plan.priceMonthlyUsd}`) {
      renderedFailures.push(`pricing.html: ${plan.name} must render the monthly price $${plan.priceMonthlyUsd} (found ${card.prices.join(', ') || 'none'}).`)
    }
    if (!card.text.includes(plan.name)) renderedFailures.push(`pricing.html: ${plan.id} card must show the plan name ${plan.name}.`)
    const expectedLines = expectedPlanLines(plan.limits)
    const renderedLines = card.features.filter((line) => allowancePattern.test(line))
    if (renderedLines.join('\n') !== expectedLines.join('\n')) {
      renderedFailures.push(`pricing.html: ${plan.name} must render exactly the allowances ${expectedLines.join('; ') || '(none)'} (found ${renderedLines.join('; ') || 'none'}).`)
    }
  }
  return renderedFailures
}

failures.push(...validatePricingCatalog(pricingCatalog), ...validateRegionCatalog(regionCatalog))
if (/\b(?:price|annualPrice|operationUnits|storageGb):\s*\d/.test(site)) {
  failures.push('site.ts: plan prices and allowances must come from static/pricing.json, not literals.')
}
const buildDir = path.join(root, 'build')
if (fs.existsSync(buildDir)) {
  const renderedPricing = path.join(buildDir, 'pricing.html')
  if (!fs.existsSync(renderedPricing)) failures.push('build/pricing.html: the pricing page must be prerendered.')
  else failures.push(...validateRenderedPricing(fs.readFileSync(renderedPricing, 'utf8'), pricingCatalog))
  for (const file of ['pricing.json', 'regions.json']) {
    const published = path.join(buildDir, file)
    if (!fs.existsSync(published) || fs.readFileSync(published, 'utf8') !== fs.readFileSync(path.join(root, 'static', file), 'utf8')) {
      failures.push(`build/${file}: the canonical catalog must be published unchanged at /${file}.`)
    }
  }
}
const englishMessages = fs.readFileSync(path.join(root, 'src/lib/i18n/messages/en.ts'), 'utf8')
for (const phrase of [
  '30-day trial',
  'Monthly support base plus metered usage',
  'Unavailable during Public Alpha',
  'Storage adds bytes in every selected region',
]) {
  if (!englishMessages.includes(phrase)) failures.push(`messages/en.ts: missing catalog wording ${phrase}.`)
}
if (/Stripe|name:\s*'(?:Developer|Scale|Business)'/i.test(`${pricing}\n${site}`)) {
  failures.push('Public Alpha pricing: Stripe and the retired Developer, Scale, and Business plans must stay removed.')
}

for (const url of [
  'https://dash.terrascale.tech/register',
  'https://dash.terrascale.tech/login',
  'https://status.terrascale.tech',
  'https://docs.terrascale.tech',
  'https://docs.terrascale.tech/changelog/',
]) {
  if (!site.includes(url)) failures.push(`site.ts: missing customer link ${url}`)
}

export function validateProductFooterLinks(siteSource) {
  const productFailures = []
  const expectedProductLinks = [
    ['changelog', 'https://docs.terrascale.tech/changelog/'],
    ['documentation', 'https://docs.terrascale.tech'],
  ]
  const productColumn = siteSource.match(/key:\s*'product'[\s\S]*?links:\s*\[([\s\S]*?)\n\s*\],/)?.[1] ?? ''
  const actualProductLinks = [...productColumn.matchAll(/\{\s*key:\s*'([^']+)'\s*,\s*href:\s*'([^']+)'\s*\}/g)].map(([, key, href]) => [key, href])
  for (const [key, href] of expectedProductLinks) {
    if (!actualProductLinks.some(([actualKey, actualHref]) => actualKey === key && actualHref === href)) {
      productFailures.push(`site.ts: product footer link ${key} must use ${href}.`)
    }
  }
  const productExternalLinks = actualProductLinks.filter(([, href]) => /^https:\/\//.test(href)).map(([, href]) => href)
  if (new Set(productExternalLinks).size !== productExternalLinks.length) {
    productFailures.push('site.ts: product footer links must point to distinct canonical destinations.')
  }
  return productFailures
}

failures.push(...validateProductFooterLinks(site))

const redirects = fs.readFileSync(path.join(root, 'static/_redirects'), 'utf8')
if (!redirects.includes('/signup https://dash.terrascale.tech/register 308')) {
  failures.push('static/_redirects: /signup must redirect permanently to the canonical registration URL.')
}
if (!redirects.includes('https://dashboard.terrascale.tech/* https://dash.terrascale.tech/:splat 308')) {
  failures.push('static/_redirects: dashboard host must redirect to dash while preserving the safe path.')
}

const expectedRoutes = ['/', '/about', '/contact', '/pricing']
const sitemap = fs.readFileSync(path.join(root, 'src/routes/sitemap.xml/+server.ts'), 'utf8')
for (const route of expectedRoutes) {
  if (!sitemap.includes(`'${route}'`)) failures.push(`sitemap.xml: missing published route ${route}`)
}
if (fs.existsSync(path.join(root, 'src/routes/terms/+page.svelte'))) {
  failures.push('src/routes/terms/+page.svelte: terms must remain unpublished.')
}

if (failures.length) {
  console.error('Landing content validation failed:')
  failures.forEach(failure => console.error(`- ${failure}`))
  process.exit(1)
}
console.log('landing content validation passed')
