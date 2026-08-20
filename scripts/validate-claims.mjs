#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const sourceRoot = path.join(root, 'src')
const renderedRoot = path.join(root, 'build')
const failures = []

const claimInventory = [
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

const walk = (directory, extensions) => {
  const files = []
  if (!fs.existsSync(directory)) return files
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name)
    if (entry.isDirectory()) files.push(...walk(full, extensions))
    else if (extensions.has(path.extname(entry.name))) files.push(full)
  }
  return files
}

for (const claim of claimInventory) {
  if (!/^https:\/\/docs\.terrascale\.tech\//.test(claim.source)) {
    failures.push(`${claim.id}: claim source must be a canonical HTTPS documentation URL.`)
  }
}

const useRenderedOutput = process.argv.includes('--rendered')
const files = useRenderedOutput
  ? walk(renderedRoot, new Set(['.html']))
  : walk(sourceRoot, new Set(['.svelte', '.ts']))
if (!files.length) failures.push(`${useRenderedOutput ? 'build' : 'src'}: no files available for claim inventory.`)
const corpus = files.map((file) => fs.readFileSync(file, 'utf8')).join('\n')

for (const claim of claimInventory) {
  for (const pattern of claim.patterns) {
    if (!pattern.test(corpus)) failures.push(`${claim.id}: missing claim evidence for ${pattern}`)
  }
}

if (failures.length) {
  console.error(`Public Alpha claim validation failed (${useRenderedOutput ? 'rendered output' : 'source'}):`)
  for (const failure of failures) console.error(`- ${failure}`)
  process.exit(1)
}

console.log(`Public Alpha claim validation passed (${useRenderedOutput ? 'rendered output' : 'source'})`)
