#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptPath = fileURLToPath(import.meta.url)
const defaultRoot = path.resolve(path.dirname(scriptPath), '..')

export const requiredLinks = [
  'https://dash.terrascale.tech/register',
  'https://dash.terrascale.tech/login',
  'https://status.terrascale.tech',
  'https://docs.terrascale.tech/en/security/',
  'https://docs.terrascale.tech/en/legal/terms/',
  'https://docs.terrascale.tech/en/legal/privacy/',
  'https://docs.terrascale.tech/en/legal/acceptable-use/',
  'https://docs.terrascale.tech/en/legal/data-processing-terms/',
  'https://docs.terrascale.tech/en/support/',
]

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

const decodeHtmlEntities = (text) => text.replaceAll('&amp;', '&').replaceAll('&quot;', '"').replaceAll('&#39;', "'")

export function extractAttributes(html) {
  const attributes = []
  const pattern = /\b(href|src|action|content)\s*=\s*(["'])(.*?)\2/gi
  for (const match of html.matchAll(pattern)) {
    attributes.push({ name: match[1].toLowerCase(), value: decodeHtmlEntities(match[3]) })
  }
  return attributes
}

const collectJsonUrls = (value, urls) => {
  if (typeof value === 'string' && /^https?:\/\//i.test(value)) urls.add(value)
  else if (Array.isArray(value)) value.forEach((item) => collectJsonUrls(item, urls))
  else if (value && typeof value === 'object') Object.values(value).forEach((item) => collectJsonUrls(item, urls))
}

export function extractStructuredDataUrls(html) {
  const urls = new Set()
  const scripts = html.matchAll(/<script\b[^>]*type=["']application\/ld\+json["'][^>]*>([\s\S]*?)<\/script>/gi)
  for (const match of scripts) {
    try {
      collectJsonUrls(JSON.parse(match[1]), urls)
    } catch {
      urls.add('invalid-json-ld://payload')
    }
  }
  return [...urls]
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
  const escaped = fragment.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  if (!new RegExp(`(?:id|name)="${escaped}"`).test(html)) failures.push(`${source}: missing internal anchor #${fragment}`)
}

const isExternalHttpUrl = (value) => /^https?:\/\//i.test(value)

const validateExternalUrl = (value, source, failures) => {
  let parsed
  try {
    parsed = new URL(value)
  } catch {
    failures.push(`${source}: invalid external URL ${value}`)
    return
  }
  if (parsed.protocol !== 'https:') failures.push(`${source}: external URLs must use HTTPS: ${value}`)
  if (parsed.username || parsed.password) failures.push(`${source}: external URLs must not contain credentials: ${value}`)
  if (/localhost|127\.0\.0\.1|preview|\.test$|terrascale\.(?:com|io)$/i.test(parsed.hostname)) {
    failures.push(`${source}: preview or non-canonical external host: ${value}`)
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
    for (const attribute of extractAttributes(html)) {
      links.push({ file, ...attribute })
      if (attribute.name === 'href' || attribute.name === 'content') requiredCorpus.push(attribute.value)
      if (attribute.name === 'content') metadataUrls.push({ file, value: attribute.value })
    }
    for (const value of extractStructuredDataUrls(html)) {
      structuredDataUrls.push({ file, value })
      requiredCorpus.push(value)
    }
  }

  for (const { file, name, value: href } of links) {
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
    if (isExternalHttpUrl(value)) validateExternalUrl(value, `${file} metadata or structured data`, failures)
  }

  const linkCorpus = requiredCorpus.join('\n')
  for (const required of requiredLinks) {
    if (!linkCorpus.includes(required)) failures.push(`build: missing required public link ${required}`)
  }

  return { failures, linkCount: links.length, externalLinks }
}

export async function probeExternalLinks(urls = requiredLinks, {
  fetchImpl = globalThis.fetch,
  timeoutMs = Number(process.env.TERRASCALE_VALIDATOR_TIMEOUT_MS ?? 10_000),
  maxHops = 5,
} = {}) {
  const failures = []
  if (typeof fetchImpl !== 'function') return ['release link check requires a fetch implementation']

  for (const href of urls) {
    let current = href
    const seen = new Set()
    let reachable = false
    for (let hop = 0; hop <= maxHops; hop += 1) {
      if (seen.has(current)) {
        failures.push(`external link redirect loop: ${href}`)
        reachable = true
        break
      }
      seen.add(current)
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
        reachable = true
        break
      }
      const status = Number(response?.status)
      if (status >= 200 && status < 300) {
        reachable = true
        break
      }
      if (status < 300 || status >= 400) {
        failures.push(`external link returned ${Number.isInteger(status) ? status : 'an invalid status'}: ${current}`)
        reachable = true
        break
      }
      const location = typeof response?.headers?.get === 'function'
        ? response.headers.get('location')
        : response?.headers?.location ?? response?.headers?.Location
      if (!location) {
        failures.push(`external link returned ${status} without a Location header: ${current}`)
        reachable = true
        break
      }
      let next
      try {
        next = new URL(location, current)
      } catch {
        failures.push(`external link returned an invalid Location header: ${location}`)
        reachable = true
        break
      }
      if (next.protocol !== 'https:') failures.push(`external link redirect must use HTTPS: ${next.href}`)
      current = next.href
      if (hop === maxHops) failures.push(`external link redirect chain exceeded ${maxHops} hops: ${href}`)
    }
    if (!reachable) failures.push(`external link probe did not complete: ${href}`)
  }
  return failures
}

async function main() {
  const release = process.argv.includes('--external') || process.argv.includes('--release')
  const root = path.resolve(process.env.TERRASCALE_VALIDATOR_ROOT ?? defaultRoot)
  const buildRoot = path.join(root, 'build')
  const result = validateRenderedLinks({ buildRoot })
  const failures = [...result.failures]
  if (release) failures.push(...(await probeExternalLinks()))

  if (failures.length) {
    console.error(`Landing link validation failed${release ? ' (release mode, including external probes)' : ''}:`)
    for (const failure of failures) console.error(`- ${failure}`)
    process.exitCode = 1
    return
  }

  console.log(`Landing link validation passed (${result.linkCount} rendered links, ${result.externalLinks.size} HTTPS links${release ? ', external probes complete' : ''})`)
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) await main()
