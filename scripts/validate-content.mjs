import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const failures = []
const extensions = new Set(['.svelte', '.ts'])
const forbidden = [
  [/terrascale\.(?:com|io)\b/i, 'Use the canonical terrascale.tech domain.'],
  [/discord\.gg\/(?:terrascale|your-|placeholder|invite-link)/i, 'Use the configured Discord invite.'],
  [/\b(?:thousands of|miles de|milhares de)\b/i, 'Unverified customer-count claims are prohibited.'],
  [/\b(?:99\.9+%|support SLA|SLA de soporte|SLA de suporte)\b/i, 'Unapproved availability or support SLA claims are prohibited.'],
  [/dash\.terrascale\.tech\/(?:signup|usage)\b/i, 'Use the canonical dashboard registration URL.'],
  [/\b(?:Frankfurt|Guarulhos)\b/i, 'Use the Public Alpha launch regions.'],
  [/\b(?:Stripe|NATS JetStream)\b/i, 'Do not advertise deferred products or providers.'],
  [/\b(?:queue|queued|colas?|enfileir(?:ada|ado|ar))\b/i, 'Do not advertise deferred queue products.'],
  [/\b(?:zero[- ]latency|instant replication|bounded lag|majority write|serve from any region)\b/i, 'Unsupported consistency or latency claims are prohibited.'],
  [/href=["']\/terms["']/i, 'The terms route must remain unpublished until approved.'],
]

function* walk(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name)
    if (entry.isDirectory()) yield* walk(full)
    else if (extensions.has(path.extname(entry.name))) yield full
  }
}

for (const file of walk(path.join(root, 'src'))) {
  const relative = path.relative(root, file)
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/)
  for (const [pattern, reason] of forbidden) {
    lines.forEach((line, index) => {
      if (pattern.test(line)) failures.push(`${relative}:${index + 1}: ${reason}`)
    })
  }
}

const pricing = fs.readFileSync(path.join(root, 'src/components/sections/PricingSection.svelte'), 'utf8')
const site = fs.readFileSync(path.join(root, 'src/lib/site.ts'), 'utf8')
const expectedPlans = new Map([
  ['Developer', 0],
  ['Starter', 5],
  ['Pro', 25],
  ['Enterprise', 500],
])
const planMatches = [...site.matchAll(/name:\s*'([^']+)'[\s\S]*?price:\s*(\d+)/g)]
if (planMatches.length !== expectedPlans.size) {
  failures.push('PricingSection.svelte: Public Alpha catalog must contain exactly four plans.')
}
for (const [, plan, priceText] of planMatches) {
  if (expectedPlans.get(plan) !== Number(priceText)) {
    failures.push(`PricingSection.svelte: unexpected ${plan} monthly price ${priceText}.`)
  }
  expectedPlans.delete(plan)
}
for (const plan of expectedPlans.keys()) {
  failures.push(`PricingSection.svelte: missing Public Alpha plan ${plan}.`)
}
const expectedLimits = {
  Developer: [1, 3, 3, 1, 10_000],
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
  'Non-expiring',
  '30-day trial',
  'Monthly support base plus metered usage',
  'Unavailable during Public Alpha',
  'Storage adds bytes in every selected region',
]) {
  if (!englishMessages.includes(phrase)) failures.push(`messages/en.ts: missing catalog wording ${phrase}.`)
}
if (/annual|Stripe|name:\s*'(?:Scale|Business)'/i.test(`${pricing}\n${site}`)) {
  failures.push('PricingSection.svelte: annual, Stripe, Scale, and Business pricing must stay removed.')
}

for (const url of [
  'https://dash.terrascale.tech/register',
  'https://dash.terrascale.tech/login',
  'https://status.terrascale.tech',
  'https://docs.terrascale.tech/en/security/',
  'https://docs.terrascale.tech/en/legal/terms/',
  'https://docs.terrascale.tech/en/legal/privacy/',
  'https://docs.terrascale.tech/en/legal/acceptable-use/',
  'https://docs.terrascale.tech/en/legal/data-processing-terms/',
  'https://docs.terrascale.tech/en/support/',
]) {
  if (!site.includes(url)) failures.push(`site.ts: missing customer link ${url}`)
}

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
