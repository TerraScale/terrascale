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
  { id: 'deferred-queue-product', pattern: /\b(?:queue|queued|colas?|filas?|enfileir(?:ada|ado|ar))\b/i, reason: 'Do not advertise deferred queue products.' },
  { id: 'unsupported-consistency-or-latency', pattern: /\b(?<!sin promesa de )(?<!sem promessa de )(?:zero[- ]latency|latencia cero|latência zero|instant replication|replicaci[oó]n instant[aá]nea|replica[cç][aã]o instant[aâ]nea|bounded lag|lag acotado|retraso acotado|atraso limitado|majority writes?|quorum writes?|escrituras? de mayor[ií]a|escritas majorit[aá]rias|escrituras? de qu[oó]rum|escritas? de qu[oó]rum|serve from any region|serves? from any region|sirve? desde cualquier regi[oó]n|servir de cualquier regi[oó]n|atender de qualquer regi[aã]o)\b/i, reason: 'Unsupported consistency or latency claims are prohibited.' },
  { id: 'deferred-authentication-product', pattern: /\b(?:authentication product|producto de autenticaci[oó]n|produto de autentica[cç][aã]o)\b/i, reason: 'Do not advertise deferred authentication products.' },
  { id: 'deferred-backup-product', pattern: /\b(?:backups?|copias? de seguridad|c[oó]pias? de seguran[cç]a)\b/i, reason: 'Do not advertise deferred backup products.' },
  { id: 'available-backup-product', pattern: /\b(?:backups?|copias? de seguridad|c[oó]pias? de seguran[cç]a)\s+(?:available|disponible|disponibles|disponível|disponíveis|autom[aá]tic[oa]s?)\b/i, reason: 'Do not advertise deferred backup products.' },
  { id: 'obsolete-annual-pricing', pattern: /\b(?:annual billing|annual plan|facturaci[oó]n anual|faturamento anual|cobran[cç]a anual|precios? anuales?|pre[cç]os anuais?)\b/i, reason: 'Annual pricing is not part of the Public Alpha catalog.' },
  { id: 'obsolete-plan', pattern: /\b(?:Scale|Business)\s+(?:plan|pricing|tier|subscription)|\b(?:plan|plano|pricing|precios?|pre[cç]os?)\s+(?:Scale|Business)\b/i, reason: 'Scale and Business plans are retired from the Public Alpha catalog.' },
  { id: 'deferred-queue-product-localized', pattern: /\b(?:filas?|fila)\b/i, reason: 'Do not advertise deferred queue products.' },
  { id: 'positive-paid-overages', pattern: /\b(?:(?:paid\s+overages?|overages?)\s+(?:are|remain|supported|available|included|offered)|excedentes?\s+(?:pagados?|pagos?)\s+(?:est[aá]\s+)?(?:disponibles?|dispon[ií]ve(?:l|is)))\b/i, reason: 'Paid overages are not available during Public Alpha.' },
  { id: 'positive-enterprise-availability', pattern: /\bEnterprise\s+(?:(?:is|remains)\s+available|(?:est[aá]\s+)?(?:disponibles?|dispon[ií]ve(?:l|is)))\b/i, reason: 'Enterprise is not available during Public Alpha.' },
  { id: 'unpublished-terms-route', pattern: /href=["']\/terms["']/i, reason: 'The terms route must remain unpublished until approved.' },
]

const enterpriseAvailabilityQuestionPattern = /^(?:¿\s*)?(?:Enterprise\s+(?:(?:est[aá]\s+)(?:disponibles?|dispon[ií]ve(?:l|is))|(?:is|remains)\s+available)|O\s+Enterprise\s+est[aá]\s+dispon[ií]ve(?:l|is))\s*\?$/i
const htmlVoidElements = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr'])
const htmlRawElements = new Set(['script', 'style'])
const sourceStringPattern = /(['"])((?:\\.|(?!\1)[\s\S])*)\1/g

const isWholeEnterpriseAvailabilityQuestion = (value) => enterpriseAvailabilityQuestionPattern.test(value.trim())

const findHtmlTagEnd = (corpus, start) => {
  let quote = null
  for (let index = start; index < corpus.length; index += 1) {
    const character = corpus[index]
    if (quote) {
      if (character === quote) quote = null
    } else if (character === '"' || character === "'") {
      quote = character
    } else if (character === '>') {
      return index
    }
  }
  return -1
}

const parseHtmlAttributes = (corpus, start, end) => {
  const ranges = []
  let index = start
  while (index < end) {
    while (index < end && /\s/.test(corpus[index])) index += 1
    if (index >= end) break
    if (corpus[index] === '/') return null

    const nameStart = index
    while (index < end && !/[\s=]/.test(corpus[index])) index += 1
    if (index === nameStart) return null
    while (index < end && /\s/.test(corpus[index])) index += 1
    if (corpus[index] !== '=') continue
    index += 1
    while (index < end && /\s/.test(corpus[index])) index += 1
    if (index >= end) return null

    const quote = corpus[index] === '"' || corpus[index] === "'" ? corpus[index] : null
    const valueStart = quote ? index + 1 : index
    if (quote) {
      index += 1
      while (index < end && corpus[index] !== quote) index += 1
      if (index >= end) return null
      if (isWholeEnterpriseAvailabilityQuestion(corpus.slice(valueStart, index))) ranges.push([valueStart, index])
      index += 1
      continue
    }

    while (index < end && !/\s/.test(corpus[index])) {
      if (/[<>"'`=]/.test(corpus[index])) return null
      index += 1
    }
    if (index === valueStart) return null
    if (isWholeEnterpriseAvailabilityQuestion(corpus.slice(valueStart, index))) ranges.push([valueStart, index])
  }
  return ranges
}

const findHtmlQuestionRanges = (corpus) => {
  const ranges = []
  const stack = []
  let index = 0
  let sawTag = false

  while (index < corpus.length) {
    if (corpus[index] !== '<') {
      const nextTag = corpus.indexOf('<', index)
      const end = nextTag < 0 ? corpus.length : nextTag
      if (stack.length === 0 && corpus.slice(index, end).trim()) return null
      if (stack.length > 0 && isWholeEnterpriseAvailabilityQuestion(corpus.slice(index, end))) ranges.push([index, end])
      index = end
      continue
    }

    sawTag = true
    if (corpus.startsWith('<!--', index)) {
      const commentEnd = corpus.indexOf('-->', index + 4)
      if (commentEnd < 0) return null
      index = commentEnd + 3
      continue
    }
    if (/^<!doctype\b/i.test(corpus.slice(index))) {
      const doctypeEnd = findHtmlTagEnd(corpus, index + 2)
      if (doctypeEnd < 0) return null
      index = doctypeEnd + 1
      continue
    }

    const closing = corpus.slice(index).match(/^<\/([a-z][\w:-]*)\s*>/i)
    if (closing) {
      if (stack.pop() !== closing[1].toLowerCase()) return null
      index += closing[0].length
      continue
    }

    const opening = corpus.slice(index).match(/^<([a-z][\w:-]*)/i)
    if (!opening) return null
    const tagName = opening[1].toLowerCase()
    const tagEnd = findHtmlTagEnd(corpus, index + opening[0].length)
    if (tagEnd < 0) return null
    let attributesEnd = tagEnd
    while (attributesEnd > index && /\s/.test(corpus[attributesEnd - 1])) attributesEnd -= 1
    const selfClosing = corpus[attributesEnd - 1] === '/'
    if (selfClosing) attributesEnd -= 1
    const attributeRanges = parseHtmlAttributes(corpus, index + opening[0].length, attributesEnd)
    if (!attributeRanges) return null
    ranges.push(...attributeRanges)
    index = tagEnd + 1

    if (selfClosing || htmlVoidElements.has(tagName)) continue
    if (htmlRawElements.has(tagName)) {
      const rawClose = corpus.slice(index).match(new RegExp(`</${tagName}\\s*>`, 'i'))
      if (!rawClose) return null
      index += rawClose.index + rawClose[0].length
      continue
    }
    stack.push(tagName)
  }

  if (!sawTag || stack.length > 0) return null
  return ranges
}

const isCompleteSourceStringLiteral = (corpus, literal) => {
  if (!isWholeEnterpriseAvailabilityQuestion(literal[2]) || literal[2].includes('\n')) return false

  const lineStart = corpus.lastIndexOf('\n', literal.index) + 1
  const lineEnd = corpus.indexOf('\n', literal.index)
  const line = corpus.slice(lineStart, lineEnd < 0 ? corpus.length : lineEnd)
  const valueStart = literal.index - lineStart
  const valueEnd = valueStart + literal[2].length + 2
  const prefix = line.slice(0, valueStart).trimEnd()
  const suffix = line.slice(valueEnd).trim()
  const isValuePrefix = !prefix || /(?:^|[=:])\s*$/.test(prefix) || /\breturn\s*$/.test(prefix)
  if (!isValuePrefix || suffix && !/^[,;}\]]*$/.test(suffix)) return false
  return true
}

const isEnterpriseQuestionValue = (corpus, matchIndex, htmlQuestionRanges, htmlCandidate) => {
  if (htmlCandidate && !htmlQuestionRanges) return false
  if (isWholeEnterpriseAvailabilityQuestion(corpus)) return true

  const lineStart = corpus.lastIndexOf('\n', matchIndex) + 1
  const lineEnd = corpus.indexOf('\n', matchIndex)
  if (isWholeEnterpriseAvailabilityQuestion(corpus.slice(lineStart, lineEnd < 0 ? corpus.length : lineEnd))) return true

  for (const literal of corpus.matchAll(sourceStringPattern)) {
    const valueStart = literal.index + 1
    const valueEnd = valueStart + literal[2].length
    if (matchIndex >= valueStart && matchIndex < valueEnd && isCompleteSourceStringLiteral(corpus, literal)) return true
  }
  if (htmlQuestionRanges?.some(([valueStart, valueEnd]) => matchIndex >= valueStart && matchIndex < valueEnd)) return true
  return false
}

const findClaimMatch = (corpus, claim) => {
  if (claim.id !== 'positive-enterprise-availability') return corpus.match(claim.pattern)
  const pattern = claim.pattern.flags.includes('g') ? claim.pattern : new RegExp(claim.pattern.source, `${claim.pattern.flags}g`)
  const htmlQuestionRanges = findHtmlQuestionRanges(corpus)
  const htmlCandidate = /^\s*</.test(corpus)
  for (const match of corpus.matchAll(pattern)) {
    if (!isEnterpriseQuestionValue(corpus, match.index, htmlQuestionRanges, htmlCandidate)) return match
  }
  return null
}

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
    .map((claim) => {
      const match = findClaimMatch(corpus, claim)
      return { id: claim.id, reason: claim.reason, match: match?.[0] }
    })
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
