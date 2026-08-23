#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
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
const htmlIgnoredElements = new Set(['script', 'style'])
const htmlInlineElements = new Set(['a', 'abbr', 'b', 'bdi', 'bdo', 'br', 'button', 'cite', 'code', 'data', 'del', 'dfn', 'em', 'i', 'img', 'ins', 'kbd', 'label', 'mark', 'q', 's', 'samp', 'small', 'span', 'strong', 'sub', 'sup', 'time', 'u', 'var', 'wbr'])
const sourceStringPattern = /(['"])((?:\\.|(?!\1)[\s\S])*)\1/g

const isWholeEnterpriseAvailabilityQuestion = (value) => enterpriseAvailabilityQuestionPattern.test(value.trim())

const isElement = (node) => Boolean(node?.tagName)

const isRangeInside = (range, offset) => range && offset >= range.startOffset && offset < range.endOffset

const decodeCodePoint = (value, radix = 10) => {
  const codePoint = Number.parseInt(value, radix)
  return Number.isInteger(codePoint) && codePoint >= 0 && codePoint <= 0x10ffff ? String.fromCodePoint(codePoint) : ''
}

const decodeHtmlEntities = (text) =>
  text
    .replace(/&#x([0-9a-f]+);?/gi, (_, value) => decodeCodePoint(value, 16))
    .replace(/&#(\d+);?/g, (_, value) => decodeCodePoint(value))
    .replace(/&(amp|quot|apos|#39|lt|gt|nbsp);/gi, (_, entity) => ({
      amp: '&',
      quot: '"',
      apos: "'",
      '#39': "'",
      lt: '<',
      gt: '>',
      nbsp: '\u00a0',
    })[entity.toLowerCase()])

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
      segments: [{ start: 0, end: text.length, text, owner: null, boundaryBefore: null }],
      ignored: [],
      fallback: '',
    }
  }

  const parseErrors = []
  const isDocument = /<html(?:\s|>)/i.test(corpus) || /^\s*<!doctype\b/i.test(corpus)
  const tree = isDocument
    ? parseHtml(corpus, { sourceCodeLocationInfo: true, onParseError: (error) => parseErrors.push(error) })
    : parseFragment(corpus, { sourceCodeLocationInfo: true, onParseError: (error) => parseErrors.push(error) })

  const segments = []
  const ignored = []
  const owners = []
  const explicitEndTags = new Set()
  const opaqueRanges = []
  let structuralError = false
  let text = ''
  let previousSegment = null

  const newOwner = ({ kind, node = null, textParts = [], allow = false }) => {
    const owner = { kind, node, textParts, allow, text: '' }
    owners.push(owner)
    return owner
  }

  const appendSegment = (value, owner, boundaryBefore = null) => {
    if (!value) return
    let boundary = boundaryBefore
    if (previousSegment && previousSegment.owner !== owner && boundary === null) {
      if (previousSegment.owner?.kind === 'attribute' || owner?.kind === 'attribute') boundary = 'value'
      else if (previousSegment.owner?.kind === 'block' || owner?.kind === 'block') boundary = 'block'
      else boundary = 'inline'
    }
    if (boundary === 'block') text += ' '
    if (boundary === 'value') text += '\u0000'
    const start = text.length
    text += value
    const segment = { start, end: text.length, text: value, owner, boundaryBefore: boundary }
    segments.push(segment)
    previousSegment = segment
  }

  const appendText = (value, owner) => {
    if (!value) return
    if (owner) owner.textParts.push(value)
    appendSegment(value, owner)
  }

  const walk = (node, blockAncestor = null, flowAncestor = null) => {
    if (node.sourceCodeLocation && node.nodeName === '#text') opaqueRanges.push(node.sourceCodeLocation)
    if (node.sourceCodeLocation && node.nodeName === '#comment') opaqueRanges.push(node.sourceCodeLocation)
    if (node.sourceCodeLocation && node.nodeName === '#documentType') opaqueRanges.push(node.sourceCodeLocation)
    if (!isElement(node)) {
      if (node.nodeName === '#text') appendText(node.value, blockAncestor ?? flowAncestor)
      for (const child of node.childNodes ?? []) walk(child, blockAncestor, flowAncestor)
      return
    }

    const location = node.sourceCodeLocation
    if (location?.endTag) explicitEndTags.add(location.endTag.startOffset)
    for (const attribute of Object.values(location?.attrs ?? {})) {
      opaqueRanges.push(attribute)
    }

    const isIgnored = htmlIgnoredElements.has(node.tagName)
    const isBlock = !htmlInlineElements.has(node.tagName) && !htmlVoidElements.has(node.tagName) && !isIgnored
    if (isIgnored) {
      for (const attribute of node.attrs) ignored.push({ raw: attribute.value, text: decodeHtmlEntities(attribute.value) })
      const raw = rawDescendantText(node)
      ignored.push({ raw, text: decodeHtmlEntities(raw) })
      return
    }

    const currentBlock = isBlock ? newOwner({ kind: 'block', node, allow: Boolean(location?.endTag) }) : blockAncestor
    const currentFlow = !currentBlock && !flowAncestor ? newOwner({ kind: 'inline', node, allow: Boolean(location?.endTag) }) : flowAncestor
    for (const attribute of node.attrs) {
      const owner = newOwner({ kind: 'attribute', node, textParts: [attribute.value], allow: true })
      appendSegment(attribute.value, owner, 'value')
    }
    for (const child of node.childNodes ?? []) walk(child, currentBlock, currentFlow)
  }
  walk(tree)

  const addIgnoredValue = (node) => {
    if (node.nodeName === '#comment') ignored.push({ raw: node.data, text: decodeHtmlEntities(node.data) })
    for (const child of node.childNodes ?? []) addIgnoredValue(child)
  }
  addIgnoredValue(tree)

  for (const match of corpus.matchAll(/<\/\s*[a-z][\w:-]*\s*>/gi)) {
    if (!opaqueRanges.some((range) => isRangeInside(range, match.index)) && !explicitEndTags.has(match.index)) structuralError = true
  }
  structuralError ||= parseErrors.length > 0
  for (const owner of owners) {
    owner.text = owner.textParts.join('')
    if (structuralError) owner.allow = false
  }
  return { raw: corpus, text, segments, ignored, fallback: structuralError ? decodeHtmlEntities(corpus) : '' }
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

const isSourceEnterpriseQuestionValue = (corpus, matchIndex) => {
  if (isWholeEnterpriseAvailabilityQuestion(corpus)) return true

  for (const literal of corpus.matchAll(sourceStringPattern)) {
    const valueStart = literal.index + 1
    const valueEnd = valueStart + literal[2].length
    if (matchIndex >= valueStart && matchIndex < valueEnd && isCompleteSourceStringLiteral(corpus, literal)) return true
  }
  return false
}

const findSourceClaimMatch = (corpus, claim) => {
  if (claim.id !== 'positive-enterprise-availability') return corpus.match(claim.pattern)
  const pattern = claim.pattern.flags.includes('g') ? claim.pattern : new RegExp(claim.pattern.source, `${claim.pattern.flags}g`)
  for (const match of corpus.matchAll(pattern)) {
    if (!isSourceEnterpriseQuestionValue(corpus, match.index)) return match
  }
  return null
}

const isRenderedEnterpriseQuestion = (rendered, match) => {
  const matchEnd = match.index + match[0].length
  const matchingSegments = rendered.segments.filter(({ start, end }) => end > match.index && start < matchEnd)
  const owners = [...new Set(matchingSegments.map(({ owner }) => owner))]
  if (owners.length !== 1 || !owners[0]?.allow || !isWholeEnterpriseAvailabilityQuestion(owners[0].text)) return false

  const suffix = rendered.segments.find(({ start, text: value }) => start >= matchEnd && value.trim())
  if (!suffix) return true
  if (suffix.boundaryBefore === 'value') return true
  if (suffix.boundaryBefore === 'inline') return false
  const suffixText = rendered.text.slice(suffix.start).trimStart()
  return !/^[^\p{L}]*\p{Ll}/u.test(suffixText)
}

const findRenderedClaimMatch = (rendered, claim) => {
  if (claim.id === 'unpublished-terms-route') return rendered.raw.match(claim.pattern)

  // Raw markup catches structural claims, while the decoded stream catches
  // visible text and attribute values that parse5 normalizes. Ignored nodes
  // stay separate so a claim cannot be assembled across hidden content.
  if (claim.id === 'positive-enterprise-availability') {
    const pattern = claim.pattern.flags.includes('g') ? claim.pattern : new RegExp(claim.pattern.source, `${claim.pattern.flags}g`)
    for (const match of rendered.text.matchAll(pattern)) {
      if (!isRenderedEnterpriseQuestion(rendered, match)) return match
    }
    for (const value of [...rendered.ignored, ...(rendered.fallback ? [{ raw: rendered.fallback, text: rendered.fallback }] : [])]) {
      for (const source of [value.raw, value.text]) {
        const match = source.match(claim.pattern)
        if (match) return match
      }
    }
    return null
  }

  for (const value of [rendered.raw, rendered.text, ...rendered.ignored.flatMap(({ raw, text }) => [raw, text]), rendered.fallback]) {
    const match = value.match(claim.pattern)
    if (match) return match
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

export function findProhibitedClaims(text, { mode = 'source' } = {}) {
  const rendered = mode === 'rendered'
  const corpus = rendered ? parseRenderedDocument(text) : decodeHtmlEntities(text)
  return prohibitedClaims
    .map((claim) => {
      const match = rendered
        ? findRenderedClaimMatch(corpus, claim)
        : findSourceClaimMatch(claim.id === 'positive-enterprise-availability' ? text : corpus, claim)
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
