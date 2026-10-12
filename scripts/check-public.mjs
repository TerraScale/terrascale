import assert from 'node:assert/strict'
import { existsSync, readFileSync, statSync } from 'node:fs'
import { dirname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { auditTree, filesIn, findings } from './public-audit.mjs'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const dist = join(root, 'docs/.vitepress/dist')
assert.ok(existsSync(dist), 'Build the docs before checking public output')
const problems = auditTree(dist)
const sources = filesIn(join(root, 'docs')).filter((file) =>
  !file.includes('/.vitepress/') && !file.includes('/public/') && /\.(?:md|ts)$/.test(file))
for (const source of sources) problems.push(...findings(readFileSync(source, 'utf8'), relative(root, source)))
if (problems.length) {
  for (const { file, line, category } of problems.slice(0, 30))
    console.error(`${file}:${line}: ${category}`)
  throw new Error(`Public privacy audit failed: ${problems.length} finding(s); values withheld`)
}

const htmlFiles = filesIn(dist).filter((file) => file.endsWith('.html'))
for (const source of sources.filter((file) => file.endsWith('.md'))) {
  const page = join(dist, relative(join(root, 'docs'), source).replace(/\.md$/, '.html'))
  assert.ok(existsSync(page), `Missing generated page: ${relative(root, source)}`)
}
assert.ok(htmlFiles.length > 0, 'The output contains no rendered pages')
const anchorCache = new Map()
let links = 0
for (const file of htmlFiles) {
  const rel = relative(dist, file)
  const html = readFileSync(file, 'utf8')
  // VitePress's generated 404 shell is hydrated in the browser.
  if (rel !== '404.html') assert.ok(/<main\b/.test(html), `${rel}: missing main landmark`)
  assert.ok(/<html[^>]*\blang=/.test(html), `${rel}: missing document language`)
  for (const [, attribute] of html.matchAll(/\b(?:href|src)="([^"]+)"/g)) {
    if (/^(?:https?:|mailto:|data:|javascript:)/.test(attribute)) continue
    const url = new URL(attribute.replaceAll('&amp;', '&'), `https://docs.example/${rel}`)
    const path = decodeURIComponent(url.pathname).replace(/^\//, '')
    const candidates = [join(dist, path), join(dist, `${path}.html`), join(dist, path, 'index.html')]
    const target = candidates.find((candidate) => existsSync(candidate) && statSync(candidate).isFile())
    assert.ok(target, `${rel}: broken local asset or link ${url.pathname}`)
    if (url.hash && target.endsWith('.html')) {
      let anchors = anchorCache.get(target)
      if (!anchors) {
        anchors = new Set([...readFileSync(target, 'utf8').matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]))
        anchorCache.set(target, anchors)
      }
      assert.ok(anchors.has(decodeURIComponent(url.hash.slice(1))), `${rel}: missing link anchor ${url.pathname}${url.hash}`)
    }
    links++
  }
}

console.log(`Public audit passed: ${sources.length} source files, ${filesIn(dist).length} bundled files, ${htmlFiles.length} pages, ${links} local links/assets and anchors.`)
