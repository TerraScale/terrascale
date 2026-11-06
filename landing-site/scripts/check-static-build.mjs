import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { existsSync, readFileSync } from 'node:fs'
import { secureHtml } from './security-headers.mjs'

const read = (file) => readFileSync(new URL(`../build/${file}`, import.meta.url), 'utf8')
const pages = [
  ['index.html', '/', 'TerraScale Public Alpha | Regional SQL Database'],
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

const contactMain = read('contact.html').match(/<main\b[^>]*>([\s\S]*?)<\/main>/)?.[1]
assert.ok(contactMain, 'contact: prerendered main content')
const contactLinks = [...contactMain.matchAll(/<a\b([^>]*)>/g)].map(([, attributes]) => attributes)
assert.equal(contactLinks.filter((attributes) => /href="mailto:support@terrascale.tech"/.test(attributes)).length, 2,
  'contact: the primary email button and readable address both open the mail client')
for (const href of ['https://github.com/TerraScale', 'https://docs.terrascale.tech',
  'https://status.terrascale.tech', 'https://api.terrascale.tech/scalar/']) {
  const attributes = contactLinks.find((attributes) => attributes.includes(`href="${href}"`))
  assert.ok(attributes, `contact: prerendered resource link ${href}`)
  assert.match(attributes, /target="_blank"/, `contact: resource opens separately ${href}`)
  assert.match(attributes, /rel="[^"]*noopener/, `contact: safe resource link ${href}`)
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
const sdk = read('sdk.html')
assert.ok(sdk.includes('content="terrascale.tech/sdk git https://github.com/TerraScale/terrascale sdk/go"'), 'Go 1.25+ subdirectory discovery')
assert.ok(sdk.includes('name="go-source"'), 'Go source links')
assert.ok(!sdk.includes('github.com/TerraScale/ts-go'), 'SDK source uses monorepo')
