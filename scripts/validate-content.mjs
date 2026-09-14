import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
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
const expectedPlans = new Map([
  ['Starter', 5],
  ['Pro', 25],
  ['Enterprise', 500],
])
const planMatches = [...site.matchAll(/name:\s*'([^']+)'[\s\S]*?price:\s*(\d+)/g)]
if (planMatches.length !== expectedPlans.size) {
  failures.push('site.ts: Public Alpha catalog must contain exactly three plans.')
}
for (const [, plan, priceText] of planMatches) {
  if (expectedPlans.get(plan) !== Number(priceText)) {
    failures.push(`site.ts: unexpected ${plan} monthly price ${priceText}.`)
  }
  expectedPlans.delete(plan)
}
for (const plan of expectedPlans.keys()) {
  failures.push(`site.ts: missing Public Alpha plan ${plan}.`)
}
const expectedLimits = {
  Starter: [5, 10, 5, 5, 1_000_000],
  Pro: [25, 50, 25, 50, 10_000_000],
  Enterprise: [null, null, null, null, null],
}
for (const [plan, limits] of Object.entries(expectedLimits)) {
  const block = site.match(new RegExp(`name:\\s*'${plan}'([\\s\\S]*?)(?=\\n\\s*},)`))?.[1] ?? ''
  const actual = [...block.matchAll(/(?:projects|databases|members|storageGb|operationUnits):\s*(\d[\d_]*|null)/g)].map(
    ([, value]) => (value === 'null' ? null : Number(value.replaceAll('_', ''))),
  )
  if (actual.length !== limits.length || actual.some((value, i) => value !== limits[i])) {
    failures.push(`site.ts: ${plan} limits do not match the Public Alpha catalog.`)
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
  'https://docs.terrascale.tech/legal/security/',
  'https://docs.terrascale.tech/legal/terms/',
  'https://docs.terrascale.tech/legal/terms/#acceptable-use',
  'https://docs.terrascale.tech/legal/privacy/',
  'https://docs.terrascale.tech/legal/dpa/',
  'https://docs.terrascale.tech/legal/support/',
  'https://docs.terrascale.tech',
  'https://docs.terrascale.tech/changelog/',
]) {
  if (!site.includes(url)) failures.push(`site.ts: missing customer link ${url}`)
}

const expectedPolicyLinks = [
  ['terms', 'https://docs.terrascale.tech/legal/terms/'],
  ['privacy', 'https://docs.terrascale.tech/legal/privacy/'],
  ['acceptableUse', 'https://docs.terrascale.tech/legal/terms/#acceptable-use'],
  ['dataProcessingTerms', 'https://docs.terrascale.tech/legal/dpa/'],
]
const registrationPolicyBlock = site.match(/export const registrationPolicyLinks = \[([\s\S]*?)\] as const/)?.[1] ?? ''
const actualPolicyLinks = [...registrationPolicyBlock.matchAll(/\{\s*key:\s*'([^']+)'\s*,\s*href:\s*'([^']+)'\s*\}/g)].map(([, key, href]) => [key, href])
if (actualPolicyLinks.length !== expectedPolicyLinks.length || actualPolicyLinks.some(([key, href], index) => key !== expectedPolicyLinks[index][0] || href !== expectedPolicyLinks[index][1])) {
  failures.push('site.ts: registration policy links must use the expected key-to-URL mapping.')
}
if (new Set(actualPolicyLinks.map(([, href]) => href)).size !== actualPolicyLinks.length) {
  failures.push('site.ts: policy links must point to distinct canonical destinations.')
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
