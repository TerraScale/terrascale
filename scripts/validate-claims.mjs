#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { decodeHTML } from 'entities'
import { parse as parseHtml, parseFragment } from 'parse5'

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
  { id: 'unsupported-availability', pattern: /(?:\b99[.,]9+\s*%|\b(?:uptime|availability|disponibilidad|disponibilidade|tiempo de actividad|tempo de atividade|SLA)\s+(?:guarantee|guaranteed|SLA|garantía|garantizado|garantizada|garantia|garantido|garantida)\b|\b(?:guaranteed|garantizado|garantizada|garantido|garantida)\s+(?:uptime|availability|disponibilidad|disponibilidade|tiempo de actividad|tempo de atividade)\b|\bsupport SLA\b|\bSLA de soporte\b|\bSLA de suporte\b)/i, reason: 'Unapproved availability or support SLA claims are prohibited.' },
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
  { id: 'positive-enterprise-availability', pattern: /\bEnterprise\b[\s:;,–—-]*(?:(?:is|remains|est[aá])\s+)?(?:available|disponibles?|dispon[ií]ve(?:l|is))\b/i, reason: 'Enterprise is not available during Public Alpha.' },
  { id: 'unpublished-terms-route', pattern: /href=["']\/terms["']/i, reason: 'The terms route must remain unpublished until approved.' },
]

const htmlVoidElements = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr'])
const htmlOptionalEndElements = new Set(['body', 'colgroup', 'dd', 'dt', 'head', 'html', 'li', 'optgroup', 'option', 'p', 'rb', 'rp', 'rt', 'rtc', 'tbody', 'td', 'tfoot', 'th', 'thead', 'tr'])
const htmlIgnoredElements = new Set(['script', 'style'])
const htmlInlineElements = new Set(['a', 'abbr', 'b', 'bdi', 'bdo', 'br', 'button', 'cite', 'code', 'data', 'del', 'dfn', 'em', 'i', 'img', 'ins', 'kbd', 'label', 'mark', 'q', 's', 'samp', 'small', 'span', 'strong', 'sub', 'sup', 'time', 'u', 'var', 'wbr'])

const isElement = (node) => Boolean(node?.tagName)

const isRangeInside = (range, offset) => range && offset >= range.startOffset && offset < range.endOffset

const collapseClaimWhitespace = (text) => String(text).replace(/\p{White_Space}+/gu, ' ')
export const normalizeClaimText = (text) => collapseClaimWhitespace(decodeHTML(String(text)))

const canonicalSiteOrigin = 'https://terrascale.tech'
const canonicalSiteOrigins = new Set([new URL(canonicalSiteOrigin).origin])

const normalizeRoutePath = (pathname) => {
  const decodedPath = decodeURIComponent(pathname)
  const segments = decodedPath.split('/')
  const normalized = []
  for (const segment of segments) {
    if (!segment || segment === '.') continue
    if (segment === '..') normalized.pop()
    else normalized.push(segment)
  }
  const route = `/${normalized.join('/')}`
  return decodedPath.endsWith('/') && route !== '/' ? `${route}/` : route
}

const canonicalizeTermsHref = (value) => {
  const decoded = normalizeClaimText(value).trim()
  let url
  try {
    url = new URL(decoded, `${canonicalSiteOrigin}/`)
  } catch {
    return null
  }
  if (url.protocol !== 'https:' || !canonicalSiteOrigins.has(url.origin)) return null
  let pathname
  try {
    pathname = normalizeRoutePath(url.pathname)
  } catch {
    return null
  }
  return pathname === '/terms' || pathname === '/terms/' ? url : null
}

const sourceHrefValues = (html) => {
  const values = []
  const tagPattern = /<[a-z][^<>]*>/gi
  const attributePattern = /(?:^|\s)href\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'`=<>]+))/gi
  for (const tag of html.matchAll(tagPattern)) {
    for (const match of tag[0].matchAll(attributePattern)) values.push(match[1] ?? match[2] ?? match[3])
  }
  return values
}

const descendantText = (node) => {
  if (node.nodeName === '#text') return node.value
  if (node.nodeName === '#comment' || (isElement(node) && htmlIgnoredElements.has(node.tagName))) return ''
  return (node.childNodes ?? []).map(descendantText).join('')
}

const rawDescendantText = (node) => {
  if (node.nodeName === '#text') return node.value
  return (node.childNodes ?? []).map(rawDescendantText).join('')
}

const parseRenderedDocument = (corpus) => {
  if (!corpus.includes('<')) {
    const tree = parseFragment(corpus)
    const text = descendantText(tree)
    return {
      raw: corpus,
      text,
      ignored: [],
      hrefs: [],
      fallback: '',
    }
  }

  const parseErrors = []
  const isDocument = /<html(?:\s|>)/i.test(corpus) || /^\s*<!doctype\b/i.test(corpus)
  const tree = isDocument
    ? parseHtml(corpus, { sourceCodeLocationInfo: true, onParseError: (error) => parseErrors.push(error) })
    : parseFragment(corpus, { sourceCodeLocationInfo: true, onParseError: (error) => parseErrors.push(error) })

  const ignored = []
  const hrefs = []
  const explicitEndTags = new Set()
  const opaqueRanges = []
  let structuralError = false
  let text = ''
  let previousBlock = null

  const appendText = (value, blockOwner) => {
    if (!value) return
    if (text && previousBlock !== blockOwner) text += ' '
    text += value
    previousBlock = blockOwner
  }

  const appendAttribute = (value) => {
    if (!value) return
    if (text) text += '\u0000'
    text += value
    text += '\u0000'
    previousBlock = null
  }

  const recordOpaqueDescendants = (node) => {
    if (node.sourceCodeLocation && (node.nodeName === '#text' || node.nodeName === '#comment' || node.nodeName === '#documentType')) {
      opaqueRanges.push(node.sourceCodeLocation)
    }
    for (const child of node.childNodes ?? []) recordOpaqueDescendants(child)
  }

  const walk = (node, blockAncestor = null) => {
    if (node.sourceCodeLocation && node.nodeName === '#text') opaqueRanges.push(node.sourceCodeLocation)
    if (node.sourceCodeLocation && node.nodeName === '#comment') opaqueRanges.push(node.sourceCodeLocation)
    if (node.sourceCodeLocation && node.nodeName === '#documentType') opaqueRanges.push(node.sourceCodeLocation)
    if (!isElement(node)) {
      if (node.nodeName === '#text') appendText(node.value, blockAncestor)
      for (const child of node.childNodes ?? []) walk(child, blockAncestor)
      return
    }

    const location = node.sourceCodeLocation
    if (location?.endTag) explicitEndTags.add(location.endTag.startOffset)
    if (location && !htmlVoidElements.has(node.tagName) && !htmlOptionalEndElements.has(node.tagName) && !location.endTag) structuralError = true
    for (const attribute of Object.values(location?.attrs ?? {})) {
      opaqueRanges.push(attribute)
    }
    const isIgnored = htmlIgnoredElements.has(node.tagName)
    for (const attribute of node.attrs) {
      if (attribute.name.toLowerCase() === 'href') hrefs.push(attribute.value)
      if (isIgnored) ignored.push({ raw: attribute.value, text: attribute.value, decoded: true })
      else appendAttribute(attribute.value)
    }
    const isBlock = !htmlInlineElements.has(node.tagName) && !htmlVoidElements.has(node.tagName) && !isIgnored
    if (isIgnored) {
      recordOpaqueDescendants(node)
      const raw = rawDescendantText(node)
      ignored.push({ raw, text: raw, decoded: false })
      return
    }

    const currentBlock = isBlock ? node : blockAncestor
    if (node.tagName === 'br') appendText(' ', currentBlock)
    for (const child of node.childNodes ?? []) walk(child, currentBlock)
  }
  walk(tree)

  const addIgnoredValue = (node) => {
    if (node.nodeName === '#comment') ignored.push({ raw: node.data, text: node.data, decoded: false })
    for (const child of node.childNodes ?? []) addIgnoredValue(child)
  }
  addIgnoredValue(tree)

  for (const match of corpus.matchAll(/<\/\s*[a-z][\w:-]*\s*>/gi)) {
    if (!opaqueRanges.some((range) => isRangeInside(range, match.index)) && !explicitEndTags.has(match.index)) structuralError = true
  }
  structuralError ||= parseErrors.length > 0
  return { raw: corpus, text, ignored, hrefs, fallback: structuralError ? corpus : '' }
}

const findRenderedClaimMatch = (rendered, claim) => {
  if (claim.id === 'unpublished-terms-route') {
    const href = rendered.hrefs.find((value) => canonicalizeTermsHref(value))
    if (href) return [`href="${href}"`]
    return null
  }
  const values = [
    [rendered.raw, false],
    [rendered.text, true],
    ...rendered.ignored.flatMap(({ raw, text, decoded }) => [[raw, false], [text, decoded]]),
    [rendered.fallback, false],
  ]
  for (const [value, decoded] of values) {
    const match = (decoded ? collapseClaimWhitespace(value) : normalizeClaimText(value)).match(claim.pattern)
    if (match) return match
  }
  return null
}

const findClaimMatch = (corpus, claim) => {
  if (claim.id === 'unpublished-terms-route') {
    const href = sourceHrefValues(corpus).find((value) => canonicalizeTermsHref(value))
    return href ? [`href="${href}"`] : null
  }
  return normalizeClaimText(corpus).match(claim.pattern)
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

export function findProhibitedClaims(text, { mode = 'source' } = {}) {
  const rendered = mode === 'rendered'
  const corpus = rendered ? parseRenderedDocument(text) : text
  return prohibitedClaims
    .map((claim) => {
      const match = rendered
        ? findRenderedClaimMatch(corpus, claim)
        : findClaimMatch(corpus, claim)
      return { id: claim.id, reason: claim.reason, match: match?.[0] }
    })
    .filter(({ match }) => match)
}

export function scanProhibitedClaims(files, root, { mode = 'source' } = {}) {
  const failures = []
  for (const file of files) {
    const relative = path.relative(root, file)
    for (const { id, reason, match } of findProhibitedClaims(fs.readFileSync(file, 'utf8'), { mode })) {
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

  failures.push(...scanProhibitedClaims(files, root, { mode: rendered ? 'rendered' : 'source' }))
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
