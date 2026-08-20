#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const buildRoot = path.join(root, 'build')
const failures = []
const requiredLinks = [
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

const walk = (directory) => {
  const files = []
  if (!fs.existsSync(directory)) return files
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name)
    if (entry.isDirectory()) files.push(...walk(full))
    else if (entry.name.endsWith('.html')) files.push(full)
  }
  return files
}

const htmlFiles = walk(buildRoot)
if (!htmlFiles.length) failures.push('build: no prerendered HTML files found; run npm run build first.')

const links = []
for (const file of htmlFiles) {
  const html = fs.readFileSync(file, 'utf8')
  for (const [, href] of html.matchAll(/\bhref="([^"]+)"/g)) links.push({ file, href })
}

const resolveInternalTarget = (pathname) => {
  const cleanPath = pathname.replace(/^\/+/, '')
  const candidates = [
    path.join(buildRoot, cleanPath, 'index.html'),
    path.join(buildRoot, `${cleanPath}.html`),
    path.join(buildRoot, cleanPath || 'index.html'),
  ]
  return candidates.find((candidate) => fs.existsSync(candidate))
}

const checkFragment = (target, fragment, source) => {
  if (!fragment) return
  const html = fs.readFileSync(target, 'utf8')
  const escaped = fragment.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  if (!new RegExp(`(?:id|name)="${escaped}"`).test(html)) {
    failures.push(`${source}: missing internal anchor #${fragment}`)
  }
}

const externalLinks = new Set()
for (const { file, href } of links) {
  if (href.startsWith('#')) {
    checkFragment(file, href.slice(1), file)
    continue
  }
  if (href.startsWith('mailto:')) continue
  if (href.startsWith('/') || href.startsWith('./') || href.startsWith('../')) {
    const parsed = new URL(href, 'https://terrascale.tech')
    const target = resolveInternalTarget(parsed.pathname)
    if (!target) failures.push(`${file}: broken internal link ${href}`)
    else checkFragment(target, parsed.hash.slice(1), href)
    continue
  }

  let parsed
  try {
    parsed = new URL(href)
  } catch {
    failures.push(`${file}: invalid link URL ${href}`)
    continue
  }
  if (parsed.protocol !== 'https:') failures.push(`${file}: external links must use HTTPS: ${href}`)
  if (/localhost|127\.0\.0\.1|preview|\.test$|terrascale\.(?:com|io)$/i.test(parsed.hostname)) {
    failures.push(`${file}: preview or non-canonical external host: ${href}`)
  }
  externalLinks.add(parsed.href.replace(/\/$/, ''))
}

const linkCorpus = links.map(({ href }) => href).join('\n')
for (const required of requiredLinks) {
  if (!linkCorpus.includes(required)) failures.push(`build: missing required public link ${required}`)
}

if (process.argv.includes('--external')) {
  const controllerTimeout = 10_000
  for (const href of requiredLinks) {
    const controller = new AbortController()
    const timer = setTimeout(() => controller.abort(), controllerTimeout)
    try {
      const response = await fetch(href, { redirect: 'manual', signal: controller.signal })
      if (response.status < 200 || response.status >= 400) {
        failures.push(`external link returned ${response.status}: ${href}`)
      }
    } catch (error) {
      failures.push(`external link probe failed for ${href}: ${error instanceof Error ? error.message : String(error)}`)
    } finally {
      clearTimeout(timer)
    }
  }
}

if (failures.length) {
  console.error(`Landing link validation failed${process.argv.includes('--external') ? ' (including external probes)' : ''}:`)
  for (const failure of failures) console.error(`- ${failure}`)
  process.exit(1)
}

console.log(`Landing link validation passed (${links.length} rendered links, ${externalLinks.size} HTTPS links)`)
