import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import test from 'node:test'
import { checkDeployment, checkHosted, checkProject } from './check-pages.mjs'

const project = { id: 'synthetic-project', name: 'ts-docs', production_branch: 'main', subdomain: 'synthetic-docs.pages.dev' }
const commit = 'a'.repeat(40)
const deployment = {
  type: 'pages-deploy-detailed', pages_project: 'ts-docs', environment: 'preview',
  deployment_id: '12345678-1234-1234-1234-123456789abc',
  url: 'https://12345678.synthetic-docs.pages.dev',
  deployment_trigger: { metadata: { commit_hash: commit } },
}

test('Pages project must use main and direct uploads to preserve Blacksmith builds', () => {
  assert.equal(checkProject(project).name, 'ts-docs')
  assert.throws(() => checkProject({ ...project, production_branch: 'feature' }), /production branch/)
  assert.throws(() => checkProject({ ...project, source: { type: 'github' } }), /direct-upload/)
})

test('preview checks reject production publication and a different commit', () => {
  assert.equal(checkDeployment(project, [deployment], 'feature', commit).environment, 'preview')
  assert.throws(() => checkDeployment(project, [{ ...deployment, environment: 'production' }], 'feature', commit), /environment/)
  assert.throws(() => checkDeployment(project, [deployment], 'feature', 'b'.repeat(40)), /commit/)
})

test('Pages checks reject Worker output and unrelated or credential-bearing URLs', () => {
  assert.throws(() => checkDeployment(project, [{ type: 'deploy' }], 'feature', commit), /Pages deployment/)
  for (const url of ['https://other.pages.dev', 'https://123.synthetic-docs.pages.dev.attacker.invalid', 'https://user:pass@123.synthetic-docs.pages.dev', 'http://123.synthetic-docs.pages.dev']) {
    assert.throws(() => checkDeployment(project, [{ ...deployment, url }], 'feature', commit))
  }
})

test('hosted checks reject stale assets and an HTTP 200 fallback for missing routes', async (t) => {
  const dist = mkdtempSync(join(tmpdir(), 'docs-pages-test-'))
  t.after(() => rmSync(dist, { recursive: true, force: true }))
  const files = {
    'index.html': 'synthetic home', '404.html': 'synthetic 404 shell', 'getting-started/javascript.html': 'synthetic guide',
    'ar/getting-started/index.html': 'synthetic locale', 'guides/zero-configuration.html': 'synthetic contract',
    'examples/terrabase-js/quickstart.mjs': 'synthetic example', 'assets/app.test.js': 'synthetic app',
    'assets/style.test.css': 'synthetic style', 'assets/chunks/@localSearchIndexroot.test.js': 'synthetic search',
  }
  for (const [file, text] of Object.entries(files)) {
    mkdirSync(dirname(join(dist, file)), { recursive: true })
    writeFileSync(join(dist, file), text)
  }
  let stale = false
  let missingStatus = 404
  t.mock.method(globalThis, 'fetch', async (url) => {
    const route = new URL(url).pathname
    if (route === '/__docs_deployment_missing__') return new Response(files['404.html'], { status: missingStatus })
    const file = route === '/' ? 'index.html' : route.endsWith('/') ? `${route.slice(1)}index.html` : route.includes('/assets/') || route.endsWith('.mjs') ? route.slice(1) : `${route.slice(1)}.html`
    const contentType = file.endsWith('.html') ? 'text/html' : file.endsWith('.css') ? 'text/css' : 'application/javascript'
    return new Response(stale && file.endsWith('.js') ? 'stale asset' : files[file], { headers: { 'content-type': contentType } })
  })
  await checkHosted(dist, 'https://synthetic-docs.pages.dev')
  stale = true
  await assert.rejects(checkHosted(dist, 'https://synthetic-docs.pages.dev'), /differs/)
  stale = false
  missingStatus = 200
  await assert.rejects(checkHosted(dist, 'https://synthetic-docs.pages.dev'), /HTTP 404/)
})
