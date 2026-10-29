import assert from 'node:assert/strict';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { filesIn } from './public-audit.mjs';

const dist = new URL('../docs/.vitepress/dist/', import.meta.url).pathname;
const htmlFiles = filesIn(dist).filter((file) => file.endsWith('.html'));
assert.ok(htmlFiles.length, 'Build the docs before checking links');
const anchorCache = new Map()
let links = 0
for (const file of htmlFiles) {
  const rel = relative(dist, file)
  const html = readFileSync(file, 'utf8')
  if (rel !== '404.html') assert.ok(/<main\b/.test(html), `${rel}: missing main landmark`)
  assert.ok(/<html[^>]*\blang=/.test(html), `${rel}: missing page language`)
  for (const [, attribute] of html.matchAll(/\b(?:href|src)="([^"]+)"/g)) {
    if (/^(?:mailto:|data:|javascript:)/.test(attribute)) continue
    const url = new URL(attribute.replaceAll('&amp;', '&'), `https://docs.terrascale.tech/${rel}`)
    if (url.origin !== 'https://docs.terrascale.tech') continue
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


const redirects = new Map();
for (const line of readFileSync(join(dist, '_redirects'), 'utf8').trim().split('\n')) {
  const [source, destination, status, extra] = line.trim().split(/\s+/);
  assert.ok(source.startsWith('/') && destination.startsWith('/') && status === '301' && !extra, `Invalid redirect: ${line}`);
  assert.ok(!redirects.has(source), `Duplicate redirect: ${source}`);
  redirects.set(source, destination);
  assert.ok(!existsSync(join(dist, `${source.slice(1).replace(/\.html$/, '')}.html`)), `Retired page still exists: ${source}`);
  assert.ok(existsSync(join(dist, `${destination.slice(1)}.html`)), `Missing redirect target: ${destination}`);
}
for (const destination of redirects.values()) assert.ok(!redirects.has(destination), `Chained redirect: ${destination}`);
console.log(`Link check passed: ${htmlFiles.length} rendered pages, ${links} links/assets/anchors, ${redirects.size} permanent redirects.`);
