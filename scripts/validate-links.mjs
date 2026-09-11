#!/usr/bin/env node
import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { parse as parseHtml, parseFragment } from 'parse5'

const scriptPath = fileURLToPath(import.meta.url)
const defaultRoot = path.resolve(path.dirname(scriptPath), '..')

export const requiredLinks = [
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
]

if (new Set(requiredLinks).size !== requiredLinks.length) {
  throw new Error('requiredLinks must contain distinct canonical destinations')
}

export const approvedExternalHosts = new Set(requiredLinks.map((href) => new URL(href).hostname))

const normalizeHostname = (hostname) => String(hostname).replace(/\.+$/, '').toLowerCase()

const isPrivateIpv4 = (hostname) => {
  if (net.isIP(hostname) !== 4) return false
  const octets = hostname.split('.').map(Number)
  const [first, second] = octets
  return first === 0 || first === 10 || first === 127 || (first === 169 && second === 254) || (first === 172 && second >= 16 && second <= 31) || (first === 192 && second === 168) || (first === 100 && second >= 64 && second <= 127)
}

const isPrivateHostname = (hostname) => {
  const normalized = normalizeHostname(hostname).replace(/^\[|\]$/g, '')
  if (
    normalized === 'localhost' ||
    normalized.endsWith('.localhost') ||
    normalized.endsWith('.local') ||
    normalized.endsWith('.internal') ||
    normalized.endsWith('.invalid') ||
    normalized.endsWith('.test')
  ) return true

  const mappedDotted = normalized.match(/^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/i)
  if (mappedDotted && isPrivateIpv4(mappedDotted[1])) return true
  const mappedHex = normalized.match(/^::ffff:([0-9a-f]{1,4}):([0-9a-f]{1,4})$/i)
  if (mappedHex) {
    const high = Number.parseInt(mappedHex[1], 16)
    const low = Number.parseInt(mappedHex[2], 16)
    if (isPrivateIpv4(`${high >> 8}.${high & 255}.${low >> 8}.${low & 255}`)) return true
  }

  if (net.isIP(normalized) === 6) {
    const zeroAddress = normalized.split(':').every((part) => part === '' || /^0+$/.test(part))
    return zeroAddress || normalized === '::1' || normalized.startsWith('fc') || normalized.startsWith('fd') || normalized.startsWith('fe8') || normalized.startsWith('fe9') || normalized.startsWith('fea') || normalized.startsWith('feb')
  }
  return isPrivateIpv4(normalized)
}

export function walk(directory) {
  const files = []
  if (!fs.existsSync(directory)) return files
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name)
    if (entry.isDirectory()) files.push(...walk(full))
    else if (entry.name.endsWith('.html')) files.push(full)
  }
  return files.sort()
}

const isElement = (node) => Boolean(node?.tagName)
const nodeChildren = (node) => node?.content?.childNodes ?? node?.childNodes ?? []

const parseRenderedHtml = (html, onParseError) => {
  const isDocument = /<html(?:\s|>)/i.test(html) || /^\s*<!doctype\b/i.test(html)
  const options = onParseError ? { onParseError } : undefined
  return isDocument ? parseHtml(html, options) : parseFragment(html, options)
}

const walkTree = (node, visit) => {
  visit(node)
  for (const child of nodeChildren(node)) walkTree(child, visit)
}

const collectAttributes = (tree) => {
  const attributes = []
  walkTree(tree, (node) => {
    if (!isElement(node)) return
    for (const attribute of node.attrs) {
      const name = attribute.name.toLowerCase()
      if (name === 'href' || name === 'src' || name === 'action' || name === 'content') {
        attributes.push({ name, value: attribute.value })
      }
    }
  })
  return attributes
}

export function extractAttributes(html) {
  return collectAttributes(parseRenderedHtml(html))
}

const collectJsonUrls = (value, urls) => {
  const normalized = typeof value === 'string' ? value.trim() : value
  if (typeof normalized === 'string' && /^(?:https?:\/\/|\/\/)/i.test(normalized)) urls.add(normalized)
  else if (Array.isArray(value)) value.forEach((item) => collectJsonUrls(item, urls))
  else if (value && typeof value === 'object') Object.values(value).forEach((item) => collectJsonUrls(item, urls))
}

const collectStructuredDataUrls = (tree) => {
  const urls = new Set()
  walkTree(tree, (node) => {
    if (!isElement(node) || node.tagName !== 'script') return
    const type = node.attrs.find(({ name }) => name.toLowerCase() === 'type')?.value.trim().toLowerCase()
    if (type !== 'application/ld+json') return
    const payload = nodeChildren(node).map((child) => child.nodeName === '#text' ? child.value : '').join('')
    try {
      collectJsonUrls(JSON.parse(payload), urls)
    } catch {
      urls.add('invalid-json-ld://payload')
    }
  })
  return [...urls]
}

export function extractStructuredDataUrls(html) {
  return collectStructuredDataUrls(parseRenderedHtml(html))
}

const resolveInternalTarget = (buildRoot, pathname) => {
  const cleanPath = pathname.replace(/^\/+/, '')
  const candidates = [
    path.join(buildRoot, cleanPath, 'index.html'),
    path.join(buildRoot, `${cleanPath}.html`),
    path.join(buildRoot, cleanPath || 'index.html'),
  ]
  return candidates.find((candidate) => fs.existsSync(candidate))
}

const checkFragment = (target, fragment, source, failures) => {
  if (!fragment) return
  const html = fs.readFileSync(target, 'utf8')
  const parseErrors = []
  const tree = parseRenderedHtml(html, (error) => parseErrors.push(error))
  for (const error of parseErrors) failures.push(`${target}: malformed HTML: ${error.code}`)
  let found = false
  walkTree(tree, (node) => {
    if (!isElement(node) || found) return
    found = node.attrs.some(({ name, value }) => (name === 'id' || name === 'name') && value === fragment)
  })
  if (!found) failures.push(`${source}: missing internal anchor #${fragment}`)
}

const isExternalHttpUrl = (value) => /^https?:\/\//i.test(String(value).trim())
const isProtocolRelativeUrl = (value) => /^\/\//.test(String(value).trim())

const validateExternalUrl = (value, source, failures) => {
  const normalized = String(value).trim()
  let parsed
  try {
    parsed = new URL(normalized)
  } catch {
    failures.push(`${source}: invalid external URL ${normalized}`)
    return
  }
  if (parsed.protocol !== 'https:') failures.push(`${source}: external URLs must use HTTPS: ${normalized}`)
  if (parsed.username || parsed.password) failures.push(`${source}: external URLs must not contain credentials: ${normalized}`)
  if (isPrivateHostname(parsed.hostname) || /preview|terrascale\.(?:com|io)$/i.test(parsed.hostname)) {
    failures.push(`${source}: preview or non-canonical external host: ${normalized}`)
  }
}

export function validateRenderedLinks({ buildRoot, files = walk(buildRoot) } = {}) {
  const failures = []
  if (!files.length) {
    failures.push('build: no prerendered HTML files found; run npm run build first.')
    return { failures, linkCount: 0, externalLinks: new Set() }
  }

  const links = []
  const externalLinks = new Set()
  const requiredCorpus = []
  const metadataUrls = []
  const structuredDataUrls = []
  for (const file of files) {
    const html = fs.readFileSync(file, 'utf8')
    const parseErrors = []
    const tree = parseRenderedHtml(html, (error) => parseErrors.push(error))
    for (const error of parseErrors) failures.push(`${file}: malformed HTML: ${error.code}`)
    for (const attribute of collectAttributes(tree)) {
      links.push({ file, ...attribute })
      const normalized = attribute.value.trim()
      if (attribute.name === 'href' || attribute.name === 'content') requiredCorpus.push(normalized)
      if (attribute.name === 'content') metadataUrls.push({ file, value: normalized })
    }
    for (const value of collectStructuredDataUrls(tree)) {
      structuredDataUrls.push({ file, value })
      requiredCorpus.push(value)
    }
  }

  for (const { file, name, value: rawHref } of links) {
    const href = rawHref.trim()
    if (name !== 'href' && name !== 'action' && name !== 'src') {
      if (isExternalHttpUrl(href)) {
        validateExternalUrl(href, file, failures)
      }
      continue
    }
    if (href.startsWith('#')) {
      checkFragment(file, href.slice(1), file, failures)
      continue
    }
    if (href.startsWith('mailto:') || href.startsWith('tel:') || href.startsWith('data:')) continue
    if (href.startsWith('//')) {
      failures.push(`${file}: protocol-relative links are not allowed: ${href}`)
      continue
    }
    if (href.startsWith('/') || href.startsWith('./') || href.startsWith('../')) {
      let parsed
      try {
        parsed = new URL(href, 'https://terrascale.tech')
      } catch {
        failures.push(`${file}: invalid internal link URL ${href}`)
        continue
      }
      const target = resolveInternalTarget(buildRoot, parsed.pathname)
      if (!target) failures.push(`${file}: broken internal link ${href}`)
      else checkFragment(target, parsed.hash.slice(1), href, failures)
      continue
    }

    let parsed
    try {
      parsed = new URL(href)
    } catch {
      failures.push(`${file}: invalid link URL ${href}`)
      continue
    }
    validateExternalUrl(href, file, failures)
    externalLinks.add(parsed.href.replace(/\/$/, ''))
  }

  for (const { file, value } of [...metadataUrls, ...structuredDataUrls]) {
    if (value === 'invalid-json-ld://payload') {
      failures.push(`${file}: invalid application/ld+json structured data`)
      continue
    }
    if (isProtocolRelativeUrl(value)) {
      failures.push(`${file}: protocol-relative links are not allowed: ${value}`)
      continue
    }
    if (isExternalHttpUrl(value)) {
      validateExternalUrl(value, `${file} metadata or structured data`, failures)
      try {
        const parsed = new URL(value)
        externalLinks.add(parsed.href.replace(/\/$/, ''))
      } catch {
        // The validator failure above is the user-facing diagnostic.
      }
    }
  }

  const linkCorpus = requiredCorpus.join('\n')
  for (const required of requiredLinks) {
    if (!linkCorpus.includes(required)) failures.push(`build: missing required public link ${required}`)
  }

  return { failures, linkCount: links.length, externalLinks }
}

const normalizeProbeUrl = (value) => {
  const url = new URL(value)
  if (url.pathname === '/' && !url.search && !url.hash) url.pathname = ''
  return url.href
}

const validateProbeTarget = (target, expectedHost, allowedHosts = approvedExternalHosts) => {
  const failures = []
  const normalizedTargetHost = normalizeHostname(target.hostname)
  const normalizedExpectedHost = normalizeHostname(expectedHost)
  const normalizedAllowedHosts = new Set([...allowedHosts].map(normalizeHostname))
  if (target.protocol !== 'https:') failures.push(`external link redirect must use HTTPS: ${target.href}`)
  if (target.username || target.password) failures.push(`external link redirect must not contain credentials: ${target.href}`)
  if (target.port) failures.push(`external link redirect must not contain a non-default port: ${target.href}`)
  if (normalizedTargetHost !== normalizedExpectedHost || !normalizedAllowedHosts.has(normalizedTargetHost)) {
    failures.push(`external link redirect must stay on an approved host: ${target.href}`)
  }
  if (isPrivateHostname(target.hostname)) failures.push(`external link redirect must not target a private or loopback host: ${target.href}`)
  return failures
}

export async function probeExternalLinks(urls = requiredLinks, {
  fetchImpl = globalThis.fetch,
  timeoutMs = Number(process.env.TERRASCALE_VALIDATOR_TIMEOUT_MS ?? 10_000),
  maxHops = 5,
  maxUrls = 100,
  allowedHosts = approvedExternalHosts,
} = {}) {
  const failures = []
  if (typeof fetchImpl !== 'function') return ['release link check requires a fetch implementation']

  const uniqueUrls = [...new Set(urls)]
  if (uniqueUrls.length > maxUrls) failures.push(`external link probe input exceeded the maximum of ${maxUrls} URLs`)
  for (const href of uniqueUrls.slice(0, maxUrls)) {
    let initial
    try {
      initial = new URL(href)
    } catch {
      failures.push(`external link probe received an invalid URL: ${href}`)
      continue
    }
    const initialFailures = validateProbeTarget(initial, initial.hostname, allowedHosts)
    if (initialFailures.length) {
      failures.push(...initialFailures)
      continue
    }

    let current = href
    const expectedHost = initial.hostname
    const seen = new Set()
    let completed = false
    for (let hop = 0; hop <= maxHops; hop += 1) {
      const normalizedCurrent = normalizeProbeUrl(current)
      if (seen.has(normalizedCurrent)) {
        failures.push(`external link redirect loop: ${href}`)
        completed = true
        break
      }
      seen.add(normalizedCurrent)

      let response
      try {
        const controller = new AbortController()
        const timer = setTimeout(() => controller.abort(), timeoutMs)
        try {
          response = await fetchImpl(current, { redirect: 'manual', signal: controller.signal })
        } finally {
          clearTimeout(timer)
        }
      } catch (error) {
        failures.push(`external link probe failed for ${current}: ${error instanceof Error ? error.message : String(error)}`)
        completed = true
        break
      }

      const status = response?.status
      if (typeof status !== 'number' || !Number.isInteger(status) || status < 100 || status > 599) {
        failures.push(`external link returned a malformed or non-numeric status: ${current}`)
        completed = true
        break
      }
      if (status >= 200 && status < 300) {
        completed = true
        break
      }
      if (status < 300 || status >= 400) {
        failures.push(`external link returned ${status}: ${current}`)
        completed = true
        break
      }
      const location = typeof response?.headers?.get === 'function'
        ? response.headers.get('location')
        : response?.headers?.location ?? response?.headers?.Location
      if (typeof location !== 'string' || !location.trim()) {
        failures.push(`external link returned ${status} without a Location header: ${current}`)
        completed = true
        break
      }
      let next
      try {
        next = new URL(location, current)
      } catch {
        failures.push(`external link returned an invalid Location header: ${location}`)
        completed = true
        break
      }
      const targetFailures = validateProbeTarget(next, expectedHost, allowedHosts)
      if (targetFailures.length) {
        failures.push(...targetFailures)
        completed = true
        break
      }
      if (seen.has(normalizeProbeUrl(next.href))) {
        failures.push(`external link redirect loop: ${href}`)
        completed = true
        break
      }
      if (hop === maxHops) {
        failures.push(`external link redirect chain exceeded ${maxHops} hops: ${href}`)
        completed = true
        break
      }
      current = next.href
    }
    if (!completed) failures.push(`external link probe did not complete: ${href}`)
  }
  return failures
}

async function main() {
  const release = process.argv.includes('--external') || process.argv.includes('--release')
  const root = path.resolve(process.env.TERRASCALE_VALIDATOR_ROOT ?? defaultRoot)
  const buildRoot = path.join(root, 'build')
  const result = validateRenderedLinks({ buildRoot })
  const failures = [...result.failures]
  if (release && failures.length === 0) {
    const renderedUrls = [...result.externalLinks]
    const renderedHosts = new Set()
    for (const href of renderedUrls) {
      try {
        renderedHosts.add(new URL(href).hostname)
      } catch {
        // Rendered-link validation already reports malformed URLs.
      }
    }
    failures.push(...(await probeExternalLinks(renderedUrls, { allowedHosts: renderedHosts })))
  }

  if (failures.length) {
    console.error(`Landing link validation failed${release ? ' (release mode, including external probes)' : ''}:`)
    for (const failure of failures) console.error(`- ${failure}`)
    process.exitCode = 1
    return
  }

  console.log(`Landing link validation passed (${result.linkCount} rendered links, ${result.externalLinks.size} HTTPS links${release ? ', external probes complete' : ''})`)
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) await main()
