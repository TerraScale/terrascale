import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { existsSync, readFileSync } from 'node:fs'
import { secureHtml } from './security-headers.mjs'

const read = (file) => readFileSync(new URL(`../build/${file}`, import.meta.url), 'utf8')
const pages = [
  ['index.html', '/', 'TerraScale Public Alpha | Regional Document Database'],
  ['about.html', '/about', 'About TerraScale'],
  ['pricing.html', '/pricing', 'TerraScale Public Alpha pricing'],
  ['contact.html', '/contact', 'Contact TerraScale'],
  ['404.html', '/404', '404 | TerraScale'],
]

for (const [file, route, title] of pages) {
  const html = read(file)
  assert.ok(html.includes(`<title>${title}</title>`), `${route}: page title`)
  assert.ok(html.includes(`rel="canonical" href="https://terrascale.tech${route}"`), `${route}: canonical`)
  assert.match(html, /<main\b[\s\S]*?<h1\b/, `${route}: prerendered content`)
  assert.match(html, /Content-Security-Policy/, `${route}: CSP`)
  assert.match(html, /<style data-inline-css=/, `${route}: inlined production CSS`)
  assert.doesNotMatch(html, /<form\b/, `${route}: no request-time form actions`)
  for (const [, attributes, source] of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
    if (/\bsrc\s*=/i.test(attributes) || !source.trim()) continue
    const hash = createHash('sha256').update(source).digest('base64')
    assert.ok(html.includes(`'sha256-${hash}'`), `${route}: exact inline script CSP hash`)
  }
  const primaryNav = html.match(/<nav\b[^>]*aria-label="Primary"[^>]*>([\s\S]*?)<\/nav>/)?.[1]
  assert.ok(primaryNav, `${route}: prerendered navigation`)
  const activeLinks = [...primaryNav.matchAll(/<a\b([^>]*)>/g)]
    .filter(([, attributes]) => /class="[^"]*\bis-active\b/.test(attributes))
  assert.equal(activeLinks.length, route === '/404' ? 0 : 1, `${route}: active navigation`)
  if (activeLinks.length) {
    const href = activeLinks[0][1].match(/href="([^"]+)"/)[1]
    assert.equal(new URL(href, `https://terrascale.tech${route}`).pathname, route)
  }
}

assert.match(read('robots.txt'), /Sitemap: https:\/\/terrascale.tech\/sitemap.xml/)
for (const route of ['/', '/about', '/pricing', '/contact']) {
  assert.ok(read('sitemap.xml').includes(`<loc>https://terrascale.tech${route}</loc>`))
}
for (const file of ['_redirects', '_headers', 'pricing.json', 'regions.json']) {
  assert.equal(read(file), readFileSync(new URL(`../static/${file}`, import.meta.url), 'utf8'), file)
}
assert.equal(read('sdk.html'), secureHtml(readFileSync(new URL('../static/sdk.html', import.meta.url), 'utf8')))
assert.ok(!existsSync(new URL('../build/_worker.js', import.meta.url)), 'static assets only')
console.log('Verified five prerendered pages, navigation, canonical URLs, CSP hashes, and static resources.')
