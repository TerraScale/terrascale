#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptPath = fileURLToPath(import.meta.url)
const defaultRoot = path.resolve(path.dirname(scriptPath), '..')

export const claimInventory = [
  {
    id: 'regions',
    source: 'https://docs.terrascale.tech/en/guides/public-alpha-data-contract/',
    patterns: [/São Paulo/, /San Francisco/, /Falkenstein/],
  },
  {
    id: 'routing',
    source: 'https://docs.terrascale.tech/en/guides/public-alpha-data-contract/',
    patterns: [/nearest healthy selected region/i, /strict regional endpoints/i],
  },
  {
    id: 'replication',
    source: 'https://docs.terrascale.tech/en/reference/replication/',
    patterns: [/asynchronous replication/i, /replication .*selected regions happens later/i],
  },
  {
    id: 'availability-and-latency',
    source: 'https://docs.terrascale.tech/en/about/status-and-incidents/',
    patterns: [/no uptime percentage or SLA/i, /measured API latency/i, /geographic latency estimates/i],
  },
  {
    id: 'security-and-residency',
    source: 'https://docs.terrascale.tech/en/security/',
    patterns: [/Customer content stays in selected regions/i, /TLS 1\.2 or newer/i, /qualified encryption/i, /every request still requires authorization/i],
  },
  {
    id: 'support',
    source: 'https://docs.terrascale.tech/en/support/',
    patterns: [/best-effort email support/i, /two business days/i, /one business day/i, /Enterprise (?:is )?unavailable/i],
  },
]

// These rules apply to rendered HTML as well as source dictionaries. Keep the
// inventory here so a new locale, metadata field, or JSON-LD block cannot
// bypass the launch-phase claim review.
export const prohibitedClaims = [
  { id: 'non-canonical-domain', pattern: /terrascale\.(?:com|io)\b/i, reason: 'Use the canonical terrascale.tech domain.' },
  { id: 'placeholder-community-link', pattern: /discord\.gg\/(?:terrascale|your-|placeholder|invite-link)/i, reason: 'Use the configured Discord invite.' },
  { id: 'unverified-customer-count', pattern: /\b(?:thousands of|miles de|milhares de)\b/i, reason: 'Unverified customer-count claims are prohibited.' },
  { id: 'unsupported-availability', pattern: /\b(?:99[.,]9+\s*%|(?:uptime|availability|disponibilidad|disponibilidade|tiempo de actividad|tempo de atividade|SLA)\s+(?:guarantee|guaranteed|SLA|garantía|garantizado|garantizada|garantia|garantido|garantida)|(?:guaranteed|garantizado|garantizada|garantido|garantida)\s+(?:uptime|availability|disponibilidad|disponibilidade|tiempo de actividad|tempo de atividade)|support SLA|SLA de soporte|SLA de suporte)\b/i, reason: 'Unapproved availability or support SLA claims are prohibited.' },
  { id: 'retired-dashboard-path', pattern: /dash\.terrascale\.tech\/(?:signup|usage)\b/i, reason: 'Use the canonical dashboard registration URL.' },
  { id: 'unsupported-region', pattern: /\b(?:Frankfurt|Guarulhos)\b/i, reason: 'Use the Public Alpha launch regions.' },
  { id: 'deferred-provider', pattern: /\b(?:Stripe|NATS JetStream)\b/i, reason: 'Do not advertise deferred products or providers.' },
  { id: 'deferred-queue-product', pattern: /\b(?:queue|queued|colas?|enfileir(?:ada|ado|ar))\b/i, reason: 'Do not advertise deferred queue products.' },
  { id: 'unsupported-consistency-or-latency', pattern: /\b(?:zero[- ]latency|instant replication|bounded lag|majority write|serve from any region)\b/i, reason: 'Unsupported consistency or latency claims are prohibited.' },
  { id: 'deferred-authentication-product', pattern: /\bauthentication product\b/i, reason: 'Do not advertise deferred authentication products.' },
  { id: 'deferred-backup-product', pattern: /\bbackups?\b/i, reason: 'Do not advertise deferred backup products.' },
  { id: 'obsolete-annual-pricing', pattern: /\b(?:annual billing|annual plan|facturaci[oó]n anual|faturamento anual|cobran[cç]a anual|precios? anuales?|pre[cç]os anuais?)\b/i, reason: 'Annual pricing is not part of the Public Alpha catalog.' },
  { id: 'obsolete-plan', pattern: /\b(?:Scale|Business)\s+(?:plan|pricing|tier|subscription)|\b(?:plan|plano|pricing|precios?|pre[cç]os?)\s+(?:Scale|Business)\b/i, reason: 'Scale and Business plans are retired from the Public Alpha catalog.' },
  { id: 'deferred-queue-product-localized', pattern: /\b(?:filas?|fila)\b/i, reason: 'Do not advertise deferred queue products.' },
  { id: 'positive-paid-overages', pattern: /\b(?:paid\s+overages?|overages?)\s+(?:are|remain|supported|available|included|offered)\b/i, reason: 'Paid overages are not available during Public Alpha.' },
  { id: 'unpublished-terms-route', pattern: /href=["']\/terms["']/i, reason: 'The terms route must remain unpublished until approved.' },
]

export function walk(directory, extensions) {
  const files = []
  if (!fs.existsSync(directory)) return files
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name)
    if (entry.isDirectory()) files.push(...walk(full, extensions))
    else if (extensions.has(path.extname(entry.name))) files.push(full)
  }
  return files.sort()
}

const decodeHtmlEntities = (text) =>
  text
    .replaceAll('&amp;', '&')
    .replaceAll('&quot;', '"')
    .replaceAll('&#39;', "'")
    .replaceAll('&lt;', '<')
    .replaceAll('&gt;', '>')

export function findProhibitedClaims(text) {
  const corpus = decodeHtmlEntities(text)
  return prohibitedClaims
    .map(({ id, reason, pattern }) => ({ id, reason, match: corpus.match(pattern)?.[0] }))
    .filter(({ match }) => match)
}

export function scanProhibitedClaims(files, root) {
  const failures = []
  for (const file of files) {
    const relative = path.relative(root, file)
    for (const { id, reason, match } of findProhibitedClaims(fs.readFileSync(file, 'utf8'))) {
      failures.push(`${relative}: ${id}: ${reason} (${match})`)
    }
  }
  return failures
}

const validateClaimSources = () => {
  const failures = []
  for (const claim of claimInventory) {
    if (!/^https:\/\/docs\.terrascale\.tech\//.test(claim.source)) {
      failures.push(`${claim.id}: claim source must be a canonical HTTPS documentation URL.`)
    }
  }
  return failures
}

export function validateClaims({ root = defaultRoot, rendered = false } = {}) {
  const sourceRoot = path.join(root, 'src')
  const renderedRoot = path.join(root, 'build')
  const failures = validateClaimSources()
  const files = rendered ? walk(renderedRoot, new Set(['.html'])) : walk(sourceRoot, new Set(['.svelte', '.ts']))
  if (!files.length) failures.push(`${rendered ? 'build' : 'src'}: no files available for claim inventory.`)

  failures.push(...scanProhibitedClaims(files, root))
  const corpus = files.map((file) => fs.readFileSync(file, 'utf8')).join('\n')
  for (const claim of claimInventory) {
    for (const pattern of claim.patterns) {
      if (!pattern.test(corpus)) failures.push(`${claim.id}: missing claim evidence for ${pattern}`)
    }
  }
  return failures
}

async function main() {
  const rendered = process.argv.includes('--rendered')
  const root = path.resolve(process.env.TERRASCALE_VALIDATOR_ROOT ?? defaultRoot)
  const failures = validateClaims({ root, rendered })
  if (failures.length) {
    console.error(`Public Alpha claim validation failed (${rendered ? 'rendered output' : 'source'}):`)
    for (const failure of failures) console.error(`- ${failure}`)
    process.exitCode = 1
    return
  }

  console.log(`Public Alpha claim validation passed (${rendered ? 'rendered output' : 'source'})`)
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) await main()
